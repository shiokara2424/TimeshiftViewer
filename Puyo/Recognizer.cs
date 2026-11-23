using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;

namespace TimeshiftViewer.Puyo
{
    // ------------------------------------------------------------
    //  画像（BGR 24bit）。座標は 1920×1080 基準で指定し、実際の解像度に合わせて換算する
    // ------------------------------------------------------------
    sealed class FrameImage
    {
        public readonly int W, H, Stride;
        public readonly byte[] Px;           // BGR
        // 切り出した画像のとき: 左上が 1920×1080 基準でどこか、と倍率（実画素 / 基準）
        public readonly float OriginX, OriginY, Scale;

        public FrameImage(byte[] bgr, int w, int h, int stride)
        {
            Px = bgr; W = w; H = h; Stride = stride;
            OriginX = OriginY = 0; Scale = 0;
        }

        public FrameImage(byte[] bgr, int w, int h, int stride, float originX, float originY, float scale)
        {
            Px = bgr; W = w; H = h; Stride = stride;
            OriginX = originX; OriginY = originY; Scale = scale;
        }

        public static FrameImage FromBitmap(Bitmap src)
        {
            using (var bmp = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb))
            {
                using (var g = Graphics.FromImage(bmp)) g.DrawImage(src, 0, 0, src.Width, src.Height);
                var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                var buf = new byte[data.Stride * bmp.Height];
                Marshal.Copy(data.Scan0, buf, 0, buf.Length);
                bmp.UnlockBits(data);
                return new FrameImage(buf, bmp.Width, bmp.Height, data.Stride);
            }
        }

        public static FrameImage Load(string path)
        {
            using (var b = new Bitmap(path)) return FromBitmap(b);
        }

        /// <summary>1920×1080 の画像から、1920×1080 基準の矩形を切り出す（解析用の映像と同じ形にする）</summary>
        public FrameImage Crop(Rectangle r)
        {
            int x0 = (int)(r.X * Sx), y0 = (int)(r.Y * Sy), w = (int)(r.Width * Sx), h = (int)(r.Height * Sy);
            int stride = w * 3;
            var buf = new byte[stride * h];
            for (int y = 0; y < h; y++) Buffer.BlockCopy(Px, (y0 + y) * Stride + x0 * 3, buf, y * stride, stride);
            return new FrameImage(buf, w, h, stride, r.X, r.Y, Sx);
        }

        /// <summary>開発用: PNG に保存</summary>
        public void Save(string path)
        {
            using (var bmp = new Bitmap(W, H, PixelFormat.Format24bppRgb))
            {
                var data = bmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
                for (int y = 0; y < H; y++) Marshal.Copy(Px, y * Stride, data.Scan0 + y * data.Stride, W * 3);
                bmp.UnlockBits(data);
                bmp.Save(path, ImageFormat.Png);
            }
        }

        public float Sx => Scale > 0 ? Scale : W / 1920f;
        public float Sy => Scale > 0 ? Scale : H / 1080f;

        /// <summary>1920×1080 基準の矩形の画素を順に返す（実座標の x, y と RGB）</summary>
        public IEnumerable<(int x, int y, byte r, byte g, byte b)> Pixels(float x1, float y1, float x2, float y2)
        {
            int ix1 = Clamp((int)((x1 - OriginX) * Sx), 0, W - 1), ix2 = Clamp((int)Math.Ceiling((x2 - OriginX) * Sx), 0, W - 1);
            int iy1 = Clamp((int)((y1 - OriginY) * Sy), 0, H - 1), iy2 = Clamp((int)Math.Ceiling((y2 - OriginY) * Sy), 0, H - 1);
            for (int y = iy1; y <= iy2; y++)
            {
                int o = y * Stride + ix1 * 3;
                for (int x = ix1; x <= ix2; x++, o += 3)
                    yield return (x, y, Px[o + 2], Px[o + 1], Px[o]);
            }
        }

        static int Clamp(int v, int a, int b) => v < a ? a : v > b ? b : v;

