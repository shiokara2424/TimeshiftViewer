using System;
using System.Collections.Generic;
using System.Linq;

namespace TimeshiftViewer.Puyo
{
    // ------------------------------------------------------------
    //  盤面（通ルール）
    //  横6 × 縦14。y=0 が一番下。
    //  y=0〜11: 見えている12段 / y=12: 13段目（見えない。連鎖では消えない）/ y=13: 14段目（置いたぷよは消える）
    // ------------------------------------------------------------
    sealed class Field
    {
        public const int W = 6, H = 14;
        public const int Visible = 12;      // 連鎖で消える範囲（y < 12）
        public const int Row13 = 12;
        public const int DeathX = 2, DeathY = 11;   // 3列目・12段目が埋まると負け

        public readonly byte[,] C = new byte[W, H];

        public Field Clone()
        {
            var f = new Field();
            Array.Copy(C, f.C, C.Length);
            return f;
        }

        public byte this[int x, int y]
        {
            get => C[x, y];
            set => C[x, y] = value;
        }

        public bool IsEmpty
        {
            get { foreach (var c in C) if (c != 0) return false; return true; }
        }

        public bool IsDead => C[DeathX, DeathY] != 0;

        /// <summary>その列で次にぷよが入る段（0〜13。14なら満杯）</summary>
        public int Height(int x)
        {
            int y = 0;
            while (y < H && C[x, y] != 0) y++;
            return y;
        }

        public bool SameAs(Field o)
        {
            for (int x = 0; x < W; x++) for (int y = 0; y < H; y++) if (C[x, y] != o.C[x, y]) return false;
            return true;
        }

        // ---------------- 組ぷよの置き方 ----------------
        // rot: 0=子が上 1=子が右 2=子が下 3=子が左（軸が x 列）
        public static int ChildDx(int rot) => rot == 1 ? 1 : rot == 3 ? -1 : 0;

        public static bool ValidPos(int x, int rot)
        {
            int cx = x + ChildDx(rot);
            return x >= 0 && x < W && cx >= 0 && cx < W;
        }

        /// <summary>
        /// 組ぷよを置く（ちぎりも含めてそれぞれ下まで落とす）。14段目より上に来たぷよは消える。
        /// 置けない位置なら false。
        /// </summary>
        public bool Place(int x, int rot, byte axis, byte child)
        {
            if (!ValidPos(x, rot)) return false;
            int cx = x + ChildDx(rot);
            if (rot == 0) { Drop(x, axis); Drop(x, child); }
            else if (rot == 2) { Drop(x, child); Drop(x, axis); }
            else { Drop(x, axis); Drop(cx, child); }
            return true;
        }

        void Drop(int x, byte c)
        {
            int y = Height(x);
            if (y < H - 1) C[x, y] = c;   // 14段目（y=13）以上に来たぷよは消える
        }

        /// <summary>置いたときの着地位置（表示用）。y が 13 以上なら消える。</summary>
        public void LandingRows(int x, int rot, out int axisY, out int childY)
        {
            int cx = x + ChildDx(rot);
            if (rot == 0) { axisY = Height(x); childY = axisY + 1; }
            else if (rot == 2) { childY = Height(x); axisY = childY + 1; }
            else { axisY = Height(x); childY = Height(cx); }
        }

        // ---------------- 重力 ----------------
        public bool ApplyGravity()
        {
            bool moved = false;
            for (int x = 0; x < W; x++)
            {
                int w = 0;
                for (int y = 0; y < H; y++)
                {
                    byte c = C[x, y];
                    if (c == 0) continue;
                    if (w != y) { C[x, w] = c; C[x, y] = 0; moved = true; }
                    w++;
                }
            }
            return moved;
        }

        // ---------------- 連鎖 ----------------
        static readonly int[] ChainPower = { 0, 8, 16, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448, 480, 512 };
        static readonly int[] ColorBonus = { 0, 0, 3, 6, 12, 24 };
        static int ConnBonus(int n) => n <= 4 ? 0 : n >= 11 ? 10 : n - 3;

