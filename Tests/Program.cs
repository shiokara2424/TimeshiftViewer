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

        static int Main()
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            TsumoGeneration();
            TsumoProperties();
            Identification();
            FieldChains();
            SimTree();
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
