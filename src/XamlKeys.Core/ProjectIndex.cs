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
    private readonly List<(string Dir, bool IsApplication)> _projects = [];

    public ProjectIndex(string root, IEnumerable<string> projectFiles, IEnumerable<string> xamlFiles)
    {
        var xamlByDir = xamlFiles.ToLookup(f => Path.GetDirectoryName(Path.GetFullPath(f)) ?? "",
                                           StringComparer.OrdinalIgnoreCase);

        foreach (var proj in projectFiles)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(proj));
            if (dir is null) continue;

            _projects.Add((dir, IsApplicationProject(proj, dir, xamlByDir)));
            if (IsWinUiProject(proj)) AnyWinUiProject = true;
        }

        // 最深的目錄排前面：巢狀專案要先比對到自己，不是父專案
        _projects.Sort((a, b) => b.Dir.Length.CompareTo(a.Dir.Length));

        HasAnyApplication = _projects.Any(p => p.IsApplication);
        // 連一個 csproj 都沒有時（純資源資料夾、sparse clone），無從判斷 ——
        // 退回「當成應用程式」，因為那是比較常見的掃描對象。
        if (_projects.Count == 0) HasAnyApplication = true;
    }

    public bool HasAnyApplication { get; }

    /// <summary>有任何專案是 WinUI 3／UWP。
    /// ⚠ 這是判斷方言的**主要**依據，不要只靠 {ThemeResource} ——
    /// wpfui 是 WPF 函式庫但刻意沿用 WinUI 的鍵名與寫法，只看 XAML 會判錯，
    /// 而方言判錯會把合併字典的優先序整個倒過來（shadowed 的贏家與輸家對調）。</summary>
    public bool AnyWinUiProject { get; private set; }

    public int ProjectCount => _projects.Count;
    public int ApplicationCount => _projects.Count(p => p.IsApplication);

    /// <summary>這個檔屬於應用程式專案嗎？找不到所屬專案時當成應用程式（保守：維持完整檢查）。</summary>
    public bool IsInApplication(string file)
    {
        var full = Path.GetFullPath(file);
        foreach (var (dir, isApp) in _projects)
            if (full.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return isApp;

        return _projects.Count == 0;
    }

    /// <summary>這個檔所屬的專案目錄（找不到回 null）。</summary>
    public string? ProjectDirOf(string file)
    {
        var full = Path.GetFullPath(file);
        foreach (var (dir, _) in _projects)
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
