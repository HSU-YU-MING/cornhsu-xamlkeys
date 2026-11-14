using System.Text.RegularExpressions;

namespace XamlKeys.Core;

/// <summary>
/// 掃描範圍內的專案，以及每個專案是「應用程式」還是「函式庫」。
///
/// **為什麼這件事是生死攸關的**（2026-11-14 外部語料實測）：
/// 本工具原本假設掃到的是一個應用程式。函式庫（HandyControl、MahApps、wpfui、
/// MaterialDesignInXamlToolkit —— 七個外部專案裡有四個）把每一條假設都打破：
/// 它們的資源字典是**給使用者合併的**，不是自己合併的。結果是
///   - orphan-dictionary：HandyControl 140 筆、MaterialDesign 103 筆（全部假的）
///   - unused：MaterialDesign 3372 個鍵裡報了 1767 個（那些鍵是它的公開 API）
///   - out-of-scope：wpfui 1339 筆（使用者會合併哪幾本，我們不可能知道）
///
/// 所以：函式庫專案不報 unused 與 orphan，作用域也放寬成整個專案。
/// </summary>
public sealed partial class ProjectIndex
{
    private readonly List<(string Dir, bool IsApplication, bool IsWinUi)> _projects = [];

    public ProjectIndex(string root, IEnumerable<string> projectFiles, IEnumerable<string> xamlFiles)
    {
        var xamlByDir = xamlFiles.ToLookup(f => Path.GetDirectoryName(Path.GetFullPath(f)) ?? "",
                                           StringComparer.OrdinalIgnoreCase);

        foreach (var proj in projectFiles)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(proj));
            if (dir is null) continue;

