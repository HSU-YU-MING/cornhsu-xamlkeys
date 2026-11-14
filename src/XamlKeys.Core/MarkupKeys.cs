namespace XamlKeys.Core;

/// <summary>
/// 不是字串的資源鍵：<c>{x:Static local:Keys.AccentBrushKey}</c> 與 <c>{ComponentResourceKey …}</c>。
/// 把它們換成一個可以比對的「身分」字串，讓定義與引用照一般字串鍵的規則判定。
/// </summary>
/// <remarks>
/// ⚠ 2026-11-15 量過 17 個外部專案：這類引用 591 處，其中 553 處是**框架自己的鍵**
///   （<c>SystemColors.ControlTextBrushKey</c>、<c>SystemParameters.FocusVisualStyleKey</c>、
///   <c>MenuItem.SeparatorStyleKey</c>…）。所以框架鍵一定要先認出來：
///   引用永遠不是懸空、重新定義它不算沒人用（框架的樣板在讀）。
/// ⚠ 專案自己的鍵找不到時**不報懸空**，照舊算 unresolved：x:Static 的成員也可能在 C# 裡
///   用 <c>Resources.Add(Keys.X, …)</c> 定義，或者在別的組件的 generic.xaml 以 ComponentResourceKey 定義。
///   能確定的是：同一本字典重複（載入時丟例外）、互相覆蓋、定義在別處但不在作用域、沒人引用。
/// </remarks>
public static class MarkupKeys
{
    /// <param name="Key">比對用的身分（<c>{x:Static Ns.Type.Member}</c>、<c>{ComponentResourceKey Ns.Type, Id}</c>）。</param>
    /// <param name="Framework">型別屬於框架（預設命名空間、或 System.*／Microsoft.UI.*／Avalonia.*／Microsoft.Maui.*）。</param>
    /// <param name="Member">在 C# 裡找得到就算有人用的名字（x:Static 的成員名、ComponentResourceKey 的 ResourceId）。</param>
    /// <param name="TypeName">型別的短名。C# 裡找不到它 = 型別在別的組件（函式庫的鍵，由函式庫自己的樣板讀）。</param>
    public readonly record struct Canonical(string Key, bool Framework, string Member, string TypeName);

    private static readonly string[] PresentationNamespaces =
    [
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation",
        "http://schemas.microsoft.com/winfx/2006/xaml",
        "https://github.com/avaloniaui",
        "http://schemas.microsoft.com/dotnet/2021/maui",
        "http://schemas.microsoft.com/winfx/2009/xaml",
        "http://xamarin.com/schemas/2014/forms",
    ];

    private static readonly string[] FrameworkClrPrefixes =
        ["System", "Microsoft.UI", "Microsoft.Maui", "Windows.UI", "Avalonia"];

