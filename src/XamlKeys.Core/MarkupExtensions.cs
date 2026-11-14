namespace XamlKeys.Core;

/// <summary>在屬性值裡找出的一個資源引用。Key 是 null 表示「看得出是資源引用，但鍵名不是字串」
/// （<c>{StaticResource {x:Type Button}}</c>、<c>{StaticResource {x:Static SystemColors.ControlBrushKey}}</c>）
/// —— 那是 unresolved，不是懸空。</summary>
public readonly record struct ExtensionRef(RefKind Kind, string? Key, string Raw);

/// <summary>
/// XAML markup extension 的小型掃描器。
///
/// 為什麼不用 regex：引用會巢狀出現在別的 extension 裡
/// （<c>{Binding X, Converter={StaticResource K}}</c>、<c>{Binding Source={StaticResource K}}</c>），
/// 而且鍵名本身可能又是一個 extension。XamlContrast 只需要「屬性值是不是一個資源引用」，
/// 用前綴比對就夠；本工具要的是**全部**引用，漏一個就會把有用的鍵報成沒人用。
/// </summary>
public static class MarkupExtensions
{
    /// <summary>掃一段屬性值（或文字節點），回傳裡面所有的資源引用，含巢狀。</summary>
    public static List<ExtensionRef> Scan(string value)
    {
        var found = new List<ExtensionRef>();
        if (string.IsNullOrEmpty(value)) return found;

        // ⚠ XAML 只在值的**開頭**是 { 時才當成 markup extension。
        //   中間出現的 { 是字面文字。
        //   實查（WinUI Gallery）：Text="…is the code equivalent of {ThemeResource Key}. Switch…"
        //   —— 那是一句說明文字，舊版把它當成引用,於是報出一個叫 Key 的假懸空。
        var trimmed = value.AsSpan().TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '{') return found;

        var start = value.Length - trimmed.Length;

        // {} 跳脫：整個值是字面文字
        if (trimmed.Length > 1 && trimmed[1] == '}') return found;

        ParseExtension(value, start, found);
        return found;
    }

    /// <summary>解析 open 位置的一個 extension，並遞迴進它的參數找巢狀的引用。</summary>
    private static void ParseExtension(string s, int open, List<ExtensionRef> found)
    {
        var end = FindClosing(s, open);
        if (end < 0) return;                 // 沒有對應的 } —— 壞掉的 XAML，不猜

        var typeName = ReadTypeName(s, open + 1, out var afterName);
        if (typeName is null) return;

        var body = s[afterName..end].Trim();
        var kind = KindOf(typeName);

        if (kind is not null)
            found.Add(new ExtensionRef(kind.Value, ExtractKey(body), s[open..(end + 1)]));

        // 不論外層是不是資源引用，參數裡都可能有
        //（{Binding X, Converter={StaticResource K}} 是最常見的形狀）
        foreach (var part in SplitTopLevel(body))
        {
            var seg = part.Trim();
            if (seg.Length == 0) continue;

            var eq = IndexOfTopLevel(seg, '=');
            var valuePart = eq > 0 ? seg[(eq + 1)..].TrimStart() : seg;
            if (valuePart.Length == 0 || valuePart[0] != '{') continue;

            // 以原字串的位移遞迴，才能切出正確的 Raw
            var offset = s.IndexOf(valuePart, afterName, StringComparison.Ordinal);
            if (offset >= 0 && offset < end) ParseExtension(s, offset, found);
        }
    }

    /// <summary>從 extension 的參數區取出鍵名。取不到（巢狀 extension、空的）回傳 null。</summary>
    private static string? ExtractKey(string body)
    {
        if (body.Length == 0) return null;

        // 具名參數：ResourceKey=Foo。也可能混在別的參數之間。
        foreach (var part in SplitTopLevel(body))
        {
            var seg = part.Trim();
            if (seg.Length == 0) continue;

            var eq = IndexOfTopLevel(seg, '=');
            if (eq > 0)
            {
                var name = seg[..eq].Trim();
                if (name.Equals("ResourceKey", StringComparison.OrdinalIgnoreCase))
                    return CleanKey(seg[(eq + 1)..].Trim());
                continue;   // 別的具名參數（TargetType= 之類），不是鍵
            }

            // 位置參數：第一個就是鍵
            return CleanKey(seg);
        }

        return null;
    }

    /// <summary>剝掉引號。巢狀 extension（以 { 開頭）回傳 null —— 鍵名不是字串，靜態不可知。</summary>
    private static string? CleanKey(string raw)
    {
        var s = raw.Trim();
        if (s.Length == 0) return null;
        if (s[0] == '{') return null;

        if (s.Length >= 2 && (s[0] == '\'' || s[0] == '"') && s[^1] == s[0])
            s = s[1..^1];

        // XAML 的 extension 參數可以用 \ 跳脫逗號與大括號
        s = s.Replace("\\,", ",").Replace("\\}", "}").Replace("\\{", "{");

        return s.Length == 0 ? null : s;
    }

    private static string? ReadTypeName(string s, int start, out int afterName)
    {
        afterName = start;
        var i = start;
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;

        var nameStart = i;
        while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == ':' || s[i] == '_' || s[i] == '.')) i++;
        if (i == nameStart) return null;

        afterName = i;
        return s[nameStart..i];
    }

    private static RefKind? KindOf(string typeName)
    {
        // 去掉命名空間前綴（x:、sys: 之類）；也去掉 XAML 允許省略的 Extension 後綴
        var colon = typeName.LastIndexOf(':');
        var name = colon >= 0 ? typeName[(colon + 1)..] : typeName;
        if (name.EndsWith("Extension", StringComparison.Ordinal))
            name = name[..^"Extension".Length];

        return name switch
        {
            "StaticResource" => RefKind.Static,
            "DynamicResource" => RefKind.Dynamic,
            "ThemeResource" => RefKind.Theme,
            _ => null,
        };
    }

    /// <summary>從 open 位置的 { 找到配對的 }。考慮巢狀與引號。</summary>
    private static int FindClosing(string s, int open)
    {
        var depth = 0;
        char quote = '\0';

        for (var i = open; i < s.Length; i++)
        {
            var c = s[i];

            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                continue;
            }

            switch (c)
            {
                case '\\': i++; break;          // 跳脫下一個字
                case '\'' or '"': quote = c; break;
                case '{': depth++; break;
                case '}':
                    depth--;
                    if (depth == 0) return i;
                    break;
            }
        }

        return -1;
    }

    /// <summary>以頂層逗號切開參數（巢狀 extension 與引號內的逗號不算）。</summary>
    private static IEnumerable<string> SplitTopLevel(string s)
    {
        var depth = 0;
        char quote = '\0';
        var start = 0;

        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];

            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                continue;
            }

            switch (c)
            {
                case '\\': i++; break;
                case '\'' or '"': quote = c; break;
                case '{': depth++; break;
                case '}': depth--; break;
                case ',' when depth == 0:
                    yield return s[start..i];
                    start = i + 1;
                    break;
            }
        }

        if (start < s.Length) yield return s[start..];
    }

    private static int IndexOfTopLevel(string s, char target)
    {
        var depth = 0;
        char quote = '\0';

        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];

            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                continue;
            }

            switch (c)
            {
                case '\\': i++; break;
                case '\'' or '"': quote = c; break;
                case '{': depth++; break;
                case '}': depth--; break;
                default:
                    if (c == target && depth == 0) return i;
                    break;
            }
        }

        return -1;
    }
}
