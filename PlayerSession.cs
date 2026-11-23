using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TimeshiftViewer.Puyo;

namespace TimeshiftViewer
{
    // ------------------------------------------------------------
    //  再生中のセッション
    //  ffmpeg（取り込み）→ mpv（プレーヤーのウィンドウに埋め込み）、
    //  ffmpeg の解析用出力 → 対戦の検知 → シークバーへの表示、シミュレーターへの盤面読み込み
    // ------------------------------------------------------------
    sealed class PlayerSession
    {
        readonly PlayerForm player;
        readonly bool analyze;
        MpvIpc ipc;
        AnalysisPipe pipe;
        readonly MatchAnalyzer analyzer = new MatchAnalyzer();

        // mpv の時刻との対応（demuxer-cache-time を定期的に取得）
        double syncMpv = double.NaN; long syncAt;
        readonly object syncLock = new object();

        public PlayerSession(PlayerForm p, bool analyzeMatches)
        {
            player = p;
            analyze = analyzeMatches;
            player.LoadRequested += () => _ = LoadSimFromScreen();
            analyzer.NameReader = (f, side) => NameOcr.ReadName(f, side).Result;
        }

        /// <summary>ffmpeg の出力を mpv に流し込み、mpv が閉じるまで待つ。ffmpeg が先に落ちたらそのログを返す</summary>
        public async Task<string> Run(string ffArgs, string mpvArgs)
        {
            var log = new StringBuilder();
            var logFile = new StreamWriter(Paths.Log, false, new UTF8Encoding(false)) { AutoFlush = true };

            // 解析用の出力（切り出した BGR 画像を 10fps で名前付きパイプへ）
            string id = Process.GetCurrentProcess().Id + "-" + Environment.TickCount;
            if (analyze)
            {
                var c = PuyoLayout.AnalysisCrop;
                pipe = new AnalysisPipe("tsv-ana-" + id, c.Width, c.Height, OnFrame);
                // -y: 名前付きパイプは「既にあるファイル」扱いになるので上書きを許可する
                ffArgs += $" -y -map 0:v:0 -an -vf \"fps=10,scale=1920:1080,crop={c.Width}:{c.Height}:{c.X}:{c.Y}\" -pix_fmt bgr24 -f rawvideo \"{pipe.PipePath}\"";
                analyzer.Changed += PushMatches;
            }
            logFile.WriteLine($"[{DateTime.Now}] ffmpeg {ffArgs}");

            // mpv はプレーヤーのウィンドウに埋め込む（--wid）。IPC で script-message や全画面の切り替えを受け取る
            string ipcName = "tsv-" + id;
            mpvArgs = $"--input-ipc-server=\\\\.\\pipe\\{ipcName} --wid={player.VideoHost.Handle.ToInt64()} " +
                      (Environment.GetEnvironmentVariable("TSV_MPV_EXTRA") ?? "") + " " + mpvArgs;   // 開発用: --log-file= など

            var mp = Process.Start(new ProcessStartInfo(Paths.Mpv, mpvArgs)
            { UseShellExecute = false, RedirectStandardInput = true, WorkingDirectory = Paths.Base });
            ipc = await Task.Run(() => MpvIpc.Connect(ipcName, mp));
            if (ipc != null) { player.Attach(ipc); StartClockSync(mp); }

            var ff = new Process
            {
                StartInfo = new ProcessStartInfo(Paths.Ffmpeg, ffArgs)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                    RedirectStandardError = true, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = Paths.Base,
                }
            };
            ff.ErrorDataReceived += (s, e) =>
            {
                if (e.Data == null) return;
                lock (log) { log.AppendLine(e.Data); if (log.Length > 20000) log.Remove(0, 10000); }
                try { lock (logFile) logFile.WriteLine(e.Data); } catch { }
            };
            ff.Start();
            ff.BeginErrorReadLine();