    /// <param name="raw">整段 markup extension，含大括號。</param>
    /// <param name="resolveNs">前綴 → 命名空間 URI；空字串前綴是預設命名空間。不認得回傳 null。</param>
    public static Canonical? From(string raw, Func<string, string?> resolveNs)
    {
        var s = raw.Trim();
        if (s.Length < 3 || s[0] != '{' || s[^1] != '}') return null;

        var inner = s[1..^1].Trim();
        var sp = inner.IndexOfAny([' ', '\t', '\r', '\n']);
        var name = sp < 0 ? inner : inner[..sp];
        var args = sp < 0 ? "" : inner[(sp + 1)..].Trim();
        var local = name.Contains(':') ? name[(name.LastIndexOf(':') + 1)..] : name;

        if (local is "Static" or "StaticExtension")
        {
            var member = args;
            if (member.StartsWith("Member", StringComparison.Ordinal) && member.Contains('='))
                member = member[(member.IndexOf('=') + 1)..].Trim();
            if (member.Length == 0 || member.Contains('{') || member.Contains(',') || member.Contains(' ')) return null;

            var (ns, rest, fw) = Qualify(member, resolveNs);
            var dot = rest.LastIndexOf('.');
            if (dot <= 0 || dot == rest.Length - 1) return null;   // 一定要是 Type.Member
            var typePart = rest[..dot];
            return new Canonical($"{{x:Static {ns}{rest}}}", fw, rest[(dot + 1)..],
                                 typePart.Contains('.') ? typePart[(typePart.LastIndexOf('.') + 1)..] : typePart);
        }

        if (local is "ComponentResourceKey" or "ComponentResourceKeyExtension")
        {
            string? type = null, id = null;
            var pos = 0;
            foreach (var part in SplitTopLevel(args))
            {
                var p = part.Trim();
                if (p.Length == 0) continue;
                var eq = IndexOfTopLevel(p, '=');
                if (eq > 0)
                {
                    var n = p[..eq].Trim();
                    var v = p[(eq + 1)..].Trim();
                    if (n == "TypeInTargetAssembly") type = v;
                    else if (n == "ResourceId") id = v;
                }
                else
                {
                    if (pos == 0) type = p;
                    else if (pos == 1) id = p;
                    pos++;
                }
            }
            if (type is null || id is null) return null;

            type = type.Trim();
            if (type.StartsWith('{'))
            {
                // {x:Type local:Foo} 或 {x:Type TypeName=local:Foo}
                if (!type.EndsWith('}')) return null;
                var t = type[1..^1].Trim();
                var sp2 = t.IndexOfAny([' ', '\t', '\r', '\n']);
                if (sp2 < 0) return null;
                var tn = t[..sp2];
                var tl = tn.Contains(':') ? tn[(tn.LastIndexOf(':') + 1)..] : tn;
                if (tl is not ("Type" or "TypeExtension")) return null;
                type = t[(sp2 + 1)..].Trim();
                if (type.StartsWith("TypeName", StringComparison.Ordinal) && type.Contains('='))
                    type = type[(type.IndexOf('=') + 1)..].Trim();
            }
            id = id.Trim().Trim('\'', '"');
            if (type.Length == 0 || id.Length == 0 || type.Contains('{') || id.Contains('{')) return null;

            var (ns, rest, fw) = Qualify(type, resolveNs);
            return new Canonical($"{{ComponentResourceKey {ns}{rest}, {id}}}", fw, id,
                                 rest.Contains('.') ? rest[(rest.LastIndexOf('.') + 1)..] : rest);
        }

        return null;
    }

    /// <summary>「前綴:Type.Member」→（命名空間前置字、Type.Member、是不是框架）。</summary>
    private static (string Ns, string Name, bool Framework) Qualify(string text, Func<string, string?> resolveNs)
    {
        var colon = text.IndexOf(':');
        var prefix = colon >= 0 ? text[..colon] : "";
        var rest = colon >= 0 ? text[(colon + 1)..] : text;
        var uri = resolveNs(prefix);

        if (uri is null) return ($"?{prefix}:", rest, false);

        string? clr = null;
        if (uri.StartsWith("clr-namespace:", StringComparison.Ordinal))
            clr = uri["clr-namespace:".Length..].Split(';')[0].Trim();
        else if (uri.StartsWith("using:", StringComparison.Ordinal))
            clr = uri["using:".Length..].Trim();

        if (clr is not null)
        {
            var fw = FrameworkClrPrefixes.Any(p => clr == p || clr.StartsWith(p + ".", StringComparison.Ordinal));
            return (clr.Length > 0 ? clr + "." : "", rest, fw);
        }

        if (PresentationNamespaces.Contains(uri, StringComparer.Ordinal)) return ("", rest, true);

        // 函式庫以 XmlnsDefinition 公開的網址命名空間：用網址本身當身分（同一個網址寫法的定義與引用對得上）
        return ($"{uri}|", rest, false);
    }

    private static IEnumerable<string> SplitTopLevel(string s)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            switch (s[i])
            {
                case '{': depth++; break;
                case '}': depth--; break;
                case ',' when depth == 0:
                    yield return s[start..i];
                    start = i + 1;
                    break;
            }
        }
        yield return s[start..];
    }

    private static int IndexOfTopLevel(string s, char c)
    {
        var depth = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '{') depth++;
            else if (s[i] == '}') depth--;
            else if (s[i] == c && depth == 0) return i;
        }
        return -1;
    }
}
