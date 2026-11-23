using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TimeshiftViewer
{
    class Item
    {
        public string Text; public object Value;
        public Item(string t, object v) { Text = t; Value = v; }
        public override string ToString() => Text;
    }

    class MainForm : Form
    {
        static readonly Color Accent = Color.FromArgb(232, 52, 68);
        static readonly Color Dim = Color.FromArgb(110, 110, 118);

        readonly AppSettings cfg = AppSettings.Load();
        ComboBox cbVideo, cbAudio, cbRes, cbFps, cbQual, cbMin, cbEnc;
        Label lblEst, lblStatus;
        CheckBox chkPenClear, chkAnalyze;
        ProgressBar progress;
        Button btnStart, btnRefresh;
        bool busy;
        public static bool AutoStart;   // 開発用: 起動したらすぐ開始（--autostart）

        public MainForm()
        {
            Text = "Timeshift Viewer";
            Font = new Font("Yu Gothic UI", 10f);
            BackColor = Color.White;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(24, 18, 24, 18);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var root = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
            Controls.Add(root);

            root.Controls.Add(new Label { Text = "Timeshift Viewer", AutoSize = true, Font = new Font(Font.FontFamily, 16f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 2) });
            root.Controls.Add(new Label { Text = "キャプチャボードの映像を表示しながら、直近の映像をメモリに保持して巻き戻せます。", AutoSize = true, ForeColor = Dim, Margin = new Padding(0, 0, 0, 14) });

            var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Margin = new Padding(0) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            root.Controls.Add(grid);
            ComboBox Row(string label, int width)
            {
                grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 16, 6) });
                var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = width, Margin = new Padding(0, 4, 0, 4), FlatStyle = FlatStyle.System };
                grid.Controls.Add(c);
                return c;
            }
            cbVideo = Row("映像デバイス", 380);
            cbAudio = Row("音声", 380);
            cbRes   = Row("解像度", 180);
            cbFps   = Row("フレームレート", 180);
            cbQual  = Row("巻き戻し映像の画質", 180);
            cbMin   = Row("巻き戻し可能な時間", 180);
            cbEnc   = Row("エンコーダ", 180);

            lblEst = new Label { AutoSize = true, ForeColor = Dim, Margin = new Padding(0, 8, 0, 0) };
            root.Controls.Add(lblEst);

            chkPenClear = new CheckBox { Text = "ライブに戻ったらペンの書き込みを消す", AutoSize = true, Checked = cfg.PenAutoClear != 0, Margin = new Padding(0, 10, 0, 0) };
            root.Controls.Add(chkPenClear);
            chkAnalyze = new CheckBox { Text = "対戦を自動で検知してシークバーに表示する（ぷよぷよeスポーツ）", AutoSize = true, Checked = cfg.Analyze != 0, Margin = new Padding(0, 4, 0, 0) };
            root.Controls.Add(chkAnalyze);

            var help = new Label
            {
                AutoSize = true, ForeColor = Dim, Margin = new Padding(0, 12, 0, 0),
                Text = "【操作】\n" +
                       "Space 一時停止　← → 5秒　Shift+← → 10秒　J 60秒戻る　End ライブへ　Home 最古へ\n" +
                       ", . コマ送り　↑↓・ホイール 音量　M ミュート　F・ダブルクリック 全画面\n" +
                       "画面下のボタンで5秒・10秒移動（連打すると積み上げ）、バーのクリックでその位置へ\n" +
                       "P ペン（左ドラッグで書く。✎ の真上のパレットで色・戻す・全消し）　Tab 操作パネルを隠す\n" +
                       "T シミュレーター（今の画面の盤面とツモを読み込んで映像の上に表示）\n" +
                       "Q またはウィンドウを閉じるとこの画面に戻ります"
            };
            root.Controls.Add(help);

            progress = new ProgressBar { Width = 520, Height = 10, Visible = false, Margin = new Padding(0, 14, 0, 0) };
            root.Controls.Add(progress);
            lblStatus = new Label { AutoSize = true, ForeColor = Dim, Margin = new Padding(0, 8, 0, 0), Text = "" };
            root.Controls.Add(lblStatus);

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 14, 0, 0) };
            btnStart = new Button { Text = "開始", Width = 130, Height = 38, FlatStyle = FlatStyle.Flat, BackColor = Accent, ForeColor = Color.White, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(8, 0, 0, 0) };
            btnStart.FlatAppearance.BorderSize = 0;
            btnRefresh = new Button { Text = "デバイス再検出", Width = 140, Height = 38, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(240, 240, 243) };
            btnRefresh.FlatAppearance.BorderColor = Color.FromArgb(215, 215, 220);
            var btnSim = new Button { Text = "シミュレーター", Width = 140, Height = 38, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(240, 240, 243), Margin = new Padding(0, 0, 8, 0) };
            btnSim.FlatAppearance.BorderColor = Color.FromArgb(215, 215, 220);
            btnSim.Click += (s, e) => Puyo.SimForm.ShowSingleton();
            buttons.Controls.Add(btnStart); buttons.Controls.Add(btnRefresh); buttons.Controls.Add(btnSim);
            root.Controls.Add(buttons);
            AcceptButton = btnStart;

            FillStatic();
            cbQual.SelectedIndexChanged += (s, e) => UpdateEstimate();
            cbMin.SelectedIndexChanged += (s, e) => UpdateEstimate();
            btnRefresh.Click += async (s, e) => { if (cbVideo.SelectedItem != null) cfg.Video = (string)cbVideo.SelectedItem; await FillDevices(); };
            btnStart.Click += async (s, e) => await StartSession();
            Shown += async (s, e) => await OnFirstShown();
            UpdateEstimate();
        }

        // ---------------- 選択肢 ----------------
        static void Select(ComboBox c, object value)
        {
            for (int i = 0; i < c.Items.Count; i++)
                if (Equals(((Item)c.Items[i]).Value, value)) { c.SelectedIndex = i; return; }
            if (c.Items.Count > 0) c.SelectedIndex = 0;
        }
        static T Val<T>(ComboBox c) => (T)((Item)c.SelectedItem).Value;

        void FillStatic()
        {
            cbRes.Items.AddRange(new object[] {
                new Item("入力のまま", "source"), new Item("3840×2160", "3840x2160"), new Item("2560×1440", "2560x1440"),
                new Item("1920×1080", "1920x1080"), new Item("1280×720", "1280x720") });
            cbFps.Items.AddRange(new object[] { new Item("入力のまま", "source"), new Item("60 fps", "60"), new Item("30 fps", "30") });
            cbQual.Items.AddRange(new object[] {
                new Item("低 (4 Mbps)", 4000), new Item("標準 (8 Mbps)", 8000), new Item("高 (14 Mbps)", 14000), new Item("最高 (24 Mbps)", 24000) });
            foreach (int m in new[] { 5, 10, 15, 20, 30 }) cbMin.Items.Add(new Item(m + " 分", m));
            cbEnc.Items.AddRange(new object[] {
                new Item("自動", "auto"), new Item("NVIDIA (NVENC)", "h264_nvenc"), new Item("Intel (Quick Sync)", "h264_qsv"),
                new Item("AMD (AMF)", "h264_amf"), new Item("CPU (x264)", "libx264") });
            Select(cbRes, cfg.Resolution); Select(cbFps, cfg.Fps); Select(cbQual, cfg.Kbps);
            Select(cbMin, cfg.Minutes); Select(cbEnc, cfg.Encoder);
        }

        void UpdateEstimate()
        {
            if (cbQual.SelectedItem == null || cbMin.SelectedItem == null) return;
            long mb = (long)((Val<int>(cbQual) + 220) * 1000.0 / 8 * Val<int>(cbMin) * 60 / 1048576);
            lblEst.Text = $"推定メモリ使用量: 約 {mb} MB（一時停止中・巻き戻し中は最大でこの約2倍）";
        }

        async Task FillDevices()
        {
            SetBusy(true, "デバイスを検出中...");
            var (video, audio) = await FF.ListDevices();
            SetBusy(false, "");

            cbVideo.Items.Clear();
            foreach (var v in video) cbVideo.Items.Add(v);
            int vi = video.IndexOf(cfg.Video);
            if (vi < 0) vi = video.FindIndex(v => Regex.IsMatch(v, "capture|gv-|4k60|elgato|avermedia|live gamer|hdmi|cam link|genki|magewell|usb video", RegexOptions.IgnoreCase));
            if (vi < 0 && video.Count > 0) vi = 0;
            cbVideo.SelectedIndex = vi;

            cbAudio.Items.Clear();
            cbAudio.Items.Add(new Item("キャプチャボードの音声（映像デバイスから取得）", "video|"));
            cbAudio.Items.Add(new Item("音声なし", "none|"));
            foreach (var a in audio) cbAudio.Items.Add(new Item("入力: " + a, "device|" + a));
            Select(cbAudio, cfg.AudioMode + "|" + (cfg.AudioMode == "device" ? cfg.AudioDevice : ""));

            if (video.Count == 0) lblStatus.Text = "映像デバイスが見つかりません。キャプチャボードの接続とドライバを確認してください。";
        }

        void SetBusy(bool on, string status, int percent = -1)
        {
            busy = on;
            foreach (Control c in new Control[] { cbVideo, cbAudio, cbRes, cbFps, cbQual, cbMin, cbEnc, chkPenClear, chkAnalyze, btnStart, btnRefresh }) c.Enabled = !on;
            progress.Visible = on;
            if (percent >= 0) { progress.Style = ProgressBarStyle.Continuous; progress.Value = Math.Min(100, percent); }
            else progress.Style = ProgressBarStyle.Marquee;
            lblStatus.ForeColor = Dim;
            lblStatus.Text = status;
        }

        void ShowError(string text) => MessageBox.Show(this, text, "Timeshift Viewer", MessageBoxButtons.OK, MessageBoxIcon.Error);

        // ---------------- 初回 ----------------
        async Task OnFirstShown()
        {
            try { Paths.ExtractMpvConfig(); } catch (Exception ex) { ShowError("設定ファイルを書き出せませんでした: " + ex.Message); }
            if (!ToolInstaller.Installed)
            {
                var ans = MessageBox.Show(this,
                    "初回セットアップとして、動画処理ツール ffmpeg と動画プレーヤー mpv をダウンロードします（合計 約200MB）。\n\n" +
                    "保存先: " + Paths.Tools + "\n\n続けますか？", "Timeshift Viewer", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
                if (ans != DialogResult.OK) { Close(); return; }
                try
                {
                    SetBusy(true, "準備中...");
                    await ToolInstaller.Install((msg, p) => BeginInvoke((Action)(() => SetBusy(true, p >= 0 ? $"{msg} {p}%" : msg, p))));
                    SetBusy(false, "セットアップが完了しました。");
                }
                catch (Exception ex)
                {
                    SetBusy(false, "");
                    ShowError("ダウンロードに失敗しました。\n" + ex.Message + "\n\n手動で入れる場合は、ffmpeg.exe と mpv.exe を次のフォルダに置いてください:\n" + Paths.Tools);
                    Close(); return;
                }
            }
            await FillDevices();
            if (AutoStart) { AutoStart = false; await StartSession(); }
        }

        // ---------------- 再生セッション ----------------
        async Task StartSession()
        {
            if (busy || (cbVideo.SelectedItem == null && FF.TestInput == null)) return;
            cfg.Video = (string)cbVideo.SelectedItem;
            var am = Val<string>(cbAudio).Split(new[] { '|' }, 2);
            cfg.AudioMode = am[0]; cfg.AudioDevice = am[1];
            cfg.Resolution = Val<string>(cbRes); cfg.Fps = Val<string>(cbFps);
            cfg.Kbps = Val<int>(cbQual); cfg.Minutes = Val<int>(cbMin); cfg.Encoder = Val<string>(cbEnc);
            cfg.PenAutoClear = chkPenClear.Checked ? 1 : 0;
            cfg.Analyze = chkAnalyze.Checked ? 1 : 0;
            cfg.Save();

            try
            {
                // 1) エンコーダ
                string enc = cfg.Encoder;
                if (enc == "auto")
                {
                    if (string.IsNullOrEmpty(cfg.DetectedEncoder))
                    {
                        SetBusy(true, "エンコーダを確認中...");
                        cfg.DetectedEncoder = await FF.DetectEncoder(s => lblStatus.Text = s);
                        cfg.Save();
                    }
                    enc = cfg.DetectedEncoder;
                }

                // 2) デバイスが開けるか確認（だめなら条件を緩めて再試行）
                SetBusy(true, "キャプチャデバイスを確認中...");
                string audioMode = cfg.AudioMode;
                bool withFormat = true;
                var r = await FF.Run(FF.InputArgs(cfg, true, audioMode) + " -t 2 -f null -", 20000);
                if (!r.Ok && (cfg.Resolution != "source" || cfg.Fps != "source"))
                {
                    var r2 = await FF.Run(FF.InputArgs(cfg, false, audioMode) + " -t 2 -f null -", 20000);
                    if (r2.Ok) { withFormat = false; r = r2; }
                }
                if (!r.Ok && audioMode != "none")
                {
                    var r3 = await FF.Run(FF.InputArgs(cfg, withFormat, "none") + " -t 2 -f null -", 20000);
                    if (r3.Ok)
                    {
                        SetBusy(false, "");
                        var ans = MessageBox.Show(this, "音声を取り込めませんでした。音声なしで開始しますか？\n\n(詳細)\n" + FF.Tail(r.Log, 6),
                                                  "Timeshift Viewer", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                        if (ans != DialogResult.Yes) return;
                        audioMode = "none"; r = r3;
                    }
                }
                if (!r.Ok)
                {
                    SetBusy(false, "");
                    ShowError("キャプチャデバイスを開けませんでした。\n他のソフトと同時に使えない機種の場合は、そちらを閉じてから再度お試しください。\n\n(詳細)\n" + FF.Tail(r.Log));
                    return;
                }

                // 3) 起動
                int gop = cfg.Fps == "source" ? 60 : int.Parse(cfg.Fps);
                string ffArgs = FF.InputArgs(cfg, withFormat, audioMode) +
                                " -map 0:v:0 -map 0:a:0? " + FF.EncoderArgs(enc, cfg.Kbps, gop) +
                                " -c:a aac -b:a 192k -ar 48000 -ac 2 -f mpegts -muxdelay 0 -muxpreload 0 -flush_packets 1 -nostats -loglevel warning -";
                int sec = cfg.Minutes * 60;
                long bytes = (long)((cfg.Kbps + 220) * 1000.0 / 8 * sec * 1.2);
                string mpvArgs = $"--config-dir=\"{Paths.MpvConf}\" --demuxer-max-back-bytes={bytes} --demuxer-max-bytes={bytes} " +
                                 $"--script-opts=timeshift-buffer={sec},timeshift-pen_autoclear={(cfg.PenAutoClear != 0 ? "yes" : "no")} --volume={cfg.Volume} -";

                string note = withFormat ? "" : "（指定の解像度/フレームレートで開けなかったため入力のまま）";
                SetBusy(true, $"再生中… エンコーダ: {enc} {note}");
                Hide();
                var player = new PlayerForm();
                player.Show();
                string failLog;
                try { failLog = await new PlayerSession(player, cfg.Analyze != 0).Run(ffArgs, mpvArgs); }
                finally { player.AllowClose = true; player.Close(); }
                Show(); Activate();
                SetBusy(false, "");
                if (failLog != null) ShowError("取り込みが停止しました。\n\n(詳細)\n" + FF.Tail(failLog));
            }
            catch (Exception ex)
            {
                Show();
                SetBusy(false, "");
                ShowError("エラーが発生しました:\n" + ex.Message);
            }
        }

    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Paths.Init();
            // --sim: シミュレーターだけを開く（--tsumo=4:21926 で色数とシード値を指定）
            if (args.Contains("--sim"))
            {
                var sim = new Puyo.SimForm();
                var t = args.FirstOrDefault(a => a.StartsWith("--tsumo="));
                if (t != null)
                {
                    var parts = t.Substring(8).Split(':');
                    if (parts.Length == 2 && int.TryParse(parts[0], out int mode) && int.TryParse(parts[1], out int seed) && mode >= 3 && mode <= 5 && seed >= 0 && seed < 65536)
                        sim.SetTsumo(Puyo.TsumoSource.FromSeed(seed, mode));
                }
                Application.Run(sim);
                return;
            }
            var ti = args.FirstOrDefault(a => a.StartsWith("--test-input="));
            if (ti != null) FF.TestInput = ti.Substring(13);
            MainForm.AutoStart = args.Contains("--autostart");
            Application.Run(new MainForm());
        }
    }
}