            var copy = Task.Run(async () =>
            {
                try { await ff.StandardOutput.BaseStream.CopyToAsync(mp.StandardInput.BaseStream, 65536); } catch { }
                try { mp.StandardInput.Close(); } catch { }   // ffmpeg が止まったら mpv に終端を伝える
            });

            // mpv が閉じるまで待つ（ffmpeg が先に止まった場合、mpv は受信済みの分を再生し終えると閉じる）
            await Task.Run(() => mp.WaitForExit());
            // mpv を閉じても ffmpeg は自分からは終了しない（出力が詰まるだけ）ので、終了済みなら異常停止
            bool ffDiedFirst = ff.HasExited;
            if (!ff.HasExited) { try { ff.Kill(); } catch { } }
            try { ff.WaitForExit(3000); } catch { }
            try { lock (logFile) logFile.Dispose(); } catch { }
            pipe?.Dispose();
            ipc?.Dispose();
            if (ffDiedFirst) { lock (log) return log.ToString(); }
            return null;
        }

        // ---------------- 時刻合わせ ----------------
        void StartClockSync(Process mp)
        {
            Task.Run(async () =>
            {
                while (!mp.HasExited)
                {
                    var t = await ipc.GetDouble("demuxer-cache-time");
                    if (t.HasValue)
                        lock (syncLock) { syncMpv = t.Value; syncAt = AnalysisPipe.Clock.ElapsedMilliseconds; }
                    await Task.Delay(500);
                }
            });
        }

        /// <summary>ストップウォッチの時刻（ms）→ mpv の時刻（秒）。まだ対応が取れていなければ NaN</summary>
        long firstFrameAt = -1;
        double ToMpvTime(long at)
        {
            lock (syncLock)
            {
                if (firstFrameAt < 0) firstFrameAt = at;
                // mpv との対応がまだ取れていない最初の数秒は、最初の画像からの経過時間で代用する
                // （取り込みも mpv の時刻も 0 から始まるので、ずれは小さい）
                if (double.IsNaN(syncMpv)) return (at - firstFrameAt) / 1000.0;
                return syncMpv + (at - syncAt) / 1000.0;
            }
        }

        // ---------------- 解析 ----------------
        long lastDebug;
        bool dumped;
        void OnFrame(FrameImage f, long at)
        {
            double t = ToMpvTime(at);
            if (Environment.GetEnvironmentVariable("TSV_DEBUG") != null && at - lastDebug > 1000)
            {
                lastDebug = at;
                try
                {
                    File.AppendAllText(Path.Combine(Paths.Base, "debug-matches.txt"),
                        $"[{DateTime.Now:HH:mm:ss.f}] frame t={t:F2} recv={pipe?.Received} proc={pipe?.Processed} go={Recognizer.GoRatio(f):P1} " +
                        $"wins={Recognizer.ReadWins(f)} puyos={Recognizer.CountPuyos(f, 0)}/{Recognizer.CountPuyos(f, 1)}\n");
                }
                catch { }
            }
            if (Environment.GetEnvironmentVariable("TSV_DUMP") != null && !dumped && t > 4)
            {
                dumped = true;
                try { f.Save(Path.Combine(Paths.Base, "debug-frame.png")); } catch { }
            }
            if (double.IsNaN(t)) return;
            lock (analyzer) analyzer.Process(f, t);
        }

        long lastPush;
        bool pushPending;
        void PushMatches()
        {
            // 連続した変化はまとめて送る（最大で 0.3 秒に1回）
            long now = AnalysisPipe.Clock.ElapsedMilliseconds;
            if (now - lastPush < 300)
            {
                if (pushPending) return;
                pushPending = true;
                Task.Delay(300).ContinueWith(_ => { pushPending = false; PushMatches(); });
                return;
            }
            lastPush = now;
            List<Dictionary<string, object>> list;
            lock (analyzer) list = analyzer.Matches.Select(m => m.ToJson()).ToList();
            string json = MpvIpc.ToJson(list);
            ipc?.ToScript("ts-matches", json);
            if (Environment.GetEnvironmentVariable("TSV_DEBUG") != null)
                try { File.AppendAllText(Path.Combine(Paths.Base, "debug-matches.txt"), $"[{DateTime.Now:HH:mm:ss.f}] recv={pipe?.Received} proc={pipe?.Processed} {json}\n"); } catch { }
        }

