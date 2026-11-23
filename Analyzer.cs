using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using TimeshiftViewer.Puyo;

namespace TimeshiftViewer
{
    // ------------------------------------------------------------
    //  1試合の記録
    // ------------------------------------------------------------
    sealed class MatchInfo
    {
        public int Id;
        public double Start, End = -1;                 // mpv の時刻（demuxer の時刻）。End < 0 は試合中
        public string[] Names = { null, null };
        public int[] WinsStart = { -1, -1 }, WinsEnd = { -1, -1 };
        public int[] Sets = { -1, -1 };
        public bool NumberMode;
        public int Winner = -1;                        // 0=1P 1=2P -1=不明
        // ツモ（色は盤面の 1〜5。上を子として読んだまま）
        public readonly List<(byte axis, byte child)> Seq = new List<(byte, byte)>();
        public readonly List<double>[] Advances = { new List<double>(), new List<double>() };   // 各側の NEXT が進んだ時刻
        public TsumoSource Tsumo;                      // 識別結果（候補が複数なら無作為に選んだもの）
        public bool Swapped;                           // 軸と子を入れ替えたほうが一致した
        public string TsumoKey = "";                   // 識別に使った並び（変わったら識別し直す）
        public bool NamesReread;                       // 試合中にもう一度名前を読んだ

        /// <summary>時刻 t に操作中の組ぷよが何手目か（0始まり）</summary>
        public int HandIndexAt(int side, double t)
        {
            int n = Advances[side].Count(x => x <= t + 0.05);
            return Math.Max(0, n - 1);
        }

        public Dictionary<string, object> ToJson() => new Dictionary<string, object>
        {
            ["id"] = Id, ["s"] = Start, ["e"] = End,
            ["n1"] = Names[0] ?? "1P", ["n2"] = Names[1] ?? "2P",
            ["w1"] = WinsStart[0], ["w2"] = WinsStart[1], ["ew1"] = WinsEnd[0], ["ew2"] = WinsEnd[1],
            ["set1"] = Sets[0], ["set2"] = Sets[1], ["win"] = Winner,
            ["tsumo"] = Tsumo == null ? "" : Tsumo.Label.Replace("\n", " "),
            ["hands"] = Seq.Count,
        };
    }

    // ------------------------------------------------------------
    //  対戦の検知（ffmpeg から受け取った切り出し映像を1枚ずつ見る）
    // ------------------------------------------------------------
    sealed class MatchAnalyzer
    {
        public readonly List<MatchInfo> Matches = new List<MatchInfo>();
        public event Action Changed;                   // 解析スレッドから呼ばれる
        public Func<FrameImage, int, string> NameReader;   // 名前の読み取り（null なら読まない）

        MatchInfo cur;
        int nextId = 1;
        WinInfo lastWins;
        string lastWinKey;
        int winStable;
        double lastGoAt = -100;
        readonly SideTracker[] sides = { new SideTracker(), new SideTracker() };
        public string Status = "";

        public MatchInfo Current => cur;

