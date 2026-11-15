using XamlKeys.Core;

namespace XamlKeys.Tests;

/// <summary>
/// 第十六輪語料（2026-11-16）：ControlzEx、XamlBehaviorsWpf、CommunityToolkit.Windows、Text-Grab、TreeDataGrid、NodeEditor。
/// 歸因在 docs/實查歸因-2026-11-14.md 的「第十六輪」。
/// </summary>
public class SixteenthRoundTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("xamlkeys-round16");
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

    private const string Xaml = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";
    private const string WpfExe = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType><UseWPF>true</UseWPF></PropertyGroup></Project>";
    private const string WinUiExe = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType><UseWinUI>true</UseWinUI></PropertyGroup></Project>";

    private static List<string> Keys(AnalysisResult r, FindingKind k) =>
        r.Findings.Where(f => f.Kind == k).Select(f => f.Key).Distinct().Order(StringComparer.Ordinal).ToList();

    // ── 98：執行期主題樣板（{{佔位字}}） ─────────────────────────────────

    private static Dictionary<string, string> Showcase(string template) => new()
    {
        // ControlzEx.Showcase：Themes/Themes/Theme.Template.xaml 由 ThemeManager 填好佔位字、載入、加進 Application.Resources
        ["Showcase/Showcase.csproj"] = WpfExe,
        ["Showcase/App.xaml"] = $"<Application {Xaml}><Application.Resources><ResourceDictionary/></Application.Resources></Application>",
        ["Showcase/Themes/Themes/Theme.Template.xaml"] = template,
        ["Showcase/MainWindow.xaml"] = $"<Window {Xaml}><Border Background=\"{{DynamicResource Control.MouseOver.Background}}\"/></Window>",
    };

    [Fact]
    public void A_theme_template_with_placeholders_is_loaded_at_runtime_into_application_scope()
    {
        var r = Analyze(Showcase($"<ResourceDictionary {Xaml}><x:String x:Key=\"Theme.Name\">{{{{ThemeName}}}}</x:String>" +
                                 "<SolidColorBrush x:Key=\"Control.MouseOver.Background\" Color=\"{{ControlzEx.Colors.Gray8}}\"/></ResourceDictionary>"));

        Assert.DoesNotContain(r.Findings, f => f.Kind is FindingKind.OrphanDictionary or FindingKind.OutOfScope);
        Assert.Equal(1, r.RuntimeLoadedDictionaries);
    }

    [Fact]
    public void A_dictionary_without_placeholders_is_still_an_orphan()
    {
        var r = Analyze(Showcase($"<ResourceDictionary {Xaml}><x:String x:Key=\"Theme.Name\">Light</x:String>" +
                                 "<SolidColorBrush x:Key=\"Control.MouseOver.Background\" Color=\"Gray\"/></ResourceDictionary>"));

        Assert.Contains(r.Findings, f => f.Kind == FindingKind.OrphanDictionary);
        Assert.Equal(0, r.RuntimeLoadedDictionaries);
    }

    [Fact]
    public void A_merged_template_is_not_counted_again()
    {
        // 有人合併就照合併算，不是「執行期才載入」
        var files = Showcase($"<ResourceDictionary {Xaml}><x:String x:Key=\"Theme.Name\">{{{{ThemeName}}}}</x:String>" +
                             "<SolidColorBrush x:Key=\"Control.MouseOver.Background\" Color=\"Gray\"/></ResourceDictionary>");
        files["Showcase/App.xaml"] = $"<Application {Xaml}><Application.Resources><ResourceDictionary Source=\"Themes/Themes/Theme.Template.xaml\"/></Application.Resources></Application>";
        var r = Analyze(files);

        Assert.Equal(0, r.RuntimeLoadedDictionaries);
    }

    // ── 99：WinUI 的 ThemeResource 找不到 = 剖析例外，跟 StaticResource 一樣 ─────

    [Fact]
    public void A_missing_ThemeResource_in_an_app_is_gated_like_StaticResource()
    {
        // CommunityToolkit.Windows 的 TabbedCommandBarItem：{ThemeResource Transparent}
        var r = Analyze(new()
        {
            ["App/App.csproj"] = WinUiExe,
            ["App/App.xaml"] = $"<Application {Xaml}><Application.Resources><ResourceDictionary/></Application.Resources></Application>",
            ["App/MainPage.xaml"] = $"<Page {Xaml}><Border BorderBrush=\"{{ThemeResource Transparent}}\"/></Page>",
        });

        var f = Assert.Single(r.Findings, f => f.Key == "Transparent");
        Assert.Equal(FindingKind.DanglingStatic, f.Kind);
        Assert.Equal(Severity.Fail, f.Severity);
        Assert.Contains("ThemeResource throws", f.Message);
    }

    [Fact]
    public void A_missing_ThemeResource_in_a_library_is_a_warning()
    {
        var r = Analyze(new()
        {
            ["Lib/Lib.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><UseWinUI>true</UseWinUI></PropertyGroup></Project>",
            ["Lib/Themes/Generic.xaml"] = $"<ResourceDictionary {Xaml}><Style x:Key=\"S\" TargetType=\"Button\"><Setter Property=\"BorderBrush\" Value=\"{{ThemeResource Transparent}}\"/></Style></ResourceDictionary>",
        });

        var f = Assert.Single(r.Findings, f => f.Key == "Transparent");
        Assert.Equal(FindingKind.DanglingStatic, f.Kind);
        Assert.Equal(Severity.Warn, f.Severity);
    }

    [Fact]
    public void A_ThemeResource_named_in_code_is_not_excused()
    {
        // DynamicResource 的放寬（名字出現在程式裡，可能執行期才寫進去）不適用：ThemeResource 載入當下就要找得到
        var r = Analyze(new()
        {
            ["App/App.csproj"] = WinUiExe,
            ["App/App.xaml"] = $"<Application {Xaml}><Application.Resources><ResourceDictionary/></Application.Resources></Application>",
            ["App/MainPage.xaml"] = $"<Page {Xaml}><Border Background=\"{{ThemeResource AccentStrip}}\"/></Page>",
            ["App/Theme.cs"] = "class Theme { string Name = \"AccentStrip\"; }",
        });

        Assert.Equal(["AccentStrip"], Keys(r, FindingKind.DanglingStatic));
    }

    // ── 100：UWP 系統內建控制項的佈景資源 ───────────────────────────────

    private static Dictionary<string, string> InkSample(string marker) => new()
    {
        // Windows-universal-samples 的 SimpleInk：{ThemeResource InkToolbarButtonBackgroundThemeBrush}（Windows SDK 的 generic.xaml 才有）
        ["Ink/Ink.csproj"] = $"<Project><PropertyGroup><OutputType>AppContainerExe</OutputType>{marker}</PropertyGroup></Project>",
        ["Ink/App.xaml"] = $"<Application {Xaml}><Application.Resources><ResourceDictionary/></Application.Resources></Application>",
        ["Ink/Scenario7.xaml"] = $"<Page {Xaml}><StackPanel><Button Background=\"{{ThemeResource InkToolbarButtonBackgroundThemeBrush}}\"/>" +
                                 "<Button Background=\"{ThemeResource NoSuchBrush}\"/></StackPanel></Page>",
    };

    [Fact]
    public void A_UWP_inbox_control_resource_is_external_but_a_typo_is_still_dangling()
    {
        var r = Analyze(InkSample("<TargetPlatformIdentifier>UAP</TargetPlatformIdentifier>"));

        Assert.Equal(["InkToolbarButtonBackgroundThemeBrush"], Keys(r, FindingKind.External));
        Assert.Equal(["NoSuchBrush"], Keys(r, FindingKind.DanglingStatic));
    }

    [Fact]
    public void Outside_UWP_the_inbox_control_resource_is_dangling()
    {
        var r = Analyze(InkSample("<UseWinUI>true</UseWinUI>"));

        Assert.Equal(["InkToolbarButtonBackgroundThemeBrush", "NoSuchBrush"], Keys(r, FindingKind.DanglingStatic));
    }

    [Fact]
    public void A_cpp_Windows_Store_project_is_UWP()
    {
        var files = InkSample("");
        files.Remove("Ink/Ink.csproj");
        files["Ink/Ink.vcxproj"] = "<Project><PropertyGroup><ApplicationType>Windows Store</ApplicationType><AppContainerApplication>true</AppContainerApplication>" +
                                   "<ConfigurationType>Application</ConfigurationType></PropertyGroup></Project>";
        var r = Analyze(files);

        Assert.Equal(["InkToolbarButtonBackgroundThemeBrush"], Keys(r, FindingKind.External));
    }

    [Fact]
    public void A_shared_page_linked_into_a_UWP_project_counts_as_UWP()
    {
        // SimpleInk 的 shared/Scenario7.xaml 由 cs、cpp 專案用 Link 連進去
        var r = Analyze(new()
        {
            ["SimpleInk/cs/SimpleInk.csproj"] = "<Project><PropertyGroup><OutputType>AppContainerExe</OutputType><TargetPlatformIdentifier>UAP</TargetPlatformIdentifier></PropertyGroup>" +
                                                "<ItemGroup><Page Include=\"..\\shared\\Scenario7.xaml\"><Link>Scenario7.xaml</Link></Page></ItemGroup></Project>",
            ["SimpleInk/cs/App.xaml"] = $"<Application {Xaml}><Application.Resources><ResourceDictionary/></Application.Resources></Application>",
            ["SimpleInk/shared/Scenario7.xaml"] = $"<Page {Xaml}><Button Background=\"{{ThemeResource InkToolbarButtonForegroundThemeBrush}}\"/></Page>",
        });

        Assert.Equal(["InkToolbarButtonForegroundThemeBrush"], Keys(r, FindingKind.External));
    }

    [Fact]
    public void A_page_included_from_outside_without_a_Link_belongs_to_that_project()
    {
        // SimpleInk 的 cpp 專案：<Page Include="..\\shared\\Scenario7.xaml" />（C++ 專案不寫 Link）
        var r = Analyze(new()
        {
            ["SimpleInk/cpp/SimpleInk.vcxproj"] = "<Project><PropertyGroup><ApplicationType>Windows Store</ApplicationType><ConfigurationType>Application</ConfigurationType></PropertyGroup>" +
                                                  "<ItemGroup><Page Include=\"..\\shared" + "\\Scenario7.xaml\" /></ItemGroup></Project>",
            ["SimpleInk/cpp/App.xaml"] = $"<Application {Xaml}><Application.Resources><ResourceDictionary/></Application.Resources></Application>",
            ["SimpleInk/shared/Scenario7.xaml"] = $"<Page {Xaml}><Button Background=\"{{ThemeResource InkToolbarButtonForegroundThemeBrush}}\"/></Page>",
        });

        Assert.Equal(["InkToolbarButtonForegroundThemeBrush"], Keys(r, FindingKind.External));
    }
}
