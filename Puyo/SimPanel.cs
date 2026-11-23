using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace TimeshiftViewer.Puyo
{
    // ------------------------------------------------------------
    //  ぷよぷよシミュレーター（ウィンドウに埋め込める部品。単独で開くときは SimForm）
    //  操作: ← → 移動 / Z X ↑ 回転 / ↓ Space 置く / Ctrl+Z 戻す / Ctrl+Y やり直し
    // ------------------------------------------------------------
    class SimPanel : UserControl
    {
        static readonly Color Dim = Color.FromArgb(150, 152, 165);
        static readonly Color Bg = Color.FromArgb(30, 32, 40);

        readonly SimState state = new SimState();
        readonly FieldView view;
        readonly PairListView nextView;
        readonly FieldView compareView;
        readonly Label lblTsumo, lblChain, lblTotal, lblCompare, lblHelp;
        readonly TreeView tree;
        readonly RadioButton rbPlay, rbEdit;
        readonly FlowLayoutPanel palette;
        readonly NumericUpDown numHand;
        readonly CheckBox chkAnim;
        SimNode compareNode;
        readonly FlowLayoutPanel bar;

        int pairX = 2, pairRot = 0;
        byte paintColor = PuyoColor.R;
        Field editDraft;                     // 編集中の盤面（確定すると局面になる）
        bool syncing;

        // 連鎖アニメ
        readonly Timer animTimer = new Timer { Interval = 380 };
        readonly Queue<(Field f, bool[,] hl, string cap)> anim = new Queue<(Field, bool[,], string)>();

        public SimPanel()
        {
            Font = new Font("Yu Gothic UI", 10f);
            BackColor = Bg;
            ForeColor = Color.Gainsboro;
            AutoScaleMode = AutoScaleMode.Dpi;

            // ---------- 上のツールバー ----------
            bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10, 8, 10, 4), WrapContents = true };
            rbPlay = new RadioButton { Text = "操作", AutoSize = true, Checked = true, Margin = new Padding(0, 6, 8, 0) };
            rbEdit = new RadioButton { Text = "盤面を編集", AutoSize = true, Margin = new Padding(0, 6, 16, 0) };
            bar.Controls.Add(rbPlay); bar.Controls.Add(rbEdit);
            bar.Controls.Add(MakeButton("ツモの設定…", (s, e) => OpenTsumoDialog()));
            bar.Controls.Add(MakeButton("連鎖させる", (s, e) => { CommitEdit(); var n = state.RunChain(); if (n != null) StartAnim(n); }));
            bar.Controls.Add(MakeButton("盤面を空にする", (s, e) => { CommitEdit(); state.AddEdit(new Field(), state.Current.TsumoIndex, "盤面を空に"); }));
            chkAnim = new CheckBox { Text = "連鎖アニメ", AutoSize = true, Checked = true, Margin = new Padding(12, 6, 0, 0) };
            bar.Controls.Add(chkAnim);

            palette = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10, 0, 10, 4), Visible = false };
            palette.Controls.Add(new Label { Text = "左クリックで置く・右クリックで消す:", AutoSize = true, Margin = new Padding(0, 8, 8, 0) });
            foreach (byte c in new[] { PuyoColor.R, PuyoColor.G, PuyoColor.B, PuyoColor.Y, PuyoColor.P, PuyoColor.Ojama, PuyoColor.None })
            {
                byte cc = c;
                string name = c == PuyoColor.None ? "消す" : c == PuyoColor.Ojama ? "おじゃま" : PuyoColor.Names[c - 1];
                var b = new RadioButton
                {
                    Appearance = Appearance.Button, Text = name, Width = 72, Height = 30, TextAlign = ContentAlignment.MiddleCenter,
                    FlatStyle = FlatStyle.Flat, Checked = c == paintColor, Margin = new Padding(0, 0, 4, 0),
                    BackColor = c == PuyoColor.None ? Color.FromArgb(70, 72, 84) : PuyoDraw.ColorOf(c),
                    ForeColor = c == PuyoColor.Y || c == PuyoColor.Ojama ? Color.Black : Color.White,
                };
                b.FlatAppearance.CheckedBackColor = PuyoDraw.Dark(b.BackColor, 0.7f);
                b.FlatAppearance.BorderColor = Color.White;
                b.CheckedChanged += (s, e) => { if (b.Checked) paintColor = cc; };
                palette.Controls.Add(b);
            }

            // ---------- 本体 ----------
            var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Padding = new Padding(10, 4, 10, 10) };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            view = new FieldView { Cell = 40, ShowPair = true };
            view.Size = view.Preferred;
            view.Margin = new Padding(0, 0, 12, 0);
            body.Controls.Add(view, 0, 0);

            // 中央: NEXT と情報
            var mid = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Margin = new Padding(0, 0, 12, 0) };
            mid.Controls.Add(Caption("NEXT"));
            nextView = new PairListView { Cell = 34, Width = 150, Height = (34 * 2 + 22) * 2 + 6, BackColor = Bg };
            mid.Controls.Add(nextView);
            lblTsumo = InfoLabel(); mid.Controls.Add(lblTsumo);
            var handRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
            handRow.Controls.Add(new Label { Text = "今の手", AutoSize = true, ForeColor = Dim, Margin = new Padding(0, 6, 6, 0) });
            numHand = new NumericUpDown { Minimum = 1, Maximum = 10000, Width = 70 };
            handRow.Controls.Add(numHand);
            handRow.Controls.Add(new Label { Text = "手目", AutoSize = true, ForeColor = Dim, Margin = new Padding(4, 6, 0, 0) });
            mid.Controls.Add(handRow);
            mid.Controls.Add(Caption("連鎖"));
            lblChain = InfoLabel(); mid.Controls.Add(lblChain);
            lblTotal = InfoLabel(); mid.Controls.Add(lblTotal);
            lblHelp = new Label
            {
                AutoSize = true, ForeColor = Dim, Margin = new Padding(0, 16, 0, 0),
                Text = "← → 移動\nZ / X（↑）回転\n↓ / Space 置く\nCtrl+Z 戻す / Ctrl+Y やり直し\nE 編集の切り替え\nR 連鎖させる\nT ツモの設定"
            };
            mid.Controls.Add(lblHelp);
            body.Controls.Add(mid, 1, 0);

            // 右: 履歴と比較
            var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var histHead = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            histHead.Controls.Add(Caption("履歴（クリックで移動・分岐も残ります）"));
            right.Controls.Add(histHead, 0, 0);
            tree = new TreeView
            {
                Dock = DockStyle.Fill, BackColor = Color.FromArgb(22, 24, 30), ForeColor = Color.Gainsboro, BorderStyle = BorderStyle.FixedSingle,
                HideSelection = false, FullRowSelect = true, ShowLines = true, MinimumSize = new Size(300, 200),
            };
            var menu = new ContextMenuStrip();
            menu.Items.Add("この局面と比べる", null, (s, e) => { if (tree.SelectedNode?.Tag is SimNode n) SetCompare(n); });
            menu.Items.Add("この局面から下を削除", null, (s, e) => { if (tree.SelectedNode?.Tag is SimNode n) state.Delete(n); });
            tree.ContextMenuStrip = menu;
            tree.NodeMouseClick += (s, e) => { if (e.Button == MouseButtons.Right) tree.SelectedNode = e.Node; };
            right.Controls.Add(tree, 0, 1);

            var cmpHead = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
            cmpHead.Controls.Add(MakeButton("今の局面を比較に固定", (s, e) => SetCompare(state.Current)));
            cmpHead.Controls.Add(MakeButton("比較をやめる", (s, e) => SetCompare(null)));
            right.Controls.Add(cmpHead, 0, 2);
            var cmpRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            compareView = new FieldView { Cell = 20, ShowPair = false, Visible = false };
            compareView.Size = compareView.Preferred;
            cmpRow.Controls.Add(compareView);
            lblCompare = new Label { AutoSize = true, ForeColor = Dim, MaximumSize = new Size(240, 0), Margin = new Padding(8, 0, 0, 0) };
            cmpRow.Controls.Add(lblCompare);
            right.Controls.Add(cmpRow, 0, 3);
            body.Controls.Add(right, 2, 0);

            Controls.Add(body);
            Controls.Add(palette);
            Controls.Add(bar);

            Size = new Size(1060, Math.Max(720, view.Height + 110));

            // ---------- イベント ----------
            state.Changed += Refresh2;
            rbEdit.CheckedChanged += (s, e) => SetEditMode(rbEdit.Checked);
            view.CellPainted += OnCellPainted;
            view.PaintFinished += () => { };
            tree.AfterSelect += (s, e) => { if (!syncing && e.Node?.Tag is SimNode n && n != state.Current) { StopAnim(); CommitEdit(); state.GoTo(n); } };
            numHand.ValueChanged += (s, e) =>
            {
                if (syncing) return;
                CommitEdit();
                int idx = (int)numHand.Value - 1;
                state.AddEdit(state.Current.Field, idx, $"{idx + 1}手目に移動");
            };
            animTimer.Tick += (s, e) => AnimStep();

            Refresh2();
        }

        static Button MakeButton(string text, EventHandler click)
        {
            var b = new Button
            {
                Text = text, AutoSize = true, Height = 30, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 0, 6, 0),
                BackColor = Color.FromArgb(52, 56, 70), ForeColor = Color.White, TabStop = false,
            };
            b.FlatAppearance.BorderColor = Color.FromArgb(80, 84, 100);
            b.Click += click;
            return b;
        }

        static Label Caption(string t) => new Label { Text = t, AutoSize = true, ForeColor = Color.White, Font = new Font("Yu Gothic UI", 10f, FontStyle.Bold), Margin = new Padding(0, 10, 0, 4) };
        static Label InfoLabel() => new Label { AutoSize = true, MaximumSize = new Size(220, 0), Margin = new Padding(0, 2, 0, 0) };

        // ---------------- 表示の更新 ----------------
        void Refresh2()
        {
            var cur = state.Current;
            syncing = true;
            try
            {
                // 盤面
                if (animTimer.Enabled) { /* アニメ中はアニメ側が描く */ }
                else
                {
                    view.Field = editDraft ?? cur.Field;
                    view.Highlight = null;
                    view.Caption = cur.Field.IsDead && editDraft == null ? "ばたんきゅ～" : null;
                }
                bool hasPair = state.TryCurrentPair(out var pair);
                view.ShowPair = !rbEdit.Checked && hasPair && !cur.Field.IsDead && !animTimer.Enabled;
                if (hasPair) { view.PairAxis = PuyoColor.ToCell(pair.axis); view.PairChild = PuyoColor.ToCell(pair.child); }
                view.PairX = pairX; view.PairRot = pairRot;
                view.Invalidate();

                // NEXT
                nextView.Pairs.Clear(); nextView.Labels.Clear();
                for (int i = 1; i <= 2; i++)
                {
                    if (state.TryPair(i, out var p)) nextView.Pairs.Add((PuyoColor.ToCell(p.axis), PuyoColor.ToCell(p.child)));
                    else nextView.Pairs.Add(null);
                    nextView.Labels.Add(i == 1 ? "NEXT" : "NEXT2");
                }
                nextView.Invalidate();

                // 情報
                var t = state.Tsumo;
                string handInfo = t.Seed >= 0 ? $"{cur.TsumoIndex % 128 + 1} / 128 手（{cur.TsumoIndex / 128 + 1}周目）" : t.Pairs.Count > 0 ? $"{cur.TsumoIndex + 1} / {t.Pairs.Count} 手" : "";
                lblTsumo.Text = $"ツモ: {t.Label}\n{handInfo}" + (!hasPair && !cur.Field.IsDead ? "\nツモがありません（ツモの設定で入力）" : "");
                numHand.Value = Math.Min(numHand.Maximum, cur.TsumoIndex + 1);

                var c = cur.Chain;
                if (c != null && c.Chains > 0)
                {
                    var steps = string.Join("\n", c.Steps.Select(s => $"  {s.Chain}連鎖目: {s.Count}個 {s.Colors}色 ×{s.Bonus} = {s.Score}"));
                    lblChain.Text = $"{c.Chains}連鎖  {c.Score:N0}点\nおじゃま {c.Ojama}個" + (c.AllClear ? "  全消し！" : "") + "\n" + steps;
                }
                else lblChain.Text = "（直前の手で連鎖なし）";
                lblTotal.Text = $"ここまでの合計: {cur.TotalScore:N0}点";

                RebuildTree();
            }
            finally { syncing = false; }
            UpdateCompare();
        }

        void RebuildTree()
        {
            tree.BeginUpdate();
            tree.Nodes.Clear();
            TreeNode selected = null;
            TreeNode Add(SimNode n, TreeNodeCollection into)
            {
                var tn = new TreeNode(n.Label) { Tag = n };
                if (n == compareNode) tn.Text += "  [比較]";
                if (n.Field.IsDead) tn.ForeColor = Color.IndianRed;
                into.Add(tn);
                if (n == state.Current) selected = tn;
                foreach (var ch in n.Children) Add(ch, tn.Nodes);
                return tn;
            }
            Add(state.Root, tree.Nodes);
            tree.ExpandAll();
            if (selected != null) { tree.SelectedNode = selected; selected.EnsureVisible(); }
            tree.EndUpdate();
        }

        void SetCompare(SimNode n)
        {
            compareNode = n;
            Refresh2();
        }

        void UpdateCompare()
        {
            if (compareNode == null) { compareView.Visible = false; lblCompare.Text = "分岐を比べたいときは、局面を比較に固定してください。"; return; }
            compareView.Visible = true;
            compareView.Field = compareNode.Field;
            compareView.Invalidate();
            var cur = state.Current;
            lblCompare.Text = $"比較: {compareNode.Label}\n合計 {compareNode.TotalScore:N0}点\n\n今の局面: 合計 {cur.TotalScore:N0}点\n差 {cur.TotalScore - compareNode.TotalScore:+#,0;-#,0;0}点";
        }

        // ---------------- 操作 ----------------
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (ActiveControl is TextBox || ActiveControl is NumericUpDown || (ActiveControl != null && ActiveControl.Parent is NumericUpDown))
                return base.ProcessCmdKey(ref msg, keyData);

            if (keyData == (Keys.Control | Keys.Z) || keyData == Keys.Back) { StopAnim(); CommitEdit(); state.Undo(); return true; }
            if (keyData == (Keys.Control | Keys.Y) || keyData == (Keys.Control | Keys.Shift | Keys.Z)) { StopAnim(); CommitEdit(); state.Redo(); return true; }
            if (keyData == Keys.E) { rbEdit.Checked = !rbEdit.Checked; rbPlay.Checked = !rbEdit.Checked; return true; }
            if (keyData == Keys.R) { CommitEdit(); var n = state.RunChain(); if (n != null) StartAnim(n); return true; }
            if (keyData == Keys.T) { StopAnim(); BeginInvoke((Action)OpenTsumoDialog); return true; }

            if (rbEdit.Checked) return base.ProcessCmdKey(ref msg, keyData);
            if (animTimer.Enabled) { if (keyData == Keys.Left || keyData == Keys.Right || keyData == Keys.Down || keyData == Keys.Space) { StopAnim(); return true; } }

            switch (keyData)
            {
                case Keys.Left: MovePair(-1); return true;
                case Keys.Right: MovePair(1); return true;
                case Keys.Z: Rotate(-1); return true;
                case Keys.X: case Keys.Up: Rotate(1); return true;
                case Keys.Down: case Keys.Space: case Keys.Enter: Drop(); return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void MovePair(int dx)
        {
            if (Field.ValidPos(pairX + dx, pairRot)) { pairX += dx; Refresh2(); }
        }

        void Rotate(int dir)
        {
            int r = (pairRot + (dir > 0 ? 1 : 3)) % 4;
            int x = pairX;
            // 壁にぶつかる場合は反対側へずらす
            if (!Field.ValidPos(x, r)) x += Field.ChildDx(r) > 0 ? -1 : 1;
            if (!Field.ValidPos(x, r)) return;
            pairX = x; pairRot = r;
            Refresh2();
        }

        void Drop()
        {
            if (!state.TryCurrentPair(out _) || state.Current.Field.IsDead) return;
            var n = state.Place(pairX, pairRot);
            if (n == null) return;
            pairX = 2; pairRot = 0;
            StartAnim(n);
        }

        // ---------------- 連鎖アニメ ----------------
        void StartAnim(SimNode n)
        {
            if (!chkAnim.Checked || n.Chain == null || n.Chain.Chains == 0) { Refresh2(); return; }
            anim.Clear();
            foreach (var s in n.Chain.Steps)
            {
                anim.Enqueue((s.Before, null, null));
                anim.Enqueue((s.Before, s.Popped, $"{s.Chain}連鎖"));
            }
            animTimer.Start();
            AnimStep();
        }

        void AnimStep()
        {
            if (anim.Count == 0) { StopAnim(); return; }
            var (f, hl, cap) = anim.Dequeue();
            view.Field = f; view.Highlight = hl; view.Caption = cap; view.ShowPair = false;
            view.Invalidate();
        }

        void StopAnim()
        {
            if (!animTimer.Enabled) return;
            animTimer.Stop();
            anim.Clear();
            Refresh2();
        }

        // ---------------- 編集 ----------------
        void SetEditMode(bool on)
        {
            StopAnim();
            if (!on) CommitEdit();
            palette.Visible = on;
            view.Editable = on;
            view.Cursor = on ? Cursors.Hand : Cursors.Default;
            Refresh2();
            view.Focus();
        }

        void OnCellPainted(int x, int y, MouseButtons btn)
        {
            if (editDraft == null) editDraft = state.Current.Field.Clone();
            byte c = btn == MouseButtons.Right ? PuyoColor.None : paintColor;
            if (editDraft[x, y] == c) return;
            editDraft[x, y] = c;
            view.Field = editDraft;
            view.Invalidate();
        }

        void CommitEdit()
        {
            if (editDraft == null) return;
            var d = editDraft;
            editDraft = null;
            state.AddEdit(d, state.Current.TsumoIndex, "盤面を編集");
        }

        // ---------------- ツモ ----------------
        void OpenTsumoDialog()
        {
            CommitEdit();
            string initial = "";
            int mode = state.Tsumo.Seed >= 0 ? state.Tsumo.Mode : 4;
            if (state.Tsumo.Seed < 0 && state.Tsumo.Pairs.Count > 0) initial = state.Tsumo.ToText();
            using (var dlg = new TsumoDialog(initial, mode))
            {
                if (dlg.ShowDialog(FindForm()) != DialogResult.OK || dlg.Result == null) return;
                state.SetTsumo(dlg.Result);
                if (dlg.StartIndex >= 0 && dlg.StartIndex != state.Current.TsumoIndex)
                    state.AddEdit(state.Current.Field, dlg.StartIndex, $"ツモ設定（{dlg.StartIndex + 1}手目から）");
                pairX = 2; pairRot = 0;
                Refresh2();
            }
        }

        public void SetTsumo(TsumoSource src) { state.SetTsumo(src); }
        public TsumoSource Tsumo => state.Tsumo;

        /// <summary>ツールバーの右端にボタンを足す（埋め込み先の「閉じる」「左右切替」など）</summary>
        public Button AddToolButton(string text, EventHandler click)
        {
            var b = MakeButton(text, click);
            b.Margin = new Padding(12, 0, 0, 0);
            bar.Controls.Add(b);
            return b;
        }

        /// <summary>外部（盤面認識など）から盤面とツモ位置を渡して開始する</summary>
        public void LoadPosition(Field f, int tsumoIndex, string label)
        {
            StopAnim();
            editDraft = null;
            state.Reset(f, tsumoIndex, label);
            pairX = 2; pairRot = 0;
            Refresh2();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) animTimer.Dispose();
            base.Dispose(disposing);
        }
    }
}
