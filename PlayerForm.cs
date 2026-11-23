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
        bool simRight = true;
        MpvIpc ipc;
        bool fullscreen;
        FormWindowState prevState = FormWindowState.Maximized;
        public bool AllowClose;
        public event Action<string, string[]> PlayerMessage;   // UI スレッドで呼ぶ

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
            // 映像の上でクリックしたら mpv にキー入力を戻す（シミュレーターからフォーカスを外す）
            VideoHost.MouseDown += (s, e) => VideoHost.Focus();
        }

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
        public void ToggleSim()
        {
            if (sim != null && sim.Visible) HideSim();
            else ShowSim();
        }

        public SimPanel ShowSim()
        {
            if (sim == null)
            {
                sim = new SimPanel();
                sim.AddToolButton("左右を入れ替え", (s, e) => { simRight = !simRight; LayoutSim(); });
                sim.AddToolButton("閉じる（T）", (s, e) => HideSim());
                Controls.Add(sim);
                // DPI の自動スケーリングでサイズが変わったら置き直す
                sim.SizeChanged += (s, e) => { if (!laying) BeginInvoke((Action)LayoutSim); };
            }
            sim.Visible = true;
            sim.BringToFront();
            LayoutSim();
            BeginInvoke((Action)LayoutSim);
            sim.Focus();
            sim.SelectNextControl(sim, true, true, true, true);
            return sim;
        }

        void HideSim()
        {
            if (sim == null) return;
            sim.Visible = false;
            VideoHost.Focus();
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
                int w = Math.Min(cs.Width, Math.Max(cs.Width / 2, (int)(900 * k)));
                var want = new Rectangle(simRight ? cs.Width - w : 0, 0, w, h);
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
            if (sim != null && sim.Visible && sim.ContainsFocus)
            {
                if (keyData == Keys.T) { HideSim(); return true; }
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
