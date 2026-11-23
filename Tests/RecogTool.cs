using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using TimeshiftViewer.Puyo;

namespace PuyoTests
{
    // 開発用: 画像認識の確認と、数字の見本づくり
    static class RecogTool
    {
        // samples/ の画像と、そこに写っているスコア（1P, 2P。読まないものは null）
        public static readonly (string file, string s1, string s2)[] Scores =
        {
            ("end_1p_lose_stars.jpg", "00000040", "00000058"),
            ("start_go_stars.jpg", "00000000", "00000000"),
            ("play_numbers_sets.jpg", "00000092", "00000098"),
            ("play_stars_a.jpg", "00000060", "00000328"),
            ("play_stars_b.jpg", "00000075", "00002839"),
            ("play_numbers_chain.jpg", null, "00000723"),
        };

        public static readonly (string file, string left, string right)[] WinDigits =
        {
            ("play_numbers_chain.jpg", "0", "1"),
            ("play_numbers_sets.jpg", "0", "0"),
        };

        public static void MakeTemplates(string samples, string outFile)
        {
            var acc = new Dictionary<int, List<bool[]>>();
            // 勝利数の数字（スコアと同じフォント）。「1」はここからしか取れない
            foreach (var (file, l, r) in WinDigits)
            {
                var f = FrameImage.Load(Path.Combine(samples, file));
                foreach (var (right, s) in new[] { (false, l), (true, r) })
                {
                    var shapes = Recognizer.WinDigitShapes(f, right);
                    if (shapes.Count != s.Length) continue;
                    for (int i = 0; i < s.Length; i++) { int d = s[i] - (char)48; if (!acc.ContainsKey(d)) acc[d] = new List<bool[]>(); acc[d].Add(shapes[i]); }
                }
            }
            foreach (var (file, s1, s2) in Scores)
            {
                var f = FrameImage.Load(Path.Combine(samples, file));
                for (int side = 0; side < 2; side++)
                {
                    string s = side == 0 ? s1 : s2;
                    if (s == null) continue;
                    var shapes = Recognizer.ScoreDigitShapes(f, side);
                    if (shapes.Count != s.Length) { Console.WriteLine($"  {file} side{side}: {shapes.Count} 個（期待 {s.Length}）→ 使わない"); continue; }
                    for (int i = 0; i < s.Length; i++)
                    {
                        int d = s[i] - '0';
                        if (!acc.ContainsKey(d)) acc[d] = new List<bool[]>();
                        acc[d].Add(shapes[i]);
                    }
                }
            }
            var sb = new StringBuilder();
            sb.AppendLine("namespace TimeshiftViewer.Puyo");
            sb.AppendLine("{");
            sb.AppendLine("    static partial class DigitTemplates");
            sb.AppendLine("    {");
            sb.AppendLine("        // 自動生成（Tests --make-templates）。samples/ のスコア欄の数字の多数決");
            sb.AppendLine("        static readonly string[] Data =");
            sb.AppendLine("        {");
            for (int d = 0; d < 10; d++)
            {
                if (!acc.TryGetValue(d, out var list)) { sb.AppendLine($"            null, // {d}: 見本なし"); Console.WriteLine($"  {d}: 見本なし"); continue; }
                var s = new char[Recognizer.TW * Recognizer.TH];
                for (int k = 0; k < s.Length; k++) s[k] = list.Count(x => x[k]) * 2 > list.Count ? '#' : '.';
                sb.AppendLine($"            \"{new string(s)}\", // {d}（{list.Count}個）");
                Console.WriteLine($"  {d}: {list.Count} 個");
                for (int y = 0; y < Recognizer.TH; y++) Console.WriteLine("      " + new string(s, y * Recognizer.TW, Recognizer.TW));
            }
            sb.AppendLine("        };");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            File.WriteAllText(outFile, sb.ToString(), new UTF8Encoding(false));
            Console.WriteLine("書き出し: " + outFile);
        }

        public static void Dump(string path)
        {
            var f = FrameImage.Load(path);
            var crop = TimeshiftViewer.Puyo.PuyoLayout.AnalysisCrop;
            if (f.W == crop.Width && f.H == crop.Height) f = new FrameImage(f.Px, f.W, f.H, f.Stride, crop.X, crop.Y, 1f);   // 解析用の切り出し画像
            Console.WriteLine($"== {Path.GetFileName(path)} ({f.W}x{f.H})  GO率={Recognizer.GoRatio(f):P1}  勝利数={Recognizer.ReadWins(f)}  [{Recognizer.DebugWins(f)}] set:{Recognizer.SetDebug}");
            for (int side = 0; side < 2; side++)
            {
                var field = Recognizer.ReadField(f, side, false);
                var (n1, n2) = Recognizer.ReadNext(f, side);
                string P((byte axis, byte child)? p) => p == null ? "??" : $"{".RGBYPO"[p.Value.axis]}{".RGBYPO"[p.Value.child]}";
                Console.WriteLine($"  {(side == 0 ? "1P" : "2P")}  NEXT(軸子)={P(n1)} NEXT2={P(n2)}");
                var rows = field.ToString().Split('\n');
                foreach (var r in rows) Console.WriteLine("    " + r);
            }
        }
    }
}
