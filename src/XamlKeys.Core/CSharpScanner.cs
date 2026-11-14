using System.Text;
using System.Text.RegularExpressions;

namespace XamlKeys.Core;

/// <summary>C# 裡一個「鍵名不是字面」的查找點。靜態不可知 —— 計數並警示，不當成沒人用。</summary>
public sealed record DynamicKeySite(SourceLocation Location, string Expression)
{
    /// <summary>組出來的鍵名裡固定的開頭（<c>$"Icon{name}"</c> → <c>Icon</c>）。取不到時是 null。</summary>
    public string? Prefix { get; init; }

    /// <summary>組出來的鍵名裡固定的結尾（<c>$"{state}Brush"</c> → <c>Brush</c>）。</summary>
    public string Suffix { get; init; } = "";

    /// <summary>這個查找點所在的專案目錄 —— 樣式只套用到同一個專案的鍵（跨專案比對的教訓，見 CLAUDE.md）。</summary>
    public string? Project { get; init; }

    /// <summary>
    /// 開頭加結尾至少要有幾個固定字元才當成樣式。
    /// ⚠ 太短的樣式（<c>$"{a}{b}"</c>、<c>$"x{n}"</c>）會把半個專案都算成「可能有人用」，等於關掉 unused。
    /// </summary>
    public const int MinPatternLength = 3;

    public bool HasPattern => Prefix is not null && Prefix.Length + Suffix.Length >= MinPatternLength;

