using System;
using System.Collections.Generic;
using System.Linq;

namespace TimeshiftViewer.Puyo
{
    // ------------------------------------------------------------
    //  ツモの出どころ（シード値から生成 / 手入力）
    // ------------------------------------------------------------
    sealed class TsumoSource
    {
        public readonly List<(byte axis, byte child)> Pairs = new List<(byte, byte)>();   // 色は 0〜4
        public int Seed = -1;          // シード値から作った場合
        public int Mode;               // 色数（シード値から作った場合）
        public bool Loop;              // 128手でループする
        public int Candidates = 1;     // 選んだときの候補数（1 なら確定。2 以上なら候補から無作為に選んだ仮のツモ）
        public int KnownHands;         // 識別に使った（判明していた）手数

        public static TsumoSource FromSeed(int seed, int mode)
        {
            var p = TsumoGen.Generate(seed);
            var s = new TsumoSource { Seed = seed, Mode = mode, Loop = true };
            for (int k = 0; k < 128; k++) s.Pairs.Add(p.Hand(mode, k));
            return s;
        }

        /// <summary>
        /// 判明している開幕からの並びでパターンを決める。1件に絞れなければ候補から無作為に選ぶ
        /// （何も分かっていなければ全 65536 通りから）。一致する候補がなければ null。
        /// </summary>
        public static TsumoSource FromKnown(int mode, IList<byte> known, MatchMode match = MatchMode.Exact)
        {
            int seed, count;
            if (known == null || known.Count == 0) { seed = TsumoIdentifier.RandomSeedAll(); count = TsumoGen.SeedCount; }
            else
            {
                var r = TsumoIdentifier.Identify(mode, known, match);
                if (r.Count == 0) return null;
                seed = r.RandomSeed; count = r.Count;
            }
            var s = FromSeed(seed, mode);
            s.Candidates = count;
            s.KnownHands = known?.Count / 2 ?? 0;
            return s;
        }

        public bool IsGuess => Seed >= 0 && Candidates > 1;

        /// <summary>"RG BB YP" のような文字列から（奇数個なら最後の1個は捨てる）</summary>
        public static TsumoSource FromString(string text)
        {
            var seq = PuyoColor.Parse(text);
            var s = new TsumoSource();
            for (int i = 0; i + 1 < seq.Count; i += 2) s.Pairs.Add((seq[i], seq[i + 1]));
            return s;
        }

        public bool TryGet(int k, out (byte axis, byte child) pair)
        {
            pair = default;
            if (k < 0 || Pairs.Count == 0) return false;
            if (Loop) { pair = Pairs[k % Pairs.Count]; return true; }
            if (k >= Pairs.Count) return false;
            pair = Pairs[k];
            return true;
        }

        public string Label =>
            Seed >= 0 ? (IsGuess ? $"No.{Seed + 1}（{Mode}色・仮）\n候補 {Candidates:N0} 件から無作為" : $"No.{Seed + 1}（{Mode}色）")
            : Pairs.Count > 0 ? $"手入力 {Pairs.Count}手" : "ツモなし";

        public string ToText()
        {
            var flat = new List<byte>();
            foreach (var (a, c) in Pairs) { flat.Add(a); flat.Add(c); }
            return PuyoColor.Format(flat);
        }
    }

    // ------------------------------------------------------------
    //  履歴の1局面
    // ------------------------------------------------------------
    sealed class SimNode
    {
        static int nextId;
        public readonly int Id = ++nextId;
        public Field Field;                 // この局面の盤面（連鎖が終わった後）
        public int TsumoIndex;              // 次に置くのは何手目か（0始まり）
        public SimNode Parent;
        public readonly List<SimNode> Children = new List<SimNode>();
        public SimNode LastVisited;         // やり直し用: 最後に選んだ子
        public ChainResult Chain;           // この局面に来るときに起きた連鎖
        public string Label = "";
        public (int x, int rot, byte axis, byte child)? Move;   // 置いた手
        public int TotalScore;              // ここまでの得点の合計

        public int Depth { get { int d = 0; for (var n = Parent; n != null; n = n.Parent) d++; return d; } }
    }

    // ------------------------------------------------------------
    //  シミュレーターの状態（画面とは独立）
    // ------------------------------------------------------------
    sealed class SimState
    {
        public SimNode Root { get; private set; }
        public SimNode Current { get; private set; }
        public TsumoSource Tsumo { get; private set; } = TsumoSource.FromKnown(4, null);   // 最初は全パターンから無作為（仮）
        public event Action Changed;

        public SimState() { Reset(new Field(), 0); }

        void Fire() => Changed?.Invoke();

        public void Reset(Field f, int tsumoIndex, string label = "開始")
        {
            Root = new SimNode { Field = f.Clone(), TsumoIndex = tsumoIndex, Label = label };
            Current = Root;
            Fire();
        }

