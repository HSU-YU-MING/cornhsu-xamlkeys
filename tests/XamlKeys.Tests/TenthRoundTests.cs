using XamlKeys.Core;

namespace XamlKeys.Tests;

/// <summary>
/// 第十輪語料（2026-11-15）：DesktopWidgets3、Uno.Samples、AvaloniaEdit、SteamTools（Watt Toolkit）、Windows-universal-samples、
/// ImageGlass、PicView。歸因在 docs/實查歸因-2026-11-14.md 的「第十輪語料」。
/// </summary>
public class TenthRoundTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("xamlkeys-round10");
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
    private const string Ava = "xmlns=\"https://github.com/avaloniaui\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";
    private const string AvaApp = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup></Project>";

    private static List<string> Keys(AnalysisResult r, FindingKind k) =>
        r.Findings.Where(f => f.Kind == k).Select(f => f.Key).Distinct().Order(StringComparer.Ordinal).ToList();

    /// <summary>Windows-universal-samples 的形狀：頁面在 shared/，App.xaml 與 Styles.xaml 在 SharedContent/xaml/，
    /// 都用 $(SharedContentDir) 連進 UWP 專案。</summary>
    private static Dictionary<string, string> UwpSample(string sharedContentDir, string pageRef = "SampleHeaderTextStyle") => new()
    {
        ["LICENSE"] = "MIT",
        ["SharedContent/xaml/App.xaml"] = $"<Application {Wpf}><Application.Resources><ResourceDictionary><ResourceDictionary.MergedDictionaries>" +
                                          "<ResourceDictionary Source=\"/Styles/Styles.xaml\"/></ResourceDictionary.MergedDictionaries></ResourceDictionary></Application.Resources></Application>",
        ["SharedContent/xaml/Styles.xaml"] = $"<ResourceDictionary {Wpf}><Style x:Key=\"SampleHeaderTextStyle\" TargetType=\"TextBlock\"/></ResourceDictionary>",
        ["Samples/Ink/shared/Scenario1.xaml"] = $"<Page {Wpf} x:Class=\"SDKTemplate.Scenario1\" Background=\"{{ThemeResource ApplicationPageBackgroundThemeBrush}}\"><TextBlock Style=\"{{StaticResource {pageRef}}}\"/></Page>",
        ["Samples/Ink/cs/Ink.csproj"] =
            "<Project><PropertyGroup>" +
            $"<SharedContentDir>{sharedContentDir}</SharedContentDir>" +
            "<OutputType>AppContainerExe</OutputType><TargetPlatformIdentifier>UAP</TargetPlatformIdentifier></PropertyGroup><ItemGroup>" +
            "<ApplicationDefinition Include=\"$(SharedContentDir)\\xaml\\App.xaml\"><Link>App.xaml</Link></ApplicationDefinition>" +
            "<Page Include=\"$(SharedContentDir)\\xaml\\Styles.xaml\"><Link>Styles\\Styles.xaml</Link></Page>" +
            "<Page Include=\"..\\shared\\Scenario1.xaml\"><Link>Scenario1.xaml</Link></Page>" +
            "</ItemGroup></Project>",
    };

    private const string FileAbove = "$([MSBuild]::GetDirectoryNameOfFileAbove($(MSBuildThisFileDirectory), LICENSE))\\SharedContent";

    // ── 77：csproj 屬性與「連結進來的檔屬於連它的專案」────────────────────

    [Theory]
    [InlineData(FileAbove)]
    [InlineData("$(MSBuildThisFileDirectory)..\\..\\..\\SharedContent")]
    public void Shared_content_linked_through_a_project_property_resolves(string sharedContentDir)
    {
        var r = Analyze(UwpSample(sharedContentDir));

        Assert.DoesNotContain(r.Findings, f => f.Kind is FindingKind.External or FindingKind.DanglingStatic or FindingKind.DanglingDynamic
                                                or FindingKind.OutOfScope or FindingKind.OrphanDictionary);
        Assert.Equal(100.0, r.Coverage);
    }

    [Fact]
    public void A_linked_page_still_reports_a_key_nobody_defines()
    {
        // 連結進 WinUI 專案的頁面：WinUI 內建的鍵算框架提供，但真的沒人定義的鍵照樣是懸空
        var r = Analyze(UwpSample(FileAbove, "NoSuchStyle"));

        Assert.Equal(["NoSuchStyle"], Keys(r, FindingKind.DanglingStatic));
    }

    [Fact]
    public void A_named_resource_in_a_linked_page_is_used_by_the_code_behind_in_the_linking_project()
    {
        // shared/Scenario1.xaml 的 x:Name="HighlightedFaceBox"，code-behind 在 cs/Scenario1.xaml.cs
        var files = UwpSample(FileAbove);
        files["Samples/Ink/shared/Scenario1.xaml"] =
            $"<Page {Wpf} x:Class=\"SDKTemplate.Scenario1\"><Page.Resources><DataTemplate x:Name=\"HighlightedFaceBox\"><Border/></DataTemplate>" +
            "<DataTemplate x:Name=\"NobodyUsesThis\"><Border/></DataTemplate></Page.Resources>" +
            "<TextBlock Style=\"{StaticResource SampleHeaderTextStyle}\"/></Page>";
        files["Samples/Ink/cs/Scenario1.xaml.cs"] = "partial class Scenario1 { void A() { Show(this.HighlightedFaceBox); } }";
        var r = Analyze(files);

        Assert.Equal(["NobodyUsesThis"], Keys(r, FindingKind.Unused));
        Assert.Equal(1, r.KeysUsedAsFields);
    }

    [Fact]
    public void A_resource_WinUI_s_own_templates_use_without_defining_is_framework_provided()
    {
        // XamlPivot：{StaticResource ScrollViewerScrollBarlessTemplate} —— WinUI 自己的 Pivot 樣板也這樣引用（原生資源）
        var r = Analyze(new()
        {
            ["App/App.csproj"] = "<Project><PropertyGroup><OutputType>AppContainerExe</OutputType><TargetPlatformIdentifier>UAP</TargetPlatformIdentifier></PropertyGroup></Project>",
            ["App/App.xaml"] = $"<Application {Wpf}><Application.Resources><SolidColorBrush x:Key=\"Own\" Color=\"Red\"/></Application.Resources></Application>",
            ["App/Page.xaml"] = $"<Page {Wpf} Background=\"{{StaticResource Own}}\"><ScrollViewer Template=\"{{StaticResource ScrollViewerScrollBarlessTemplate}}\"/></Page>",
        });

        Assert.Empty(Keys(r, FindingKind.DanglingStatic));
    }

    [Fact]
    public void A_linked_page_does_not_see_an_unrelated_project_s_App_xaml()
    {
        // 別的範例的 App.xaml 合併一本讀不到的字典：連結進來的頁面不能因此把真的懸空降成 external
        var files = UwpSample(FileAbove, "NoSuchStyle");
        files["Other/vb/Other.csproj"] = "<Project><PropertyGroup><OutputType>AppContainerExe</OutputType><TargetPlatformIdentifier>UAP</TargetPlatformIdentifier></PropertyGroup></Project>";
        files["Other/vb/App.xaml"] = $"<Application {Wpf}><Application.Resources><ResourceDictionary><ResourceDictionary.MergedDictionaries>" +
                                     "<ResourceDictionary Source=\"/Styles/Missing.xaml\"/></ResourceDictionary.MergedDictionaries></ResourceDictionary></Application.Resources></Application>";
        var r = Analyze(files);

        Assert.Equal(["NoSuchStyle"], Keys(r, FindingKind.DanglingStatic));
    }

    [Theory]
    [InlineData("$(SomethingUndefined)\\SharedContent")]
    [InlineData("$([MSBuild]::GetDirectoryNameOfFileAbove($(MSBuildThisFileDirectory), NO_SUCH_FILE))\\SharedContent")]
    [InlineData("$([System.IO.Path]::GetFullPath('..'))\\SharedContent")]
    public void A_property_that_cannot_be_evaluated_is_not_guessed(string sharedContentDir)
    {
        var r = Analyze(UwpSample(sharedContentDir));

        Assert.True(r.Coverage < 100.0);
        Assert.DoesNotContain(r.Findings, f => f.Kind == FindingKind.DanglingStatic);
    }

    [Fact]
    public void The_file_above_search_stays_inside_the_scan_root()
    {
        // 要找的檔只在掃描根的上一層（範圍外）：不能往外找
        var probeName = "LICENSE-xamlkeys-probe-" + Guid.NewGuid().ToString("N");
        var parentProbe = Path.Combine(_dir.Parent!.FullName, probeName);
        File.WriteAllText(parentProbe, "x");
        try
        {
            var files = UwpSample($"$([MSBuild]::GetDirectoryNameOfFileAbove($(MSBuildThisFileDirectory), {probeName}))\\SharedContent");
            files.Remove("LICENSE");
            var r = Analyze(files);
            Assert.True(r.Coverage < 100.0);
        }
        finally { File.Delete(parentProbe); }
    }

    [Fact]
    public void A_VB_project_is_read()
    {
        var r = Analyze(new()
        {
            ["App/App.vbproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType><UseWPF>true</UseWPF></PropertyGroup></Project>",
            ["App/Styles/Styles.xaml"] = $"<ResourceDictionary {Wpf}><Style x:Key=\"Header\" TargetType=\"TextBlock\"/></ResourceDictionary>",
            ["App/App.xaml"] = $"<Application {Wpf}><Application.Resources><ResourceDictionary Source=\"/Styles/Styles.xaml\"/></Application.Resources></Application>",
            ["App/Main.xaml"] = $"<Window {Wpf}><TextBlock Style=\"{{StaticResource Header}}\"/></Window>",
        });

        Assert.Equal(100.0, r.Coverage);
        Assert.Empty(r.Findings);
        Assert.Equal(1, r.Projects);
    }

    [Fact]
    public void A_UWP_AppContainerExe_project_is_an_application()
    {
        var r = Analyze(new()
        {
            ["App/App.csproj"] = "<Project><PropertyGroup><OutputType>AppContainerExe</OutputType><TargetPlatformIdentifier>UAP</TargetPlatformIdentifier></PropertyGroup></Project>",
            ["App/App.xaml"] = $"<Application {Wpf}><Application.Resources><SolidColorBrush x:Key=\"Unused\" Color=\"Red\"/></Application.Resources></Application>",
            ["App/MainPage.xaml"] = $"<Page {Wpf}><TextBlock Style=\"{{StaticResource Missing}}\"/></Page>",
        });

        Assert.Equal(1, r.ApplicationProjects);
        Assert.Contains(r.Findings, f => f.Kind == FindingKind.DanglingStatic && f.Severity == Severity.Fail);
        Assert.Equal(["Unused"], Keys(r, FindingKind.Unused));
    }

    [Fact]
    public void A_linked_page_s_own_resources_use_the_linking_project_s_dialect()
    {
        // 連結進 UWP 專案的頁面有自己的 Page.Resources：那本字典也是 WinUI 的，WinUI 內建的鍵算框架提供
        var files = UwpSample(FileAbove);
        files["Samples/Ink/shared/Scenario1.xaml"] =
            $"<Page {Wpf} x:Class=\"SDKTemplate.Scenario1\"><Page.Resources><Style x:Key=\"Local\" TargetType=\"Grid\">" +
            "<Setter Property=\"Background\" Value=\"{ThemeResource ApplicationPageBackgroundThemeBrush}\"/></Style></Page.Resources>" +
            "<Grid Style=\"{StaticResource Local}\"><TextBlock Style=\"{StaticResource SampleHeaderTextStyle}\"/></Grid></Page>";
        var r = Analyze(files);

        Assert.DoesNotContain(r.Findings, f => f.Kind is FindingKind.DanglingDynamic or FindingKind.DanglingStatic or FindingKind.External);
    }

    // ── 79：不在 Resources 裡的 ResourceDictionary（圖表 Palette）─────────────

    [Fact]
    public void A_chart_palette_dictionary_is_its_own_scope_and_its_keys_are_not_unused()
    {
        // Silverlight／WPF Toolkit 的圖表：Palette 裡每一本字典定義 Background 與用它的 DataPointStyle
        var r = Analyze(new()
        {
            ["App/App.csproj"] = AvaApp,
            ["App/Charts.xaml"] =
                $"<Window {Wpf} xmlns:toolkit=\"clr-namespace:System.Windows.Controls.DataVisualization;assembly=DotNetProjects.DataVisualization.Toolkit\" " +
                "xmlns:charting=\"clr-namespace:System.Windows.Controls.DataVisualization.Charting;assembly=DotNetProjects.DataVisualization.Toolkit\">" +
                "<Window.Resources><Color x:Key=\"ChartColor1\">Red</Color></Window.Resources>" +
                "<charting:Chart><charting:Chart.Palette><toolkit:ResourceDictionaryCollection>" +
                "<ResourceDictionary><SolidColorBrush x:Key=\"Background\" Color=\"{StaticResource ChartColor1}\"/>" +
                "<Style x:Key=\"DataPointStyle\" TargetType=\"Control\"><Setter Property=\"Background\" Value=\"{StaticResource Background}\"/></Style></ResourceDictionary>" +
                "</toolkit:ResourceDictionaryCollection></charting:Chart.Palette></charting:Chart></Window>",
        });

        Assert.Empty(Keys(r, FindingKind.DanglingStatic));
        Assert.Empty(Keys(r, FindingKind.Unused));
        Assert.Equal(1, r.InlineDictionaryKeys);   // Background 有人引用（DataPointStyle），只有 DataPointStyle 需要豁免
    }

    [Fact]
    public void A_palette_dictionary_still_reports_a_key_nobody_defines()
    {
        var r = Analyze(new()
        {
            ["App/App.csproj"] = AvaApp,
            ["App/Charts.xaml"] =
                $"<Window {Wpf} xmlns:charting=\"clr-namespace:Charting\"><charting:Chart><charting:Chart.Palette><charting:PaletteCollection>" +
                "<ResourceDictionary><Style x:Key=\"DataPointStyle\" TargetType=\"Control\"><Setter Property=\"Background\" Value=\"{StaticResource NoSuchBrush}\"/></Style></ResourceDictionary>" +
                "</charting:PaletteCollection></charting:Chart.Palette></charting:Chart></Window>",
        });

        Assert.Equal(["NoSuchBrush"], Keys(r, FindingKind.DanglingStatic));
    }

    // ── 78：FluentAvalonia、AvaloniaEdit 的鍵名清單 ─────────────────────

    [Theory]
    [InlineData("<sty:FluentAvaloniaTheme/>", "ControlCornerRadius", "fluent-avalonia")]
    [InlineData("<StyleInclude Source=\"avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml\"/>", "SearchPanelFontSize", "avaloniaedit")]
    public void Avalonia_library_theme_keys_resolve_when_the_tree_merges_them(string include, string key, string pack)
    {
        var r = Analyze(new()
        {
            ["App/App.csproj"] = AvaApp,
            ["App/App.axaml"] = $"<Application {Ava} xmlns:sty=\"using:FluentAvalonia.Styling\"><Application.Styles>{include}</Application.Styles></Application>",
            ["App/Main.axaml"] = $"<UserControl {Ava}><Border CornerRadius=\"{{DynamicResource {key}}}\"/></UserControl>",
        });

        Assert.DoesNotContain(r.Findings, f => f.Kind is FindingKind.External or FindingKind.DanglingDynamic);
        Assert.Contains(pack, r.ThemePacksInUse);
    }
}