            _projects.Add((dir, IsApplicationProject(proj, dir, xamlByDir), IsWinUiProject(proj)));
        }

        // 最深的目錄排前面：巢狀專案要先比對到自己，不是父專案
        _projects.Sort((a, b) => b.Dir.Length.CompareTo(a.Dir.Length));

        // <ProjectReference>：哪些專案在建置上連在一起（見 Related）
        foreach (var proj in projectFiles)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(proj));
            if (dir is null) continue;
            string text;
            try { text = File.ReadAllText(proj); }
            catch (IOException) { continue; }
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(text, """<ProjectReference\s+Include\s*=\s*"([^"]+)"""))
            {
                var target = Path.GetDirectoryName(Path.GetFullPath(Path.Combine(dir, m.Groups[1].Value.Replace('\\', Path.DirectorySeparatorChar))));
                if (target is null) continue;
                AddEdge(_references, dir, target);   // dir 參考 target
                AddEdge(_referencedBy, target, dir);
            }
        }

        HasAnyApplication = _projects.Any(p => p.IsApplication);
        // 連一個 csproj 都沒有時（純資源資料夾、sparse clone），無從判斷 ——
        // 退回「當成應用程式」，因為那是比較常見的掃描對象。
        if (_projects.Count == 0) HasAnyApplication = true;
    }

    public bool HasAnyApplication { get; }


    public int ProjectCount => _projects.Count;
    public int ApplicationCount => _projects.Count(p => p.IsApplication);

    /// <summary>這個檔屬於應用程式專案嗎？找不到所屬專案時當成應用程式（保守：維持完整檢查）。</summary>
    public bool IsInApplication(string file)
    {
        var full = Path.GetFullPath(file);
        foreach (var (dir, isApp, _) in _projects)
            if (full.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return isApp;

        return _projects.Count == 0;
    }

    /// <summary>
    /// 這個檔所在的專案是 WinUI 嗎？
    ///
    /// ⚠ 必須**逐專案**問，不能問「這棵樹裡有沒有 WinUI 專案」。
    /// 實測（ModernWpf）：那是一個 WPF 函式庫，但 repo 裡有一個
    /// `tools/theme-shadow/WinUIReferenceCapture` 輔助工具寫了 `<UseWinUI>true</UseWinUI>` ——
    /// 一個 csproj 就把整次掃描的合併優先序翻過來，shadowed 的贏家與輸家全部對調。
    /// </summary>
    public bool IsInWinUi(string file)
    {
        var full = Path.GetFullPath(file);
        foreach (var (dir, _, winui) in _projects)
            if (full.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return winui;

        return false;
    }

    /// <summary>這個檔所屬的專案目錄（找不到回 null）。</summary>
    private readonly Dictionary<string, HashSet<string>> _references = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _referencedBy = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _related = new(StringComparer.OrdinalIgnoreCase);

    private static void AddEdge(Dictionary<string, HashSet<string>> map, string a, string b)
    {
        if (!map.TryGetValue(a, out var set)) map[a] = set = new(StringComparer.OrdinalIgnoreCase);
        set.Add(b);
    }

    /// <summary>
    /// 這個專案參考的（可遞移）、參考這個專案的（可遞移），加上自己。**不含兄弟**：兩個專案只是參考同一個函式庫，不算相關。
    /// </summary>
    /// <remarks>
    /// ⚠ 「C# 讓它算有人用」的規則要限在這個範圍，不能是整棵樹、也不能只有自己：
    ///   maui-samples 的 231 個範例彼此獨立，一個範例的字串不能赦免另一個的鍵；
    ///   但 Flow Launcher 的主題檔在 App 專案、讀它們的 Theme.cs 在 Flow.Launcher.Core（App 參考它）——
    ///   只認同一個專案的話，SystemBG、WindowStyle 這些真的有人讀的鍵會被報成沒人用。
    /// ⚠ 第一版是「任一方向相連」（無向圖的連通塊）：在 monorepo 裡大家都參考同一個共用函式庫，
    ///   於是幾乎整棵樹都「相關」。實測 CommunityToolkit.Maui：範例 App 裡兩個沒人用的鍵，
    ///   被單元測試專案（跟範例只是都參考同一個函式庫）裡碰巧同名的字串救了回去。
    /// </remarks>
    public IReadOnlySet<string> Related(string? projectDir)
    {
        var key = projectDir ?? "";
        if (_related.TryGetValue(key, out var cached)) return cached;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { key };
        foreach (var map in new[] { _references, _referencedBy })
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { key };
            var stack = new Stack<string>();
            stack.Push(key);
            while (stack.Count > 0)
                if (map.TryGetValue(stack.Pop(), out var next))
                    foreach (var n in next)
                        if (visited.Add(n)) { seen.Add(n); stack.Push(n); }
        }
        _related[key] = seen;
        return seen;
    }

    public string? ProjectDirOf(string file)
    {
        var full = Path.GetFullPath(file);
        foreach (var (dir, _, _) in _projects)
            if (full.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return dir;

        return null;
    }

    private static bool IsApplicationProject(string proj, string dir, ILookup<string, string> xamlByDir)
    {
        try
        {
            var text = File.ReadAllText(proj);
            var m = OutputTypeRegex().Match(text);
            if (m.Success)
            {
                var t = m.Groups["t"].Value.Trim();
                // WinExe／Exe = 應用程式；Library（或沒寫）= 函式庫
                if (t.Equals("WinExe", StringComparison.OrdinalIgnoreCase)
                    || t.Equals("Exe", StringComparison.OrdinalIgnoreCase)) return true;
                if (t.Equals("Library", StringComparison.OrdinalIgnoreCase)) return false;
            }
        }
        catch (IOException) { /* 讀不到就靠 App.xaml 判斷 */ }

        // 退路：專案目錄底下有 App.xaml／Application 根元素
        foreach (var f in xamlByDir[dir])
            if (Path.GetFileName(f).Equals("App.xaml", StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    /// <summary>WinUI 3／UWP 專案的辨識：csproj 的宣告，不是 XAML 的寫法。</summary>
    private static bool IsWinUiProject(string proj)
    {
        try
        {
            var text = File.ReadAllText(proj);
            return text.Contains("<UseWinUI>true</UseWinUI>", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Microsoft.WindowsAppSDK", StringComparison.OrdinalIgnoreCase)
                || text.Contains("<TargetPlatformIdentifier>UAP", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Microsoft.UI.Xaml", StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException) { return false; }
    }

    [GeneratedRegex(@"<OutputType>(?<t>[^<]*)</OutputType>", RegexOptions.IgnoreCase)]
    private static partial Regex OutputTypeRegex();
}