        /// <summary>
        /// 落下と連鎖を最後まで進める。各連鎖の途中経過（消える直前の盤面と消えるぷよ）を返す。
        /// </summary>
        public ChainResult Settle()
        {
            var res = new ChainResult();
            ApplyGravity();
            while (true)
            {
                var groups = FindGroups();
                if (groups.Count == 0) break;

                int chain = res.Steps.Count + 1;
                var popped = new bool[W, H];
                int count = 0, conn = 0;
                var colors = new HashSet<byte>();
                foreach (var g in groups)
                {
                    count += g.Count;
                    conn += ConnBonus(g.Count);
                    colors.Add(C[g[0].x, g[0].y]);
                    foreach (var (x, y) in g) popped[x, y] = true;
                }
                // 消えるぷよに隣接するおじゃま（見えている範囲のみ）
                for (int x = 0; x < W; x++)
                    for (int y = 0; y < Visible; y++)
                    {
                        if (!popped[x, y] || C[x, y] == PuyoColor.Ojama) continue;
                        foreach (var (nx, ny) in Neighbors(x, y))
                            if (ny < Visible && C[nx, ny] == PuyoColor.Ojama) popped[nx, ny] = true;
                    }

                int power = ChainPower[Math.Min(chain, ChainPower.Length) - 1];
                int bonus = power + conn + ColorBonus[colors.Count];
                bonus = Math.Max(1, Math.Min(999, bonus));
                int score = 10 * count * bonus;

                var step = new ChainStep
                {
                    Chain = chain, Before = Clone(), Popped = popped, Count = count,
                    Colors = colors.Count, Bonus = bonus, Score = score,
                };
                res.Steps.Add(step);
                res.Score += score;

                for (int x = 0; x < W; x++) for (int y = 0; y < H; y++) if (popped[x, y]) C[x, y] = 0;
                ApplyGravity();
            }
            res.AllClear = res.Steps.Count > 0 && IsEmpty;
            return res;
        }

        IEnumerable<(int x, int y)> Neighbors(int x, int y)
        {
            if (x > 0) yield return (x - 1, y);
            if (x < W - 1) yield return (x + 1, y);
            if (y > 0) yield return (x, y - 1);
            if (y < H - 1) yield return (x, y + 1);
        }

        /// <summary>4個以上つながった色ぷよのかたまり（13段目以上は対象外）</summary>
        public List<List<(int x, int y)>> FindGroups(int min = 4)
        {
            var seen = new bool[W, H];
            var result = new List<List<(int x, int y)>>();
            for (int x = 0; x < W; x++)
                for (int y = 0; y < Visible; y++)
                {
                    byte c = C[x, y];
                    if (seen[x, y] || c == 0 || c == PuyoColor.Ojama) continue;
                    var g = new List<(int x, int y)>();
                    var st = new Stack<(int x, int y)>();
                    st.Push((x, y)); seen[x, y] = true;
                    while (st.Count > 0)
                    {
                        var p = st.Pop();
                        g.Add(p);
                        foreach (var n in Neighbors(p.x, p.y))
                        {
                            if (n.y >= Visible || seen[n.x, n.y] || C[n.x, n.y] != c) continue;
                            seen[n.x, n.y] = true;
                            st.Push(n);
                        }
                    }
                    if (g.Count >= min) result.Add(g);
                }
            return result;
        }

        // ---------------- 文字列（テスト・保存用） ----------------
        // 上の段から1行6文字。. = 空、R G B Y P = 色、O = おじゃま
        const string CellChars = ".RGBYPO";

        public static Field FromRows(params string[] rowsTopToBottom)
        {
            var f = new Field();
            int n = rowsTopToBottom.Length;
            for (int i = 0; i < n; i++)
            {
                int y = n - 1 - i;
                string r = rowsTopToBottom[i];
                for (int x = 0; x < W && x < r.Length; x++)
                {
                    int c = CellChars.IndexOf(char.ToUpperInvariant(r[x]));
                    f.C[x, y] = (byte)Math.Max(0, c);
                }
            }
            return f;
        }

        public override string ToString()
        {
            var lines = new List<string>();
            for (int y = H - 1; y >= 0; y--)
            {
                var s = new char[W];
                for (int x = 0; x < W; x++) s[x] = CellChars[C[x, y]];
                lines.Add(new string(s));
            }
            return string.Join("\n", lines.SkipWhile(l => l == "......"));
        }
    }

    sealed class ChainStep
    {
        public int Chain;          // 何連鎖目か
        public Field Before;       // 消える直前の盤面
        public bool[,] Popped;     // 消えるぷよ
        public int Count;          // 消えた色ぷよの数
        public int Colors;         // 同時に消えた色数
        public int Bonus;          // 連鎖・連結・色数ボーナスの合計
        public int Score;
    }

    sealed class ChainResult
    {
        public List<ChainStep> Steps = new List<ChainStep>();
        public int Score;
        public bool AllClear;
        public int Chains => Steps.Count;
        public int Ojama => Score / 70;
    }
}
