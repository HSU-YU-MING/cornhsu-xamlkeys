using System.Text;
using System.Text.RegularExpressions;

namespace XamlKeys.Core;

/// <summary>C# 裡一個「鍵名不是字面」的查找點。靜態不可知 —— 計數並警示，不當成沒人用。</summary>
public sealed record DynamicKeySite(SourceLocation Location, string Expression);

/// <summary>
/// C# 側的資源用法掃描。
///
/// **為什麼不用 Roslyn**（規劃書 4.4 原本寫 Roslyn，這裡反過來，理由寫在這）：
///   1. Roslyn 會讓這個 tool 套件大上一個數量級。本工具對 ReSharper 的賣點就是「輕」，
///      為了四個方法名付 10MB+ 的相依不划算。
///   2. Roslyn 要能解析到型別才有附加價值，那等於要求專案建置得起來 ——
///      靜態工具不該有這個前提（使用者的專案可能缺 NuGet、缺 SDK）。
///   3. 我們只需要區分「字面字串」與「組出來的字串」，那是語法層的事，不需要語意層。
///
/// 取捨：註解與字串裡出現 <c>FindResource("X")</c> 會被當成引用。
/// **這個誤差的方向是安全的** —— 它只會讓「沒人用」少報，不會讓人誤刪。
/// 即使如此仍先剝掉註解，成本很低。
/// </summary>
public sealed partial class CSharpScanner
{
    private readonly string _root;
    private readonly List<KeyReference> _references = [];
    private readonly List<DynamicKeySite> _dynamicSites = [];
    private readonly List<(string Path, string? Project)> _loadedDictionaryPaths = [];

    public CSharpScanner(string root) => _root = Path.GetFullPath(root);

    public IReadOnlyList<KeyReference> References => _references;
    public IReadOnlyList<DynamicKeySite> DynamicSites => _dynamicSites;

    /// <summary>程式碼裡載入的字典路徑（new ResourceDictionary { Source = new Uri("…") }）。
    /// 用來避免把它們誤報成 orphan-dictionary。</summary>
    public IReadOnlyList<(string Path, string? Project)> LoadedDictionaryPaths => _loadedDictionaryPaths;

    /// <summary>掃描前先告訴它每個檔屬於哪個專案 —— 載入字典的比對要限定在同一個專案裡。</summary>
    public Func<string, string?>? ProjectOf { get; set; }

    public void ScanFile(string file)
    {
        string text;
        try { text = File.ReadAllText(file); }
        catch (IOException) { return; }

        if (!text.Contains("Resource", StringComparison.Ordinal)
            && !text.Contains(".xaml", StringComparison.OrdinalIgnoreCase)) return;

        var code = StripComments(text);
        var rel = Rel(file);

        foreach (var m in LiteralLookupRegex().Matches(code).Cast<Match>())
            _references.Add(new KeyReference
            {
                Key = Unescape(m.Groups["k"].Value),
                Location = new SourceLocation(rel, LineAt(code, m.Index)),
                Kind = RefKind.CSharp,
                ScopeChain = [],   // 執行期沿真實樹查找，沒有靜態作用域
            });

        foreach (var m in DynamicLookupRegex().Matches(code).Cast<Match>())
            _dynamicSites.Add(new DynamicKeySite(
                new SourceLocation(rel, LineAt(code, m.Index)),
                Shorten(m.Value)));

        var project = ProjectOf?.Invoke(file);
        foreach (var m in XamlPathRegex().Matches(code).Cast<Match>())
            _loadedDictionaryPaths.Add((m.Groups["p"].Value, project));
    }

    /// <summary>把註解換成等長的空白 —— 位移不變，行號才算得準。</summary>
    internal static string StripComments(string s)
    {
        var sb = new StringBuilder(s.Length);
        var i = 0;

        while (i < s.Length)
        {
            var c = s[i];

            // 逐字字串 @"…"（"" 是跳脫的引號）
            if (c == '@' && i + 1 < s.Length && s[i + 1] == '"')
            {
                var start = i;
                i += 2;
                while (i < s.Length)
                {
                    if (s[i] == '"')
                    {
                        if (i + 1 < s.Length && s[i + 1] == '"') { i += 2; continue; }
                        i++; break;
                    }
                    i++;
                }
                sb.Append(s, start, i - start);
                continue;
            }

            // 一般字串
            if (c == '"')
            {
                var start = i++;
                while (i < s.Length)
                {
                    if (s[i] == '\\') { i += 2; continue; }
                    if (s[i] == '"') { i++; break; }
                    if (s[i] == '\n') break;        // 未閉合，別吃掉整個檔
                    i++;
                }
                sb.Append(s, start, Math.Min(i, s.Length) - start);
                continue;
            }

            // 字元字面
            if (c == '\'')
            {
                var start = i++;
                while (i < s.Length)
                {
                    if (s[i] == '\\') { i += 2; continue; }
                    if (s[i] == '\'') { i++; break; }
                    if (s[i] == '\n') break;
                    i++;
                }
                sb.Append(s, start, Math.Min(i, s.Length) - start);
                continue;
            }

            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                while (i < s.Length && s[i] != '\n') { sb.Append(' '); i++; }
                continue;
            }

            if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                while (i < s.Length && !(s[i] == '*' && i + 1 < s.Length && s[i + 1] == '/'))
                {
                    sb.Append(s[i] == '\n' ? '\n' : ' ');   // 換行要留著
                    i++;
                }
                if (i < s.Length) { sb.Append("  "); i += 2; }
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    private static int LineAt(string s, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < s.Length; i++)
            if (s[i] == '\n') line++;
        return line;
    }

    private static string Unescape(string s) =>
        s.Replace("\\\"", "\"").Replace("\\\\", "\\");

    private static string Shorten(string s)
    {
        var one = WhitespaceRegex().Replace(s.Trim(), " ");
        return one.Length <= 80 ? one : one[..77] + "...";
    }

    private string Rel(string file)
    {
        var full = Path.GetFullPath(file);
        return full.StartsWith(_root, StringComparison.OrdinalIgnoreCase)
            ? full[_root.Length..].TrimStart(Path.DirectorySeparatorChar, '/').Replace('\\', '/')
            : full.Replace('\\', '/');
    }

    // FindResource("K") / TryFindResource("K") / SetResourceReference(prop, "K") / Resources["K"]
    [GeneratedRegex("""
        (?:(?:Try)?FindResource\s*\(\s*"(?<k>(?:[^"\\]|\\.)*)"\s*\))
        | (?:SetResourceReference\s*\([^,()]*,\s*"(?<k>(?:[^"\\]|\\.)*)"\s*\))
        | (?:Resources\s*\[\s*"(?<k>(?:[^"\\]|\\.)*)"\s*\])
        """, RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex LiteralLookupRegex();

    // 同樣的呼叫但參數不是字面字串 —— $"Icon{name}"、變數、常數
    [GeneratedRegex("""
        (?:(?:Try)?FindResource\s*\(\s*(?!")[^)\n]{1,80}\))
        | (?:Resources\s*\[\s*(?!")[^\]\n]{1,80}\])
        """, RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex DynamicLookupRegex();

    [GeneratedRegex("""["'](?<p>[^"'\n]*\.xaml)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex XamlPathRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
