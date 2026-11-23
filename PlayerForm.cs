using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using TimeshiftViewer.Puyo;

namespace TimeshiftViewer
{
    // ------------------------------------------------------------
    //  プレーヤーのウィンドウ
    //  mpv の映像を --wid で埋め込み、その上にシミュレーターを重ねる。
    //  配信（OBS のウィンドウキャプチャ）にシミュレーターも映るよう、同じウィンドウに入れている。
    // ------------------------------------------------------------
    class PlayerForm : Form
    {
        public readonly Panel VideoHost;
        SimPanel sim;
        MpvIpc ipc;
        bool fullscreen;
        FormWindowState prevState = FormWindowState.Maximized;
        public bool AllowClose;
        public event Action<string, string[]> PlayerMessage;   // UI スレッドで呼ぶ
        public event Action LoadRequested;                     // 画面の盤面（1P・2P 両方）をシミュレーターに読み込む
        bool simShown;

        const int BottomReserve = 130;    // mpv の操作パネル（timeshift.lua の PANEL_H）を隠さない

        public PlayerForm()
        {
            Text = "Timeshift Viewer";
            BackColor = Color.Black;
            Font = new Font("Yu Gothic UI", 10f);
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(1280, 760);
            WindowState = FormWindowState.Maximized;
            KeyPreview = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            VideoHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black };
            Controls.Add(VideoHost);

            // --wid で埋め込んだ mpv はマウスを受け取らない（マウスはこのパネルに来る）ので、IPC で mpv に送る。
            // 座標はパネルの座標 = mpv の OSD の座標
            VideoHost.MouseMove += (s, e) =>
            {
                if (e.Location == lastMouse) return;
                lastMouse = e.Location;
                ipc?.Send("mouse", e.X, e.Y);
            };
            VideoHost.MouseDown += (s, e) =>
            {
                // 映像をクリックしたらキー入力をプレーヤー側に戻す（シミュレーターからフォーカスを外す）
                ActiveControl = null;
                Focus();
                string b = MouseName(e.Button);
                if (ipc == null || b == null) return;
                ipc.Send("mouse", e.X, e.Y);
                ipc.Send("keydown", b);       // ダブルクリック（MBTN_LEFT_DBL）は mpv が押す間隔から判定する
            };
            VideoHost.MouseUp += (s, e) =>
            {
                string b = MouseName(e.Button);
                if (ipc == null || b == null) return;
                ipc.Send("mouse", e.X, e.Y);
                ipc.Send("keyup", b);
            };
            VideoHost.MouseWheel += (s, e) =>
            {
                if (ipc == null) return;
                int n = Math.Max(1, Math.Abs(e.Delta) / 120);
                for (int i = 0; i < n; i++) ipc.Send("keypress", e.Delta > 0 ? "WHEEL_UP" : "WHEEL_DOWN");
            };
            // ホイールはフォーカスのあるコントロールに届くので、映像の上にマウスが来たらフォーカスを取る（シミュレーター操作中は除く）
            VideoHost.MouseEnter += (s, e) => { if (sim == null || !sim.ContainsFocus) { ActiveControl = null; Focus(); } };
        }

        Point lastMouse = new Point(-1, -1);

        static string MouseName(MouseButtons b) =>
            b == MouseButtons.Left ? "MBTN_LEFT" : b == MouseButtons.Right ? "MBTN_RIGHT" : b == MouseButtons.Middle ? "MBTN_MID" : null;


        public SimPanel Sim => sim;

        public void Attach(MpvIpc mpv)
        {
            ipc = mpv;
            ipc.Observe("fullscreen");
            ipc.PropertyChanged += (name, val) =>
            {
                if (name == "fullscreen" && val is bool b) UI(() => SetFullscreen(b));
            };
            ipc.ClientMessage += (name, args) =>
            {
                if (name == "tsv-sim") UI(ToggleSim);
                else if (name.StartsWith("tsv-")) UI(() => PlayerMessage?.Invoke(name, args));
            };
        }

        void UI(Action a)
        {
            if (IsDisposed) return;
            try { BeginInvoke(a); } catch { }
        }

        // ---------------- シミュレーター ----------------
        static void Dbg(string s) { if (Environment.GetEnvironmentVariable("TSV_DEBUG") != null) try { System.IO.File.AppendAllText(System.IO.Path.Combine(Paths.Base, "debug-layout.txt"), s + Environment.NewLine); } catch { } }

        public void ToggleSim()
        {
            Dbg($"ToggleSim visible={sim?.Visible}");
            if (sim != null && sim.Visible) HideSim();
            else ShowSim();
        }

        public SimPanel ShowSim()
        {
            if (sim == null)
            {
                sim = new SimPanel();
                sim.TsumoKey = false;     // T はシミュレーターを閉じるキー
                sim.AddToolButton("画面から読み込む", (s, e) => LoadRequested?.Invoke());
                sim.AddToolButton("閉じる（T）", (s, e) => HideSim());
                Controls.Add(sim);
                // DPI の自動スケーリングでサイズが変わったら置き直す
                sim.SizeChanged += (s, e) => { if (!laying) BeginInvoke((Action)LayoutSim); };
            }
            bool wasHidden = !simShown;
            simShown = true;
            sim.Visible = true;
            sim.BringToFront();
            LayoutSim();
            BeginInvoke((Action)LayoutSim);
            // 開いたら再生を止め（開く前の状態を覚えておく）、止めた画面の盤面を読み込む
            if (wasHidden) _ = PauseAndLoad();
            sim.FocusBoard();
            return sim;
        }

