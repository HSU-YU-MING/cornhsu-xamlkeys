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
    /// <summary>組件名（小寫） → 該專案的目錄。由掃描範圍內的 *.csproj 推出來。</summary>
    private readonly Dictionary<string, string> _assemblies;

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
                if (m.Success && m.Groups["n"].Value.Trim() is { Length: > 0 } overridden)
                    name = overridden;
            }
            catch (IOException) { /* 讀不到就用檔名 */ }

            _assemblies[name] = dir;
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
            if (directoryProbe(dir)) _assemblies[assembly] = dir;
        }
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

        foreach (var item in doc.Descendants())
        {
            var include = item.Attribute("Include")?.Value;
            if (include is null) continue;
            if (!include.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
                && !include.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase)) continue;

            var link = item.Attribute("Link")?.Value
                    ?? item.Elements().FirstOrDefault(e => e.Name.LocalName == "Link")?.Value;
            if (string.IsNullOrWhiteSpace(link) || link.Contains("%(")) continue;

            var target = Path.GetFullPath(Path.Combine(dir, include.Replace('\\', Path.DirectorySeparatorChar)));
            _links[(dir, NormalizeLink(link))] = target;
        }
    }

    private static string NormalizeLink(string p) => p.Replace('\\', '/').TrimStart('/').ToLowerInvariant();

    /// <summary>這個專案目錄底下的相對路徑，是不是連結進來的檔？</summary>
    private string? Linked(string projectDir, string relative) =>
        _links.TryGetValue((projectDir, NormalizeLink(relative)), out var target) && Exists(target) ? target : null;

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
            if (!_assemblies.TryGetValue(assembly, out var projDir))
                return new(null, $"assembly '{assembly}' is not in the scanned tree", assembly);

            var crossed = Path.GetFullPath(Path.Combine(projDir, path));
            return Exists(crossed)
                ? new(crossed, null, assembly)
                : new(null, $"'{path}' not found in assembly '{assembly}'", assembly);
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
                if (slash > 0 && _assemblies.TryGetValue(path[..slash], out var asmDir))
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

    /// <summary>從檔案往上找最近的 *.csproj 目錄；一路找不到就用掃描根。</summary>
    private IEnumerable<string> ProjectRootsFor(string file)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(file));
        while (dir is not null && dir.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.EnumerateFiles(dir, "*.csproj").Any()) yield return dir;
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
