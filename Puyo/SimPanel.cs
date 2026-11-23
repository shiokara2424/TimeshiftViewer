using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace TimeshiftViewer.Puyo
{
    // ------------------------------------------------------------
    //  ぷよぷよシミュレーター（ウィンドウに埋め込める部品。単独で開くときは SimForm）
    //  1P と 2P の盤面を並べて持ち、操作するのはどちらか一方（既定は 1P、Tab・ボタン・盤面のクリックで切り替え）。
    //  ツモは両方で共通、何手目かはそれぞれ。
    //  操作: ← → 移動 / Z X ↑ 回転 / ↓ Space 置く / Ctrl+Z 戻す / Ctrl+Y やり直し
    // ------------------------------------------------------------
    class SimPanel : UserControl
    {
        static readonly Color Dim = Color.FromArgb(150, 152, 165);
        static readonly Color Bg = Color.FromArgb(30, 32, 40);
        static readonly Color Accent = Color.FromArgb(255, 196, 60);

        // 片側（1P / 2P）の状態と表示
        sealed class Side
        {
            public int Index;
            public readonly SimState State = new SimState();
            public FieldView View;
            public PairListView Next;
            public Label Header;
            public FlowLayoutPanel Box;
            public int PairX = 2, PairRot;
            public Field EditDraft;               // 編集中の盤面（確定すると局面になる）
            public SimNode CompareNode;
            public string Name => Index == 0 ? "1P" : "2P";
        }

        readonly Side[] sides = { new Side { Index = 0 }, new Side { Index = 1 } };
        int act;                                   // 操作中の側
        Side A => sides[act];
        SimState state => A.State;
        FieldView view => A.View;

        readonly FieldView compareView;
        readonly Label lblTsumo, lblChain, lblTotal, lblCompare, lblHelp, lblActive;
        readonly TreeView tree;
        readonly RadioButton rbPlay, rbEdit;
        readonly FlowLayoutPanel palette;
        readonly NumericUpDown numHand;
        readonly CheckBox chkAnim;
        readonly FlowLayoutPanel bar;
        readonly Button btnSide;
        readonly TableLayoutPanel body;

        byte paintColor = PuyoColor.R;
        bool syncing;

        // 連鎖アニメ（操作中の側だけ）
        readonly Timer animTimer = new Timer { Interval = 380 };
        readonly Queue<(Field f, bool[,] hl, string cap)> anim = new Queue<(Field, bool[,], string)>();
        Side animSide;

        public SimPanel()
        {
            Font = new Font("Yu Gothic UI", 10f);
            BackColor = Bg;
            ForeColor = Color.Gainsboro;
            AutoScaleMode = AutoScaleMode.Dpi;

            // ---------- 上のツールバー ----------
            bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10, 8, 10, 4), WrapContents = true };
            btnSide = MakeButton("操作: 1P ⇄ 2P（Tab）", (s, e) => SetActive(1 - act));
            btnSide.BackColor = Color.FromArgb(90, 74, 30);
            btnSide.Margin = new Padding(0, 0, 16, 0);
            bar.Controls.Add(btnSide);
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

            // ---------- 本体: [1P] [情報・履歴] [2P] ----------
            body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Padding = new Padding(10, 4, 10, 10) };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            foreach (var sd in sides)
            {
                sd.View = new FieldView { Cell = 40, ShowPair = true };
                sd.Next = new PairListView { Cell = 32, BackColor = Bg };
                sd.Header = new Label { AutoSize = true, Font = new Font("Yu Gothic UI", 13f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 4) };
                var col = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Margin = new Padding(0) };
                col.Controls.Add(sd.Header);
                sd.Box = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), Padding = new Padding(3) };
                // 1P は 盤面→NEXT、2P は NEXT→盤面（ゲーム画面と同じ並び）
                if (sd.Index == 0) { sd.Box.Controls.Add(sd.View); sd.Box.Controls.Add(sd.Next); }
                else { sd.Box.Controls.Add(sd.Next); sd.Box.Controls.Add(sd.View); }
                col.Controls.Add(sd.Box);
                body.Controls.Add(col, sd.Index == 0 ? 0 : 2, 0);

                var s2 = sd;
                // 盤面や NEXT をクリックしたらその側を操作対象にする
                sd.View.MouseDown += (s, e) => { if (act != s2.Index) SetActive(s2.Index); };
                sd.Next.MouseDown += (s, e) => { if (act != s2.Index) SetActive(s2.Index); };
                sd.Header.MouseDown += (s, e) => { if (act != s2.Index) SetActive(s2.Index); };
                sd.View.CellPainted += (x, y, b) => { if (act == s2.Index) OnCellPainted(x, y, b); };
                sd.State.Changed += () => OnStateChanged(s2);
            }

            // 中央: 操作中の側の情報と履歴
            var mid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Margin = new Padding(16, 0, 16, 0) };
            mid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            mid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            mid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            mid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            mid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            mid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            lblActive = new Label { AutoSize = true, ForeColor = Accent, Font = new Font("Yu Gothic UI", 12f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 4) };
            mid.Controls.Add(lblActive, 0, 0);

            var info = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = new Padding(0) };
            var infoL = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Margin = new Padding(0, 0, 24, 0) };
            lblTsumo = InfoLabel(); infoL.Controls.Add(lblTsumo);
            var handRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
            handRow.Controls.Add(new Label { Text = "今の手", AutoSize = true, ForeColor = Dim, Margin = new Padding(0, 6, 6, 0) });
            numHand = new NumericUpDown { Minimum = 1, Maximum = 10000, Width = 70, TabStop = false };
            handRow.Controls.Add(numHand);
            handRow.Controls.Add(new Label { Text = "手目", AutoSize = true, ForeColor = Dim, Margin = new Padding(4, 6, 0, 0) });
            infoL.Controls.Add(handRow);
            info.Controls.Add(infoL);
            var infoR = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Margin = new Padding(0) };
            infoR.Controls.Add(Caption("連鎖"));
            lblChain = InfoLabel(); infoR.Controls.Add(lblChain);
            lblTotal = InfoLabel(); infoR.Controls.Add(lblTotal);
            info.Controls.Add(infoR);
            mid.Controls.Add(info, 0, 1);

            var histHead = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            histHead.Controls.Add(Caption("履歴（クリックで移動・分岐も残ります）"));
            mid.Controls.Add(histHead, 0, 2);
            tree = new TreeView
            {
                Dock = DockStyle.Fill, BackColor = Color.FromArgb(22, 24, 30), ForeColor = Color.Gainsboro, BorderStyle = BorderStyle.FixedSingle,
                HideSelection = false, FullRowSelect = true, ShowLines = true, MinimumSize = new Size(200, 120), TabStop = false,
            };
            var menu = new ContextMenuStrip();
            menu.Items.Add("この局面と比べる", null, (s, e) => { if (tree.SelectedNode?.Tag is SimNode n) SetCompare(n); });
            menu.Items.Add("この局面から下を削除", null, (s, e) => { if (tree.SelectedNode?.Tag is SimNode n) state.Delete(n); });
            tree.ContextMenuStrip = menu;
            tree.NodeMouseClick += (s, e) => { if (e.Button == MouseButtons.Right) tree.SelectedNode = e.Node; };
            mid.Controls.Add(tree, 0, 3);

            var cmpHead = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
            cmpHead.Controls.Add(MakeButton("今の局面を比較に固定", (s, e) => SetCompare(state.Current)));
            cmpHead.Controls.Add(MakeButton("比較をやめる", (s, e) => SetCompare(null)));
            mid.Controls.Add(cmpHead, 0, 4);
            var cmpRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            compareView = new FieldView { Cell = 16, ShowPair = false, Visible = false };
            compareView.Size = compareView.Preferred;
            cmpRow.Controls.Add(compareView);
            lblCompare = new Label { AutoSize = true, ForeColor = Dim, MaximumSize = new Size(260, 0), Margin = new Padding(8, 0, 0, 0) };
            cmpRow.Controls.Add(lblCompare);
            lblHelp = new Label
            {
                AutoSize = true, ForeColor = Dim, Margin = new Padding(16, 0, 0, 0),
                Text = "← → 移動　Z / X（↑）回転　↓ / Space 置く\nCtrl+Z 戻す　Ctrl+Y やり直し\nTab 1P/2P 切替　E 編集　R 連鎖　T ツモ"
            };
            cmpRow.Controls.Add(lblHelp);
            mid.Controls.Add(cmpRow, 0, 5);
            body.Controls.Add(mid, 1, 0);

            Controls.Add(body);
            Controls.Add(palette);
            Controls.Add(bar);

            Size = new Size(1400, 820);

            // ---------- イベント ----------
            rbEdit.CheckedChanged += (s, e) => SetEditMode(rbEdit.Checked);
            tree.AfterSelect += (s, e) => { if (!syncing && e.Node?.Tag is SimNode n && n != state.Current) { StopAnim(); CommitEdit(); state.GoTo(n); } };
            numHand.ValueChanged += (s, e) =>
            {
                if (syncing) return;
                CommitEdit();
                int idx = (int)numHand.Value - 1;
                state.AddEdit(state.Current.Field, idx, $"{idx + 1}手目に移動");
            };
            animTimer.Tick += (s, e) => AnimStep();

            ResizeBoards();
            RefreshAll();
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
        static Label InfoLabel() => new Label { AutoSize = true, MaximumSize = new Size(300, 0), Margin = new Padding(0, 2, 0, 0) };

        // ---------------- 大きさ ----------------
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            ResizeBoards();
        }

        int lastCell = -1;
        void ResizeBoards()
        {
            if (sides[0].View == null) return;
            int top = bar.Height + (palette.Visible ? palette.Height : 0) + 50;
            int byH = (ClientSize.Height - top - 20) / 15;                         // 盤面は 15 行分（12段 + 13段目 + 上の2行）
            int byW = (ClientSize.Width - 420) / (2 * 6 + 2 * 3);                  // 中央に 420px 残す。NEXT は盤面の約 3 マス分
            int cell = Math.Max(20, Math.Min(80, Math.Min(byH, byW)));
            if (cell == lastCell) return;
            lastCell = cell;
            foreach (var sd in sides)
            {
                sd.View.Cell = cell;
                sd.View.Size = sd.View.Preferred;
                int nc = Math.Max(16, (int)(cell * 0.8));
                sd.Next.Cell = nc;
                sd.Next.Size = new Size(nc + 70, (nc * 2 + 22) * 2 + 10);
                sd.Next.Margin = new Padding(sd.Index == 0 ? 8 : 0, cell * 2, sd.Index == 0 ? 0 : 8, 0);
            }
        }

        // ---------------- 操作する側 ----------------
        public int ActiveSide => act;

        /// <summary>操作中の盤面にフォーカスを置く（キー操作を受けられるように）</summary>
        public void FocusBoard() => A.View.Focus();

        public void SetActive(int side)
        {
            if (side == act) { RefreshAll(); return; }
            StopAnim();
            CommitEdit();
            sides[act].View.Editable = false;
            act = side;
            A.View.Editable = rbEdit.Checked;
            A.View.Cursor = rbEdit.Checked ? Cursors.Hand : Cursors.Default;
            RefreshAll();
            A.View.Focus();
        }

        // ---------------- 表示の更新 ----------------
        void OnStateChanged(Side sd)
        {
            RefreshSide(sd);
            if (sd == A) RefreshInfo();
        }

        void RefreshAll()
        {
            foreach (var sd in sides) RefreshSide(sd);
            RefreshInfo();
        }

        void RefreshSide(Side sd)
        {
            var st = sd.State;
            var cur = st.Current;
            bool active = sd == A;
            if (!(animTimer.Enabled && animSide == sd))
            {
                sd.View.Field = sd.EditDraft ?? cur.Field;
                sd.View.Highlight = null;
                sd.View.Caption = cur.Field.IsDead && sd.EditDraft == null ? "ばたんきゅ～" : null;
            }
            bool hasPair = st.TryCurrentPair(out var pair);
            // 操作中の側だけ組ぷよを出す
            sd.View.ShowPair = active && !rbEdit.Checked && hasPair && !cur.Field.IsDead && !animTimer.Enabled;
            if (hasPair) { sd.View.PairAxis = PuyoColor.ToCell(pair.axis); sd.View.PairChild = PuyoColor.ToCell(pair.child); }
            sd.View.PairX = sd.PairX; sd.View.PairRot = sd.PairRot;
            sd.View.Invalidate();

            sd.Next.Pairs.Clear(); sd.Next.Labels.Clear();
            // 操作していない側は、組ぷよ（今の手）も NEXT 欄に出す
            int from = active ? 1 : 0;
            for (int i = from; i <= from + 1; i++)
            {
                if (st.TryPair(i, out var p)) sd.Next.Pairs.Add((PuyoColor.ToCell(p.axis), PuyoColor.ToCell(p.child)));
                else sd.Next.Pairs.Add(null);
                sd.Next.Labels.Add(active ? (i == 1 ? "NEXT" : "NEXT2") : (i == 0 ? "今" : "NEXT"));
            }
            sd.Next.Invalidate();

            sd.Header.Text = $"{sd.Name}  {cur.TsumoIndex + 1}手目" + (active ? "　（操作中）" : "");
            sd.Header.ForeColor = active ? Accent : Dim;
            sd.Box.BackColor = active ? Color.FromArgb(70, 60, 30) : Bg;
        }

        void RefreshInfo()
        {
            var cur = state.Current;
            syncing = true;
            try
            {
                lblActive.Text = $"操作中: {A.Name}";
                bool hasPair = state.TryCurrentPair(out _);
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
                if (n == A.CompareNode) tn.Text += "  [比較]";
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
            A.CompareNode = n;
            RefreshInfo();
        }

        void UpdateCompare()
        {
            var cn = A.CompareNode;
            if (cn == null) { compareView.Visible = false; lblCompare.Text = "分岐を比べたいときは、局面を比較に固定してください。"; return; }
            compareView.Visible = true;
            compareView.Field = cn.Field;
            compareView.Invalidate();
            var cur = state.Current;
            lblCompare.Text = $"比較: {cn.Label}\n合計 {cn.TotalScore:N0}点\n\n今の局面: 合計 {cur.TotalScore:N0}点\n差 {cur.TotalScore - cn.TotalScore:+#,0;-#,0;0}点";
        }

        // ---------------- 操作 ----------------
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (ActiveControl is TextBox || ActiveControl is NumericUpDown || (ActiveControl != null && ActiveControl.Parent is NumericUpDown))
                return base.ProcessCmdKey(ref msg, keyData);
            return HandleKey(keyData) || base.ProcessCmdKey(ref msg, keyData);
        }

        /// <summary>シミュレーターの操作キー。処理したら true（単独ウィンドウでフォーカスが外にあるときも呼ばれる）</summary>
        public bool HandleKey(Keys keyData)
        {

            if (keyData == Keys.Tab) { SetActive(1 - act); return true; }
            if (keyData == (Keys.Control | Keys.Z) || keyData == Keys.Back) { StopAnim(); CommitEdit(); state.Undo(); return true; }
            if (keyData == (Keys.Control | Keys.Y) || keyData == (Keys.Control | Keys.Shift | Keys.Z)) { StopAnim(); CommitEdit(); state.Redo(); return true; }
            if (keyData == Keys.E) { rbEdit.Checked = !rbEdit.Checked; rbPlay.Checked = !rbEdit.Checked; return true; }
            if (keyData == Keys.R) { CommitEdit(); var n = state.RunChain(); if (n != null) StartAnim(n); return true; }
            if (keyData == Keys.T && TsumoKey) { StopAnim(); BeginInvoke((Action)OpenTsumoDialog); return true; }

            if (rbEdit.Checked) return false;
            if (animTimer.Enabled) { if (keyData == Keys.Left || keyData == Keys.Right || keyData == Keys.Down || keyData == Keys.Space) { StopAnim(); return true; } }

            switch (keyData)
            {
                case Keys.Left: MovePair(-1); return true;
                case Keys.Right: MovePair(1); return true;
                case Keys.Z: Rotate(-1); return true;
                case Keys.X: case Keys.Up: Rotate(1); return true;
                case Keys.Down: case Keys.Space: case Keys.Enter: Drop(); return true;
            }
            return false;
        }

        void MovePair(int dx)
        {
            if (Field.ValidPos(A.PairX + dx, A.PairRot)) { A.PairX += dx; RefreshSide(A); }
        }

        void Rotate(int dir)
        {
            int r = (A.PairRot + (dir > 0 ? 1 : 3)) % 4;
            int x = A.PairX;
            // 壁にぶつかる場合は反対側へずらす
            if (!Field.ValidPos(x, r)) x += Field.ChildDx(r) > 0 ? -1 : 1;
            if (!Field.ValidPos(x, r)) return;
            A.PairX = x; A.PairRot = r;
            RefreshSide(A);
        }

        void Drop()
        {
            if (!state.TryCurrentPair(out _) || state.Current.Field.IsDead) return;
            int x = A.PairX, rot = A.PairRot;
            A.PairX = 2; A.PairRot = 0;
            var n = state.Place(x, rot);
            if (n == null) return;
            StartAnim(n);
        }

        // ---------------- 連鎖アニメ ----------------
        void StartAnim(SimNode n)
        {
            if (!chkAnim.Checked || n.Chain == null || n.Chain.Chains == 0) { RefreshSide(A); return; }
            anim.Clear();
            animSide = A;
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
            if (anim.Count == 0 || animSide == null) { StopAnim(); return; }
            var (f, hl, cap) = anim.Dequeue();
            var v = animSide.View;
            v.Field = f; v.Highlight = hl; v.Caption = cap; v.ShowPair = false;
            v.Invalidate();
        }

        void StopAnim()
        {
            if (!animTimer.Enabled) return;
            animTimer.Stop();
            anim.Clear();
            var sd = animSide;
            animSide = null;
            if (sd != null) RefreshSide(sd);
        }

        // ---------------- 編集 ----------------
        void SetEditMode(bool on)
        {
            StopAnim();
            if (!on) CommitEdit();
            palette.Visible = on;
            foreach (var sd in sides) sd.View.Editable = on && sd == A;
            view.Cursor = on ? Cursors.Hand : Cursors.Default;
            lastCell = -1; ResizeBoards();
            RefreshAll();
            view.Focus();
        }

        void OnCellPainted(int x, int y, MouseButtons btn)
        {
            if (A.EditDraft == null) A.EditDraft = state.Current.Field.Clone();
            byte c = btn == MouseButtons.Right ? PuyoColor.None : paintColor;
            if (A.EditDraft[x, y] == c) return;
            A.EditDraft[x, y] = c;
            view.Field = A.EditDraft;
            view.Invalidate();
        }

        void CommitEdit()
        {
            if (A.EditDraft == null) return;
            var d = A.EditDraft;
            A.EditDraft = null;
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
                SetTsumo(dlg.Result);
                if (dlg.StartIndex >= 0 && dlg.StartIndex != state.Current.TsumoIndex)
                    state.AddEdit(state.Current.Field, dlg.StartIndex, $"ツモ設定（{dlg.StartIndex + 1}手目から）");
                A.PairX = 2; A.PairRot = 0;
                RefreshAll();
            }
        }

        /// <summary>T キーでツモの設定を開くか（プレーヤーに埋め込むときは T がシミュレーターを閉じるキーなので false）</summary>
        public bool TsumoKey
        {
            get => tsumoKey;
            set { tsumoKey = value; lblHelp.Text = lblHelp.Text.Replace(value ? "T 閉じる" : "T ツモ", value ? "T ツモ" : "T 閉じる"); }
        }
        bool tsumoKey = true;

        /// <summary>ツモを設定する（1P・2P 共通）</summary>
        public void SetTsumo(TsumoSource src) { foreach (var sd in sides) sd.State.SetTsumo(src); }
        public TsumoSource Tsumo => state.Tsumo;

        /// <summary>ツールバーの右端にボタンを足す（埋め込み先の「閉じる」など）</summary>
        public Button AddToolButton(string text, EventHandler click)
        {
            var b = MakeButton(text, click);
            b.Margin = new Padding(12, 0, 0, 0);
            bar.Controls.Add(b);
            return b;
        }

        /// <summary>外部（盤面認識など）から、片側の盤面とツモ位置を渡して開始する</summary>
        public void LoadPosition(int side, Field f, int tsumoIndex, string label)
        {
            StopAnim();
            var sd = sides[side];
            sd.EditDraft = null;
            sd.State.Reset(f, tsumoIndex, label);
            sd.PairX = 2; sd.PairRot = 0;
            sd.CompareNode = null;
            RefreshAll();
        }

        /// <summary>操作中の側に読み込む</summary>
        public void LoadPosition(Field f, int tsumoIndex, string label) => LoadPosition(act, f, tsumoIndex, label);

        protected override void Dispose(bool disposing)
        {
            if (disposing) animTimer.Dispose();
            base.Dispose(disposing);
        }
    }
}
