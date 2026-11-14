using XamlKeys.Core;

namespace XamlKeys.Tests;

/// <summary>
/// 設定選項的**行為**測試。
///
/// `ConfigAndBaselineTests` 測的是「設定檔讀不讀得進來」；這裡測的是「讀進來之後有沒有作用」。
/// 2026-11-14 發版前從頭掃描時發現：`themeFiles`、`exclude`、`dialect` 三個選項
/// 已經寫進凍結契約，卻沒有任何一條測試證明它們真的會動 ——
/// 凍結一個從沒驗證過的功能，等於承諾一個未知數。
/// </summary>
public class ConfigBehaviourTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("xamlkeys-cfgb");
    public void Dispose() => _dir.Delete(true);

    private AnalysisResult Analyze(Dictionary<string, string> files, Config? cfg = null)
    {
        foreach (var (rel, content) in files)
        {
            var path = Path.Combine(_dir.FullName, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
        return Analyzer.Run(_dir.FullName, cfg ?? new Config());
    }

    private const string AppProj = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup></Project>";

    private static string Dict(string body) => $"""
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
        {body}
        </ResourceDictionary>
        """;

    private static string AppMerging(params string[] sources) => $"""
        <Application xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Application.Resources>
            <ResourceDictionary>
              <ResourceDictionary.MergedDictionaries>
                {string.Join("\n", sources.Select(s => $"<ResourceDictionary Source=\"{s}\" />"))}
              </ResourceDictionary.MergedDictionaries>
            </ResourceDictionary>
          </Application.Resources>
        </Application>
        """;

    private static string Window(string body) => $"""
        <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
        {body}
        </Window>
        """;

    // ── themeFiles ─────────────────────────────────────────────────────────

    private Dictionary<string, string> TwoThemeFiles() => new()
    {
        ["App.csproj"] = AppProj,
        ["App.xaml"] = AppMerging("Themes/Dark.xaml", "Themes/Light.xaml"),
        // 兩個檔各有一個共用的鍵；Dark 多一個只有它有的
        ["Themes/Dark.xaml"] = Dict("""
            <SolidColorBrush x:Key="Fg" Color="#FFF" />
            <SolidColorBrush x:Key="OnlyInDark" Color="#111" />
            """),
        ["Themes/Light.xaml"] = Dict("""<SolidColorBrush x:Key="Fg" Color="#000" />"""),
        ["Main.xaml"] = Window("""
            <Border Background="{StaticResource Fg}" />
            <Border Background="{StaticResource OnlyInDark}" />
            """),
    };

    [Fact]
    public void Without_themeFiles_two_theme_files_look_like_one_shadowing_the_other()
    {
        // 這是 themeFiles 存在的理由：工具不知道這兩個檔是「二選一」
        var r = Analyze(TwoThemeFiles());
        Assert.Contains(r.Findings, f => f.Kind == FindingKind.Shadowed && f.Key == "Fg");
    }

    [Fact]
    public void With_themeFiles_the_two_sides_no_longer_shadow_each_other()
    {
        var cfg = new Config();
        cfg.DarkFiles.Add("Themes/Dark");
        cfg.LightFiles.Add("Themes/Light");

        var r = Analyze(TwoThemeFiles(), cfg);

        Assert.DoesNotContain(r.Findings, f => f.Kind is FindingKind.Shadowed or FindingKind.ShadowedIdentical);
        Assert.Equal([ThemeSide.Dark, ThemeSide.Light], r.Sides);
    }

    [Fact]
    public void With_themeFiles_a_key_defined_on_one_side_only_is_reported_for_the_other_side()
    {
        // 只在深色檔定義的鍵，切到淺色主題時解析不到 —— 這正是分側要抓的東西
        var cfg = new Config();
        cfg.DarkFiles.Add("Themes/Dark");
        cfg.LightFiles.Add("Themes/Light");

        var r = Analyze(TwoThemeFiles(), cfg);

        var f = Assert.Single(r.Findings, x => x.Key == "OnlyInDark");
        Assert.Equal(ThemeSide.Light, f.Side);
        Assert.Contains("light side", f.Message);
    }

    // ── exclude ────────────────────────────────────────────────────────────

    [Fact]
    public void Exclude_keeps_matching_paths_out_of_the_scan()
    {
        var files = new Dictionary<string, string>
        {
            ["App.csproj"] = AppProj,
            ["App.xaml"] = AppMerging(),
            ["Generated/Broken.xaml"] = Dict("""
                <SolidColorBrush x:Key="Dup" Color="#1" />
                <SolidColorBrush x:Key="Dup" Color="#2" />
                """),
            ["Kept.xaml"] = Dict("""<SolidColorBrush x:Key="K" Color="#3" />"""),
        };

        Assert.Contains(Analyze(files).Findings, f => f.Kind == FindingKind.DuplicateKey);

        var cfg = new Config();
        cfg.Exclude.Add("Generated");
        var r = Analyze(files, cfg);

        Assert.DoesNotContain(r.Findings, f => f.Kind == FindingKind.DuplicateKey);
        Assert.Equal(2, r.XamlFiles);      // App.xaml 與 Kept.xaml
        Assert.True(cfg.HasOverrides, "an exclusion changes the result, so it must show in the summary");
    }

    // ── dialect ────────────────────────────────────────────────────────────

    private Dictionary<string, string> TwoMergedDefinitions() => new()
    {
        ["App.csproj"] = AppProj,
        ["App.xaml"] = AppMerging("First.xaml", "Second.xaml"),
        ["First.xaml"] = Dict("""<SolidColorBrush x:Key="K" Color="#111" />"""),
        ["Second.xaml"] = Dict("""<SolidColorBrush x:Key="K" Color="#222" />"""),
        ["Main.xaml"] = Window("""<Border Background="{StaticResource K}" />"""),
    };

    [Fact]
    public void Under_the_wpf_dialect_the_last_merged_dictionary_wins()
    {
        var r = Analyze(TwoMergedDefinitions(), new Config { Dialect = XamlDialect.Wpf, DialectAutoDetected = false });

        var f = Assert.Single(r.Findings, x => x.Kind == FindingKind.Shadowed);
        Assert.Contains("First.xaml", f.Location.File);           // 輸家
        Assert.Contains("Second.xaml", f.Related!.Value.File);    // 贏家
    }

    [Fact]
    public void Forcing_the_winui_dialect_swaps_the_winner_and_the_loser()
    {
        // ⚠ 兩種方言的合併優先序相反。這條測試守住「明寫 dialect 真的會改變答案」——
        //   如果它跟上一條得到一樣的結果，表示 --dialect 是個沒有作用的旗標。
        var r = Analyze(TwoMergedDefinitions(), new Config { Dialect = XamlDialect.WinUi, DialectAutoDetected = false });

        var f = Assert.Single(r.Findings, x => x.Kind == FindingKind.Shadowed);
        Assert.Contains("Second.xaml", f.Location.File);          // 輸家
        Assert.Contains("First.xaml", f.Related!.Value.File);     // 贏家
        Assert.Contains("WinUI", f.Message);
    }
}
