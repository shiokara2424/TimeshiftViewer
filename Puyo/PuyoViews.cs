using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace TimeshiftViewer.Puyo
{
    // ------------------------------------------------------------
    //  ぷよの描画
    // ------------------------------------------------------------
    static class PuyoDraw
    {
        // 盤面の色 1〜6
        static readonly Color[] Fill =
        {
            Color.Empty,
            Color.FromArgb(232, 56, 66),    // 赤
            Color.FromArgb(56, 186, 74),    // 緑
            Color.FromArgb(48, 108, 232),   // 青
            Color.FromArgb(238, 196, 36),   // 黄
            Color.FromArgb(158, 72, 214),   // 紫
            Color.FromArgb(178, 180, 190),  // おじゃま
        };

        public static Color ColorOf(byte cell) => cell < Fill.Length ? Fill[cell] : Color.Magenta;

        public static void Puyo(Graphics g, RectangleF r, byte cell, int alpha = 255)
        {
            if (cell == 0) return;
            var c = ColorOf(cell);
            var inner = RectangleF.Inflate(r, -r.Width * 0.06f, -r.Height * 0.06f);
            using (var b = new SolidBrush(Color.FromArgb(alpha, c)))
                g.FillEllipse(b, inner);
            using (var p = new Pen(Color.FromArgb(alpha, Dark(c, 0.55f)), Math.Max(1f, r.Width * 0.05f)))
                g.DrawEllipse(p, inner);
            if (cell == PuyoColor.Ojama)
            {
                // おじゃま: 目を2つ
                float e = inner.Width * 0.16f;
                using (var b = new SolidBrush(Color.FromArgb(alpha, 70, 70, 80)))
                {
                    g.FillEllipse(b, inner.X + inner.Width * 0.28f - e / 2, inner.Y + inner.Height * 0.42f - e / 2, e, e);
                    g.FillEllipse(b, inner.X + inner.Width * 0.72f - e / 2, inner.Y + inner.Height * 0.42f - e / 2, e, e);
                }
            }
            else
            {
                using (var b = new SolidBrush(Color.FromArgb(alpha * 150 / 255, 255, 255, 255)))
                    g.FillEllipse(b, inner.X + inner.Width * 0.2f, inner.Y + inner.Height * 0.14f, inner.Width * 0.32f, inner.Height * 0.24f);
            }
        }

        /// <summary>同じ色で隣り合うぷよをつなぐ帯</summary>
        public static void Link(Graphics g, RectangleF a, RectangleF b, byte cell)
        {
            var c = ColorOf(cell);
            using (var br = new SolidBrush(c))
            {
                if (Math.Abs(a.Y - b.Y) < 0.5f)
                {
                    float y = a.Y + a.Height * 0.25f;
                    g.FillRectangle(br, Math.Min(a.X, b.X) + a.Width / 2, y, Math.Abs(a.X - b.X), a.Height * 0.5f);
                }
                else
                {
                    float x = a.X + a.Width * 0.25f;
                    g.FillRectangle(br, x, Math.Min(a.Y, b.Y) + a.Height / 2, a.Width * 0.5f, Math.Abs(a.Y - b.Y));
                }
            }
        }

        public static Color Dark(Color c, float k) => Color.FromArgb(c.A, (int)(c.R * k), (int)(c.G * k), (int)(c.B * k));
    }

    // ------------------------------------------------------------
    //  盤面の表示（編集・操作中の組ぷよ・着地予想つき）
    // ------------------------------------------------------------
    class FieldView : Control
    {
        public int Cell = 40;
        public Field Field = new Field();
        public bool[,] Highlight;           // 消えるぷよ（連鎖アニメ）
        public string Caption;               // 盤面の上に重ねる文字（連鎖数など）

        // 操作中の組ぷよ（色は盤面の 1〜5）
        public bool ShowPair;
        public int PairX = 2, PairRot;
        public byte PairAxis, PairChild;

        public bool Editable;
        public event Action<int, int, MouseButtons> CellPainted;   // 編集: x, y, ボタン

        const int TopRows = 2;               // 盤面の上に組ぷよを表示する行
        const int Rows = 13;                 // 12段 + 13段目
        const int Pad = 6;

        public FieldView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(24, 26, 34);
        }

        public Size Preferred => new Size(Field.W * Cell + Pad * 2, (Rows + TopRows) * Cell + Pad * 2);

        RectangleF CellRect(int x, int y)   // y: 0=最下段 … 12=13段目, 13・14=上の表示行
        {
            int row = Rows + TopRows - 1 - y;
            return new RectangleF(Pad + x * Cell, Pad + row * Cell, Cell, Cell);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var f = Field ?? new Field();

            // 盤面の背景
            var top12 = CellRect(0, Field.Visible - 1);
            var fieldRect = new RectangleF(Pad, top12.Y, Field.W * Cell, Field.Visible * Cell);
            using (var b = new SolidBrush(Color.FromArgb(40, 44, 58))) g.FillRectangle(b, fieldRect);
            var r13 = CellRect(0, Field.Row13);
            using (var b = new SolidBrush(Color.FromArgb(32, 34, 44))) g.FillRectangle(b, Pad, r13.Y, Field.W * Cell, Cell);
            using (var p = new Pen(Color.FromArgb(52, 56, 72)))
            {
                for (int x = 1; x < Field.W; x++) g.DrawLine(p, Pad + x * Cell, r13.Y, Pad + x * Cell, fieldRect.Bottom);
                for (int y = 1; y <= Field.Visible; y++) { float yy = CellRect(0, y).Bottom; g.DrawLine(p, Pad, yy, Pad + Field.W * Cell, yy); }
            }
            using (var p = new Pen(Color.FromArgb(90, 96, 120), 2)) g.DrawRectangle(p, fieldRect.X, fieldRect.Y, fieldRect.Width, fieldRect.Height);

            // × 印（3列目・12段目）
            if (f[Field.DeathX, Field.DeathY] == 0)
            {
                var xr = RectangleF.Inflate(CellRect(Field.DeathX, Field.DeathY), -Cell * 0.3f, -Cell * 0.3f);
                using (var p = new Pen(Color.FromArgb(150, 220, 60, 60), Cell * 0.09f))
                {
                    g.DrawLine(p, xr.Left, xr.Top, xr.Right, xr.Bottom);
                    g.DrawLine(p, xr.Right, xr.Top, xr.Left, xr.Bottom);
                }
            }

            // つながり
            for (int x = 0; x < Field.W; x++)
                for (int y = 0; y < Field.Visible; y++)
                {
                    byte c = f[x, y];
                    if (c == 0 || c == PuyoColor.Ojama) continue;
                    if (x + 1 < Field.W && f[x + 1, y] == c) PuyoDraw.Link(g, CellRect(x, y), CellRect(x + 1, y), c);
                    if (y + 1 < Field.Visible && f[x, y + 1] == c) PuyoDraw.Link(g, CellRect(x, y), CellRect(x, y + 1), c);
                }
            // ぷよ
            for (int x = 0; x < Field.W; x++)
                for (int y = 0; y < Field.H - 1; y++)
                {
                    byte c = f[x, y];
                    if (c == 0) continue;
                    PuyoDraw.Puyo(g, CellRect(x, y), c, y >= Field.Row13 ? 150 : 255);
                    if (Highlight != null && Highlight[x, y])
                        using (var p = new Pen(Color.White, Cell * 0.1f)) g.DrawEllipse(p, RectangleF.Inflate(CellRect(x, y), -2, -2));
                }

            // 操作中の組ぷよと着地予想
            if (ShowPair && Field.ValidPos(PairX, PairRot))
            {
                f.LandingRows(PairX, PairRot, out int ay, out int cy);
                int cx = PairX + Field.ChildDx(PairRot);
                if (ay < Field.H - 1) PuyoDraw.Puyo(g, CellRect(PairX, ay), PairAxis, 90);
                if (cy < Field.H - 1) PuyoDraw.Puyo(g, CellRect(cx, cy), PairChild, 90);

                int baseY = Rows;                            // 上の表示行の下側
                int axY = PairRot == 2 ? baseY + 1 : baseY;
                int chY = PairRot == 0 ? baseY + 1 : PairRot == 2 ? baseY : baseY;
                PuyoDraw.Puyo(g, CellRect(PairX, axY), PairAxis);
                PuyoDraw.Puyo(g, CellRect(cx, chY), PairChild);
                // 軸の印
                var ar = CellRect(PairX, axY);
                using (var b = new SolidBrush(Color.FromArgb(220, 255, 255, 255)))
                    g.FillEllipse(b, ar.X + ar.Width * 0.42f, ar.Y + ar.Height * 0.42f, ar.Width * 0.16f, ar.Height * 0.16f);
            }

            if (!string.IsNullOrEmpty(Caption))
            {
                using (var font = new Font("Yu Gothic UI", Cell * 0.5f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                {
                    var rc = new RectangleF(Pad, CellRect(0, 8).Y, Field.W * Cell, Cell * 2);
                    using (var path = new GraphicsPath())
                    {
                        path.AddString(Caption, font.FontFamily, (int)FontStyle.Bold, font.Size, rc, sf);
                        using (var p = new Pen(Color.FromArgb(220, 0, 0, 0), Cell * 0.16f) { LineJoin = LineJoin.Round }) g.DrawPath(p, path);
                        g.FillPath(Brushes.White, path);
                    }
                }
            }
        }

        // ---------------- 編集 ----------------
        MouseButtons painting = MouseButtons.None;

        bool HitCell(Point pt, out int x, out int y)
        {
            x = (int)Math.Floor((pt.X - Pad) / (double)Cell);
            int row = (int)Math.Floor((pt.Y - Pad) / (double)Cell);
            y = Rows + TopRows - 1 - row;
            return x >= 0 && x < Field.W && y >= 0 && y <= Field.Row13;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (!Editable) return;
            painting = e.Button;
            if (HitCell(e.Location, out int x, out int y)) CellPainted?.Invoke(x, y, e.Button);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!Editable || painting == MouseButtons.None) return;
            if (HitCell(e.Location, out int x, out int y)) CellPainted?.Invoke(x, y, painting);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (painting != MouseButtons.None) { painting = MouseButtons.None; PaintFinished?.Invoke(); }
        }

        public event Action PaintFinished;

        protected override bool IsInputKey(Keys keyData) => true;   // 矢印キーを親に渡す
    }

    // ------------------------------------------------------------
    //  ツモの並び（NEXT / NEXT-NEXT やツモ入力の確認用）
    //  縦に組ぷよを並べる。上が子、下が軸
    // ------------------------------------------------------------
    class PairListView : Control
    {
        public int Cell = 30;
        public List<(byte axis, byte child)?> Pairs = new List<(byte, byte)?>();  // 色は盤面の 1〜5。null は「なし」
        public List<string> Labels = new List<string>();
        public bool Horizontal;

        public PairListView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(24, 26, 34);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var font = new Font("Yu Gothic UI", Math.Max(9f, Cell * 0.36f), GraphicsUnit.Pixel))
            {
                for (int i = 0; i < Pairs.Count; i++)
                {
                    float x0, y0;
                    if (Horizontal) { x0 = 6 + i * (Cell + 8); y0 = 6; }
                    else { x0 = 6; y0 = 6 + i * (Cell * 2 + 22); }
                    if (i < Labels.Count)
                    {
                        var lbl = Labels[i];
                        if (Horizontal) g.DrawString(lbl, font, Brushes.Gainsboro, x0, y0 + Cell * 2 + 2);
                        else g.DrawString(lbl, font, Brushes.Gainsboro, x0 + Cell + 6, y0 + Cell * 0.7f);
                    }
                    var p = Pairs[i];
                    if (p == null)
                    {
                        using (var pen = new Pen(Color.FromArgb(80, 84, 100)) { DashStyle = DashStyle.Dash })
                            g.DrawRectangle(pen, x0 + 2, y0 + 2, Cell - 4, Cell * 2 - 4);
                        continue;
                    }
                    PuyoDraw.Puyo(g, new RectangleF(x0, y0, Cell, Cell), p.Value.child);
                    PuyoDraw.Puyo(g, new RectangleF(x0, y0 + Cell, Cell, Cell), p.Value.axis);
                }
            }
        }
    }
}