    /// <summary>這個鍵可能是這個查找點組出來的嗎？</summary>
    public bool Matches(string key) =>
        HasPattern && key.Length >= Prefix!.Length + Suffix.Length
                   && key.StartsWith(Prefix, StringComparison.Ordinal)
                   && key.EndsWith(Suffix, StringComparison.Ordinal);
}

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
    private readonly List<(string Text, SourceLocation Location)> _literalsNearDynamicLookups = [];

    public CSharpScanner(string root) => _root = Path.GetFullPath(root);

    public IReadOnlyList<KeyReference> References => _references;
    public IReadOnlyList<DynamicKeySite> DynamicSites => _dynamicSites;

    /// <summary>
    /// 有「用變數查找」的檔裡出現的每一個像鍵名的字串字面。分析時，跟定義過的鍵同名的會算成有人用。
    /// </summary>
    /// <remarks>
    /// ⚠ 在 Kindling 上實際用的時候抓到的：
    /// <code>
    /// (string Glyph, string BrushKey)? icon = image switch { ... => ("glyph", "Blue"), ... };
    /// IconText.SetResourceReference(TextBlock.ForegroundProperty, ic.BrushKey);
    /// </code>
    /// 查找點只看得到 <c>ic.BrushKey</c>，鍵名在上面十行的 switch 裡。工具原本說 <c>Blue</c> 沒人用 ——
    /// 照做的話，資訊對話框的圖示就沒有顏色了。這是這支工具最不該犯的錯（叫人刪掉有用的東西）。
    /// <para>
    /// 用變數查找的鍵名，九成就寫在同一個檔裡（switch、三元、陣列、對照表）。所以「同一個檔裡、
    /// 跟某個鍵同名的字串」當成引用。誤差方向是安全的：只會讓 unused 少報，而且有計數
    /// （<c>keysNamedNearDynamicLookups</c>），使用者看得到工具在這裡放寬了。
    /// </para>
    /// </remarks>
    public IReadOnlyList<(string Text, SourceLocation Location)> LiteralsNearDynamicLookups => _literalsNearDynamicLookups;

    /// <summary>
    /// C# 裡每一個像鍵名、而且**不是查找參數**的字串字面，連同它所在的專案。
    /// </summary>
    /// <remarks>
    /// ⚠ 2026-11-15 第五輪語料抓到的兩種形狀，鍵名都是一般字串、不在任何認得的查找呼叫裡：
    /// <list type="bullet">
    /// <item>程式在執行期把鍵**寫進**資源：dnSpy 的 <c>ResourceKey = "EnvironmentCommandBar"</c>（ColorInfos.cs）
    ///   由 Theme.cs 的 <c>resources[kv.Item1] = kv.Item2</c> 寫入；wpfui 的
    ///   <c>Resources["AccentFillColorDefault"] = …</c>；SukiUI 的 <c>AddColorWithOpacities(resources, "SukiAccentColor", …)</c>。
    ///   XAML 端的 DynamicResource 因此被報成懸空 —— dnSpy 982 筆。</item>
    /// <item>鍵名交給自己寫的輔助方法：dnSpy 的 <c>SetStyle(… ?? "FileTabGroupTabControlStyle")</c>、
    ///   SukiUI 的 <c>BindBrush(prop, this, "FnBlade")</c>。定義端被報成沒人用。</item>
    /// </list>
    /// 查找參數（<c>FindResource("K")</c> 這種讀取）不算 —— 讀不到的鍵不能因為「程式讀它」就變成有定義。
    /// </remarks>
    /// <para>
    /// <c>InWriter</c>：這個字串所在的檔用變數把鍵**寫進**資源（<c>Resources[key] = …</c>）。
    /// 這種檔裡的鍵名是在「定義」，不是在「使用」—— Kindling 的 ThemeService 把整張色盤寫進資源，
    /// 色盤裡的 AmberBorder 其實沒有人讀。判 unused 時不算它們；判懸空時算（那正是執行期定義）。
    /// </para>
    public IReadOnlyList<(string Text, string? Project, bool InWriter)> NamesInCode => _namesInCode;

    /// <summary>
    /// 組出來的鍵名的固定**結尾**（<c>$"{baseName}75"</c> → <c>75</c>）。跟 <see cref="NamesInCode"/> 配起來用：
    /// SukiUI 寫入 <c>SukiAccentColor75</c> 的方式是字串 <c>"SukiAccentColor"</c> 傳進一個 <c>$"{baseName}75"</c>。
    /// </summary>
    public IReadOnlyCollection<string> ComposedSuffixesInCode => _composedSuffixes;

    private readonly List<(string Text, string? Project, bool InWriter)> _namesInCode = [];

    /// <summary>C# 用 <c>Resources.Add("K", …)</c>／<c>Resources.Add(nameof(K), …)</c>／<c>Resources["K"] = …</c>
    /// 加進資源的鍵 —— 那是**定義**。</summary>
    /// <remarks>
    /// ⚠ 2026-11-15 第六輪語料（CommunityToolkit.Maui）：範例頁在 code-behind 的 InitializeComponent() 之前
    ///   <c>Resources.Add(nameof(StringToListConverter), new StringToListConverter { … })</c>，XAML 再用
    ///   <c>{StaticResource StringToListConverter}</c> —— 完全合法的寫法，工具卻報成 gate 擋的懸空。
    /// </remarks>
    public IReadOnlyCollection<(string Key, string? Project)> KeysAddedInCode => _keysAddedInCode;
    private readonly HashSet<(string Key, string? Project)> _keysAddedInCode = [];

    /// <summary>要找的識別字（markup 鍵的成員名，見 MarkupKeys）。掃描前由 Analyzer 填。</summary>
    public HashSet<string> WordsToFind { get; } = new(StringComparer.Ordinal);
    /// <summary>WordsToFind 裡，在某個 C# 檔以完整的字出現過的。</summary>
    public HashSet<string> WordsFound { get; } = new(StringComparer.Ordinal);

    /// <summary>要找「在程式裡建立」的控制項類別（短名）。掃描前由 Analyzer 填：在 XAML 裡被放置過的 x:Class。</summary>
    public HashSet<string> ClassesToFind { get; } = new(StringComparer.Ordinal);
    /// <summary>ClassesToFind 裡，C# 有 <c>new X(</c>、<c>typeof(X)</c>、<c>&lt;X&gt;</c>（DI 註冊、導覽）的 ——
    /// 它的宿主不只 XAML 裡看得到的那幾處。</summary>
    public HashSet<string> ClassesCreatedInCode { get; } = new(StringComparer.Ordinal);
    /// <summary>同上，連建立它的檔所在的專案（判「字典類別在程式裡被 new 出來」要限相關專案）。</summary>
    public HashSet<(string Class, string? Project)> ClassesCreatedInCodeByProject { get; } = [];

    private void FindCreatedClasses(string code, string? project)
    {
        foreach (var c in ClassesToFind)
        {
            if (ClassesCreatedInCodeByProject.Contains((c, project)) || !code.Contains(c, StringComparison.Ordinal)) continue;
            var n = Regex.Escape(c);
            if (Regex.IsMatch(code, $@"\bnew\s+(?:[\w.]+\.)?{n}\s*[({{]|\btypeof\s*\(\s*(?:[\w.]+\.)?{n}\s*\)|[<,]\s*(?:[\w.]+\.)?{n}\s*[>,]"))
            {
                ClassesCreatedInCode.Add(c);
                ClassesCreatedInCodeByProject.Add((c, project));
            }
        }
    }

    /// <summary>要找「宣告」的型別名（markup 鍵的型別）。只是用到不算 —— dnSpy 用了 SharpTreeView，但它在套件裡。</summary>
    public HashSet<string> TypesToFind { get; } = new(StringComparer.Ordinal);
    public HashSet<string> TypesDeclared { get; } = new(StringComparer.Ordinal);
    /// <summary>同上，連命名空間（"Ns.Type"）。一個檔宣告了幾個命名空間就每個都記 —— 寧可多認（多認 = 照舊判 unused）。</summary>
    /// <remarks>⚠ 2026-11-15 第七輪 AvalonDock：MLibTest 覆寫 ColorPickerLib.Themes.ResourceKeys 的鍵（函式庫在套件裡），
    ///   但 AvalonDock 自己也有一個叫 ResourceKeys 的類別 —— 只比短名的話，10 個覆寫被報成沒人用。</remarks>
    public HashSet<string> QualifiedTypesDeclared { get; } = new(StringComparer.Ordinal);

    private void FindTypes(string code)
    {
        List<string>? namespaces = null;
        foreach (var t in TypesToFind)
        {
            if (!code.Contains(t, StringComparison.Ordinal)) continue;
            if (!Regex.IsMatch(code, $@"(?<![\w])(?:class|struct|record|interface|enum)\s+{Regex.Escape(t)}(?![\w])")) continue;
            TypesDeclared.Add(t);
            namespaces ??= NamespaceDeclRegex().Matches(code).Select(m => m.Groups["n"].Value).ToList();
            foreach (var ns in namespaces) QualifiedTypesDeclared.Add($"{ns}.{t}");
            // 檔裡看不到命名空間宣告（全域型別、或我們讀不懂的寫法）：不知道它在哪個命名空間，當成哪裡都對得上
            if (namespaces.Count == 0) QualifiedTypesDeclared.Add(t);
        }
    }

    [GeneratedRegex(@"(?<![\w])namespace\s+(?<n>[\w.]+)")]
    private static partial Regex NamespaceDeclRegex();

    private void FindWords(string code)
    {
        foreach (var w in WordsToFind)
        {
            if (WordsFound.Contains(w) || !code.Contains(w, StringComparison.Ordinal)) continue;
            if (Regex.IsMatch(code, $@"(?<![\w]){Regex.Escape(w)}(?![\w])")) WordsFound.Add(w);
        }
    }
    private readonly HashSet<string> _composedSuffixes = new(StringComparer.Ordinal);
    private readonly HashSet<(string Prefix, string Suffix)> _composedNames = [];

    /// <summary>C# 裡組鍵名的內插字串（<c>$"TEInherited{name}Foreground"</c>）的固定開頭與結尾 ——
    /// 不限查找呼叫。dnSpy 用這種輔助方法把一整組鍵寫進資源。</summary>
    public IReadOnlyCollection<(string Prefix, string Suffix)> ComposedNamesInCode => _composedNames;
    private readonly HashSet<(string Prefix, string Suffix, string? Project)> _composedLiterals = [];
    /// <summary>前後都有固定部分（各至少 3 個字）的組出來的字串，與所在專案。</summary>
    public IReadOnlyCollection<(string Prefix, string Suffix, string? Project)> ComposedLiteralsInCode => _composedLiterals;

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

        // C++/WinRT：寬字串 L"Key" 當成一般字串（其餘規則照 C# 的讀）
        if (file.EndsWith(".cpp", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".h", StringComparison.OrdinalIgnoreCase))
            text = CppWideString().Replace(text, " \"");

        var code = StripComments(text);
        var project = ProjectOf?.Invoke(file);
        if (WordsToFind.Count > 0) FindWords(code);
        if (TypesToFind.Count > 0) FindTypes(code);
        if (ClassesToFind.Count > 0) FindCreatedClasses(code, project);

        // 不是查找參數的字串字面（見 NamesInCode）。讀取的查找要排除；寫入（Resources["K"] = v）留著。
        // ⚠ 這一段要對**每一個** C# 檔做：鍵名常常在跟資源完全無關的檔裡（dnSpy 的 TabGroup.cs 只寫了
        //   SetStyle(… ?? "FileTabGroupTabControlStyle")，整個檔沒有「Resource」這個字）。
        var writer = VariableWriteRegex().IsMatch(code);
        var readSpans = new List<(int Start, int End)>();
        foreach (var m in LiteralLookupRegex().Matches(code).Cast<Match>())
            if (!WriteAfterRegex().IsMatch(code, m.Index + m.Length))
                readSpans.Add((m.Index, m.Index + m.Length));
        foreach (var m in KeyLikeLiteralRegex().Matches(code).Cast<Match>())
            if (!readSpans.Any(s => m.Index >= s.Start && m.Index < s.End))
                _namesInCode.Add((m.Groups["s"].Value, project, writer));
        foreach (var m in ComposedSuffixRegex().Matches(code).Cast<Match>())
            _composedSuffixes.Add(m.Groups["s"].Value);
        // 產生出來的在地化存取器：Localize.GameMode()、ShellLocalization.X —— 方法名就是鍵名
        // ⚠ 2026-11-15 實測 Flow Launcher：359 筆 unused 裡 158 筆其實是這樣用的（source generator 依語系檔產生）
        foreach (var m in LocalizationAccessorRegex().Matches(code).Cast<Match>())
            _namesInCode.Add((m.Groups["k"].Value, project, false));
        foreach (var m in ResourceAddRegex().Matches(code).Cast<Match>())
        {
            var k = m.Groups["k"].Value;
            if (k.StartsWith("nameof", StringComparison.Ordinal))
            {
                var inner = k[(k.IndexOf('(') + 1)..k.LastIndexOf(')')].Trim();
                k = inner.Contains('.') ? inner[(inner.LastIndexOf('.') + 1)..] : inner;
            }
            else k = Unescape(k[1..^1]);
            if (k.Length > 0) _keysAddedInCode.Add((k, project));
        }
        // $"TEInherited{name}Foreground"（dnSpy）：只有一個洞、前後都是像鍵名的字，合計至少 3 個字
        foreach (var m in ComposedNameRegex().Matches(code).Cast<Match>())
        {
            var pre = m.Groups["p"].Value;
            var suf = m.Groups["s"].Value;
            // ⚠ 開頭至少 3 個字：只有結尾的（$"{x}Background"）會把整棵樹每個 …Background 都算進來 ——
            //   第一版就是這樣把 dnSpy 一筆真的懸空（AppSettingsTreeViewBackground）也吃掉了。
            //   只有結尾的情形另外由 ComposedSuffixesInCode 處理，那邊要求開頭是一個 C# 字串。
            if (pre.Length >= DynamicKeySite.MinPatternLength) _composedNames.Add((pre, suf));
            // 前後都夠長的（$"Match.Group.{i}.Highlight.Background"，dnGrep）：先存進變數再查找的寫法，
            // 查找點只看到變數 —— 判 unused 時也要認（見 Analyzer 規則 64）
            if (pre.Length >= DynamicKeySite.MinPatternLength && suf.Length >= DynamicKeySite.MinPatternLength)
                _composedLiterals.Add((pre, suf, project));
        }

        if (!text.Contains("Resource", StringComparison.Ordinal)
            && !text.Contains("xaml", StringComparison.OrdinalIgnoreCase)) return;   // 含 .axaml

        var rel = Rel(file);

        foreach (var m in LiteralLookupRegex().Matches(code).Cast<Match>())
            _references.Add(new KeyReference
            {
                Key = Unescape(m.Groups["k"].Value),
                Location = new SourceLocation(rel, LineAt(code, m.Index)),
                Kind = RefKind.CSharp,
                ScopeChain = [],   // 執行期沿真實樹查找，沒有靜態作用域
                IsWrite = WriteAfterRegex().IsMatch(code, m.Index + m.Length),
            });

        var dynamicBefore = _dynamicSites.Count;
        foreach (var m in DynamicLookupRegex().Matches(code).Cast<Match>())
        {
            var (prefix, suffix) = ComposedPattern(m.Value);
            _dynamicSites.Add(new DynamicKeySite(
                new SourceLocation(rel, LineAt(code, m.Index)),
                Shorten(m.Value))
            {
                Prefix = prefix,
                Suffix = suffix ?? "",
                Project = project,
            });
        }

        if (_dynamicSites.Count > dynamicBefore)
            foreach (var m in KeyLikeLiteralRegex().Matches(code).Cast<Match>())
                _literalsNearDynamicLookups.Add((m.Groups["s"].Value, new SourceLocation(rel, LineAt(code, m.Index))));

        foreach (var m in XamlPathRegex().Matches(code).Cast<Match>())
            _loadedDictionaryPaths.Add((m.Groups["p"].Value, project));
    }

    /// <summary>
    /// 從一個「鍵名組出來」的查找點取出固定的開頭與結尾：
    /// <c>FindResource($"Icon{name}")</c> → (<c>Icon</c>, ""）、
    /// <c>Resources[$"{state}Brush"]</c> → ("", <c>Brush</c>）、
    /// <c>FindResource("Icon" + name)</c> → (<c>Icon</c>, ""）。取不到時是 (null, null)。
    /// </summary>
    /// <remarks>
    /// ⚠ 2026-11-14 量過 17 個外部專案：243 個用變數查找的地方裡，25 個是這種「有固定開頭」的寫法 ——
    ///   典型是一組圖示（<c>IconSave</c>、<c>IconOpen</c>…）由程式依名稱取用。舊版把它們全報成沒人用。
    /// </remarks>
    internal static (string? Prefix, string? Suffix) ComposedPattern(string site)
    {
        // 鍵是第幾個參數：SetResourceReference／SetDynamicResource 是第二個，其他是第一個
        var open = site.IndexOfAny(['(', '[']);
        if (open < 0) return (null, null);
        var arg = site[(open + 1)..];
        if (site.Contains("SetResourceReference", StringComparison.Ordinal)
            || site.Contains("SetDynamicResource", StringComparison.Ordinal))
        {
            var comma = TopLevelComma(arg);
            if (comma < 0) return (null, null);
            arg = arg[(comma + 1)..];
        }
        arg = arg.TrimStart();

        // 內插字串：$"…"、$@"…"、@$"…"
        var interp = arg.StartsWith("$\"", StringComparison.Ordinal) ? 2
                   : arg.StartsWith("$@\"", StringComparison.Ordinal) || arg.StartsWith("@$\"", StringComparison.Ordinal) ? 3
                   : -1;
        if (interp > 0)
        {
            var body = new StringBuilder();
            int depth = 0, i = interp;
            int firstHole = -1, lastClose = -1;
            for (; i < arg.Length; i++)
            {
                var c = arg[i];
                if (depth == 0 && c == '"') break;
                if (c == '{' && i + 1 < arg.Length && arg[i + 1] == '{' && depth == 0) { body.Append('{'); i++; continue; }
                if (c == '}' && i + 1 < arg.Length && arg[i + 1] == '}' && depth == 0) { body.Append('}'); i++; continue; }
                if (c == '{') { if (depth == 0 && firstHole < 0) firstHole = body.Length; depth++; continue; }
                if (c == '}' && depth > 0) { depth--; if (depth == 0) lastClose = body.Length; continue; }
                if (depth == 0) body.Append(c);
            }
            if (firstHole < 0 || i >= arg.Length) return (null, null);   // 沒有洞，或字串沒有結束
            var text = body.ToString();
            return (text[..firstHole], text[lastClose..]);
        }

        // 串接："Icon" + name、prefix + "Brush"
        var pre = ConcatPrefixRegex().Match(arg);
        var suf = ConcatSuffixRegex().Match(arg);
        if (!pre.Success && !suf.Success) return (null, null);
        return (pre.Success ? pre.Groups["s"].Value : "", suf.Success ? suf.Groups["s"].Value : "");
    }

    private static int TopLevelComma(string s)
    {
        int depth = 0;
        bool inString = false;
        for (int i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '"') inString = !inString;
            if (inString) continue;
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') { if (depth == 0) return -1; depth--; }
            else if (c == ',' && depth == 0) return i;
        }
        return -1;
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

    // 字面鍵名的查找。每一種都是在語料裡實際看到的寫法：
    //   WPF        FindResource("K")、TryFindResource("K")、SetResourceReference(prop, "K")、Resources["K"]
    //   Avalonia   TryFindResource("K", out v)、TryFindResource("K", theme, out v)、TryGetResource("K", theme, out v)、
    //              GetResourceObservable("K")、new DynamicResourceExtension("K")
    //   MAUI       SetDynamicResource(prop, "K")、Resources.TryGetValue("K", out v)
    // ⚠ 2026-11-14 之前只認 WPF 那四種。Avalonia 的 TryFindResource 多一個 out 參數，
    //   原本的「字串後面緊接右括號」對不上，於是那些鍵被當成沒人用。
    [GeneratedRegex("""
        (?:(?:Try)?FindResource\s*\(\s*"(?<k>(?:[^"\\]|\\.)*)"\s*[,)])
        | (?:Set(?:ResourceReference|DynamicResource)\s*\([^,()]*,\s*"(?<k>(?:[^"\\]|\\.)*)"\s*\))
        | (?:Resources\s*\[\s*"(?<k>(?:[^"\\]|\\.)*)"\s*\])
        | (?:TryGetResource\s*\(\s*"(?<k>(?:[^"\\]|\\.)*)"\s*,)
        | (?:Resources\s*\.\s*TryGetValue\s*\(\s*"(?<k>(?:[^"\\]|\\.)*)"\s*,)
        | (?:GetResourceObservable\s*\(\s*"(?<k>(?:[^"\\]|\\.)*)"\s*[,)])
        | (?:(?:Dynamic|Static)ResourceExtension\s*\(\s*"(?<k>(?:[^"\\]|\\.)*)"\s*\))
        """, RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex LiteralLookupRegex();

    // 同樣的呼叫但鍵名不是字面字串 —— $"Icon{name}"、變數、常數、三元。
    // ⚠ (?!["\s]) 而不是 (?!")：「FindResource( "K")」左括號後面有空白時，\s* 會退回去，
    //   讓 (?!") 對著那個空白成立，字面查找就被誤算成一個執行期查找。
    // ⚠ typeof(X) 是隱含樣式的鍵（型別），跟 x:Key 無關，不算。
    // ⚠ Resources[key] = value 是**寫入**，不是查找 —— 算進來的話，主題服務那種
    //   「整張色盤寫進資源」的檔會讓 LiteralsNearDynamicLookups 把色盤裡每個鍵都算成有人用。
    // ⚠ 「不是字面」指的是**整個參數**不是單一字串字面，不是「不以引號開頭」。
    //   2026-11-14 之前寫成 (?!["\s])，於是 FindResource("Pic01" + n) 這種字串開頭的串接
    //   既不算字面、也不算執行期查找 —— 完全沒看到。實測 WPF-Samples 的 PhotoFlipperDemo：
    //   Pic011～Pic016 全被報成沒人用，summary 也沒提到有看不見的查找。
    // ⚠ 參數裡可以換行（停在分號）：參數寫成兩行的 FindResource(ok ? a : b) 原本也是完全隱形。
    [GeneratedRegex("""
        (?:(?:Try)?FindResource\s*\(\s*(?!\s|"(?:[^"\\]|\\.)*"\s*[,)])[^);]{1,80}\))
        | (?:Set(?:ResourceReference|DynamicResource)\s*\([^,()]*,\s*(?!\s|"(?:[^"\\]|\\.)*"\s*\)|typeof\s*\()[^);]{1,80}\))
        | (?:Resources\s*\[\s*(?!\s|"(?:[^"\\]|\\.)*"\s*\])[^\];]{1,80}\](?!\s*=[^=]))
        | (?:TryGetResource\s*\(\s*(?!\s|"(?:[^"\\]|\\.)*"\s*,)[^,);]{1,80},)
        | (?:Resources\s*\.\s*TryGetValue\s*\(\s*(?!\s|"(?:[^"\\]|\\.)*"\s*,)[^,);]{1,80},)
        | (?:GetResourceObservable\s*\(\s*(?!\s|"(?:[^"\\]|\\.)*"\s*[,)])[^,);]{1,80}[,)])
        | (?:(?:Dynamic|Static)ResourceExtension\s*\(\s*(?!\s|\)|"(?:[^"\\]|\\.)*"\s*\))[^);]{1,80}\))
        """, RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex DynamicLookupRegex();

    // 長得像鍵名的一般字串字面（不含 $"…" 與 @"…"）。用在 LiteralsNearDynamicLookups。
    [GeneratedRegex(@"(?<![\w])L""")]
    private static partial Regex CppWideString();

    [GeneratedRegex("""(?<![@$\w"])"(?<s>[A-Za-z_][\w.\-]{0,99})(?=")""")]
    private static partial Regex KeyLikeLiteralRegex();

    // ⚠ .axaml 也要認（Avalonia）。組出來的路徑（$"Themes/{name}.xaml"）也會被這條抓到，
    //   路徑裡帶著 {…} —— 由 Analyzer.MarkRuntimeLoaded 當成樣式比對。
    [GeneratedRegex("""["'](?<p>[^"'\n]*\.a?xaml)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex XamlPathRegex();

    /// <summary>[assembly: XmlnsDefinition("網址", "CLR 命名空間")] —— 只讀含這個字的檔，便宜。</summary>
    public static IEnumerable<(string Url, string Clr)> ReadXmlnsDefinitions(IEnumerable<string> files)
    {
        foreach (var f in files)
        {
            string text;
            try { text = File.ReadAllText(f); }
            catch (IOException) { continue; }
            if (!text.Contains("XmlnsDefinition", StringComparison.Ordinal)) continue;

            foreach (var m in XmlnsDefinitionRegex().Matches(StripComments(text)).Cast<Match>())
                yield return (m.Groups["u"].Value, m.Groups["n"].Value);
        }
    }

    [GeneratedRegex("""XmlnsDefinition(?:Attribute)?\s*\(\s*"(?<u>[^"]+)"\s*,\s*"(?<n>[^"]+)"\s*\)""")]
    private static partial Regex XmlnsDefinitionRegex();

    // 用變數當鍵把值寫進資源：Resources[key] = …、resources[kv.Item1] = …（不分大小寫的 resources）
    [GeneratedRegex("""[Rr]esources\s*\[\s*(?!["\s])[^\]\n]{1,80}\]\s*=(?!=)""")]
    private static partial Regex VariableWriteRegex();

    // Localize.Key / Localization.Key / XxxLocalizer.Key（…Localized 是列舉屬性，不算）
    [GeneratedRegex("""\b\w*(?:Localize|Localization|Localizer)\s*\.\s*(?<k>[A-Za-z_]\w*)""")]
    private static partial Regex LocalizationAccessorRegex();

    // 把鍵加進資源：Resources.Add("K", …)／Resources.Add(nameof(K), …)／Resources["K"] = …／Resources[nameof(K)] = …
    [GeneratedRegex("""\b\w*[Rr]esources\s*(?:\.\s*(?:Add|TryAdd)\s*\(\s*(?<k>"(?:[^"\\]|\\.)*"|nameof\s*\(\s*[\w.]+\s*\))\s*,|\[\s*(?<k>"(?:[^"\\]|\\.)*"|nameof\s*\(\s*[\w.]+\s*\))\s*\]\s*=(?!=))""")]
    private static partial Regex ResourceAddRegex();

    // 緊接在查找後面的「= 值」—— 那是寫入，不是讀取（== 是比較，不算）
    [GeneratedRegex("""\G\s*=(?!=)""")]
    private static partial Regex WriteAfterRegex();

    // $"Pre{x}Suf" —— 恰好一個洞、前後只有像鍵名的字元（有空白、冒號的是訊息字串，不是鍵）
    [GeneratedRegex("""\$@?"(?<p>[A-Za-z0-9_.\-]*)\{[^{}"]{1,80}\}(?<s>[A-Za-z0-9_.\-]*)"(?!")""")]
    private static partial Regex ComposedNameRegex();

    // $"{baseName}75" —— 以洞開頭、固定結尾的內插字串，取結尾
    [GeneratedRegex("""\$@?"\{[^{}"]{1,80}\}(?<s>[A-Za-z0-9_.\-]{1,30})"(?!")""")]
    private static partial Regex ComposedSuffixRegex();

    // "Icon" + name —— 參數一開頭就是字串字面再接 +
    [GeneratedRegex("""^"(?<s>(?:[^"\\]|\\.)*)"\s*\+""")]
    private static partial Regex ConcatPrefixRegex();

    // name + "Brush" —— 參數最後是 + 字串字面
    [GeneratedRegex("""\+\s*"(?<s>(?:[^"\\]|\\.)*)"\s*[)\],]""")]
    private static partial Regex ConcatSuffixRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
