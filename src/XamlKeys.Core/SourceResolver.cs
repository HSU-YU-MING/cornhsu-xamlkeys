using System.Text.RegularExpressions;

namespace XamlKeys.Core;

/// <summary>Source 字串解析的結果。</summary>
public readonly record struct SourceResolution(string? Path, string? ExternalReason, string? Assembly)
{
    public bool IsExternal => Path is null;
}

/// <summary>
/// 把 <c>&lt;ResourceDictionary Source="…"/&gt;</c> 的字串解析成磁碟上的檔案。
///
/// ⚠ 這是本工具最大的假警報來源。解析不到的時候**一定要回 external**，
/// 不能當成「這本字典是空的」—— 把 HandyControl 的字典當空的，專案裡每一個
/// 引用它的鍵的地方都會變成懸空，工具第一天就被關掉。
/// </summary>
public sealed partial class SourceResolver
{
    private readonly string _root;
    /// <summary>組件名（不分大小寫） → 用這個組件名的專案目錄（可能不只一個）。由掃描範圍內的 *.csproj 推出來。</summary>
    /// <remarks>⚠ 2026-11-15 第七輪：WPF-Samples 有兩個專案都叫 ExpenseIt9（入門教學與完整範例）。
    ///   舊版一個名字只記一個目錄（後讀到的蓋掉先讀到的），入門教學的 App.xaml 合併
    ///   /ExpenseIt9;component/Styles.xaml 時接到了另一個專案的 Styles.xaml —— 報出 10 筆假 out-of-scope。
    ///   同名時先用**引用所在的那個專案**。</remarks>
    private readonly Dictionary<string, List<string>> _assemblies;

    public SourceResolver(string root, IEnumerable<string> projectFiles,
                          IReadOnlyDictionary<string, string>? externalDictionaries = null)
        : this(root, projectFiles, externalDictionaries, Directory.Exists) { }

