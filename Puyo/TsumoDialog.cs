using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace TimeshiftViewer.Puyo
{
    // ------------------------------------------------------------
    //  ツモの設定: 開幕からの色の並びでパターン（シード値）を識別する / 手入力
    // ------------------------------------------------------------
    class TsumoDialog : Form
    {
        static readonly Color Dim = Color.FromArgb(110, 110, 118);

        readonly ComboBox cbMode, cbMatch;
        readonly TextBox tbInput;
        readonly PairListView preview;
        readonly Label lblResult, lblNext;
        readonly ListBox lbCands;
        readonly CheckBox chkStart;
        readonly Button btnUseSeed, btnUseManual, btnRandom;

        /// <summary>結果: 選ばれたツモ</summary>
        public TsumoSource Result;
        /// <summary>結果: 何手目から始めるか（0始まり）。-1 なら変えない</summary>
        public int StartIndex = -1;

        public TsumoDialog(string initialText, int initialMode)
        {
            Text = "ツモの設定";
            Font = new Font("Yu Gothic UI", 10f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(16);
            BackColor = Color.White;

            var root = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
            Controls.Add(root);

            root.Controls.Add(new Label
            {
                AutoSize = true, ForeColor = Dim, MaximumSize = new Size(560, 0), Margin = new Padding(0, 0, 0, 10),
                Text = "開幕からのツモを、軸→子の順に入力してください（R 赤 / G 緑 / B 青 / Y 黄 / P 紫）。\n" +
                       "4色なら8手、5色なら7手でパターンが1つに決まります（ぷよぷよeスポーツ 対戦モード）。"
            });

            var opts = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 0, 0, 6) };
            opts.Controls.Add(new Label { Text = "色数", AutoSize = true, Margin = new Padding(0, 7, 6, 0) });
            cbMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 80 };
            cbMode.Items.AddRange(new object[] { "3色", "4色", "5色" });
            cbMode.SelectedIndex = Math.Max(0, Math.Min(2, initialMode - 3));
            opts.Controls.Add(cbMode);
            opts.Controls.Add(new Label { Text = "比べ方", AutoSize = true, Margin = new Padding(16, 7, 6, 0) });
            cbMatch = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
            cbMatch.Items.AddRange(new object[] { "色・軸子の順まで一致", "軸と子の順は区別しない", "色は無視して並び方だけ" });
            cbMatch.SelectedIndex = 0;
            opts.Controls.Add(cbMatch);
            root.Controls.Add(opts);

            // 入力欄と色ボタン
            tbInput = new TextBox { Width = 560, CharacterCasing = CharacterCasing.Upper, Font = new Font("Consolas", 13f), Text = initialText ?? "" };
            root.Controls.Add(tbInput);
            var colorRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
            for (int i = 0; i < 5; i++)
            {
                int ci = i;
                var b = new Button
                {
                    Text = PuyoColor.Names[i], Width = 64, Height = 32, FlatStyle = FlatStyle.Flat,
                    BackColor = PuyoDraw.ColorOf(PuyoColor.ToCell((byte)i)), ForeColor = i == 3 ? Color.Black : Color.White,
                    Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 0, 6, 0), TabStop = false,
                };
                b.FlatAppearance.BorderSize = 0;
                b.Click += (s, e) => { AppendLetter(PuyoColor.Letters[ci]); };
                colorRow.Controls.Add(b);
            }
            var back = new Button { Text = "1つ消す", Width = 80, Height = 32, Margin = new Padding(12, 0, 6, 0), TabStop = false };
            back.Click += (s, e) => RemoveLast();
            var clear = new Button { Text = "クリア", Width = 70, Height = 32, TabStop = false };
            clear.Click += (s, e) => { tbInput.Text = ""; };
            colorRow.Controls.Add(back); colorRow.Controls.Add(clear);
            root.Controls.Add(colorRow);

            preview = new PairListView { Horizontal = true, Cell = 22, Width = 560, Height = 22 * 2 + 30, Margin = new Padding(0, 8, 0, 0) };
            root.Controls.Add(preview);

            lblResult = new Label { AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 8, 0, 0) };
            root.Controls.Add(lblResult);
            lblNext = new Label { AutoSize = true, ForeColor = Dim, MaximumSize = new Size(560, 0) };
            root.Controls.Add(lblNext);
            lbCands = new ListBox { Width = 560, Height = 120, Margin = new Padding(0, 6, 0, 0), Font = new Font("Consolas", 10f) };
            root.Controls.Add(lbCands);

            chkStart = new CheckBox { Text = "入力した手の次から始める（盤面は今のまま）", AutoSize = true, Checked = true, Margin = new Padding(0, 8, 0, 0) };
            root.Controls.Add(chkStart);

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0) };
            var cancel = new Button { Text = "キャンセル", Width = 100, Height = 34, DialogResult = DialogResult.Cancel };
            btnUseSeed = new Button { Text = "このパターンを使う", AutoSize = true, Height = 34, Padding = new Padding(8, 0, 8, 0), Enabled = false, Margin = new Padding(8, 0, 0, 0) };
            btnUseManual = new Button { Text = "入力した並びをそのまま使う", AutoSize = true, Height = 34, Padding = new Padding(8, 0, 8, 0), Margin = new Padding(8, 0, 0, 0) };
            btnRandom = new Button { Text = "候補から無作為に選ぶ", AutoSize = true, Height = 34, Padding = new Padding(8, 0, 8, 0), Margin = new Padding(8, 0, 0, 0) };
            buttons.Controls.Add(cancel); buttons.Controls.Add(btnUseSeed); buttons.Controls.Add(btnRandom); buttons.Controls.Add(btnUseManual);
            root.Controls.Add(buttons);
            CancelButton = cancel;

            tbInput.TextChanged += (s, e) => Recalc();
            cbMode.SelectedIndexChanged += (s, e) => Recalc();
            cbMatch.SelectedIndexChanged += (s, e) => Recalc();
            lbCands.SelectedIndexChanged += (s, e) => btnUseSeed.Enabled = lbCands.SelectedItem is Cand;
            lbCands.DoubleClick += (s, e) => UseSeed();
            btnUseSeed.Click += (s, e) => UseSeed();
            btnUseManual.Click += (s, e) => UseManual();
            btnRandom.Click += (s, e) => UseRandom();

            Shown += (s, e) => { tbInput.Focus(); tbInput.SelectionStart = tbInput.TextLength; Recalc(); };
        }

        int Mode => cbMode.SelectedIndex + 3;
        MatchMode Match => (MatchMode)cbMatch.SelectedIndex;
        List<byte> Known => PuyoColor.Parse(tbInput.Text);

        void AppendLetter(char c)
        {
            var seq = Known;
            seq.Add((byte)PuyoColor.FromLetter(c));
            tbInput.Text = PuyoColor.Format(seq);
            tbInput.SelectionStart = tbInput.TextLength;
            tbInput.Focus();
        }

        void RemoveLast()
        {
            var seq = Known;
            if (seq.Count == 0) return;
            seq.RemoveAt(seq.Count - 1);
            tbInput.Text = PuyoColor.Format(seq);
            tbInput.SelectionStart = tbInput.TextLength;
        }

        class Cand
        {
            public int Seed; public string Text;
            public override string ToString() => Text;
        }

        void Recalc()
        {
            var known = Known;
            // 入力の確認表示
            preview.Pairs.Clear(); preview.Labels.Clear();
            for (int i = 0; i < known.Count; i += 2)
            {
                byte a = PuyoColor.ToCell(known[i]);
                byte c = i + 1 < known.Count ? PuyoColor.ToCell(known[i + 1]) : (byte)0;
                preview.Pairs.Add((a, c));
                preview.Labels.Add(((i / 2) + 1).ToString());
            }
            preview.Invalidate();

            lbCands.Items.Clear();
            btnUseSeed.Enabled = false;
            btnUseManual.Enabled = known.Count >= 2;
            btnRandom.Enabled = true;
            if (known.Count == 0)
            {
                lblResult.Text = "全 65536 パターン";
                lblNext.Text = "何も入力せずに「候補から無作為に選ぶ」と、全パターンから1つ選びます。";
                return;
            }

            var r = TsumoIdentifier.Identify(Mode, known, Match);
            if (r.Count == 0)
            {
                lblResult.Text = "一致するパターンがありません";
                lblNext.Text = "入力ミス、色数の違い、チャレンジモードのツモなどが考えられます。";
                btnRandom.Enabled = false;
                return;
            }
            lblResult.Text = r.Count == 1 ? $"1件に決まりました: No.{r.Seeds[0] + 1}" : $"候補 {r.Count} 件";
            string nexts = string.Join("　", r.NextHands.Take(10).Select(kv => $"{kv.Key} {kv.Value}件"));
            lblNext.Text = r.Count == 1 ? "" : $"{r.NextHandIndex + 1}手目の候補: {nexts}" + (r.NextHands.Count > 10 ? " …" : "");

            foreach (int seed in r.Seeds)
            {
                var p = TsumoGen.Generate(seed);
                lbCands.Items.Add(new Cand { Seed = seed, Text = $"No.{seed + 1,-6} {PuyoColor.Format(p.Seq[Mode], 0, 32)} …" });
            }
            if (r.Count > r.Seeds.Count) lbCands.Items.Add($"… ほか {r.Count - r.Seeds.Count} 件");
            if (r.Count == 1) lbCands.SelectedIndex = 0;
        }

        void UseSeed()
        {
            if (!(lbCands.SelectedItem is Cand c)) return;
            Result = TsumoSource.FromSeed(c.Seed, Mode);
            StartIndex = chkStart.Checked ? Known.Count / 2 : -1;
            DialogResult = DialogResult.OK;
        }

        void UseRandom()
        {
            var known = Known;
            var src = TsumoSource.FromKnown(Mode, known, Match);
            if (src == null) return;
            Result = src;
            StartIndex = chkStart.Checked ? known.Count / 2 : -1;
            DialogResult = DialogResult.OK;
        }

        void UseManual()
        {
            var src = TsumoSource.FromString(tbInput.Text);
            if (src.Pairs.Count == 0) return;
            Result = src;
            StartIndex = chkStart.Checked ? 0 : -1;    // 手入力は入力した並びの先頭から
            DialogResult = DialogResult.OK;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Enter && btnUseSeed.Enabled) { UseSeed(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}
