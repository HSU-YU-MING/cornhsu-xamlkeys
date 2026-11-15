using XamlKeys.Core;

namespace XamlKeys.Tests;

/// <summary>
/// 第九輪語料（2026-11-15）：HandBrake、Playnite、NETworkManager、Bloxstrap、v2rayN、VladislavAntonyuk/MauiSamples、dotnet/maui。
/// 歸因在 docs/實查歸因-2026-11-14.md 的「第九輪語料」。
/// </summary>
public class NinthRoundTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("xamlkeys-round9");
    public void Dispose() => _dir.Delete(true);

    private AnalysisResult Analyze(Dictionary<string, string> files)
    {
        foreach (var (rel, content) in files)
        {
            var path = Path.Combine(_dir.FullName, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
        return Analyzer.Run(_dir.FullName, new Config());
    }

    private const string Wpf = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";
    private const string Maui = "xmlns=\"http://schemas.microsoft.com/dotnet/2021/maui\" xmlns:x=\"http://schemas.microsoft.com/winfx/2009/xaml\"";
    private const string Ava = "xmlns=\"https://github.com/avaloniaui\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";
    private const string AppProj = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup></Project>";
    private const string LibProj = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Library</OutputType></PropertyGroup></Project>";

    private static List<string> Keys(AnalysisResult r, FindingKind k) =>
        r.Findings.Where(f => f.Kind == k).Select(f => f.Key).Distinct().Order(StringComparer.Ordinal).ToList();

    private static string AppMerging(string merged) =>
        $"<Application {Wpf} xmlns:md=\"http://materialdesigninxaml.net/winfx/xaml/themes\" xmlns:ui=\"http://schemas.lepo.co/wpfui/2022/xaml\">" +
        $"<Application.Resources><ResourceDictionary><ResourceDictionary.MergedDictionaries>{merged}" +
        "</ResourceDictionary.MergedDictionaries></ResourceDictionary></Application.Resources></Application>";

    // ── 74：MahApps.Metro、MaterialDesignThemes、WPF-UI 的鍵名清單 ─────────

    [Theory]
    [InlineData("<ResourceDictionary Source=\"pack://application:,,,/MahApps.Metro;component/Styles/Controls.xaml\"/>", "MahApps.Brushes.Accent", "mahapps")]
    [InlineData("<md:BundledTheme BaseTheme=\"Dark\" PrimaryColor=\"Blue\" SecondaryColor=\"Lime\"/>", "MaterialDesignFlatButton", "materialdesign")]
    [InlineData("<ResourceDictionary Source=\"pack://application:,,,/MaterialDesignThemes.Wpf;component/Themes/MaterialDesign2.Defaults.xaml\"/>", "MaterialDesignFlatButton", "materialdesign")]
    [InlineData("<ui:ControlsDictionary/>", "TextFillColorPrimaryBrush", "wpfui")]
    public void A_library_theme_s_keys_resolve_when_the_tree_merges_it(string merge, string key, string pack)
    {
        var r = Analyze(new()
        {
            ["App/App.csproj"] = AppProj,
            ["App/App.xaml"] = AppMerging(merge),
            ["App/Main.xaml"] = $"<Window {Wpf}><Border Background=\"{{DynamicResource {key}}}\"/></Window>",
        });

        Assert.DoesNotContain(r.Findings, f => f.Kind is FindingKind.External or FindingKind.DanglingDynamic);
        Assert.Contains(pack, r.ThemePacksInUse);
        Assert.Equal(100.0, r.Coverage);
    }

    [Fact]
    public void A_key_the_library_does_not_have_is_still_external()
    {
        var r = Analyze(new()
        {
            ["App/App.csproj"] = AppProj,
            ["App/App.xaml"] = AppMerging("<ResourceDictionary Source=\"pack://application:,,,/MahApps.Metro;component/Styles/Controls.xaml\"/>"),
            ["App/Main.xaml"] = $"<Window {Wpf}><Border Background=\"{{StaticResource MahApps.Brushes.NoSuchThing}}\"/></Window>",
        });

        Assert.Equal(["MahApps.Brushes.NoSuchThing"], Keys(r, FindingKind.External));
    }

    [Fact]
    public void Library_keys_do_not_apply_when_the_tree_does_not_merge_the_library()
    {
        var r = Analyze(new()
        {
            ["App/App.csproj"] = AppProj,
            ["App/App.xaml"] = $"<Application {Wpf}><Application.Resources><SolidColorBrush x:Key=\"Own\" Color=\"Red\"/></Application.Resources></Application>",
            ["App/Main.xaml"] = $"<Window {Wpf} Background=\"{{StaticResource Own}}\"><Border Background=\"{{DynamicResource MahApps.Brushes.Accent}}\"/></Window>",
        });

        Assert.Equal(["MahApps.Brushes.Accent"], Keys(r, FindingKind.DanglingDynamic));
    }

    [Fact]
    public void The_WPF_UI_list_does_not_apply_to_a_WinUI_file()
    {
        // 三套清單都限 WPF：WinUI 的函式庫也可能有叫 ControlsDictionary 的元素
        var r = Analyze(new()
        {
            ["App/App.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType><UseWinUI>true</UseWinUI></PropertyGroup></Project>",
            ["App/App.xaml"] = AppMerging("<ui:ControlsDictionary/>"),
            ["App/MainPage.xaml"] = $"<Page {Wpf}><Border Background=\"{{ThemeResource ApplicationBackgroundBrush}}\"/></Page>",
        });

        Assert.DoesNotContain("wpfui", r.ThemePacksInUse);
        Assert.Equal(["ApplicationBackgroundBrush"], Keys(r, FindingKind.External));
    }

    // ── 75：WPF 的命名空間＋using: 是 WinUI 檔 ─────────────────────────────

    [Fact]
    public void A_presentation_namespace_file_with_a_using_mapping_is_WinUI()
    {
        // dotnet/maui：src/Controls/src/Core/Platform/Windows/Styles/*.xaml（xmlns:uwp="using:Microsoft.Maui.Controls.Platform"）
        var r = Analyze(new()
        {
            ["Core/Core.csproj"] = LibProj,
            ["Core/Platform/Windows/Styles/PageStyle.xaml"] =
                $"<ResourceDictionary {Wpf} xmlns:uwp=\"using:Microsoft.Maui.Controls.Platform\">" +
                "<Style x:Key=\"PageStyle\" TargetType=\"uwp:PageControl\"><Setter Property=\"Foreground\" Value=\"{ThemeResource TextFillColorPrimaryBrush}\"/></Style></ResourceDictionary>",
        });

        Assert.DoesNotContain(r.Findings, f => f.Kind is FindingKind.DanglingDynamic or FindingKind.DanglingStatic);
        Assert.True(r.FrameworkProvided >= 1);
    }

    [Fact]
    public void An_Avalonia_file_with_a_using_mapping_is_not_WinUI()
    {
        var r = Analyze(new()
        {
            ["App/App.csproj"] = AppProj,
            ["App/App.axaml"] = $"<Application {Ava}><Application.Resources><SolidColorBrush x:Key=\"Own\" Color=\"Red\"/></Application.Resources></Application>",
            ["App/Main.axaml"] = $"<UserControl {Ava} xmlns:vm=\"using:App.ViewModels\" Background=\"{{StaticResource Own}}\"><Border Background=\"{{DynamicResource TextFillColorPrimaryBrush}}\"/></UserControl>",
        });

        Assert.Equal(["TextFillColorPrimaryBrush"], Keys(r, FindingKind.DanglingDynamic));
    }

    // ── 76：不屬於任何專案的 App.xaml 不接進別的專案 ─────────────────────

    [Fact]
    public void An_App_xaml_outside_every_project_does_not_join_a_project_s_scope()
    {
        // dotnet/maui：src/ProfiledAot/src/maui/App.xaml 沒有 csproj，合併一本不存在的 Styles.xaml
        var r = Analyze(new()
        {
            ["Loose/App.xaml"] = $"<Application {Maui}><Application.Resources><ResourceDictionary><ResourceDictionary.MergedDictionaries>" +
                                 "<ResourceDictionary Source=\"Resources/Styles/Styles.xaml\"/></ResourceDictionary.MergedDictionaries></ResourceDictionary></Application.Resources></Application>",
            ["Sample/Sample.csproj"] = AppProj,
            ["Sample/App.xaml"] = $"<Application {Maui}><Application.Resources><Color x:Key=\"Own\">Red</Color></Application.Resources></Application>",
            ["Sample/Page.xaml"] = $"<ContentPage {Maui} BackgroundColor=\"{{StaticResource Own}}\"><Label TextColor=\"{{StaticResource SecondaryColor}}\"/></ContentPage>",
        });

        Assert.Equal(["SecondaryColor"], Keys(r, FindingKind.DanglingStatic));
    }

    [Fact]
    public void A_file_outside_every_project_still_sees_a_project_s_App_xaml()
    {
        var r = Analyze(new()
        {
            ["App/App.csproj"] = AppProj,
            ["App/App.xaml"] = $"<Application {Maui}><Application.Resources><ResourceDictionary><ResourceDictionary.MergedDictionaries>" +
                               "<ResourceDictionary Source=\"Resources/Styles/Styles.xaml\"/></ResourceDictionary.MergedDictionaries></ResourceDictionary></Application.Resources></Application>",
            ["Loose/Page.xaml"] = $"<ContentPage {Maui}><Label TextColor=\"{{StaticResource SecondaryColor}}\"/></ContentPage>",
        });

        Assert.Empty(Keys(r, FindingKind.DanglingStatic));
        Assert.Equal(["SecondaryColor"], Keys(r, FindingKind.External));
    }

    [Fact]
    public void A_file_outside_every_project_still_sees_every_App_xaml()
    {
        // 推不出專案的引用維持保守：全部的 Application 作用域都接
        var r = Analyze(new()
        {
            ["Loose/App.xaml"] = $"<Application {Maui}><Application.Resources><ResourceDictionary><ResourceDictionary.MergedDictionaries>" +
                                 "<ResourceDictionary Source=\"Resources/Styles/Styles.xaml\"/></ResourceDictionary.MergedDictionaries></ResourceDictionary></Application.Resources></Application>",
            ["Loose/Page.xaml"] = $"<ContentPage {Maui}><Label TextColor=\"{{StaticResource SecondaryColor}}\"/></ContentPage>",
        });

        Assert.Empty(Keys(r, FindingKind.DanglingStatic));
        Assert.Equal(["SecondaryColor"], Keys(r, FindingKind.External));
    }
}