    /// <param name="directoryProbe">externalDictionaries 的目錄檢查。測試換成記錄器，確認範圍外的目錄從來沒被查過。</param>
    internal SourceResolver(string root, IEnumerable<string> projectFiles,
                            IReadOnlyDictionary<string, string>? externalDictionaries,
                            Func<string, bool> directoryProbe)
    {
        _root = Path.GetFullPath(root);
        _assemblies = new(StringComparer.OrdinalIgnoreCase);

        foreach (var proj in projectFiles)
        {
            // 預設組件名＝csproj 檔名。AssemblyName 被覆寫的情形在這裡讀一次，
            // 讀不到就退回檔名（退回只會讓解析失敗 → external，不會誤判成找到）。
            var dir = Path.GetDirectoryName(Path.GetFullPath(proj));
            if (dir is null) continue;

            var name = Path.GetFileNameWithoutExtension(proj);
            try
            {
                var text = File.ReadAllText(proj);
                var m = AssemblyNameRegex().Match(text);
                // ⚠ 2026-11-15 第十二輪 DevWinUI：<AssemblyName>$(BaseNamespace)</AssemblyName> —— 屬性在別的檔裡、展不開。
                //   照字面記成「$(BaseNamespace)」的話，ms-appx:///DevWinUI/… 永遠對不上；退回檔名（DevWinUI.csproj → DevWinUI）。
                if (m.Success && m.Groups["n"].Value.Trim() is { Length: > 0 } overridden && !overridden.Contains("$("))
                    name = overridden;
            }
            catch (IOException) { /* 讀不到就用檔名 */ }

            if (!_assemblies.TryGetValue(name, out var dirs)) _assemblies[name] = dirs = [];
            if (!dirs.Contains(dir, StringComparer.OrdinalIgnoreCase)) dirs.Add(dir);
            ReadLinks(proj, dir);
        }

        // config 的 externalDictionaries：把第三方組件的字典指到磁碟上的目錄。
        // ⚠ 這會讓原本 external 的合併邊變成可解析的，覆蓋率與 unused 的可信度都跟著升級，
        //   所以它算「非預設設定」，要出現在 summary.configOverrides。
        //   相對路徑以掃描根為基準；指不到的目錄**不是**錯誤（維持 external），
        //   但也不會悄悄成功 —— 解析失敗照舊會記成 external 並拉低覆蓋率。
        if (externalDictionaries is null) return;

        // ⚠ 只收**掃描根底下**的目錄（例如 vendored 的函式庫副本）。
        //   config 是從被掃的 repo 自動撿的 —— 對方可以寫 "path": "\\attacker\share"，
        //   光是 Directory.Exists 就會發出 SMB 連線。範圍外的目錄本來也沒有作用：
        //   這支工具只解析掃描根底下的 XAML，指過去的檔讀不到，照舊是 external。
        //   （2026-11-15 由自動安全審查指出；舊文件的範例 "../packages/…" 從來沒有生效過。）
        foreach (var (assembly, path) in externalDictionaries)
        {
            var dir = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(_root, path));
            if (!IsAllowed(dir + Path.DirectorySeparatorChar + "_")) continue;
            if (directoryProbe(dir)) _assemblies[assembly] = [dir];
        }
    }

    /// <summary>用這個組件名的專案目錄（沒有就是空的）。</summary>
    public IReadOnlyList<string> ProjectDirsOfAssembly(string assembly) =>
        _assemblies.TryGetValue(assembly, out var dirs) ? dirs : [];

    /// <summary>用這個組件名的專案目錄：引用所在的那個專案排第一（最深的優先），其餘照讀到的順序。</summary>
    private IEnumerable<string> AssemblyDirs(string assembly, string containingFile)
    {
        if (!_assemblies.TryGetValue(assembly, out var dirs)) return [];
        if (dirs.Count == 1) return dirs;
        var full = Path.GetFullPath(containingFile);
        return dirs.OrderByDescending(d => full.StartsWith(d + Path.DirectorySeparatorChar, PathComparison) ? d.Length : -1);
    }

    /// <summary>
    /// 只認**掃描根底下**的路徑，而且從掃描根到它的每一段都不是符號連結或目錄接合點。
    /// </summary>
    /// <remarks>
    /// ⚠ 這支工具會在 CI 上掃別人的 repo。合併路徑與 csproj 的 Include 都是對方寫的：
    ///   <c>Source="\\attacker\share\x.xaml"</c> 或 <c>&lt;Page Include="\\attacker\share\x.xaml" Link="…"&gt;</c>
    ///   一旦拿去查檔案存不存在，Windows runner 會向那台主機發 SMB 連線、送出 NTLM 認證；
    ///   絕對路徑則會讀到掃描範圍外的檔（內容的鍵名可能出現在報告裡）。
    ///   Path.GetFullPath 只是字串運算、不碰網路 —— 存在與否的檢查一定要先過這一關。
    ///   2026-11-15 加 csproj Link 時由自動安全審查指出；XAML 的 Source 從第一版就有同樣的洞，一起補。
    /// </remarks>
    private bool Exists(string full) => IsAllowed(full) && Probe(full);

    /// <summary>真正去查檔案系統的那一步。測試換成記錄器，確認範圍外的路徑**從來沒被查過**。</summary>
    internal Func<string, bool> Probe { get; set; } = File.Exists;

    /// <summary>讀一段路徑自己的屬性（不跟著連結走）。測試換掉它來模擬「讀不到」。</summary>
    internal Func<string, FileAttributes> Attributes { get; set; } = File.GetAttributes;

    private bool IsAllowed(string full)
    {
        var root = _root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, PathComparison)) return false;

        // 根之後不能有冒號：Windows 的替代資料流（x.xaml:s）與 Themes::$INDEX_ALLOCATION 會繞過逐段檢查
        if (full.IndexOf(':', root.Length + 1) >= 0) return false;

        // ⚠ 字串在範圍內，不代表檔案在範圍內：中間任何一段是符號連結或目錄接合點，就可能指到範圍外
        //   （包括 \\主機\分享）。只讀那一段**自己**的屬性 —— 不跟著連結走 —— 是連結就整條不碰。
        //   README 本來就寫「連結裡的內容會被跳過」，這裡讓存在檢查也守同一條。
        var cur = root;
        foreach (var part in full[(root.Length + 1)..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            cur = cur + Path.DirectorySeparatorChar + part;
            if (!_notALink.TryGetValue(cur, out var ok)) _notALink[cur] = ok = !IsLink(cur);
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>路徑比較跟著檔案系統走：Windows／macOS 不分大小寫，其他分。
    /// ⚠ 在 Linux 上一律不分大小寫的話，/repo 與 /REPO 是兩個目錄卻會被當成同一個，
    ///   快取裡 themes（真目錄）的結果也會被拿去當 Themes（連結）的答案。</summary>
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly Dictionary<string, bool> _notALink =
        new(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>這一段是不是連結。**判不出來就當成是**（fail closed）：只有「不存在」才算不是。</summary>
    private bool IsLink(string path)
    {
        try { return (Attributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return false; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return true; }
    }

    /// <summary>
    /// csproj 裡從別處連結進來的 XAML：(專案目錄, 連結後的相對路徑) → 實際的檔。
    /// </summary>
    /// <remarks>
    /// ⚠ 2026-11-15 實測 PowerToys：QuickAccess 用
    ///   <c>&lt;Page Include="..\Settings.UI\SettingsXAML\Styles\Button.xaml"&gt;&lt;Link&gt;Resources\Styles\Button.xaml&lt;/Link&gt;&lt;/Page&gt;</c>
    ///   借用設定頁的樣式，App.xaml 寫 <c>Source="/Resources/Styles/Button.xaml"</c> —— 那個路徑在磁碟上不存在，
    ///   只存在於建置後的套件裡。Link 可以是屬性也可以是子元素。
    ///   Include 有萬用字元或 $(屬性) 的靜態算不出來 —— 那種路徑在磁碟上不存在，Linked() 的 File.Exists 會擋掉，不猜。
    /// </remarks>
    private readonly Dictionary<(string Project, string Link), string> _links = [];

    private void ReadLinks(string proj, string dir)
    {
        System.Xml.Linq.XDocument doc;
        try { doc = System.Xml.Linq.XDocument.Load(proj); }
        catch (Exception e) when (e is IOException or System.Xml.XmlException or UnauthorizedAccessException) { return; }

        var props = ReadProperties(doc, dir);
        foreach (var item in doc.Descendants())
        {
            var include = item.Attribute("Include")?.Value;
            if (include is null) continue;
            if (include.Contains("$(")) include = ExpandProperties(include, props);
            if (!include.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
                && !include.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase)) continue;

            var link = item.Attribute("Link")?.Value
                    ?? item.Elements().FirstOrDefault(e => e.Name.LocalName == "Link")?.Value;
            if (string.IsNullOrWhiteSpace(link))
            {
                // 規則 101：沒寫 Link、但檔在專案資料夾外（C++ 專案常這樣：<Page Include="..\shared\Scenario7.xaml" />）——
                // 它還是這個專案編譯的檔：只記「屬於誰」，不替它編一個解析得到的路徑（那個路徑我們不知道）。
                // ⚠ 2026-11-16 第十六輪 Windows-universal-samples 的 SimpleInk：shared 頁面只由 cpp 專案這樣收，認不出是 UWP 頁面。
                var member = Path.GetFullPath(Path.Combine(dir, include.Replace('\\', Path.DirectorySeparatorChar)));
                if (!_linkedInto.TryGetValue(member, out var owners)) _linkedInto[member] = owners = [];
                if (!owners.Contains(dir, StringComparer.OrdinalIgnoreCase)) owners.Add(dir);
                continue;
            }

            // <AvaloniaResource Include="..\Other\Controls\**\*.axaml"><Link>Controls\%(RecursiveDir)%(Filename)%(Extension)</Link>
            // ⚠ 2026-11-15 第八輪 Dock：Simple 主題的 /Controls/ControlStrings.axaml 是從 Fluent 主題這樣連過來的。
            //   只認這一種形狀：萬用字元只在 Include 的最後兩段（**\*.ext 或 *.ext），Link 是「固定前綴＋%(RecursiveDir)%(Filename)%(Extension)」。
            if (link.Contains("%("))
            {
                if (WildcardLink(include, link) is { } w)
                    _wildcardLinks.Add((dir, w.LinkPrefix,
                        Path.GetFullPath(Path.Combine(dir, w.IncludeBase.Replace('\\', Path.DirectorySeparatorChar))),
                        w.Extension, w.Recursive));
                continue;
            }

            var target = Path.GetFullPath(Path.Combine(dir, include.Replace('\\', Path.DirectorySeparatorChar)));
            AddLink(dir, link, target);
        }

        // 規則 95：共用專案 <Import Project="…\X.projitems" Label="Shared"/> —— 裡面的 XAML 編進匯入它的專案，
        // 路徑是相對於 .projitems 所在的資料夾（App.xaml 寫 Source="Views/Styles/…" 就是那個路徑）。
        // ⚠ 2026-11-15 第十五輪 NuGetPackageExplorer：WinUI 頭專案匯入 NugetPackageExplorer.Shared.Legacy.projitems，
        //   13 本樣式字典全在共用專案裡 —— 舊版一本都解析不到，覆蓋率 50%，213 筆 external。
        foreach (var import in doc.Descendants().Where(e => e.Name.LocalName == "Import"))
        {
            var project = import.Attribute("Project")?.Value;
            if (project is null || !project.EndsWith(".projitems", StringComparison.OrdinalIgnoreCase)) continue;
            if (project.Contains("$(")) project = ExpandProperties(project, props);
            if (project.Contains("$(")) continue;
            var itemsFile = Path.GetFullPath(Path.Combine(dir, project.Replace('\\', Path.DirectorySeparatorChar)));
            if (!Exists(itemsFile)) continue;
            ReadSharedItems(itemsFile, dir);
        }
    }

    private void ReadSharedItems(string itemsFile, string importingDir)
    {
        System.Xml.Linq.XDocument doc;
        try { doc = System.Xml.Linq.XDocument.Load(itemsFile); }
        catch (Exception e) when (e is IOException or System.Xml.XmlException or UnauthorizedAccessException) { return; }

        var itemsDir = Path.GetDirectoryName(itemsFile)!;
        var props = ReadProperties(doc, itemsDir);
        foreach (var item in doc.Descendants())
        {
            var include = item.Attribute("Include")?.Value;
            if (include is null) continue;
            if (include.Contains("$(")) include = ExpandProperties(include, props);
            if (include.Contains("$(") || include.Contains('*')) continue;
            if (!include.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
                && !include.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase)) continue;

            var target = Path.GetFullPath(Path.Combine(itemsDir, include.Replace('\\', Path.DirectorySeparatorChar)));
            var link = item.Attribute("Link")?.Value
                    ?? item.Elements().FirstOrDefault(e => e.Name.LocalName == "Link")?.Value
                    ?? Path.GetRelativePath(itemsDir, target);
            AddLink(importingDir, link, target);
        }
    }

    private void AddLink(string dir, string link, string target)
    {
        _links[(dir, NormalizeLink(link))] = target;
        if (!_linkedInto.TryGetValue(target, out var projects)) _linkedInto[target] = projects = [];
        if (!projects.Contains(dir, StringComparer.OrdinalIgnoreCase)) projects.Add(dir);
    }

    /// <summary>用 Link 把這個檔（絕對路徑）連進去的專案目錄。</summary>
    public IReadOnlyList<string> LinkedInto(string file) =>
        _linkedInto.TryGetValue(Path.GetFullPath(file), out var p) ? p : [];

    /// <summary>檔案（絕對路徑） → 用 Link 把它連進去的專案目錄。連結進來的檔，根目錄形式的 Source 要從那些專案解析。</summary>
    private readonly Dictionary<string, List<string>> _linkedInto = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 同一個專案檔裡宣告的屬性，只認三種值：字面路徑、<c>$(MSBuildThisFileDirectory)</c>／<c>$(MSBuildProjectDirectory)</c>、
    /// 以及 <c>$([MSBuild]::GetDirectoryNameOfFileAbove($(MSBuildThisFileDirectory), 檔名))</c>（往上找到那個檔所在的目錄）。
    /// </summary>
    /// <remarks>
    /// ⚠ 2026-11-15 第十輪 Windows-universal-samples：每個範例專案都寫
    ///   <c>&lt;SharedContentDir&gt;$([MSBuild]::GetDirectoryNameOfFileAbove($(MSBuildThisFileDirectory), LICENSE))\SharedContent&lt;/SharedContentDir&gt;</c>，
    ///   App.xaml、Styles.xaml 都從那裡連結進來 —— 不展開的話 391 筆 external、覆蓋率 61%、exit 3。
    /// ⚠ 往上找只在**掃描根底下**，存在檢查一律走 <see cref="Exists"/>（安全閘門）。算不出來的屬性就不展開（維持 external）。
    /// </remarks>
    private Dictionary<string, string> ReadProperties(System.Xml.Linq.XDocument doc, string projectDir)
    {
        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MSBuildThisFileDirectory"] = projectDir + Path.DirectorySeparatorChar,
            ["MSBuildProjectDirectory"] = projectDir,
        };
        foreach (var group in doc.Descendants().Where(e => e.Name.LocalName == "PropertyGroup"))
            foreach (var p in group.Elements())
            {
                if (p.HasElements || p.Attribute("Condition") is not null) continue;
                var raw = p.Value.Trim();
                if (raw.Length == 0) continue;
                if (EvaluateProperty(raw, props, projectDir) is { } value) props[p.Name.LocalName] = value;
            }
        return props;
    }

    private string? EvaluateProperty(string raw, Dictionary<string, string> props, string projectDir)
    {
        var m = FileAboveRegex().Match(raw);
        if (m.Success)
        {
            var file = m.Groups["f"].Value.Trim();
            if (file.Length == 0 || file.IndexOfAny(['/', '\\', '$', '*', ':']) >= 0) return null;
            string? found = null;
            // 往上找：掃描根以外的路徑由 Exists 的安全閘門擋掉（不會真的去碰磁碟）
            for (var d = projectDir; d is not null; d = Path.GetDirectoryName(d))
                if (Exists(Path.Combine(d, file))) { found = d; break; }
            if (found is null) return null;
            raw = found + raw[(m.Index + m.Length)..];
        }
        return raw.Contains("$(") ? ExpandProperties(raw, props) : raw;
    }

    /// <summary>把 $(Name) 換成已知的屬性；不認得的照原樣留著（含「$(」的路徑在磁碟上不存在，解析自然失敗 —— 不猜）。</summary>
    private static string ExpandProperties(string text, Dictionary<string, string> props) =>
        PropertyRefRegex().Replace(text, m => props.TryGetValue(m.Groups["n"].Value, out var v) ? v : m.Value);

    [GeneratedRegex(@"^\$\(\[MSBuild\]::GetDirectoryNameOfFileAbove\(\s*\$\(MSBuildThisFileDirectory\)\s*,\s*(?<f>[^)]+)\)\)")]
    private static partial Regex FileAboveRegex();

    [GeneratedRegex(@"\$\((?<n>[A-Za-z_][A-Za-z0-9_.\-]*)\)")]
    private static partial Regex PropertyRefRegex();

    private static string NormalizeLink(string p) => p.Replace('\\', '/').TrimStart('/').ToLowerInvariant();

    private readonly List<(string Project, string LinkPrefix, string IncludeBase, string Extension, bool Recursive)> _wildcardLinks = [];

    /// <summary>Include「base\**\*.ext」或「base\*.ext」＋ Link「prefix\%(RecursiveDir)%(Filename)%(Extension)」→ 各部分；其餘形狀 null。</summary>
    internal static (string IncludeBase, string Extension, bool Recursive, string LinkPrefix)? WildcardLink(string include, string link)
    {
        const string tail = "%(RecursiveDir)%(Filename)%(Extension)";
        var l = link.Replace('\\', '/');
        if (!l.EndsWith(tail, StringComparison.Ordinal)) return null;
        var prefix = l[..^tail.Length];
        if (prefix.Contains('%') || prefix.Contains('*') || prefix.Contains("$(")) return null;

        var inc = include.Replace('\\', '/');
        var star = inc.LastIndexOf("/*.", StringComparison.Ordinal);
        if (star < 0) return null;
        var ext = inc[(star + 2)..];                           // ".axaml"
        if (ext.Contains('*') || ext.Contains('/')) return null;
        var head = inc[..star];
        var recursive = head.EndsWith("/**", StringComparison.Ordinal);
        if (recursive) head = head[..^3];
        if (head.Contains('*') || head.Contains("$(") || head.Contains('%')) return null;
        return (head + "/", ext, recursive, prefix);
    }

    /// <summary>這個專案目錄底下的相對路徑，是不是連結進來的檔？</summary>
    private string? Linked(string projectDir, string relative)
    {
        if (_links.TryGetValue((projectDir, NormalizeLink(relative)), out var target) && Exists(target)) return target;

        var rel = relative.Replace('\\', '/').TrimStart('/');
        foreach (var (proj, prefix, includeBase, ext, recursive) in _wildcardLinks)
        {
            if (!string.Equals(proj, projectDir, PathComparison)) continue;
            if (!rel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = rel[prefix.Length..];
            if (rest.Length == 0 || !rest.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) continue;
            if (!recursive && rest.Contains('/')) continue;
            var candidate = Path.GetFullPath(Path.Combine(includeBase, rest.Replace('/', Path.DirectorySeparatorChar)));
            // 「..」逃出 Include 的目錄：正規化之後一定不在 includeBase 底下（也擋掉 Controls/../b.axaml）
            if (!candidate.StartsWith(includeBase, PathComparison)) continue;
            if (Exists(candidate)) return candidate;
        }
        return null;
    }

    public SourceResolution Resolve(string rawSource, string containingFile)
    {
        var src = rawSource.Trim();
        if (src.Length == 0)
            return new(null, "empty Source", null);

        // 動態組出來的 Source（{Binding}、{x:Static}）—— 靜態不可知
        if (src.StartsWith('{'))
            return new(null, "Source is a markup extension", null);

        var (path, assembly) = Normalize(src);
        if (path is null)
            return new(null, $"unrecognised Source form: {src}", assembly);

        // 跨組件
        if (assembly is not null)
        {
            if (!_assemblies.ContainsKey(assembly))
                return new(null, $"assembly '{assembly}' is not in the scanned tree", assembly);

            foreach (var projDir in AssemblyDirs(assembly, containingFile))
            {
                var crossed = Path.GetFullPath(Path.Combine(projDir, path));
                if (Exists(crossed)) return new(crossed, null, assembly);
                // 規則 106：那個組件的檔是共用專案（.projitems）連結進去的 —— 同組件那條路早就會查連結，跨組件這條漏了
                // ⚠ 2026-11-16 第十七輪 Panuon.WPF.UI：九個目標框架的專案都匯入 SharedResources 的 projitems，
                //   Control.xaml 用 pack://application:,,,/Panuon.WPF.UI;component/Styles/Button.xaml 合併 —— 舊版 268 本全算看不到
                if (Linked(projDir, path) is { } linkedInAsm) return new(linkedInAsm, null, assembly);
            }
            return new(null, $"'{path}' not found in assembly '{assembly}'", assembly);
        }

        // 同組件：絕對路徑形式（/Themes/Dark.xaml）相對於「專案根」，
        // ⚠ 不是掃描根 —— 一個方案底下有多個專案時兩者不同。找不到才退回掃描根。
        if (src.StartsWith('/') || src.StartsWith("pack:", StringComparison.OrdinalIgnoreCase)
                                || src.StartsWith("ms-appx:", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var baseDir in ProjectRootsFor(containingFile))
            {
                var abs = Path.GetFullPath(Path.Combine(baseDir, path));
                if (Exists(abs)) return new(abs, null, null);
                if (Linked(baseDir, path) is { } linked) return new(linked, null, null);
            }

            // ms-appx:///組件名/路徑 —— WinUI 取用函式庫裡的資源就是這樣寫（第一段是組件名）。
            // ⚠ 2026-11-15 實測 Dev Home（ms-appx:///DevHome.Dashboard/Styles/…）、
            //   PowerToys（ms-appx:///PowerToys.Common.UI.Controls/Controls/…，組件名由 <AssemblyName> 覆寫）：
            //   舊版當成同專案路徑，找不到就算 external，覆蓋率掉到 60%，觸發 exit 3。
            // 不寫 ms-appx: 的 /組件名/路徑 也一樣（PowerToys 的 RegistryPreviewUILib/Themes/Generic.xaml）
            if (src.StartsWith("ms-appx:", StringComparison.OrdinalIgnoreCase) || src.StartsWith('/'))
            {
                var slash = path.IndexOf('/');
                if (slash > 0)
                    foreach (var asmDir in AssemblyDirs(path[..slash], containingFile))
                    {
                        var inAsm = Path.GetFullPath(Path.Combine(asmDir, path[(slash + 1)..]));
                        if (Exists(inAsm)) return new(inAsm, null, path[..slash]);
                    }
            }

            return new(null, $"'{path}' not found from any project root", null);
        }

        // 相對路徑：相對於引用它的那個檔
        var dir = Path.GetDirectoryName(Path.GetFullPath(containingFile)) ?? _root;
        var rel = Path.GetFullPath(Path.Combine(dir, path));
        if (Exists(rel)) return new(rel, null, null);

        // 退路：有些專案把相對路徑寫成相對於專案根
        foreach (var baseDir in ProjectRootsFor(containingFile))
        {
            var abs = Path.GetFullPath(Path.Combine(baseDir, path));
            if (Exists(abs)) return new(abs, null, null);
            // 連結進來的檔：相對於引用它的檔、換算成專案內的路徑再查
            if (Linked(baseDir, Path.GetRelativePath(baseDir, rel)) is { } linked) return new(linked, null, null);
        }

        return new(null, $"'{path}' not found", null);
    }

    /// <summary>把各種寫法正規化成「組件名（可為 null）＋組件內相對路徑」。</summary>
    private static (string? Path, string? Assembly) Normalize(string src)
    {
        var s = src;

        // pack://application:,,,/…  與 pack://siteoforigin:,,,/…
        if (s.StartsWith("pack://", StringComparison.OrdinalIgnoreCase))
        {
            var slash = s.IndexOf('/', "pack://".Length);
            if (slash < 0) return (null, null);
            s = s[slash..];                       // 留下 /… 的部分
        }
        else if (s.StartsWith("ms-appx:", StringComparison.OrdinalIgnoreCase))
        {
            // ms-appx:///Themes/Dark.xaml（WinUI）
            var idx = s.IndexOf("://", StringComparison.Ordinal);
            s = idx >= 0 ? s[(idx + 3)..] : s["ms-appx:".Length..];
            if (!s.StartsWith('/')) s = "/" + s;
        }
        else if (s.StartsWith("avares://", StringComparison.OrdinalIgnoreCase))
        {
            // Avalonia：avares://組件名/路徑/檔案.axaml
            // 組件名是第一段,不像 pack:// 那樣有 ;component 標記。
            var rest = s["avares://".Length..].Replace('\\', '/');
            var slash = rest.IndexOf('/');
            if (slash <= 0) return (null, null);
            return (rest[(slash + 1)..], rest[..slash]);
        }

        s = s.Replace('\\', '/');

        // /MyLib;component/Themes/Generic.xaml
        var m = ComponentRegex().Match(s);
        if (m.Success)
            return (m.Groups["p"].Value.TrimStart('/'), m.Groups["a"].Value);

        // ;component 寫法之外，還有 v1.0.0.0;… 的版本／金鑰形式，一律當跨組件
        if (s.Contains(";component", StringComparison.OrdinalIgnoreCase))
            return (null, null);

        return (s.TrimStart('/'), null);
    }

    /// <summary>從檔案往上找最近的 *.csproj／*.vcxproj 目錄；一路找不到就用掃描根。</summary>
    private IEnumerable<string> ProjectRootsFor(string file)
    {
        // 規則 77：用 Link 連進某些專案的檔（Windows-universal-samples 的 shared/、SharedContent/），根目錄形式的路徑相對於**那些專案**
        if (_linkedInto.TryGetValue(Path.GetFullPath(file), out var linkers))
            foreach (var p in linkers) yield return p;

        var dir = Path.GetDirectoryName(Path.GetFullPath(file));
        while (dir is not null && dir.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.EnumerateFiles(dir, "*.csproj").Any()
                || Directory.EnumerateFiles(dir, "*.vcxproj").Any()
                || Directory.EnumerateFiles(dir, "*.vbproj").Any()) yield return dir;
            var parent = Path.GetDirectoryName(dir);
            if (parent == dir) break;
            dir = parent;
        }

        yield return _root;
    }

    [GeneratedRegex(@"^/?(?<a>[^/;]+);component(?<p>/.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex ComponentRegex();

    [GeneratedRegex(@"<AssemblyName>(?<n>[^<]*)</AssemblyName>", RegexOptions.IgnoreCase)]
    private static partial Regex AssemblyNameRegex();
}
