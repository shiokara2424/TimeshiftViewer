namespace TimeshiftViewer.Puyo
{
    // 数字の見本（Recognizer.TW × TH、# が文字）。Tests の --make-templates で samples/ のスコア欄から作り直せる
    static partial class DigitTemplates
    {
        static bool[][] cache;

        public static bool[] Get(int d)
        {
            if (cache == null)
            {
                var c = new bool[10][];
                for (int i = 0; i < 10; i++)
                {
                    var s = Data[i];
                    if (string.IsNullOrEmpty(s)) continue;
                    var b = new bool[Recognizer.TW * Recognizer.TH];
                    for (int k = 0; k < b.Length && k < s.Length; k++) b[k] = s[k] == '#';
                    c[i] = b;
                }
                cache = c;
            }
            return cache[d];
        }
    }
}