        /// <summary>1枚分の処理。t はその画像の mpv 時刻</summary>
        public void Process(FrameImage f, double t)
        {
            var raw = Recognizer.ReadWins(f);
            bool go = Recognizer.IsGo(f);
            // 勝利数・セット数は同じ読みが3枚続いたものだけ使う（1枚だけの読み違いで終了と判定しないように）
            string wkey = raw.Valid ? raw.ToString() : null;
            if (wkey != null && wkey == lastWinKey) winStable++; else { winStable = 0; lastWinKey = wkey; }
            var wins = raw.Valid && winStable >= 2 ? raw : new WinInfo();

            // ---- 開始: 「ゴー！」が出ていて、両方の盤面がほぼ空 ----
            if (go && t - lastGoAt > 5 && Recognizer.CountPuyos(f, 0) <= 2 && Recognizer.CountPuyos(f, 1) <= 2)
            {
                lastGoAt = t;
                if (cur != null) Finish(t, -1, null);
                cur = new MatchInfo { Id = nextId++, Start = t };
                var w = raw.Valid ? raw : lastWins;   // ゴーの瞬間は1枚目でも使う（直前の安定した値があればそちら）
                if (lastWins != null && lastWins.Valid) w = lastWins;
                if (w != null && w.Valid)
                {
                    cur.WinsStart = new[] { w.Left, w.Right };
                    cur.NumberMode = w.NumberMode;
                    cur.Sets = new[] { w.SetLeft, w.SetRight };
                }
                foreach (var s in sides) s.Reset();
                Matches.Add(cur);
                if (Matches.Count > 200) Matches.RemoveAt(0);
                ReadNamesAsync(f, cur);
                Changed?.Invoke();
            }

            if (wins.Valid) lastWins = wins;
            if (cur == null) return;

            // 名前は試合ごとに読む。ゴーの瞬間は画面が暗いことがあるので、5秒後にもう一度読んで上書きする
            if (!cur.NamesReread && t - cur.Start > 5) { cur.NamesReread = true; ReadNamesAsync(f, cur, true); }

            // ---- 終了: 勝利数が増えた ----
            if (wins.Valid && cur.WinsStart[0] >= 0 && t - cur.Start > 3)
            {
                bool l = wins.Left > cur.WinsStart[0], r = wins.Right > cur.WinsStart[1];
                if ((l || r) && !(l && r)) { Finish(t, l ? 0 : 1, wins); return; }
            }
            if (cur.Sets[0] < 0 && wins.Valid && wins.SetLeft >= 0 && t - cur.Start < 30)
            {
                cur.Sets = new[] { wins.SetLeft, wins.SetRight };
                Changed?.Invoke();
            }
            if (cur.WinsStart[0] < 0 && wins.Valid)
            {
                // 開始時に勝利数が読めなかったら最初に読めた値を使う
                cur.WinsStart = new[] { wins.Left, wins.Right };
                cur.NumberMode = wins.NumberMode;
                cur.Sets = new[] { wins.SetLeft, wins.SetRight };
                Changed?.Invoke();
            }

            // ---- ツモ ----
            bool grew = false;
            for (int side = 0; side < 2; side++)
            {
                var adv = sides[side].Update(f, side);
                if (adv == null) continue;
                cur.Advances[side].Add(t);
                // 長いほうの並びを採用する（両側とも同じツモ列）
                var seq = sides[side].Seq;
                if (seq.Count > cur.Seq.Count)
                {
                    cur.Seq.Clear(); cur.Seq.AddRange(seq);
                    grew = true;
                }
            }
            if (grew) { Identify(cur); Changed?.Invoke(); }
        }

        void Finish(double t, int winner, WinInfo w)
        {
            if (cur == null) return;
            cur.End = t;
            cur.Winner = winner;
            if (w != null) cur.WinsEnd = new[] { w.Left, w.Right };
            cur = null;
            Changed?.Invoke();
        }

