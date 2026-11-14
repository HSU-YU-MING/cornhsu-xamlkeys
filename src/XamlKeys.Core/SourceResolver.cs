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
        }

        // config 的 externalDictionaries：把第三方組件的字典指到磁碟上的目錄。
        // ⚠ 這會讓原本 external 的合併邊變成可解析的，覆蓋率與 unused 的可信度都跟著升級，
        //   所以它算「非預設設定」，要出現在 summary.configOverrides。
        //   相對路徑以掃描根為基準；指不到的目錄**不是**錯誤（維持 external），
        //   但也不會悄悄成功 —— 解析失敗照舊會記成 external 並拉低覆蓋率。
        if (externalDictionaries is null) return;

        foreach (var (assembly, path) in externalDictionaries)
        {
            var dir = Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(_root, path));
            if (Directory.Exists(dir)) _assemblies[assembly] = dir;
        }
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
            if (!_assemblies.TryGetValue(assembly, out var projDir))
                return new(null, $"assembly '{assembly}' is not in the scanned tree", assembly);

            var crossed = Path.GetFullPath(Path.Combine(projDir, path));
            return File.Exists(crossed)
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
                if (File.Exists(abs)) return new(abs, null, null);
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
                    if (File.Exists(inAsm)) return new(inAsm, null, path[..slash]);
                }
            }

            return new(null, $"'{path}' not found from any project root", null);
        }

        // 相對路徑：相對於引用它的那個檔
        var dir = Path.GetDirectoryName(Path.GetFullPath(containingFile)) ?? _root;
        var rel = Path.GetFullPath(Path.Combine(dir, path));
        if (File.Exists(rel)) return new(rel, null, null);

        // 退路：有些專案把相對路徑寫成相對於專案根
        foreach (var baseDir in ProjectRootsFor(containingFile))
        {
            var abs = Path.GetFullPath(Path.Combine(baseDir, path));
            if (File.Exists(abs)) return new(abs, null, null);
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
