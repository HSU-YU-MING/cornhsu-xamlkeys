namespace XamlKeys.Core;

/// <summary>
/// 找出「看起來是同一個鍵、只是名字不一樣」的候選。
///
/// 為什麼值得做：懸空與沒人用的鍵，絕大多數是**打錯字或改名留下的**。
/// 實測（QuillNest）：`InverseBoolToVis` 被定義了卻沒人用，而 `InverseBoolToVisibilityConverter`
/// 被引用 12 次 —— 兩者是同一次重新命名的兩半。工具報出「沒人用」是對的，
/// 但直接講出另一半，使用者才看得懂發生了什麼事。
///
/// 刻意保守：寧可不給建議，也不要給錯的建議。門檻見 <see cref="Suggest"/>。
/// </summary>
public static class NearMiss
{
    /// <summary>
    /// 從 <paramref name="candidates"/> 裡找一個跟 <paramref name="key"/> 夠像的。
    /// 找不到就回 null。
    ///
    /// 兩種「夠像」：
    ///   - 其中一個是另一個的**前綴**，而且短的那個至少 6 個字（改名加後綴最常見的形狀）
    ///   - 扣掉共同前後綴之後，剩下的「相異部分」編輯距離夠小
    ///
    /// ⚠ 第二條一定要先扣掉共同前後綴。實測（ScreenToGif）：鍵名都在同一個命名空間底下
    /// （`Vector.*`、`Style.Button.*`），直接比整串的話每個鍵都跟每個鍵很像 ——
    /// `Vector.Back` 會建議 `Vector.Clock`、`TextPrimaryColor` 會建議 `LightPrimaryColor`。
    /// 那種建議比沒有建議更糟，因為它看起來像答案。
    /// </summary>
    public static string? Suggest(string key, IEnumerable<string> candidates)
    {
        if (key.Length < 4) return null;

        string? best = null;
        var bestScore = int.MaxValue;

        foreach (var c in candidates)
        {
            if (c.Length < 4) continue;
            if (c.StartsWith('{')) continue;   // markup 鍵的身分（{x:Static …}）不是打錯字的另一半
            if (string.Equals(c, key, StringComparison.Ordinal)) continue;

            // 前綴：InverseBoolToVis -> InverseBoolToVisibilityConverter
            var (shortName, longName) = key.Length <= c.Length ? (key, c) : (c, key);
            if (shortName.Length >= 6 && longName.StartsWith(shortName, StringComparison.Ordinal))
            {
                var score = longName.Length - shortName.Length;
                if (score < bestScore) { bestScore = score; best = c; }
                continue;
            }

            // 相異部分：扣掉共同前綴與共同後綴
            var p = CommonPrefix(key, c);
            var sfx = CommonSuffix(key, c, p);

            var coreA = key.Length - p - sfx;
            var coreB = c.Length - p - sfx;
            if (coreA <= 0 || coreB <= 0) continue;           // 其實是前綴／後綴關係，上面已處理

            // 相異的部分本身要夠像：距離不超過較短那一側的一半，且最多 2
            var budget = Math.Min(2, Math.Max(1, Math.Min(coreA, coreB) / 2));

            var d = Distance(key.Substring(p, coreA), c.Substring(p, coreB), budget);
            if (d >= 0 && d < bestScore) { bestScore = d; best = c; }
        }

        return best;
    }

    private static int CommonPrefix(string a, string b)
    {
        var n = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < n && a[i] == b[i]) i++;
        return i;
    }

    private static int CommonSuffix(string a, string b, int prefix)
    {
        var n = Math.Min(a.Length, b.Length) - prefix;
        var i = 0;
        while (i < n && a[a.Length - 1 - i] == b[b.Length - 1 - i]) i++;
        return i;
    }

    /// <summary>
    /// 編輯距離，超過 budget 就提早放棄（回 -1）。
    ///
    /// ⚠ 用 Damerau（相鄰兩字互換算 **1** 次編輯），不是純 Levenshtein。
    /// 轉置是最典型的打字錯誤（`PrimaryBrsuh` ↔ `PrimaryBrush`），
    /// 純 Levenshtein 算它 2 次，在相異部分只有兩三個字時一定超標。
    /// </summary>
    private static int Distance(string a, string b, int budget)
    {
        if (Math.Abs(a.Length - b.Length) > budget) return -1;

        // 三列：prev2 給轉置用
        var prev2 = new int[b.Length + 1];
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            var rowMin = cur[0];

            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                var v = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);

                // 轉置：a[i-2]a[i-1] 與 b[j-2]b[j-1] 剛好互換
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    v = Math.Min(v, prev2[j - 2] + 1);

                cur[j] = v;
                if (v < rowMin) rowMin = v;
            }

            if (rowMin > budget) return -1;      // 這一列已經全部超標，不可能再變好

            var spare = prev2;
            prev2 = prev;
            prev = cur;
            cur = spare;
        }

        return prev[b.Length] <= budget ? prev[b.Length] : -1;
    }
}
