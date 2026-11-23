using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace TimeshiftViewer.Puyo
{
    // ------------------------------------------------------------
    //  色
    //  ツモ列の中では 0〜4（赤 緑 青 黄 紫）、盤面では 1〜5 と 6（おじゃま）を使う
    // ------------------------------------------------------------
    static class PuyoColor
    {
        public const byte None = 0, R = 1, G = 2, B = 3, Y = 4, P = 5, Ojama = 6;
        public const string Letters = "RGBYP";                 // ツモ列の 0〜4
        public static readonly string[] Names = { "赤", "緑", "青", "黄", "紫" };

        /// <summary>ツモ列の色(0〜4) → 盤面の色(1〜5)</summary>
        public static byte ToCell(byte t) => (byte)(t + 1);

        /// <summary>文字 → ツモ列の色(0〜4)。該当なしは -1</summary>
        public static int FromLetter(char c) => Letters.IndexOf(char.ToUpperInvariant(c));

        /// <summary>"RG BB p y" のような文字列 → ツモ列の色の並び（RGBYP 以外は無視）</summary>
        public static List<byte> Parse(string s)
        {
            var list = new List<byte>();
            foreach (char c in s ?? "")
            {
                int i = FromLetter(c);
                if (i >= 0) list.Add((byte)i);
            }
            return list;
        }

        /// <summary>ツモ列の色の並び → "RG BB P" のような文字列</summary>
        public static string Format(IList<byte> seq, int start = 0, int count = -1)
        {
            if (count < 0) count = seq.Count - start;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < count; i++)
            {
                if (i > 0 && i % 2 == 0) sb.Append(' ');
                sb.Append(Letters[seq[start + i]]);
            }
            return sb.ToString();
        }
    }

    // ------------------------------------------------------------
    //  ツモ生成（ぷよぷよeスポーツ 対戦モード）
    //  仕様: puyo_tsumo_spec.md
    // ------------------------------------------------------------
    sealed class TsumoPattern
    {
        public int Seed;               // 0〜65535（外部ツールの No. は Seed + 1）
        public byte[] Colors;          // 色決めの結果（先頭から3色・4色・5色で使う）
        public byte[][] Seq;           // Seq[3] Seq[4] Seq[5]: 長さ256（軸,子,軸,子…）

        public int No => Seed + 1;

        /// <summary>k 手目（0始まり、128手でループ）の (軸, 子)</summary>
        public (byte axis, byte child) Hand(int mode, int k)
        {
            int i = ((k % 128) + 128) % 128 * 2;
            return (Seq[mode][i], Seq[mode][i + 1]);
        }
    }

    static class TsumoGen
    {
        public const int SeedCount = 65536;
        public const int Length = 256;

        public static TsumoPattern Generate(int seed)
        {
            uint x = (uint)seed;
            uint Next() { x = unchecked(x * 0x5D588B65u + 0x269EC3u); return x >> 16; }

            // 色決め（乱数を5回消費）
            var bag = new List<byte> { 0, 1, 2, 3, 4 };
            var colors = new byte[5];
            for (int i = 0; i < 5; i++)
            {
                int j = (int)(Next() * (uint)(5 - i) / 65536u);
                colors[i] = bag[j];
                bag.RemoveAt(j);
            }

            // 並べ替え（3色 → 4色 → 5色の順に）
            var seq = new byte[6][];
            var a = new byte[Length];
            foreach (int n in new[] { 3, 4, 5 })
            {
                for (int i = 0; i < Length; i++) a[i] = (byte)(i % n);
                foreach (int B in new[] { 16, 32, 64 })
                {
                    int s = 16 - Log2(B);
                    for (int b = 0; b < Length / B - 1; b++)
                    {
                        for (int k = 0; k < B / 2; k++)
                        {
                            int p = (int)(Next() >> s) + b * B;
                            int q = (int)(Next() >> s) + (b + 1) * B;
                            byte t = a[p]; a[p] = a[q]; a[q] = t;
                        }
                    }
                }
                var outSeq = new byte[Length];
                for (int i = 0; i < Length; i++) outSeq[i] = colors[a[i]];
                seq[n] = outSeq;
            }

            // 開幕2手は3色ツモで上書き
            for (int i = 0; i < 4; i++) { seq[4][i] = seq[3][i]; seq[5][i] = seq[3][i]; }

            return new TsumoPattern { Seed = seed, Colors = colors, Seq = seq };
        }

        static int Log2(int v) { int r = 0; while ((v >>= 1) != 0) r++; return r; }
    }

    // ------------------------------------------------------------
    //  ツモ識別
    // ------------------------------------------------------------
    enum MatchMode
    {
        Exact,      // 色・軸子の順番まで完全一致
        Unordered,  // 1手の中の軸と子の順番を区別しない
        Shape,      // 色の名前は無視し、同じ色・違う色の並びだけ比べる
    }

    sealed class IdentifyResult
    {
        public int Count;                                   // 候補数
        public List<int> Seeds = new List<int>();           // 候補のシード値（最大 MaxList 件）
        public List<KeyValuePair<string, int>> NextHands =  // 次の手の候補と出現数（多い順）
            new List<KeyValuePair<string, int>>();
        public int NextHandIndex;                           // 「次の手」が何手目か（0始まり）
        public int RandomSeed = -1;                         // 候補全体から無作為に1つ選んだシード値（候補0件なら -1）
    }

    static class TsumoIdentifier
    {
        public const int TableHands = 16;                   // 表に持つ先頭の手数
        const int TableLen = TableHands * 2;
        public const int MaxList = 500;

        static byte[][] table;                              // table[mode][seed * TableLen + i]
        static Task buildTask;
        static readonly object gate = new object();

        /// <summary>先頭 16 手の表を作る（初回のみ。数百ms 程度）</summary>
        public static Task EnsureTableAsync()
        {
            lock (gate)
            {
                if (buildTask == null) buildTask = Task.Run(() => BuildTable());
                return buildTask;
            }
        }

        public static bool TableReady => table != null;

        static readonly Random rng = new Random();
        static int NextRandom(int n) { lock (rng) return rng.Next(n); }

        /// <summary>全パターンから無作為に1つ（ツモが何も分かっていないとき）</summary>
        public static int RandomSeedAll() => NextRandom(TsumoGen.SeedCount);

        static void BuildTable()
        {
            var t = new byte[6][];
            foreach (int m in new[] { 3, 4, 5 }) t[m] = new byte[TsumoGen.SeedCount * TableLen];
            Parallel.For(0, TsumoGen.SeedCount, seed =>
            {
                var p = TsumoGen.Generate(seed);
                foreach (int m in new[] { 3, 4, 5 })
                    Buffer.BlockCopy(p.Seq[m], 0, t[m], seed * TableLen, TableLen);
            });
            table = t;
        }

        /// <summary>
        /// 開幕からの色の並び（軸→子の順。最後は軸だけでもよい）に一致するパターンを探す。
        /// 表ができていない場合は作るまで待つ。
        /// </summary>
        public static IdentifyResult Identify(int mode, IList<byte> known, MatchMode match)
        {
            EnsureTableAsync().Wait();
            var res = new IdentifyResult();
            int n = known.Count;
            int nextHand = n / 2;                             // 軸だけ判明している手は、その手を「次」とする
            res.NextHandIndex = nextHand;

            var tab = table[mode];
            var counts = new Dictionary<string, int>();
            int checkLen = Math.Min(n, TableLen);
            var normA = Normalize(known, n);

            for (int seed = 0; seed < TsumoGen.SeedCount; seed++)
            {
                int off = seed * TableLen;
                if (!Matches(tab, off, known, checkLen, match, normA)) continue;

                // 表に入りきらない長さは、生成し直して確認する（候補はこの時点でごく少ない）
                byte[] full = null;
                if (n > TableLen || (nextHand + 1) * 2 > TableLen)
                {
                    full = TsumoGen.Generate(seed).Seq[mode];
                    if (n > TableLen && !MatchesFull(full, known, match, normA)) continue;
                }

                res.Count++;
                if (res.Seeds.Count < MaxList) res.Seeds.Add(seed);
                // 候補全体から等確率で1つ選ぶ（リザーバサンプリング）
                if (NextRandom(res.Count) == 0) res.RandomSeed = seed;

                // 次の手
                int ni = nextHand * 2 % TsumoGen.Length;
                byte ax, ch;
                if (full != null) { ax = full[ni]; ch = full[ni + 1]; }
                else { ax = tab[off + ni]; ch = tab[off + ni + 1]; }
                string key = HandKey(ax, ch, match, known, n);
                counts.TryGetValue(key, out int c);
                counts[key] = c + 1;
            }
            res.NextHands = counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).ToList();
            return res;
        }

        // 色を初めて出てきた順に 0,1,2… と振り直す
        static byte[] Normalize(IList<byte> s, int n)
        {
            var map = new int[5] { -1, -1, -1, -1, -1 };
            int next = 0;
            var r = new byte[n];
            for (int i = 0; i < n; i++)
            {
                if (map[s[i]] < 0) map[s[i]] = next++;
                r[i] = (byte)map[s[i]];
            }
            return r;
        }

        static bool Matches(byte[] tab, int off, IList<byte> known, int len, MatchMode match, byte[] normKnown)
        {
            switch (match)
            {
                case MatchMode.Exact:
                    for (int i = 0; i < len; i++) if (tab[off + i] != known[i]) return false;
                    return true;
                case MatchMode.Unordered:
                    for (int i = 0; i < len; i += 2)
                    {
                        byte a = tab[off + i], b = tab[off + i + 1];
                        if (i + 1 < len)
                        {
                            byte x = known[i], y = known[i + 1];
                            if (!((a == x && b == y) || (a == y && b == x))) return false;
                        }
                        else
                        {
                            // 軸だけ判明: どちらかと一致すればよい
                            if (a != known[i] && b != known[i]) return false;
                        }
                    }
                    return true;
                default: // Shape
                    {
                        Span5 map = default; map.Init();
                        int next = 0;
                        for (int i = 0; i < len; i++)
                        {
                            byte c = tab[off + i];
                            int v = map.Get(c);
                            if (v < 0) { v = next++; map.Set(c, v); }
                            if (v != normKnown[i]) return false;
                        }
                        return true;
                    }
            }
        }

        static bool MatchesFull(byte[] full, IList<byte> known, MatchMode match, byte[] normKnown)
        {
            var tmp = new byte[known.Count];
            for (int i = 0; i < known.Count; i++) tmp[i] = full[i % TsumoGen.Length];
            return Matches(tmp, 0, known, known.Count, match, normKnown);
        }

        // 次の手の表示用キー
        static string HandKey(byte ax, byte ch, MatchMode match, IList<byte> known, int n)
        {
            string L = PuyoColor.Letters;
            switch (match)
            {
                case MatchMode.Unordered:
                    return ax <= ch ? $"{L[ax]}{L[ch]}" : $"{L[ch]}{L[ax]}";
                case MatchMode.Shape:
                    {
                        // 既知の色の対応で書き直し、未出の色は ? にする
                        // （候補ごとに色の割り当てが違うので、ここでは同じ色か違う色かだけ示す）
                        return ax == ch ? "同色" : "別色";
                    }
                default:
                    return $"{L[ax]}{L[ch]}";
            }
        }

        // 5要素の小さな対応表（割り当てなしで使う）
        struct Span5
        {
            int a0, a1, a2, a3, a4;
            public void Init() { a0 = a1 = a2 = a3 = a4 = -1; }
            public int Get(int i) => i == 0 ? a0 : i == 1 ? a1 : i == 2 ? a2 : i == 3 ? a3 : a4;
            public void Set(int i, int v) { if (i == 0) a0 = v; else if (i == 1) a1 = v; else if (i == 2) a2 = v; else if (i == 3) a3 = v; else a4 = v; }
        }
    }
}
