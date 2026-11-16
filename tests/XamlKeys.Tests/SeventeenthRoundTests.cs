using XamlKeys.Core;

namespace XamlKeys.Tests;

/// <summary>
/// 第十七輪語料（2026-11-16）：OpenUtau、ChocolateyGUI、iNKORE.UI.WPF.Modern、Panuon.WPF.UI、
/// accessibility-insights-windows、FluentTerminal、Unigram。歸因在 docs/實查歸因-2026-11-14.md 的「第十七輪」。
/// </summary>
public class SeventeenthRoundTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("xamlkeys-round17");
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
    private const string Av = "xmlns=\"https://github.com/avaloniaui\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";
    private const string WpfExe = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType><UseWPF>true</UseWPF></PropertyGroup></Project>";
    private const string AvExe = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup><ItemGroup><PackageReference Include=\"Avalonia\" Version=\"11.0.0\"/></ItemGroup></Project>";

    private static List<string> Keys(AnalysisResult r, FindingKind k) =>
        r.Findings.Where(f => f.Kind == k).Select(f => f.Key).Distinct().Order(StringComparer.Ordinal).ToList();

    private static List<string> Orphans(AnalysisResult r, string project) =>
        r.Findings.Where(f => f.Kind == FindingKind.OrphanDictionary)
                  .Select(f => f.Location.File.Replace(project + "/", "")).Order(StringComparer.Ordinal).ToList();

    // ── 102：根元素是自己寫的 Application 子類別 ─────────────────────────

    [Fact]
    public void A_root_whose_resources_are_written_Application_Resources_is_the_application()
    {
        // Unigram：<navigation:BootStrapper x:Class="Telegram.App"><Application.Resources>…
        var r = Analyze(new()
        {
            ["App/App.csproj"] = WpfExe,
            ["App/App.xaml"] = $"<nav:BootStrapper {Xaml} xmlns:nav=\"clr-namespace:App.Navigation\" x:Class=\"App.App\">" +
                               "<Application.Resources><SolidColorBrush x:Key=\"Accent\" Color=\"Red\"/></Application.Resources></nav:BootStrapper>",
            ["App/Navigation/BootStrapper.cs"] = "namespace App.Navigation { public abstract class BootStrapper : System.Windows.Application { } }",
            ["App/Views/ChatView.xaml"] = $"<UserControl {Xaml}><Border Background=\"{{StaticResource Accent}}\"/></UserControl>",
        });

        Assert.Empty(r.Findings);
    }

    [Fact]
    public void A_subclassed_control_root_with_its_own_resources_is_not_the_application()
    {
        var r = Analyze(new()
        {
            ["App/App.csproj"] = WpfExe,
            ["App/App.xaml"] = $"<Application {Xaml}><Application.Resources><ResourceDictionary/></Application.Resources></Application>",
            ["App/Views/Panel.xaml"] = $"<local:BasePanel {Xaml} xmlns:local=\"clr-namespace:App\">" +
                                       "<UserControl.Resources><SolidColorBrush x:Key=\"Accent\" Color=\"Red\"/></UserControl.Resources>" +
                                       "<Border Background=\"{StaticResource Accent}\"/></local:BasePanel>",
            ["App/Views/Other.xaml"] = $"<UserControl {Xaml}><Border Background=\"{{StaticResource Accent}}\"/></UserControl>",
        });

        Assert.Equal(["Accent"], Keys(r, FindingKind.OutOfScope));
    }

    // ── 103：有 x:Key 的子字典，名字本身也是鍵 ─────────────────────────────

    private static Dictionary<string, string> OpenUtauLike(string lookup) => new()
    {
        // OpenUtau：App.SetTheme 用 Current.Resources["themes-dark"] 取出整本
        ["src/App.csproj"] = AvExe,
        ["src/App.axaml"] = $"<Application {Av} x:Class=\"Utau.App\"><Application.Resources><ResourceDictionary>" +
                            "<ResourceInclude x:Key=\"themes-dark\" Source=\"/Colors/Dark.axaml\"/>" +
                            "<ResourceInclude x:Key=\"themes-light\" Source=\"/Colors/Light.axaml\"/>" +
                            "</ResourceDictionary></Application.Resources></Application>",
        ["src/App.axaml.cs"] = "namespace Utau { partial class App { static void SetTheme() { var d = Current.Resources[\"" + lookup + "\"]; } } }",
        ["src/Colors/Dark.axaml"] = $"<ResourceDictionary {Av}><SolidColorBrush x:Key=\"Fg\" Color=\"White\"/></ResourceDictionary>",
        ["src/Colors/Light.axaml"] = $"<ResourceDictionary {Av}><SolidColorBrush x:Key=\"Fg\" Color=\"Black\"/></ResourceDictionary>",
        ["src/Views/Main.axaml"] = $"<UserControl {Av}><TextBlock Foreground=\"{{DynamicResource Fg}}\"/></UserControl>",
    };

    [Fact]
    public void The_name_of_a_keyed_dictionary_is_a_key_code_can_look_up()
    {
        var r = Analyze(OpenUtauLike("themes-dark"));

        Assert.Empty(r.Findings);   // 也不報 themes-light 沒人用：這種字典本來就是給程式挑的
    }

    [Fact]
    public void Looking_up_a_name_no_keyed_dictionary_has_is_still_dangling()
    {
        var r = Analyze(OpenUtauLike("themes-custom"));

        Assert.Equal(["themes-custom"], Keys(r, FindingKind.DanglingDynamic));
    }

    // ── 104：MergedDictionaries 裡的子類別元素自己帶著內容 ─────────────────

    [Fact]
    public void A_library_dictionary_subclass_with_its_own_theme_dictionaries_is_read()
    {
        // iNKORE Gallery：<ui:ThemeResources><ui:ThemeResources.ThemeDictionaries><ResourceDictionary x:Key="Light">…
        var r = Analyze(new()
        {
            ["Gallery/Gallery.csproj"] = WpfExe,
            ["Gallery/App.xaml"] = $"<Application {Xaml} xmlns:ui=\"http://schemas.inkore.net/lib/ui/wpf/modern\"><Application.Resources><ResourceDictionary>" +
                                   "<ResourceDictionary.MergedDictionaries><ui:ThemeResources><ui:ThemeResources.ThemeDictionaries>" +
                                   "<ResourceDictionary x:Key=\"Light\"><SolidColorBrush x:Key=\"AppBackgroundBrush\" Color=\"White\"/></ResourceDictionary>" +
                                   "<ResourceDictionary x:Key=\"Dark\"><SolidColorBrush x:Key=\"AppBackgroundBrush\" Color=\"#232323\"/></ResourceDictionary>" +
                                   "<ResourceDictionary x:Key=\"HighContrast\"><ResourceDictionary.MergedDictionaries><ResourceDictionary Source=\"/Themes/HighContrast.xaml\"/></ResourceDictionary.MergedDictionaries>" +
                                   "<SolidColorBrush x:Key=\"AppBackgroundBrush\" Color=\"Black\"/></ResourceDictionary>" +
                                   "</ui:ThemeResources.ThemeDictionaries></ui:ThemeResources></ResourceDictionary.MergedDictionaries>" +
                                   "</ResourceDictionary></Application.Resources></Application>",
            ["Gallery/Themes/HighContrast.xaml"] = $"<ResourceDictionary {Xaml}><SolidColorBrush x:Key=\"AppBackgroundBrush\" Color=\"Black\"/></ResourceDictionary>",
            ["Gallery/MainWindow.xaml"] = $"<Window {Xaml}><Grid Background=\"{{DynamicResource AppBackgroundBrush}}\"/></Window>",
        });

        Assert.Empty(r.Findings.Where(f => f.Kind is FindingKind.OrphanDictionary or FindingKind.External)
                               .Select(f => $"{f.Kind} {f.Key} {f.Location.File}"));
        Assert.Contains("<ThemeResources/>", r.KnownThemeSources);   // 它自己的本體照舊：ModernWpf 系的已知主題來源
    }

    // ── 105：組件名也是組出來的 pack URI ─────────────────────────────────

    [Fact]
    public void A_composed_pack_uri_with_a_composed_assembly_name_matches_from_component_on()
    {
        // iNKORE Gallery：new Uri($"/{assemblyName};component/Presets/{currentPreset}.xaml", UriKind.Relative)
        var r = Analyze(new()
        {
            ["Gallery/Gallery.csproj"] = WpfExe,
            ["Gallery/App.xaml"] = $"<Application {Xaml}><Application.Resources><ResourceDictionary/></Application.Resources></Application>",
            ["Gallery/Presets/ShapePresetResources.cs"] = "namespace Gallery { class ShapePresetResources { void Apply(string assemblyName, string currentPreset) { " +
                                                          "var source = new System.Uri($\"/{assemblyName};component/Presets/{currentPreset}.xaml\", System.UriKind.Relative); } } }",
            ["Gallery/Presets/PreFluent.xaml"] = $"<ResourceDictionary {Xaml}><CornerRadius x:Key=\"ControlCornerRadius\">0</CornerRadius></ResourceDictionary>",
            ["Gallery/Other/Stray.xaml"] = $"<ResourceDictionary {Xaml}><CornerRadius x:Key=\"StrayRadius\">0</CornerRadius></ResourceDictionary>",
        });

        Assert.Equal(["Other/Stray.xaml"], Orphans(r, "Gallery"));
    }

    [Fact]
    public void A_composed_assembly_name_before_a_fixed_path_is_a_plain_file_name()
    {
        // 只有組件名是組出來的：;component/ 之後沒有 { —— 照一般檔名比，不能當成樣式（也不能當掉）
        var r = Analyze(new()
        {
            ["Gallery/Gallery.csproj"] = WpfExe,
            ["Gallery/App.xaml"] = $"<Application {Xaml}><Application.Resources><ResourceDictionary/></Application.Resources></Application>",
            ["Gallery/ThemeLoader.cs"] = "namespace Gallery { class ThemeLoader { void Apply(string assemblyName) { " +
                                         "var source = new System.Uri($\"/{assemblyName};component/Themes/Dark.xaml\", System.UriKind.Relative); } } }",
            ["Gallery/Themes/Dark.xaml"] = $"<ResourceDictionary {Xaml}><SolidColorBrush x:Key=\"Bg\" Color=\"Black\"/></ResourceDictionary>",
            ["Gallery/Themes/Light.xaml"] = $"<ResourceDictionary {Xaml}><SolidColorBrush x:Key=\"Bg\" Color=\"White\"/></ResourceDictionary>",
        });

        Assert.Equal(["Themes/Light.xaml"], Orphans(r, "Gallery"));
    }

    // ── 106：跨組件的 pack URI 指到共用專案連結進去的檔 ───────────────────

    [Fact]
    public void A_cross_assembly_pack_uri_resolves_to_a_file_linked_in_from_a_shared_project()
    {
        // Panuon.WPF.UI：每個目標框架的專案都匯入 SharedResources 的 projitems，
        // Control.xaml 用 pack://application:,,,/Panuon.WPF.UI;component/Styles/Button.xaml 合併
        var r = Analyze(new()
        {
            ["Lib/Lib.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><UseWPF>true</UseWPF></PropertyGroup>" +
                                 "<Import Project=\"..\\Shared\\Shared.projitems\" Label=\"Shared\"/></Project>",
            ["Shared/Shared.projitems"] = "<Project><ItemGroup><Page Include=\"$(MSBuildThisFileDirectory)Styles\\Button.xaml\"/>" +
                                          "<Page Include=\"$(MSBuildThisFileDirectory)Control.xaml\"/></ItemGroup></Project>",
            ["Shared/Control.xaml"] = $"<ResourceDictionary {Xaml}><ResourceDictionary.MergedDictionaries>" +
                                      "<ResourceDictionary Source=\"pack://application:,,,/Lib;component/Styles/Button.xaml\"/>" +
                                      "</ResourceDictionary.MergedDictionaries></ResourceDictionary>",
            ["Shared/Styles/Button.xaml"] = $"<ResourceDictionary {Xaml}><Style x:Key=\"PrimaryButton\" TargetType=\"Button\"/></ResourceDictionary>",
        });

        Assert.Empty(r.ExternalSources);
        Assert.DoesNotContain(r.Findings, f => f.Kind is FindingKind.External);
    }

    // ── 107：屬性值裡的 .xaml 路徑 ─────────────────────────────────────

    [Fact]
    public void A_dictionary_path_given_to_a_library_element_counts_as_loaded()
    {
        // Panuon 的 Samples：<pu:ApplicationTheme Key="Dark" ResourceDictionary="/Samples;component/Themes/Dark.xaml"/>
        var r = Analyze(new()
        {
            ["Samples/Samples.csproj"] = WpfExe,
            ["Samples/App.xaml"] = $"<Application {Xaml} xmlns:pu=\"https://opensource.panuon.com/wpf-ui\"><Application.Resources>" +
                                   "<pu:GlobalSettings x:Key=\"settings\"><pu:GlobalSettings.Themes>" +
                                   "<pu:ApplicationTheme Key=\"Dark\" ResourceDictionary=\"/Samples;component/Themes/Dark.xaml\"/>" +
                                   "</pu:GlobalSettings.Themes></pu:GlobalSettings></Application.Resources></Application>",
            ["Samples/Themes/Dark.xaml"] = $"<ResourceDictionary {Xaml}><SolidColorBrush x:Key=\"ToastBackground\" Color=\"Black\"/></ResourceDictionary>",
            ["Samples/Themes/Unmentioned.xaml"] = $"<ResourceDictionary {Xaml}><SolidColorBrush x:Key=\"Other\" Color=\"Black\"/></ResourceDictionary>",
        });

        Assert.Equal(["Themes/Unmentioned.xaml"], Orphans(r, "Samples"));
        Assert.Equal(1, r.RuntimeLoadedDictionaries);
    }
}
