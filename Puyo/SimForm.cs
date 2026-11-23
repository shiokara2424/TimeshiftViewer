using System;
using System.Drawing;
using System.Windows.Forms;

namespace TimeshiftViewer.Puyo
{
    // ------------------------------------------------------------
    //  シミュレーターを単独のウィンドウで開く（設定画面のボタン / --sim）
    //  再生中はプレーヤーのウィンドウに重ねて表示する（PlayerForm）
    // ------------------------------------------------------------
    class SimForm : Form
    {
        static SimForm instance;
        public readonly SimPanel Panel = new SimPanel { Dock = DockStyle.Fill };

        public static SimForm ShowSingleton()
        {
            if (instance == null || instance.IsDisposed) { instance = new SimForm(); instance.Show(); }
            else
            {
                if (instance.WindowState == FormWindowState.Minimized) instance.WindowState = FormWindowState.Normal;
                instance.Show();
            }
            instance.Activate();
            instance.BringToFront();
            return instance;
        }

        public SimForm()
        {
            Text = "ぷよぷよ シミュレーター";
            Font = new Font("Yu Gothic UI", 10f);
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            Controls.Add(Panel);
            ClientSize = Panel.Size;
            Shown += (s, e) => Panel.FocusBoard();
            MinimumSize = new Size(900, 600);
        }

        public void SetTsumo(TsumoSource src) => Panel.SetTsumo(src);

        // フォーカスがシミュレーターの外（ウィンドウ自体）にあるときのキーも渡す
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // ウィンドウ自体に来たキー（フォーカスが外にある・ウィンドウ宛てに送られた）
            if ((!Panel.ContainsFocus || msg.HWnd == Handle) && Panel.HandleKey(keyData)) return true;
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}