        public void SetTsumo(TsumoSource src)
        {
            Tsumo = src ?? new TsumoSource();
            Fire();
        }

        public bool TryCurrentPair(out (byte axis, byte child) pair) => Tsumo.TryGet(Current.TsumoIndex, out pair);
        public bool TryPair(int offset, out (byte axis, byte child) pair) => Tsumo.TryGet(Current.TsumoIndex + offset, out pair);

        /// <summary>今のツモを置いて連鎖まで進める。新しい局面（または同じ手の既存の局面）を返す。</summary>
        public SimNode Place(int x, int rot)
        {
            if (Current.Field.IsDead) return null;
            if (!TryCurrentPair(out var pair)) return null;
            if (!Field.ValidPos(x, rot)) return null;

            // 同じ手を既に試していればそこへ移動
            foreach (var ch in Current.Children)
                if (ch.Move.HasValue && ch.Move.Value.x == x && ch.Move.Value.rot == rot &&
                    ch.Move.Value.axis == pair.axis && ch.Move.Value.child == pair.child)
                    return GoTo(ch);

            var f = Current.Field.Clone();
            f.Place(x, rot, PuyoColor.ToCell(pair.axis), PuyoColor.ToCell(pair.child));
            var chain = f.Settle();
            var node = new SimNode
            {
                Field = f, TsumoIndex = Current.TsumoIndex + 1, Parent = Current, Chain = chain,
                Move = (x, rot, pair.axis, pair.child),
                TotalScore = Current.TotalScore + chain.Score,
                Label = MoveLabel(Current.TsumoIndex, x, rot, pair, chain, f),
            };
            Current.Children.Add(node);
            return GoTo(node);
        }

        static string MoveLabel(int k, int x, int rot, (byte axis, byte child) p, ChainResult c, Field f)
        {
            string L = PuyoColor.Letters;
            string dir = rot == 0 ? "↑" : rot == 1 ? "→" : rot == 2 ? "↓" : "←";
            string s = $"{k + 1}手目 {L[p.axis]}{L[p.child]} {x + 1}列{dir}";
            if (c.Chains > 0) s += $"  {c.Chains}連鎖 {c.Score}点";
            if (c.AllClear) s += " 全消し";
            if (f.IsDead) s += " ×";
            return s;
        }

        /// <summary>盤面やツモ位置を手で変えた局面を追加する</summary>
        public SimNode AddEdit(Field f, int tsumoIndex, string label)
        {
            if (f.SameAs(Current.Field) && tsumoIndex == Current.TsumoIndex) return Current;
            var node = new SimNode
            {
                Field = f.Clone(), TsumoIndex = tsumoIndex, Parent = Current, Label = label,
                TotalScore = Current.TotalScore,
            };
            Current.Children.Add(node);
            return GoTo(node);
        }

        /// <summary>今の盤面のまま落下・連鎖させる（編集で浮いたぷよや、消える形を作ったとき）</summary>
        public SimNode RunChain()
        {
            var f = Current.Field.Clone();
            var chain = f.Settle();
            if (chain.Chains == 0 && f.SameAs(Current.Field)) return null;
            var node = new SimNode
            {
                Field = f, TsumoIndex = Current.TsumoIndex, Parent = Current, Chain = chain,
                TotalScore = Current.TotalScore + chain.Score,
                Label = chain.Chains > 0 ? $"連鎖 {chain.Chains}連鎖 {chain.Score}点" + (chain.AllClear ? " 全消し" : "") : "落下",
            };
            Current.Children.Add(node);
            return GoTo(node);
        }

        public SimNode GoTo(SimNode n)
        {
            if (n == null) return null;
            for (var c = n; c.Parent != null; c = c.Parent) c.Parent.LastVisited = c;
            Current = n;
            Fire();
            return n;
        }

        public bool CanUndo => Current.Parent != null;
        public bool CanRedo => Current.Children.Count > 0;

        public void Undo() { if (CanUndo) GoTo(Current.Parent); }

        public void Redo()
        {
            if (!CanRedo) return;
            GoTo(Current.LastVisited != null && Current.Children.Contains(Current.LastVisited) ? Current.LastVisited : Current.Children[Current.Children.Count - 1]);
        }

        /// <summary>局面を削除（その下の分岐ごと）。根は消せない。</summary>
        public void Delete(SimNode n)
        {
            if (n?.Parent == null) return;
            bool inside = false;
            for (var c = Current; c != null; c = c.Parent) if (c == n) { inside = true; break; }
            n.Parent.Children.Remove(n);
            if (n.Parent.LastVisited == n) n.Parent.LastVisited = null;
            if (inside) GoTo(n.Parent); else Fire();
        }
    }
}
