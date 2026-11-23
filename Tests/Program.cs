using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using TimeshiftViewer.Puyo;

namespace PuyoTests
{
    static class Program
    {
        static int failed, passed;

        static void Check(bool ok, string name, string detail = "")
        {
            if (ok) { passed++; Console.WriteLine("  OK   " + name); }
            else { failed++; Console.WriteLine("  FAIL " + name + (detail != "" ? "\n       " + detail : "")); }
        }

        static string Letters(byte[] s) => new string(s.Select(c => PuyoColor.Letters[c]).ToArray());

        static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            // 開発用: --make-templates <samples> <出力.cs> / --recog <画像...>
            if (args.Length >= 3 && args[0] == "--make-templates") { RecogTool.MakeTemplates(args[1], args[2]); return 0; }
            if (args.Length >= 2 && args[0] == "--recog") { foreach (var p in args.Skip(1)) RecogTool.Dump(p); return 0; }
            if (args.Length >= 2 && args[0] == "--ocr")     // 名前欄の文字認識だけ
            {
                foreach (var p in args.Skip(1))
                {
                    var f = FrameImage.Load(p);
                    Console.WriteLine($"{System.IO.Path.GetFileName(p)}: 1P=[{NameOcr.ReadName(f, 0).Result}] 2P=[{NameOcr.ReadName(f, 1).Result}]");
                }
                return 0;
            }
            if (args.Length >= 6 && args[0] == "--px")   // --px 画像 x1 y1 x2 y2 : 範囲の画素の HSV の分布
            {
                var f = FrameImage.Load(args[1]);
                var r = args.Skip(2).Take(4).Select(float.Parse).ToArray();
                var hues = new Dictionary<int, int>();
                foreach (var p in f.Pixels(r[0], r[1], r[2], r[3]))
                {
                    FrameImage.Hsv(p.r, p.g, p.b, out float h, out float s, out float v);
                    if (s < 0.3f || v < 0.5f) continue;
                    int k = (int)(h / 10) * 10;
                    hues.TryGetValue(k, out int c); hues[k] = c + 1;
                }
                foreach (var kv in hues.OrderBy(k => k.Key)) Console.WriteLine($"  hue {kv.Key,3}: {kv.Value}");
                return 0;
            }
            TsumoGeneration();
            TsumoProperties();
            Identification();
            FieldChains();
            SimTree();
            Recognition();
            MatchDetection();
            Console.WriteLine($"\n{passed} passed, {failed} failed");
            return failed == 0 ? 0 : 1;
        }

        // ---------------- 仕様書 6.3 の正解データ ----------------
        static void TsumoGeneration()
        {
            Console.WriteLine("[ツモ生成] 仕様書 6.3");
            foreach (var d in SpecData.Patterns)
            {
                var p = TsumoGen.Generate(d.seed);
                Check(Letters(p.Colors) == d.colors, $"seed {d.seed} colors", $"{Letters(p.Colors)} != {d.colors}");
                Check(Letters(p.Seq[3]) == d.t3, $"seed {d.seed} 3色");
                Check(Letters(p.Seq[4]) == d.t4, $"seed {d.seed} 4色");
                Check(Letters(p.Seq[5]) == d.t5, $"seed {d.seed} 5色");
            }
            // 6.1 先頭18手
            var ex = new (int seed, string s)[] {
                (19703, "BG BB PP GG BR GP GP BG PP BR GB BB GG BB GB BG PR PB"),
                (45899, "GY PG GY YP GY GG PP PR RG PR GR PG YY RG PY GR PY PG"),
                (21926, "PP RP PB RG GR RG BG GG BR GG GG RB RB RG RR RP BG RB"),
            };
            foreach (var (seed, s) in ex)
            {
                var got = PuyoColor.Format(TsumoGen.Generate(seed).Seq[4], 0, 36);
                Check(got == s, $"No.{seed + 1} 先頭18手", got);
            }
        }