        // ---------------- シミュレーターへの読み込み ----------------
        /// <summary>今の画面から 1P・2P 両方の盤面と、その試合のツモ・各側の手数を読み込む</summary>
        public async Task LoadSimFromScreen()
        {
            if (ipc == null) return;
            var sim = player.Sim;
            if (sim == null) return;
            try
            {
                double t = await ipc.GetDouble("time-pos") ?? 0;
                string path = Path.Combine(Path.GetTempPath(), "tsv-sim-" + Process.GetCurrentProcess().Id + ".png");
                await ipc.Command("screenshot-to-file", path, "video");
                FrameImage f = await Task.Run(() => File.Exists(path) ? FrameImage.Load(path) : null);
                if (f == null) return;

                MatchInfo m;
                lock (analyzer) m = analyzer.Matches.LastOrDefault(x => x.Start <= t + 0.2 && (x.End < 0 || x.End >= t - 0.2));
                // 試合が分からない（ツモ未検知）ときは全パターンから仮に選ぶ
                TsumoSource src = m?.Tsumo ?? TsumoSource.FromKnown(4, null);

                var fields = new Field[2];
                var idx = new int[2];
                var labels = new string[2];
                for (int side = 0; side < 2; side++)
                {
                    fields[side] = Recognizer.ReadField(f, side);
                    var (n1, n2) = Recognizer.ReadNext(f, side);
                    string name = side == 0 ? "1P" : "2P";
                    if (m != null && m.Tsumo != null)
                    {
                        lock (analyzer) idx[side] = m.HandIndexAt(side, t);
                        idx[side] = AlignWithNext(src, idx[side], n1, n2, m.Swapped);
                        string who = m.Names[side] != null ? $"{m.Names[side]}（{name}）" : name;
                        labels[side] = $"第{m.Id}戦 {who} {idx[side] + 1}手目（画面から）";
                    }
                    else
                    {
                        idx[side] = 0;
                        labels[side] = $"{name} 画面から（試合を検知できていません）";
                    }
                }
                player.BeginInvoke((Action)(() =>
                {
                    sim.SetTsumo(src);
                    for (int side = 0; side < 2; side++) sim.LoadPosition(side, fields[side], idx[side], labels[side]);
                }));
            }
            catch (Exception ex)
            {
                if (Environment.GetEnvironmentVariable("TSV_DEBUG") != null)
                    try { File.AppendAllText(Path.Combine(Paths.Base, "debug-matches.txt"), $"LoadSim error: {ex}\n"); } catch { }
            }
        }

        /// <summary>画面の NEXT と一致するよう、手の番号を前後に少しずらして合わせる</summary>
        static int AlignWithNext(TsumoSource src, int idx, (byte axis, byte child)? n1, (byte axis, byte child)? n2, bool swapped)
        {
            if (n1 == null || n2 == null) return idx;
            (byte, byte) Conv((byte axis, byte child) p) =>
                swapped ? ((byte)(p.child - 1), (byte)(p.axis - 1)) : ((byte)(p.axis - 1), (byte)(p.child - 1));
            var a = Conv(n1.Value); var b = Conv(n2.Value);
            foreach (int d in new[] { 0, 1, -1, 2, -2, 3, -3 })
            {
                int k = idx + d;
                if (k < 0) continue;
                if (src.TryGet(k + 1, out var h1) && src.TryGet(k + 2, out var h2) && h1 == a && h2 == b) return k;
            }
            return idx;
        }
    }
}
