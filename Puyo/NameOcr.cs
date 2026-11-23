using System;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace TimeshiftViewer.Puyo
{
    // ------------------------------------------------------------
    //  プレイヤー名の読み取り（Windows 標準の文字認識）
    //  日本語と英語の両方で読み、英数字の名前なら英語の結果を使う。
    //  使えない環境（言語パックなし等）では null を返す
    // ------------------------------------------------------------
    static class NameOcr
    {
        static OcrEngine ja, en;
        static bool tried;

        static void Init()
        {
            if (tried) return;
            tried = true;
            try { ja = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("ja")); } catch { }
            try { en = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US")); } catch { }
            if (ja == null && en == null) { try { ja = OcrEngine.TryCreateFromUserProfileLanguages(); } catch { } }
        }

        public static bool Available { get { Init(); return ja != null || en != null; } }

        /// <summary>名前欄（side 0=1P, 1=2P）を読む</summary>
        public static Task<string> ReadName(FrameImage f, int side) => Read(f, PuyoLayout.Name[side]);

        public static async Task<string> Read(FrameImage f, RectangleF region)
        {
            Init();
            if (ja == null && en == null) return null;
            try
            {
                using (var sb = Prepare(f, region))
                {
                    if (sb == null) return null;
                    string a = ja != null ? Clean((await ja.RecognizeAsync(sb))?.Text) : null;
                    string b = en != null ? Clean((await en.RecognizeAsync(sb))?.Text) : null;
                    if (string.IsNullOrEmpty(a)) return string.IsNullOrEmpty(b) ? null : b;
                    if (string.IsNullOrEmpty(b)) return a;
                    // 日本語の結果がほぼ英数字なら、英語の結果のほうが正確
                    int ascii = a.Count(c => c < 128);
                    return ascii * 2 >= a.Length ? b : a;
                }
            }
            catch { return null; }
        }

        static bool IsKatakana(char c) => c >= 'ァ' && c <= 'ヺ';

        static string Clean(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Replace(" ", "").Trim();
            // 英数字の名前で日本語の認識が混ぜる文字を直す
            if (s.Count(c => c < 128) * 2 >= s.Length)
            {
                var sb = new System.Text.StringBuilder();
                foreach (char c in s)
                {
                    char d = c;
                    if (c == '〇' || c == 'Ｏ' || c == '○') d = 'O';
                    else if (c == '「' || c == '｢') d = 'r';
                    else if (c >= 'Ａ' && c <= 'Ｚ') d = (char)('A' + (c - 'Ａ'));
                    else if (c >= 'ａ' && c <= 'ｚ') d = (char)('a' + (c - 'ａ'));
                    else if (c >= '０' && c <= '９') d = (char)('0' + (c - '０'));
                    sb.Append(d);
                }
                s = sb.ToString();
            }
            // カタカナに続く「-」は長音「ー」
            var chars = s.ToCharArray();
            for (int i = 1; i < chars.Length; i++)
                if ((chars[i] == '-' || chars[i] == '－' || chars[i] == '一') && IsKatakana(chars[i - 1])) chars[i] = 'ー';
            return new string(chars);
        }

        // 文字は白（彩度が低く明るい）、背景は青や赤のバー。文字を黒・それ以外を白にして 3 倍に拡大する
        static SoftwareBitmap Prepare(FrameImage f, RectangleF region)
        {
            const int scale = 3;
            int x1 = (int)((region.Left - f.OriginX) * f.Sx), y1 = (int)((region.Top - f.OriginY) * f.Sy);
            int w = (int)(region.Width * f.Sx), h = (int)(region.Height * f.Sy);
            x1 = Math.Max(0, x1); y1 = Math.Max(0, y1);
            w = Math.Min(w, f.W - x1); h = Math.Min(h, f.H - y1);
            if (w <= 0 || h <= 0) return null;
            // 明るさの基準: 範囲内で彩度の低い画素の明るさの上位
            var vs = new System.Collections.Generic.List<float>();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int so = (y1 + y) * f.Stride + (x1 + x) * 3;
                    FrameImage.Hsv(f.Px[so + 2], f.Px[so + 1], f.Px[so], out _, out float s, out float v);
                    if (s < 0.25f) vs.Add(v);
                }
            if (vs.Count < 20) return null;
            vs.Sort();
            float vmax = vs[(int)(vs.Count * 0.95)];
            float th = Math.Max(0.4f, vmax * 0.78f);

            // 2値にして拡大する（濃淡を残す・滑らかに拡大する方法も試したが、濁点が消えるなど悪化した）
            int W = w * scale + 40, H = h * scale + 40;     // 余白をつける
            var buf = new byte[W * H * 4];
            for (int i = 0; i < buf.Length; i++) buf[i] = 255;
            for (int y = 0; y < h * scale; y++)
                for (int x = 0; x < w * scale; x++)
                {
                    int so = (y1 + y / scale) * f.Stride + (x1 + x / scale) * 3;
                    FrameImage.Hsv(f.Px[so + 2], f.Px[so + 1], f.Px[so], out _, out float s, out float v);
                    if (s < 0.3f && v >= th)
                    {
                        int o = ((y + 20) * W + x + 20) * 4;
                        buf[o] = buf[o + 1] = buf[o + 2] = 0;
                    }
                }
            return SoftwareBitmap.CreateCopyFromBuffer(buf.AsBuffer(), BitmapPixelFormat.Bgra8, W, H, BitmapAlphaMode.Premultiplied);
        }
    }
}