        // ---------------- 仕様書 3.5 の性質 ----------------
        static void TsumoProperties()
        {
            Console.WriteLine("[ツモ生成] 性質");
            var sw = Stopwatch.StartNew();
            TsumoIdentifier.EnsureTableAsync().Wait();
            Console.WriteLine($"  （表の作成 {sw.ElapsedMilliseconds} ms）");

            int sameColor = 0;
            bool countsOk = true;
            for (int seed = 0; seed < TsumoGen.SeedCount; seed++)
            {
                var p = TsumoGen.Generate(seed);
                var t = p.Seq[4];
                if (t[0] == t[1] && t[1] == t[2] && t[2] == t[3]) sameColor++;
                if (seed % 997 == 0)
                {
                    // 5色: colors[0] が52個、他は51個（上書き前の性質なので、開幕4個を除いて近似確認はしない）
                    var c3 = new int[5];
                    foreach (var c in p.Seq[3]) c3[c]++;
                    if (c3[p.Colors[0]] != 86 || c3[p.Colors[1]] != 85 || c3[p.Colors[2]] != 85) countsOk = false;
                }
            }
            Check(sameColor == 1949, "4色の開幕2手が同色 = 1949通り", sameColor.ToString());
            Check(countsOk, "3色の色数 86/85/85");
        }

        // ---------------- 仕様書 6.2 の識別テスト ----------------
        static void Identification()
        {
            Console.WriteLine("[ツモ識別]");
            var cases = new (string input, int seed)[] {
                ("PP RP PB RG GR RG BG GG", 21926),
                ("BG BB PP GG BR GP GP BG", 19703),
                ("GY PG GY YP GY GG PP PR", 45899),
            };
            foreach (var (input, seed) in cases)
            {
                var r = TsumoIdentifier.Identify(4, PuyoColor.Parse(input), MatchMode.Exact);
                Check(r.Count == 1 && r.Seeds[0] == seed, $"4色 exact {input} → seed {seed}", $"count={r.Count} first={(r.Seeds.Count > 0 ? r.Seeds[0] : -1)}");
            }

            // 2手まで（4個）の最大候補数 406 / 8手で全シード一意（4色 exact）
            var max2 = MaxCandidates(4, 4);
            Check(max2 == 406, "4色 exact 2手の最大候補数 = 406", max2.ToString());
            var max8 = MaxCandidates(4, 16);
            Check(max8 == 1, "4色 exact 8手で一意", max8.ToString());
            var max5c = MaxCandidates(5, 14);
            Check(max5c == 1, "5色 exact 7手で一意", max5c.ToString());

            // 軸だけ判明している手 / 比較方法
            var r2 = TsumoIdentifier.Identify(4, PuyoColor.Parse("PP RP PB RG GR RG BG G"), MatchMode.Exact);
            Check(r2.Count >= 1 && r2.Seeds.Contains(21926), "最後の手が軸だけでも一致", $"count={r2.Count}");
            var r3 = TsumoIdentifier.Identify(4, PuyoColor.Parse("PP PR BP GR RG GR GB GG"), MatchMode.Unordered);
            Check(r3.Seeds.Contains(21926), "unordered: 軸子を入れ替えても一致", $"count={r3.Count}");
            // 色を R→Y, P→B のように付け替えても shape では一致する
            var r4 = TsumoIdentifier.Identify(4, PuyoColor.Parse("BB YB BP YG GY YG PG GG"), MatchMode.Shape);
            Check(r4.Seeds.Contains(21926), "shape: 色を付け替えても一致", $"count={r4.Count}");
            var r5 = TsumoIdentifier.Identify(4, PuyoColor.Parse("PP RP PB RG GR RG BG GY"), MatchMode.Exact);
            Check(r5.Count == 0, "一致しない入力は0件", $"count={r5.Count}");

            // 絞り込めないときは候補から無作為に選ぶ（偏りがないこと）
            var known4 = PuyoColor.Parse("PP RP PB RG");   // 候補2件（17527 と 21926）
            var hits = new Dictionary<int, int>();
            for (int i = 0; i < 2000; i++)
            {
                var src = TsumoSource.FromKnown(4, known4);
                hits.TryGetValue(src.Seed, out int c); hits[src.Seed] = c + 1;
            }
            Check(hits.Count == 2 && hits.Values.All(v => v > 850), "候補2件から無作為（偏りなし）",
                string.Join(", ", hits.Select(kv => $"No.{kv.Key + 1}:{kv.Value}")));
            var none = TsumoSource.FromKnown(4, null);
            Check(none.Candidates == 65536 && none.IsGuess && none.Seed >= 0, "何も分かっていなければ全パターンから");
            Check(TsumoSource.FromKnown(4, PuyoColor.Parse("PP RP PB RG GR RG BG GG")).Candidates == 1, "1件に決まれば確定");
            Check(TsumoSource.FromKnown(4, PuyoColor.Parse("PP RP PB RG GR RG BG GY")) == null, "一致なしは null");

            // 次の手の候補
            var r6 = TsumoIdentifier.Identify(4, PuyoColor.Parse("PP RP PB RG GR RG BG"), MatchMode.Exact);
            Check(r6.NextHands.Any(kv => kv.Key == "GG"), "次の手の候補に GG が含まれる",
                string.Join(", ", r6.NextHands.Select(kv => kv.Key + ":" + kv.Value)));
        }