        public static void Hsv(byte r, byte g, byte b, out float h, out float s, out float v)
        {
            int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            v = max / 255f;
            s = max == 0 ? 0 : (max - min) / (float)max;
            if (max == min) { h = 0; return; }
            float d = max - min;
            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
            if (h < 0) h += 360;
        }
    }

    // ------------------------------------------------------------
    //  ぷよぷよeスポーツ 対戦画面の位置（1920×1080 基準・固定）
    // ------------------------------------------------------------
    static class PuyoLayout
    {
        public static readonly float[] FieldLeft = { 275f, 1252f };
        public const float CellW = 64.6f, CellH = 60f, FieldBottom = 880f;

        public static PointF Cell(int side, int x, int y) =>
            new PointF(FieldLeft[side] + CellW * (x + 0.5f), FieldBottom - CellH * (y + 0.5f));

        // NEXT（上が子・下が軸と仮定）と NEXT2 の中心と半径
        public static readonly PointF[] Next1Top = { new PointF(751, 193), new PointF(1167, 193) };
        public static readonly PointF[] Next1Bottom = { new PointF(751, 253), new PointF(1167, 253) };
        public static readonly PointF[] Next2Top = { new PointF(797, 320), new PointF(1122, 320) };
        public static readonly PointF[] Next2Bottom = { new PointF(797, 366), new PointF(1122, 366) };
        public const float Next1R = 20, Next2R = 15;

        // 勝利数（中央下）
        public static readonly RectangleF WinLeft = RectangleF.FromLTRB(800, 966, 925, 1012);
        public static readonly RectangleF WinRight = RectangleF.FromLTRB(995, 966, 1120, 1012);
        public static readonly RectangleF WinDigitLeft = RectangleF.FromLTRB(826, 964, 876, 1014);
        public static readonly RectangleF WinDigitRight = RectangleF.FromLTRB(1044, 964, 1094, 1014);
        // セット数（勝利数の上のタブ）
        public static readonly RectangleF SetLeft = RectangleF.FromLTRB(822, 928, 914, 958);
        public static readonly RectangleF SetRight = RectangleF.FromLTRB(1008, 928, 1100, 958);
        // スコア
        public static readonly RectangleF[] Score = { RectangleF.FromLTRB(345, 882, 680, 944), RectangleF.FromLTRB(1243, 882, 1578, 944) };
        // 名前
        public static readonly RectangleF[] Name = { RectangleF.FromLTRB(225, 958, 735, 996), RectangleF.FromLTRB(1185, 958, 1700, 996) };
        // 「ゴー！」
        public static readonly RectangleF Go = RectangleF.FromLTRB(680, 420, 1220, 660);
        // 解析用に ffmpeg で切り出す範囲（盤面・NEXT・勝利数・名前を含む）
        public static readonly Rectangle AnalysisCrop = new Rectangle(200, 140, 1520, 880);
    }

    sealed class WinInfo
    {
        public bool Valid;           // 勝利数の表示が見つかった
        public bool NumberMode;      // 数字表示（false なら★）
        public int Left = -1, Right = -1;
        public int SetLeft = -1, SetRight = -1;   // セット数（表示がなければ -1）
        public override string ToString() =>
            !Valid ? "(なし)" : $"{(NumberMode ? "数字" : "★")} {Left}-{Right}" + (SetLeft >= 0 ? $" Set {SetLeft}-{SetRight}" : "");
    }

    // ------------------------------------------------------------
    //  認識
    // ------------------------------------------------------------
    static class Recognizer
    {
        public const byte Unknown = 255;

        // 画素の色の分類（0〜4 = 赤緑青黄紫、5 = おじゃま（白っぽい灰）、-1 = それ以外）
        static int PixelClass(byte r, byte g, byte b)
        {
            FrameImage.Hsv(r, g, b, out float h, out float s, out float v);
            if (s < 0.2f && v > 0.62f) return 5;
            if (s < 0.38f || v < 0.42f) return -1;
            if (h < 18 || h >= 330) return 0;        // 赤
            if (h >= 28 && h < 68) return 3;         // 黄（オレンジ寄り）
            if (h >= 75 && h < 165) return 1;        // 緑
            if (h >= 185 && h < 252) return 2;       // 青
            if (h >= 256 && h < 325) return 4;       // 紫
            return -1;
        }

        /// <summary>中心 (cx,cy)、半径 rx×ry の範囲の多数決で、盤面の色（0=空、1〜5=色、6=おじゃま）を返す</summary>
        public static byte ClassifyCell(FrameImage f, float cx, float cy, float rx, float ry)
        {
            var counts = new int[6];
            int total = 0;
            foreach (var p in f.Pixels(cx - rx, cy - ry, cx + rx, cy + ry))
            {
                total++;
                int c = PixelClass(p.r, p.g, p.b);
                if (c >= 0) counts[c]++;
            }
            if (total == 0) return Unknown;
            int best = 0;
            for (int i = 1; i < 5; i++) if (counts[i] > counts[best]) best = i;
            if (counts[best] >= total * 0.28f) return PuyoColor.ToCell((byte)best);
            if (counts[5] >= total * 0.30f) return PuyoColor.Ojama;
            return PuyoColor.None;
        }

        /// <summary>色ぷよらしい画素（彩度・明るさが高い）の割合</summary>
        static float FillRatio(FrameImage f, float cx, float cy, float rx, float ry)
        {
            int n = 0, hit = 0;
            foreach (var p in f.Pixels(cx - rx, cy - ry, cx + rx, cy + ry)) { n++; if (PixelClass(p.r, p.g, p.b) >= 0) hit++; }
            return n == 0 ? 0 : hit / (float)n;
        }

        /// <summary>盤面（12段）を読む。浮いているぷよ（操作中の組ぷよなど）は除く</summary>
        public static Field ReadField(FrameImage f, int side, bool dropFloating = true)
        {
            var field = new Field();
            float rx = PuyoLayout.CellW * 0.33f, ry = PuyoLayout.CellH * 0.33f;
            for (int x = 0; x < Field.W; x++)
                for (int y = 0; y < Field.Visible; y++)
                {
                    var c = PuyoLayout.Cell(side, x, y);
                    byte v = ClassifyCell(f, c.X, c.Y, rx, ry);
                    // 3列目12段目の × 印は赤ぷよと紛らわしいので、ほぼ埋まっているときだけぷよとみなす
                    if (x == Field.DeathX && y == Field.DeathY && v != 0 && FillRatio(f, c.X, c.Y, rx, ry) < 0.55f) v = 0;
                    field[x, y] = v == Unknown ? (byte)0 : v;
                }
            if (dropFloating)
            {
                // 下が空いているぷよは、操作中の組ぷよやアニメーション中のものとみなして消す
                for (int x = 0; x < Field.W; x++)
                {
                    bool gap = false;
                    for (int y = 0; y < Field.Visible; y++)
                    {
                        if (field[x, y] == 0) gap = true;
                        else if (gap) field[x, y] = 0;
                    }
                }
            }
            return field;
        }

        /// <summary>NEXT と NEXT2 を読む（色は盤面の 1〜5。読めなければ null）。上を子、下を軸として返す</summary>
        public static ((byte axis, byte child)? next1, (byte axis, byte child)? next2) ReadNext(FrameImage f, int side)
        {
            (byte, byte)? Pair(PointF top, PointF bottom, float r)
            {
                byte t = ClassifyCell(f, top.X, top.Y, r, r);
                byte b = ClassifyCell(f, bottom.X, bottom.Y, r, r);
                if (t < 1 || t > 5 || b < 1 || b > 5) return null;
                return (b, t);
            }
            return (Pair(PuyoLayout.Next1Top[side], PuyoLayout.Next1Bottom[side], PuyoLayout.Next1R),
                    Pair(PuyoLayout.Next2Top[side], PuyoLayout.Next2Bottom[side], PuyoLayout.Next2R));
        }

        /// <summary>NEXT 欄の画素の平均（動きの検出用）</summary>
        public static float NextSignature(FrameImage f, int side, float[] into)
        {
            // NEXT1 と NEXT2 の4か所の平均色を into[12] に入れ、合計輝度を返す
            var pts = new[] { PuyoLayout.Next1Top[side], PuyoLayout.Next1Bottom[side], PuyoLayout.Next2Top[side], PuyoLayout.Next2Bottom[side] };
            float sum = 0;
            for (int i = 0; i < 4; i++)
            {
                float r = 0, g = 0, b = 0; int n = 0;
                foreach (var p in f.Pixels(pts[i].X - 14, pts[i].Y - 14, pts[i].X + 14, pts[i].Y + 14)) { r += p.r; g += p.g; b += p.b; n++; }
                if (n == 0) n = 1;
                into[i * 3] = r / n; into[i * 3 + 1] = g / n; into[i * 3 + 2] = b / n;
                sum += (r + g + b) / n;
            }
            return sum;
        }

        /// <summary>「ゴー！」の文字（オレンジ）が画面中央に大きく出ているか</summary>
        public static bool IsGo(FrameImage f) => GoRatio(f) > 0.07f;

        public static float GoRatio(FrameImage f)
        {
            var r = PuyoLayout.Go;
            int n = 0, hit = 0;
            foreach (var p in f.Pixels(r.Left, r.Top, r.Right, r.Bottom))
            {
                n++;
                if (p.r > 215 && p.g > 95 && p.g < 185 && p.b < 70) hit++;
            }
            return n == 0 ? 0 : hit / (float)n;
        }

        /// <summary>盤面にあるぷよの数（開始直後の判定用）</summary>
        public static int CountPuyos(FrameImage f, int side) =>
            ReadField(f, side, false).C.Cast<byte>().Count(c => c != 0);

        // ---------------- 勝利数・セット数 ----------------
        public static WinInfo ReadWins(FrameImage f)
        {
            var w = new WinInfo();
            // 数字表示か: 左右の数字の位置に白い画素が十分あるか
            float wl = Ratio(f, PuyoLayout.WinDigitLeft, IsWhite), wr = Ratio(f, PuyoLayout.WinDigitRight, IsWhite);
            if (Math.Max(wl, wr) > 0.08f && Math.Min(wl, wr) > 0.025f)
            {
                w.NumberMode = true;
                w.Left = ReadNumber(f, PuyoLayout.WinDigitLeft, IsWhite);
                w.Right = ReadNumber(f, PuyoLayout.WinDigitRight, IsWhite);
                w.Valid = w.Left >= 0 && w.Right >= 0;
                w.SetLeft = ReadSet(f, PuyoLayout.SetLeft);
                w.SetRight = ReadSet(f, PuyoLayout.SetRight);
                if (w.SetLeft < 0 || w.SetRight < 0) w.SetLeft = w.SetRight = -1;
                return w;
            }
            // ★表示: 金色の★のかたまりを数える。灰色の★（未獲得）も数えて、表示があるか確かめる
            var gl = Blobs(f, PuyoLayout.WinLeft, IsGold, 90);
            var gr = Blobs(f, PuyoLayout.WinRight, IsGold, 90);
            var sl = Blobs(f, PuyoLayout.WinLeft, IsGreyStar, 90);
            var sr = Blobs(f, PuyoLayout.WinRight, IsGreyStar, 90);
            if (gl.Count + sl.Count == 0 || gr.Count + sr.Count == 0) return w;
            w.Left = gl.Count; w.Right = gr.Count;
            w.Valid = true;
            return w;
        }

        /// <summary>開発用: 勝利数の数字の位置の白い画素の割合（左, 右）と、読んだ数字のかたまり数</summary>
        public static string DebugWins(FrameImage f) =>
            $"白率 {Ratio(f, PuyoLayout.WinDigitLeft, IsWhite):P1}/{Ratio(f, PuyoLayout.WinDigitRight, IsWhite):P1} " +
            $"かたまり {DigitBlobs(f, PuyoLayout.WinDigitLeft, IsWhite).Count}/{DigitBlobs(f, PuyoLayout.WinDigitRight, IsWhite).Count} " +
            $"値 {ReadNumber(f, PuyoLayout.WinDigitLeft, IsWhite)}/{ReadNumber(f, PuyoLayout.WinDigitRight, IsWhite)}";

        static bool IsWhite(byte r, byte g, byte b) => r > 215 && g > 215 && b > 215;
        static bool IsGold(byte r, byte g, byte b)
        {
            FrameImage.Hsv(r, g, b, out float h, out float s, out float v);
            return h >= 25 && h <= 62 && s > 0.45f && v > 0.6f;
        }
        static bool IsGreyStar(byte r, byte g, byte b)
        {
            FrameImage.Hsv(r, g, b, out float h, out float s, out float v);
            return s < 0.22f && v > 0.25f && v < 0.62f;
        }

        static float Ratio(FrameImage f, RectangleF r, Func<byte, byte, byte, bool> on)
        {
            int n = 0, hit = 0;
            foreach (var p in f.Pixels(r.Left, r.Top, r.Right, r.Bottom)) { n++; if (on(p.r, p.g, p.b)) hit++; }
            return n == 0 ? 0 : hit / (float)n;
        }

        // ---------------- 2値化とかたまり ----------------
        sealed class Blob { public int X1 = int.MaxValue, Y1 = int.MaxValue, X2 = -1, Y2 = -1, Area; public List<(int x, int y)> Pts = new List<(int, int)>(); }

        static List<Blob> Blobs(FrameImage f, RectangleF r, Func<byte, byte, byte, bool> on, float minArea1080)
        {
            int x1 = (int)((r.Left - f.OriginX) * f.Sx), y1 = (int)((r.Top - f.OriginY) * f.Sy), x2 = (int)((r.Right - f.OriginX) * f.Sx), y2 = (int)((r.Bottom - f.OriginY) * f.Sy);
            x1 = Math.Max(0, x1); y1 = Math.Max(0, y1); x2 = Math.Min(f.W - 1, x2); y2 = Math.Min(f.H - 1, y2);
            int w = x2 - x1 + 1, h = y2 - y1 + 1;
            if (w <= 0 || h <= 0) return new List<Blob>();
            var mask = new bool[w, h];
            foreach (var p in f.Pixels(r.Left, r.Top, r.Right, r.Bottom))
            {
                int mx = p.x - x1, my = p.y - y1;
                if (mx >= 0 && my >= 0 && mx < w && my < h) mask[mx, my] = on(p.r, p.g, p.b);
            }
            var seen = new bool[w, h];
            var list = new List<Blob>();
            float minArea = minArea1080 * f.Sx * f.Sy;
            var st = new Stack<(int, int)>();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (!mask[x, y] || seen[x, y]) continue;
                    var b = new Blob();
                    st.Push((x, y)); seen[x, y] = true;
                    while (st.Count > 0)
                    {
                        var (px, py) = st.Pop();
                        b.Area++; b.Pts.Add((px, py));
                        if (px < b.X1) b.X1 = px; if (px > b.X2) b.X2 = px;
                        if (py < b.Y1) b.Y1 = py; if (py > b.Y2) b.Y2 = py;
                        for (int k = 0; k < 4; k++)
                        {
                            int nx = px + (k == 0 ? 1 : k == 1 ? -1 : 0), ny = py + (k == 2 ? 1 : k == 3 ? -1 : 0);
                            if (nx < 0 || ny < 0 || nx >= w || ny >= h || seen[nx, ny] || !mask[nx, ny]) continue;
                            seen[nx, ny] = true; st.Push((nx, ny));
                        }
                    }
                    if (b.Area >= minArea) list.Add(b);
                }
            return list;
        }

        // ---------------- 数字 ----------------
        public const int TW = 12, TH = 18;

        /// <summary>かたまりを TW×TH に縮めた形（数字の見本・比較用）</summary>
        static bool[] Shape(Blob b)
        {
            var s = new bool[TW * TH];
            int bw = b.X2 - b.X1 + 1, bh = b.Y2 - b.Y1 + 1;
            var cnt = new int[TW * TH]; var tot = new int[TW * TH];
            // 各セルに入る画素の割合で判定
            var set = new HashSet<(int, int)>(b.Pts);
            for (int y = 0; y < bh; y++)
                for (int x = 0; x < bw; x++)
                {
                    int tx = Math.Min(TW - 1, x * TW / bw), ty = Math.Min(TH - 1, y * TH / bh);
                    tot[ty * TW + tx]++;
                    if (set.Contains((b.X1 + x, b.Y1 + y))) cnt[ty * TW + tx]++;
                }
            for (int i = 0; i < s.Length; i++) s[i] = tot[i] > 0 && cnt[i] * 2 >= tot[i];
            return s;
        }

        /// <summary>矩形内の数字のかたまりを左から順に返す（高さが矩形の 35% 以上のもの）</summary>
        static List<Blob> DigitBlobs(FrameImage f, RectangleF r, Func<byte, byte, byte, bool> on)
        {
            float minH = r.Height * 0.35f * f.Sy;
            return Blobs(f, r, on, 12).Where(b => b.Y2 - b.Y1 + 1 >= minH).OrderBy(b => b.X1).ToList();
        }

        /// <summary>矩形内の数字を読む（読めなければ -1）</summary>
        public static int ReadNumber(FrameImage f, RectangleF r, Func<byte, byte, byte, bool> on)
        {
            var blobs = DigitBlobs(f, r, on);
            if (blobs.Count == 0 || blobs.Count > 3) return -1;
            int v = 0;
            foreach (var b in blobs)
            {
                int d = MatchDigit(b);
                if (d < 0) return -1;
                v = v * 10 + d;
            }
            return v;
        }

        static int MatchDigit(Blob b, double maxDiff = 0.25)
        {
            int bw = b.X2 - b.X1 + 1, bh = b.Y2 - b.Y1 + 1;
            // 細長いかたまりは「1」
            if (bw * 3 < bh) return 1;
            var s = Shape(b);
            int best = -1, bestDist = int.MaxValue;
            for (int d = 0; d < 10; d++)
            {
                var t = DigitTemplates.Get(d);
                if (t == null) continue;
                int dist = 0;
                for (int i = 0; i < s.Length; i++) if (s[i] != t[i]) dist++;
                if (dist < bestDist) { bestDist = dist; best = d; }
            }
            return bestDist <= TW * TH * maxDiff ? best : -1;
        }

        /// <summary>セット数（「Set N」の N）。タブの明るい背景に暗い文字</summary>
        public static string SetDebug = "";
        static int ReadSet(FrameImage f, RectangleF tab)
        {
            // タブの背景の明るさを求め、それより十分暗い画素を文字とする
            var vs = new List<float>();
            foreach (var p in f.Pixels(tab.Left, tab.Top, tab.Right, tab.Bottom))
            {
                FrameImage.Hsv(p.r, p.g, p.b, out _, out _, out float v);
                vs.Add(v);
            }
            if (vs.Count == 0) return -1;
            vs.Sort();
            float bg = vs[(int)(vs.Count * 0.8)];
            SetDebug += $" bg={bg:F2}";
            if (bg < 0.55f) return -1;           // タブが見当たらない
            float th = bg - 0.18f;
            bool Dark(byte r, byte g, byte b) { FrameImage.Hsv(r, g, b, out _, out _, out float v); return v < th; }
            // タブの左側に「Set」の3文字があること（明るい背景を数字と読み違えないように）
            var letters = RectangleF.FromLTRB(tab.Left, tab.Top, tab.Left + tab.Width * 0.62f, tab.Bottom);
            int nLetters = Blobs(f, letters, Dark, 10).Count(b => b.Y2 - b.Y1 + 1 >= tab.Height * 0.3f * f.Sy);
            SetDebug += $" letters={nLetters}";
            if (nLetters < 2 || nLetters > 4) return -1;
            // 「Set」の後ろ（タブの右側 35%）の数字
            var digitArea = RectangleF.FromLTRB(tab.Left + tab.Width * 0.62f, tab.Top, tab.Right, tab.Bottom);
            // 文字が細く、圧縮で途切れやすいので、範囲内の暗い画素をまとめて1つの形として比べる（セット数は1桁と仮定）
            var parts = Blobs(f, digitArea, Dark, 4).Where(b => b.Y2 - b.Y1 + 1 >= tab.Height * 0.15f * f.Sy).ToList();
            if (parts.Count == 0) return -1;
            // 一番大きいかたまりを基準に、数字1文字分の幅に入るものだけまとめる
            // （「t」の一部やタブの縁が入り込むことがある）
            var core = parts.OrderByDescending(b => b.Area).First();
            float coreCx = (core.X1 + core.X2) / 2f;
            float digitW = tab.Height * 0.62f * f.Sy;
            parts = parts.Where(b => Math.Abs((b.X1 + b.X2) / 2f - coreCx) <= digitW * 0.6f && b.X2 - b.X1 + 1 >= 2).ToList();
            var all = new Blob();
            foreach (var b in parts)
            {
                all.Pts.AddRange(b.Pts); all.Area += b.Area;
                all.X1 = Math.Min(all.X1, b.X1); all.Y1 = Math.Min(all.Y1, b.Y1);
                all.X2 = Math.Max(all.X2, b.X2); all.Y2 = Math.Max(all.Y2, b.Y2);
            }
            SetDebug += $" parts={parts.Count} h={all.Y2 - all.Y1 + 1} w={all.X2 - all.X1 + 1}";
            if (all.Y2 - all.Y1 + 1 < tab.Height * 0.35f * f.Sy) return -1;
            int dres = MatchDigit(all, 0.36);
            SetDebug += $" d={dres}";
            return dres;
        }

        // ---------------- 見本づくり（開発用） ----------------
        /// <summary>スコア欄の数字のかたまりを左から順に形にして返す（見本の作成用）</summary>
        public static List<bool[]> ScoreDigitShapes(FrameImage f, int side) =>
            DigitBlobs(f, PuyoLayout.Score[side], IsWhite).Select(Shape).ToList();

        public static List<bool[]> WinDigitShapes(FrameImage f, bool right) =>
            DigitBlobs(f, right ? PuyoLayout.WinDigitRight : PuyoLayout.WinDigitLeft, IsWhite).Select(Shape).ToList();
    }
}