        bool? pausedBeforeSim;     // シミュレーターを開く前に一時停止していたか（null = 不明・未取得）

        async System.Threading.Tasks.Task PauseAndLoad()
        {
            if (ipc != null)
            {
                var p = await ipc.Command("get_property", "pause");
                pausedBeforeSim = p is bool b ? b : (bool?)null;
                if (Environment.GetEnvironmentVariable("TSV_DEBUG") != null)
                    System.IO.File.AppendAllText(System.IO.Path.Combine(Paths.Base, "debug-layout.txt"), $"PauseAndLoad pause={p} ({p?.GetType().Name}) before={pausedBeforeSim}\n");
                if (pausedBeforeSim == false) await ipc.Command("set_property", "pause", true);
            }
            LoadRequested?.Invoke();
        }

        void HideSim()
        {
            if (sim == null) return;
            simShown = false;
            sim.Visible = false;
            VideoHost.Focus();
            // 開く前の状態に戻す（再生していたなら再生、一時停止していたなら一時停止）
            if (ipc != null && pausedBeforeSim.HasValue) ipc.Send("set_property", "pause", pausedBeforeSim.Value);
            pausedBeforeSim = null;
        }

        bool laying;
        void LayoutSim()
        {
            if (sim == null || laying) return;
            laying = true;
            try
            {
                var cs = ClientSize;
                float k = DeviceDpi / 96f;
                int h = Math.Max(300, cs.Height - (int)(BottomReserve * k));
                // ウィンドウ全体に広げる（下のシークバー・操作パネルは見えるように残す）
                var want = new Rectangle(0, 0, cs.Width, h);
                if (sim.Bounds != want) sim.Bounds = want;
                if (Environment.GetEnvironmentVariable("TSV_DEBUG") != null)
                    System.IO.File.AppendAllText(System.IO.Path.Combine(Paths.Base, "debug-layout.txt"),
                        $"client={cs} dpi={DeviceDpi} want={want} actual={sim.Bounds} pref={sim.PreferredSize}\n");
            }
            finally { laying = false; }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutSim();
        }

        // ---------------- 全画面 ----------------
        // mpv の f キー・ダブルクリックは fullscreen プロパティを切り替えるだけなので、こちらでウィンドウを変える
        void SetFullscreen(bool on)
        {
            if (on == fullscreen) return;
            fullscreen = on;
            if (on)
            {
                prevState = WindowState;
                FormBorderStyle = FormBorderStyle.None;
                WindowState = FormWindowState.Normal;   // 一度戻さないとタスクバーが残る
                WindowState = FormWindowState.Maximized;
            }
            else
            {
                FormBorderStyle = FormBorderStyle.Sizable;
                WindowState = prevState;
            }
            LayoutSim();
        }

        // ---------------- キー ----------------
        // フォーカスがこのウィンドウ側（シミュレーター以外）にあるときのキーは mpv に送る
        static readonly Dictionary<Keys, string> KeyNames = new Dictionary<Keys, string>
        {
            [Keys.Space] = "SPACE", [Keys.Left] = "LEFT", [Keys.Right] = "RIGHT", [Keys.Up] = "UP", [Keys.Down] = "DOWN",
            [Keys.Home] = "HOME", [Keys.End] = "END", [Keys.Escape] = "ESC", [Keys.Tab] = "TAB", [Keys.Back] = "BS",
            [Keys.Oemcomma] = ",", [Keys.OemPeriod] = ".",
        };

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // シミュレーターにフォーカスがあるときはシミュレーターの操作（T は閉じる）
            if (sim != null && sim.Visible && sim.ContainsFocus)
            {
                if (keyData == Keys.T) { HideSim(); return true; }
                // ウィンドウ宛てに届いたキーは子の ProcessCmdKey を通らないので、ここで渡す
                if (msg.HWnd == Handle && sim.HandleKey(keyData)) return true;
                return base.ProcessCmdKey(ref msg, keyData);
            }
            if (ipc != null)
            {
                string name = MpvKeyName(keyData);
                if (name != null) { ipc.Send("keypress", name); return true; }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        static string MpvKeyName(Keys keyData)
        {
            Keys k = keyData & Keys.KeyCode;
            string mod = "";
            if ((keyData & Keys.Control) != 0) mod += "Ctrl+";
            if ((keyData & Keys.Shift) != 0) mod += "Shift+";
            if (KeyNames.TryGetValue(k, out var n)) return mod + n;
            if (k >= Keys.A && k <= Keys.Z)
            {
                char c = (char)('a' + (k - Keys.A));
                if ((keyData & Keys.Shift) != 0) return ((keyData & Keys.Control) != 0 ? "Ctrl+" : "") + char.ToUpperInvariant(c);
                return mod + c;
            }
            if (k >= Keys.D0 && k <= Keys.D9) return mod + (char)('0' + (k - Keys.D0));
            return null;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // × で閉じたら mpv に終了を伝える（mpv が終わると MainForm がこのウィンドウを閉じる）
            if (!AllowClose && ipc != null)
            {
                e.Cancel = true;
                ipc.Send("quit");
            }
            base.OnFormClosing(e);
        }
    }
}