        static int MaxCandidates(int mode, int len)
        {
            var counts = new Dictionary<string, int>();
            for (int seed = 0; seed < TsumoGen.SeedCount; seed++)
            {
                var t = TsumoGen.Generate(seed).Seq[mode];
                string k = Letters(t.Take(len).ToArray());
                counts.TryGetValue(k, out int c);
                counts[k] = c + 1;
            }
            return counts.Values.Max();
        }

        // ---------------- 連鎖 ----------------
        static void FieldChains()
        {
            Console.WriteLine("[連鎖]");
            // 1連鎖: 赤4個 → 10*4*1 = 40
            var f = Field.FromRows("RRRR..");
            var r = f.Settle();
            Check(r.Chains == 1 && r.Score == 40 && f.IsEmpty && r.AllClear, "赤4個で1連鎖 40点・全消し", $"chains={r.Chains} score={r.Score}");

            // 2連鎖: 青が消えて赤が落ちてつながる
            f = Field.FromRows(
                "R.....",
                "BBBB..",
                "RRRG..");
            r = f.Settle();
            Check(r.Chains == 2 && r.Score == 40 + 320, "2連鎖 40+320点", $"chains={r.Chains} score={r.Score}\n{f}");

            // 連結ボーナス（5個 → +2）と色数ボーナス（2色 → +3）
            f = Field.FromRows("RRRRR.", "GGGGY.");
            r = f.Settle();
            Check(r.Chains == 1 && r.Score == 10 * 9 * (2 + 3), "同時消し 5個+4個 2色 = 450点", $"score={r.Score}");

            // おじゃまは隣接して消える
            f = Field.FromRows("O.....", "RRRRO.");
            r = f.Settle();
            Check(f.IsEmpty, "隣接するおじゃまも消える", "\n" + f);

            // 13段目のぷよは消えない（12段目までの3個 + 13段目の1個）
            f = new Field();
            for (int y = 0; y < 10; y++) f[0, y] = (byte)(y % 2 == 0 ? PuyoColor.G : PuyoColor.B);
            for (int y = 0; y < 11; y++) f[1, y] = (byte)(y % 2 == 0 ? PuyoColor.B : PuyoColor.G);
            f[0, 10] = PuyoColor.R; f[0, 11] = PuyoColor.R; f[0, 12] = PuyoColor.R; f[1, 11] = PuyoColor.R;
            r = f.Settle();
            Check(r.Chains == 0, "13段目のぷよは連結に含めない", $"chains={r.Chains}");

            // 置く: 子が右、ちぎり
            f = Field.FromRows("R.....");
            f.Place(0, 1, PuyoColor.G, PuyoColor.B);
            Check(f[0, 1] == PuyoColor.G && f[1, 0] == PuyoColor.B, "子が右・ちぎり");

            // 14段目に置いたぷよは消える
            f = new Field();
            for (int y = 0; y < 13; y++) f[5, y] = (byte)(y % 2 == 0 ? PuyoColor.G : PuyoColor.B);
            f.Place(5, 0, PuyoColor.R, PuyoColor.Y);
            Check(f.Height(5) == 13, "14段目に来たぷよは消える", f.Height(5).ToString());

            // 19連鎖以上でもボーナス表の外に出ない
            Check(new Field().Settle().Chains == 0, "空の盤面");
        }