        void ReadNamesAsync(FrameImage f, MatchInfo m, bool reread = false)
        {
            if (NameReader == null) return;
            var reader = NameReader;
            // 画像の領域は次のフレームで再利用されるので複製してから別スレッドで読む
            f = new FrameImage((byte[])f.Px.Clone(), f.W, f.H, f.Stride, f.OriginX, f.OriginY, f.Scale);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var prev = Matches.LastOrDefault(x => x != m && x.Names[0] != null);
                    for (int side = 0; side < 2; side++)
                    {
                        string n = reader(f, side);
                        if (n != null) m.Names[side] = n;                         // 読めたら毎回その試合の名前にする
                        else if (!reread && m.Names[side] == null) m.Names[side] = prev?.Names[side];   // 読めなければ前の試合の名前を仮に使う
                    }
                    Changed?.Invoke();
                }
                catch { }
            });
        }

        // ---- 識別: 色数と軸子の向きを試し、候補があるものを使う ----
        static void Identify(MatchInfo m)
        {
            string key = string.Join(",", m.Seq.Select(p => $"{p.axis}{p.child}"));
            if (key == m.TsumoKey) return;
            m.TsumoKey = key;
            var colors = new HashSet<byte>(m.Seq.SelectMany(p => new[] { p.axis, p.child }));
            var modes = colors.Count >= 5 ? new[] { 5 } : colors.Count == 4 ? new[] { 4, 5 } : new[] { 4, 3, 5 };
            foreach (bool swap in new[] { false, true })
                foreach (int mode in modes)
                {
                    var known = new List<byte>();
                    foreach (var (a, c) in m.Seq)
                    {
                        byte ax = swap ? c : a, ch = swap ? a : c;
                        known.Add((byte)(ax - 1)); known.Add((byte)(ch - 1));
                    }
                    var src = TsumoSource.FromKnown(mode, known);
                    if (src == null) continue;
                    // 同じ候補集合なら、前に選んだものを使い続ける（表示がちらつかないように）
                    if (m.Tsumo != null && m.Tsumo.Mode == mode && m.Swapped == swap && m.Tsumo.Seed >= 0 && StillMatches(m.Tsumo.Seed, mode, known))
                        { m.Tsumo.Candidates = src.Candidates; m.Tsumo.KnownHands = src.KnownHands; return; }
                    m.Tsumo = src; m.Swapped = swap;
                    return;
                }
            // どれにも一致しない（読み違い）: 全パターンから仮に選ぶ
            if (m.Tsumo == null) m.Tsumo = TsumoSource.FromKnown(4, null);
        }

        static bool StillMatches(int seed, int mode, List<byte> known)
        {
            var seq = TsumoGen.Generate(seed).Seq[mode];
            for (int i = 0; i < known.Count; i++) if (seq[i % 256] != known[i]) return false;
            return true;
        }

        // ------------------------------------------------------------
        //  片側の NEXT 欄の追跡
        //  NEXT が動いて（画素の変化）落ち着いたら1手進んだとみなし、新しい NEXT2 を並びに足す
        // ------------------------------------------------------------
        sealed class SideTracker
        {
            public readonly List<(byte axis, byte child)> Seq = new List<(byte, byte)>();
            (byte, byte)? lastRead1, lastRead2;            // 前の画像の読み
            (byte, byte)? stable1, stable2;                // 最後に確定した NEXT / NEXT2
            readonly float[] sig = new float[12], prevSig = new float[12];
            bool havePrev, moved;
            int sameCount;

            public void Reset()
            {
                Seq.Clear(); lastRead1 = lastRead2 = stable1 = stable2 = null;
                havePrev = moved = false; sameCount = 0;
            }

            /// <summary>1手進んだら新しい NEXT2 を返す（最初の確定時は NEXT を返す）</summary>
            public (byte, byte)? Update(FrameImage f, int side)
            {
                Recognizer.NextSignature(f, side, sig);
                if (havePrev)
                {
                    float d = 0;
                    for (int i = 0; i < 12; i++) d += Math.Abs(sig[i] - prevSig[i]);
                    if (d > 60) moved = true;                // 動いた（組ぷよが送られた）
                }
                Array.Copy(sig, prevSig, 12); havePrev = true;

                var (n1, n2) = Recognizer.ReadNext(f, side);
                if (n1 == null || n2 == null) { lastRead1 = lastRead2 = null; sameCount = 0; return null; }
                if (Equals(n1, lastRead1) && Equals(n2, lastRead2)) sameCount++;
                else sameCount = 0;
                lastRead1 = n1; lastRead2 = n2;
                if (sameCount < 1) return null;              // 2枚続けて同じ読みになったら確定

                if (stable1 == null)
                {
                    // 開始時: NEXT と NEXT2 がそれぞれ1手目・2手目
                    stable1 = n1; stable2 = n2; moved = false;
                    Seq.Add(n1.Value); Seq.Add(n2.Value);
                    return n1;
                }
                bool changed = !Equals(n1, stable1) || !Equals(n2, stable2);
                if (!changed && !moved) return null;
                if (!changed && moved)
                {
                    // 同じ組が続いた場合も、動きがあれば1手進んだとみなす
                    // （ただし動き検出の誤りを避けるため、NEXT と NEXT2 が同じ色の組のときだけ）
                    if (!Equals(n1, n2)) { moved = false; return null; }
                }
                moved = false;
                if (!Equals(n1, stable2) && Seq.Count >= 1)
                {
                    // 1手以上取りこぼした: NEXT も新しい手として足す
                    Seq.Add(n1.Value);
                }
                stable1 = n1; stable2 = n2;
                Seq.Add(n2.Value);
                return n2;
            }
        }
    }

    // ------------------------------------------------------------
    //  ffmpeg の解析用出力（名前付きパイプの生の BGR 画像）を受け取る
    //  受け取りが遅れると ffmpeg 全体（映像の取り込み）が止まるので、受け取りと解析を分け、解析は最新の1枚だけを見る
    // ------------------------------------------------------------
    sealed class AnalysisPipe : IDisposable
    {
        public readonly string PipePath;
        readonly NamedPipeServerStream server;
        readonly int frameBytes, w, h;
        readonly Action<FrameImage, long> onFrame;     // 解析スレッドで呼ぶ（long はストップウォッチの時刻 ms）
        byte[] latest; long latestAt;
        readonly object gate = new object();
        readonly AutoResetEvent ready = new AutoResetEvent(false);
        volatile bool stop;
        public static readonly Stopwatch Clock = Stopwatch.StartNew();
        public int Received, Processed;

        public AnalysisPipe(string name, int width, int height, Action<FrameImage, long> handler)
        {
            w = width; h = height; frameBytes = w * h * 3;
            PipePath = @"\\.\pipe\" + name;
            onFrame = handler;
            server = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, frameBytes * 2, 0);
            new Thread(ReadLoop) { IsBackground = true, Name = "analysis-read" }.Start();
            new Thread(ProcessLoop) { IsBackground = true, Name = "analysis-proc" }.Start();
        }

        void ReadLoop()
        {
            try
            {
                server.WaitForConnection();
                for (int i = 0; i < 3; i++) pool.Enqueue(new byte[frameBytes]);
                while (!stop)
                {
                    byte[] buf;
                    lock (gate) buf = pool.Count > 0 ? pool.Dequeue() : new byte[frameBytes];
                    int got = 0;
                    while (got < frameBytes)
                    {
                        int n = server.Read(buf, got, frameBytes - got);
                        if (n <= 0) return;
                        got += n;
                    }
                    Received++;
                    lock (gate)
                    {
                        if (latest != null) pool.Enqueue(latest);   // 未処理の古い画像は捨てる
                        latest = buf; latestAt = Clock.ElapsedMilliseconds;
                    }
                    ready.Set();
                }
            }
            catch { }
        }

        readonly Queue<byte[]> pool = new Queue<byte[]>();
        void ProcessLoop()
        {
            while (!stop)
            {
                ready.WaitOne(500);
                byte[] buf; long at;
                lock (gate)
                {
                    buf = latest; at = latestAt; latest = null;
                }
                if (buf == null) continue;
                var crop = PuyoLayout.AnalysisCrop;
                var img = new FrameImage(buf, w, h, w * 3, crop.X, crop.Y, w / (float)crop.Width);
                try { onFrame(img, at); } catch { }
                Processed++;
                lock (gate) pool.Enqueue(buf);   // 使い終わった領域を戻す
            }
        }

        public void Dispose()
        {
            stop = true;
            try { server.Dispose(); } catch { }
            ready.Set();
        }
    }
}