        // ---------------- 画像認識（samples/） ----------------
        static string SamplesDir()
        {
            for (var d = new System.IO.DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory); d != null; d = d.Parent)
            {
                var p = System.IO.Path.Combine(d.FullName, "samples");
                if (System.IO.Directory.Exists(p)) return p;
            }
            return null;
        }

        static void Recognition()
        {
            Console.WriteLine("[画像認識]");
            var dir = SamplesDir();
            if (dir == null) { Check(false, "samples/ が見つからない"); return; }
            FrameImage F(string n) => FrameImage.Load(System.IO.Path.Combine(dir, n));
            string P((byte axis, byte child)? p) => p == null ? "??" : $"{".RGBYPO"[p.Value.axis]}{".RGBYPO"[p.Value.child]}";

            var cases = new (string file, string wins, string n1p, string n2p, string n1q, string n2q, bool go)[]
            {
                ("start_go_stars.jpg",     "★ 0-1",               "BB", "BP", "BB", "BP", true),
                ("end_1p_lose_stars.jpg",  "★ 0-1",               "YB", "BP", "YB", "BP", false),
                ("play_stars_a.jpg",       "★ 0-0",               "YY", "RP", "RY", "PG", false),
                ("play_stars_b.jpg",       "★ 0-0",               "PR", "BP", "RY", "RP", false),
                ("play_numbers_sets.jpg",  "数字 0-0 Set 0-0",    "BR", "BG", "BR", "BG", false),
                ("play_numbers_chain.jpg", "数字 0-1",            "RB", "YR", "RP", "PY", false),
                ("play_numbers_sets_video.jpg", "数字 0-0 Set 0-0", "BR", "BG", "BR", "BG", false),
            };
            // 解析用の切り出し映像（AnalysisCrop）でも同じに読めること
            FrameImage Half(string n) => F(n).Crop(PuyoLayout.AnalysisCrop);
            foreach (var c in cases.Select(x => (x, half: false)).Concat(cases.Select(x => (x, half: true))).Select(t => (t.x, t.half)))
            {
                var f = c.half ? Half(c.x.file) : F(c.x.file);
                var c0 = c; var cc = c.x;
                string tag = c.half ? " [切り出し]" : "";
                Check(Recognizer.ReadWins(f).ToString() == cc.wins, $"{cc.file}{tag} 勝利数 {cc.wins}", Recognizer.ReadWins(f).ToString());
                var a2 = Recognizer.ReadNext(f, 0); var b2 = Recognizer.ReadNext(f, 1);
                string got2 = $"{P(a2.next1)} {P(a2.next2)} {P(b2.next1)} {P(b2.next2)}";
                Check(got2 == $"{cc.n1p} {cc.n2p} {cc.n1q} {cc.n2q}", $"{cc.file}{tag} NEXT", got2);
                Check(Recognizer.IsGo(f) == cc.go, $"{cc.file}{tag} ゴー判定 {cc.go}", Recognizer.GoRatio(f).ToString("P1"));
            }
            foreach (var c in new (string file, string wins, string n1p, string n2p, string n1q, string n2q, bool go)[0])
            {
                var f = F(c.file);
                Check(Recognizer.ReadWins(f).ToString() == c.wins, $"{c.file} 勝利数 {c.wins}", Recognizer.ReadWins(f).ToString());
                var a = Recognizer.ReadNext(f, 0); var b = Recognizer.ReadNext(f, 1);
                string got = $"{P(a.next1)} {P(a.next2)} {P(b.next1)} {P(b.next2)}";
                Check(got == $"{c.n1p} {c.n2p} {c.n1q} {c.n2q}", $"{c.file} NEXT", got);
                Check(Recognizer.IsGo(f) == c.go, $"{c.file} ゴー判定 {c.go}", Recognizer.GoRatio(f).ToString("P1"));
            }
            // 盤面（下の数段だけ。上の方は演出で崩れることがある）
            var fa = Recognizer.ReadField(F("play_stars_a.jpg"), 0);
            var rows = fa.ToString().Split('\n');
            Check(rows[rows.Length - 1] == "RRPPYG" && rows[rows.Length - 2] == "GYPOYO" && rows[rows.Length - 3] == "GYY.Y.",
                "play_stars_a 1P 盤面の下3段", string.Join("|", rows.Skip(Math.Max(0, rows.Length - 3))));
            var fs = Recognizer.ReadField(F("play_numbers_sets.jpg"), 1);
            Check(fs.ToString() == "R.....\nG.BBYR\nGRRYBY\nBBBYBY", "play_numbers_sets 2P 盤面", fs.ToString().Replace("\n", "|"));
            // 名前（Windows の文字認識。使えない環境では飛ばす）
            if (NameOcr.Available)
            {
                foreach (var n in new[] { "start_go_stars.jpg", "play_numbers_sets.jpg" })
                {
                    var img = F(n).Crop(PuyoLayout.AnalysisCrop);
                    string a = NameOcr.ReadName(img, 0).Result, b = NameOcr.ReadName(img, 1).Result;
                    // 「1」と「I」は区別しきれないので COMI も可
                    Check(a == "shiokara" && (b == "COM1" || b == "COMI"), $"{n} 名前 shiokara / COM1", $"{a} / {b}");
                }
            }
            if (NameOcr.Available)
            {
                // ひらがな・漢字の名前（名前欄に書き込んだ合成画像）
                var jp = F("synthetic_names_jp.png");
                string a = NameOcr.ReadName(jp, 0).Result, b = NameOcr.ReadName(jp, 1).Result;
                Check(a == "しおから" && b == "塩辛太郎", "日本語の名前 しおから / 塩辛太郎", $"{a} / {b}");
            }
            else Console.WriteLine("  （文字認識が使えないため名前のテストは省略）");
            // 実際の解析用の映像（ffmpeg が切り出した 1520×880）
            {
                var raw = F("analysis_frame_sets.png");
                var crop = PuyoLayout.AnalysisCrop;
                var af = new FrameImage(raw.Px, raw.W, raw.H, raw.Stride, crop.X, crop.Y, 1f);
                Check(Recognizer.ReadWins(af).ToString() == "数字 0-0 Set 0-0", "解析用の映像 勝利数とセット数", Recognizer.ReadWins(af).ToString());
            }
            var go = F("start_go_stars.jpg");
            Check(Recognizer.ReadField(go, 0).IsEmpty && Recognizer.ReadField(go, 1).IsEmpty, "ゴーの画面は盤面が空");
        }

        // ---------------- 対戦の検知（画像の並びを流す） ----------------
        static void MatchDetection()
        {
            Console.WriteLine("[対戦の検知]");
            var dir = SamplesDir();
            if (dir == null) return;
            FrameImage C(string n) => FrameImage.Load(System.IO.Path.Combine(dir, n)).Crop(PuyoLayout.AnalysisCrop);
            var go = C("synthetic_go_numbers.jpg");
            var play = C("play_numbers_sets.jpg");
            var won = C("play_numbers_chain.jpg");      // 2P の勝利数が 1 になった画面
            var menu = C("menu.jpg");

            var a = new TimeshiftViewer.MatchAnalyzer();
            double t = 0;
            void Feed(FrameImage f, int n) { for (int i = 0; i < n; i++) { a.Process(f, t); t += 0.1; } }
            Feed(menu, 10);
            Feed(go, 5);
            Feed(play, 40);
            Feed(won, 10);
            Feed(menu, 20);
            Check(a.Matches.Count == 1, "1試合を検知", a.Matches.Count.ToString());
            if (a.Matches.Count == 1)
            {
                var m = a.Matches[0];
                Check(Math.Abs(m.Start - 1.0) < 0.15, "開始時刻（ゴー）", m.Start.ToString("F2"));
                Check(m.End > 5.4 && m.End < 5.8, "終了時刻（勝利数が増えて3枚後）", m.End.ToString("F2"));
                Check(m.Winner == 1, "勝者は 2P", m.Winner.ToString());
                Check(m.WinsStart[0] == 0 && m.WinsStart[1] == 0, "開始時の勝利数 0-0");
                Check(m.Sets[0] == 0 && m.Sets[1] == 0, "セット数 0-0", $"{m.Sets[0]}-{m.Sets[1]}");
                Check(m.Seq.Count >= 2 && m.Tsumo != null, "NEXT からツモを読んで仮決め",
                    $"手数={m.Seq.Count} 候補={m.Tsumo?.Candidates}");
            }
            // 続けて次の試合（終了なしでゴーが来たら前の試合を閉じる）
            Feed(go, 5); Feed(play, 20); Feed(go, 5);
            Check(a.Matches.Count == 2 || a.Matches.Count == 3, "次のゴーで新しい試合", a.Matches.Count.ToString());
        }

        // ---------------- 履歴（分岐） ----------------
        static void SimTree()
        {
            Console.WriteLine("[履歴]");
            var s = new SimState();
            s.SetTsumo(TsumoSource.FromSeed(21926, 4));
            Check(s.Current.TsumoIndex == 0, "最初は1手目");
            s.Place(0, 0);             // PP
            s.Place(1, 0);             // RP
            Check(s.Current.TsumoIndex == 2, "2手置いたら3手目");
            s.Undo();
            Check(s.Current.TsumoIndex == 1 && s.CanRedo, "元に戻す");
            s.Place(3, 1);             // 別の置き方 → 分岐
            Check(s.Current.Parent.Children.Count == 2, "分岐ができる");
            s.Undo(); s.Redo();
            Check(s.Current.TsumoIndex == 2, "やり直し（最後に選んだ分岐）");
        }
    }
}
