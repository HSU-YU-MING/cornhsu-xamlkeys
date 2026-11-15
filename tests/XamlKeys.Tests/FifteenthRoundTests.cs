using XamlKeys.Core;

namespace XamlKeys.Tests;

/// <summary>
/// 第十五輪語料（2026-11-15）：ULogViewer、NuGetPackageExplorer、Wino Mail、BililiveRecorder、FluentHub、Uno.Extensions。
/// 歸因在 docs/實查歸因-2026-11-14.md 的「第十五輪」。
/// </summary>
public class FifteenthRoundTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("xamlkeys-round15");
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
    private const string Av = "xmlns=\"https://github.com/avaloniaui\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";
    private const string AvExe = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup></Project>";

    private static List<string> Keys(AnalysisResult r, FindingKind k) =>
        r.Findings.Where(f => f.Kind == k).Select(f => f.Key).Distinct().Order(StringComparer.Ordinal).ToList();

    // ── 92：App 繼承樹外的類別 —— Application 作用域裡有看不到的東西 ──────────

    private static Dictionary<string, string> UlogLike(string appCode) => new()
    {
        // ULogViewer：App : AppSuiteApplication（CarinaStudio.AppSuite，NuGet），Fluent 主題與圖示都由那個基底在程式裡載入
        ["ULog/ULog.csproj"] = AvExe,
        ["ULog/App.axaml"] = $"<Application {Av} x:Class=\"Carina.ULog.App\"><Application.Resources><ResourceDictionary/></Application.Resources></Application>",
        ["ULog/App.axaml.cs"] = appCode,
        ["ULog/Views/Options.axaml"] = $"<UserControl {Av}><Border Padding=\"{{StaticResource TextControlThemePadding}}\"/></UserControl>",
    };

    [Fact]
    public void An_application_deriving_from_a_library_class_has_unseen_resources()
    {
        var r = Analyze(UlogLike("namespace Carina.ULog { class App : AppSuiteApplication { } }"));

        Assert.DoesNotContain(r.Findings, f => f.Kind == FindingKind.DanglingStatic);
        Assert.Equal(["TextControlThemePadding"], Keys(r, FindingKind.External));
        Assert.True(r.MergeEdges > r.MergeEdgesResolved);
    }

    [Fact]
    public void A_plain_Application_or_a_base_class_in_the_tree_is_not_an_unseen_source()
    {
        var plain = Analyze(UlogLike("namespace Carina.ULog { class App : Application { } }"));
        Assert.Equal(["TextControlThemePadding"], Keys(plain, FindingKind.DanglingStatic));
    }

    [Fact]
    public void A_base_class_declared_in_the_tree_is_seen()
    {
        var r = Analyze(UlogLike("namespace Carina.ULog { class AppBase : Application { } class App : AppBase { } }"));
        Assert.Equal(["TextControlThemePadding"], Keys(r, FindingKind.DanglingStatic));
    }

    [Fact]
    public void PrismApplication_is_known_to_add_no_resources()
    {
        // Dopamine：<prism:PrismApplication>、App : PrismApplication —— Prism 只接管啟動流程
        var r = Analyze(UlogLike("namespace Carina.ULog { class App : PrismApplication { } }"));
        Assert.Equal(["TextControlThemePadding"], Keys(r, FindingKind.DanglingStatic));
    }

    // ── 93：泛型的查找方法 ─────────────────────────────────────────────

    [Fact]
    public void Generic_lookup_methods_count_as_literal_lookups()
    {
        // ULogViewer：this.FindResourceOrDefault<IImage?>("Image/Icon.Information.Outline")
        var r = Analyze(new()
        {
            ["App/App.csproj"] = AvExe,
            ["App/App.axaml"] = $"<Application {Av}><Application.Resources><ResourceDictionary>" +
                                "<x:Double x:Key=\"ShadowSize\">4</x:Double><x:Double x:Key=\"Margin\">8</x:Double></ResourceDictionary></Application.Resources></Application>",
            ["App/Code.cs"] = "class Code { void M(Application app) { var a = app.FindResourceOrDefault<double>(\"ShadowSize\") * 2;" +
                              " app.TryFindResource<double>(\"Margin\", out var m); } }",
        });

        Assert.DoesNotContain(r.Findings, f => f.Kind == FindingKind.Unused);
    }

    // ── 94：nameof 的查找是固定鍵名 ───────────────────────────────────

    [Fact]
    public void A_nameof_lookup_is_a_literal_reference_not_a_dynamic_site()
    {
        // ULogViewer：this.Resources[nameof(addActionMenu)]（同名的 readonly 欄位）
        var r = Analyze(new()
        {
            ["App/App.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType><UseWPF>true</UseWPF></PropertyGroup></Project>",
            ["App/Editor.xaml"] = $"<UserControl {Wpf} x:Class=\"App.Editor\"><UserControl.Resources>" +
                                  "<ContextMenu x:Key=\"addActionMenu\"/><ContextMenu x:Key=\"unusedMenu\"/></UserControl.Resources><Grid/></UserControl>",
            ["App/Editor.xaml.cs"] = "namespace App { partial class Editor { readonly ContextMenu addActionMenu;" +
                                     " Editor() { addActionMenu = (ContextMenu)this.Resources[nameof(addActionMenu)]; } } }",
        });

        Assert.Equal(["unusedMenu"], Keys(r, FindingKind.Unused));
        Assert.Empty(r.DynamicKeySites);
    }

    [Fact]
    public void A_nameof_lookup_of_an_undefined_key_is_reported_like_a_literal_one()
    {
        var r = Analyze(new()
        {
            ["App/App.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType><UseWPF>true</UseWPF></PropertyGroup></Project>",
            ["App/Editor.xaml"] = $"<UserControl {Wpf} x:Class=\"App.Editor\"><Grid/></UserControl>",
            ["App/Editor.xaml.cs"] = "namespace App { partial class Editor { void M() { var x = this.FindResource(nameof(Strings.MissingMenu)); } } }",
        });

        Assert.Equal(["MissingMenu"], Keys(r, FindingKind.DanglingDynamic));
    }

    // ── 95：共用專案（.projitems） ─────────────────────────────────────

    private static Dictionary<string, string> SharedProject(string pageItem) => new()
    {
        // NuGetPackageExplorer：WinUI 頭專案 <Import Project="..\Legacy\Shared.Legacy.projitems" Label="Shared"/>，
        // 樣式字典在共用專案裡，App.xaml 寫 Source="Views/Styles/Colors.xaml"
        ["Uno/Head/Head.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType><UseWinUI>true</UseWinUI></PropertyGroup>" +
                                   "<Import Project=\"..\\Legacy\\Shared.Legacy.projitems\" Label=\"Shared\"/></Project>",
        ["Uno/Head/App.xaml"] = $"<Application {Wpf}><Application.Resources><ResourceDictionary><ResourceDictionary.MergedDictionaries>" +
                                "<ResourceDictionary Source=\"Views/Styles/Colors.xaml\"/></ResourceDictionary.MergedDictionaries></ResourceDictionary></Application.Resources></Application>",
        ["Uno/Head/MainPage.xaml"] = $"<Page {Wpf}><Grid Background=\"{{StaticResource SurfaceBrush}}\"/></Page>",
        ["Uno/Legacy/Shared.Legacy.projitems"] = $"<Project><ItemGroup>{pageItem}</ItemGroup></Project>",
        ["Uno/Legacy/Views/Styles/Colors.xaml"] = $"<ResourceDictionary {Wpf}><SolidColorBrush x:Key=\"SurfaceBrush\" Color=\"White\"/></ResourceDictionary>",
    };

    [Fact]
    public void Xaml_in_an_imported_shared_project_resolves_at_its_path_under_the_shared_folder()
    {
        var r = Analyze(SharedProject("<Page Include=\"$(MSBuildThisFileDirectory)Views\\Styles\\Colors.xaml\"/>"));

        Assert.Empty(r.Findings);
        Assert.Equal(100.0, r.Coverage);
    }

    [Fact]
    public void A_shared_item_with_an_explicit_Link_uses_the_link()
    {
        var files = SharedProject("<Page Include=\"$(MSBuildThisFileDirectory)Themes\\Palette.xaml\"><Link>Views\\Styles\\Colors.xaml</Link></Page>");
        files["Uno/Legacy/Themes/Palette.xaml"] = files["Uno/Legacy/Views/Styles/Colors.xaml"];
        files.Remove("Uno/Legacy/Views/Styles/Colors.xaml");
        var r = Analyze(files);

        Assert.Empty(r.Findings);
    }

    // ── 96：鍵名裡的斜線 ───────────────────────────────────────────────

    [Fact]
    public void Keys_with_slashes_count_as_named_and_composed_in_code()
    {
        // ULogViewer（CarinaStudio 的慣例）："Image/Icon.Chat.Outline"、$"Image/LogProfile.{iconName}"
        var r = Analyze(new()
        {
            ["App/App.csproj"] = AvExe,
            ["App/App.axaml"] = $"<Application {Av}><Application.Resources><ResourceDictionary>" +
                                "<x:String x:Key=\"Image/Icon.Chat\">c</x:String><x:String x:Key=\"Image/LogProfile.File\">f</x:String>" +
                                "<x:String x:Key=\"Image/Unused\">u</x:String></ResourceDictionary></Application.Resources></Application>",
            ["App/Code.cs"] = "class Code { object A(dynamic app) => app.GetIcon(\"Image/Icon.Chat\");" +
                              " object B(FrameworkElement e, string iconName) => e.FindResource($\"Image/LogProfile.{iconName}\"); }",
        });

        Assert.Equal(["Image/Unused"], Keys(r, FindingKind.Unused));
    }

    [Fact]
    public void A_generic_lookup_of_an_undefined_key_is_reported()
    {
        // 字面出現在程式裡只能證明「有人提到」，認得出是查找才報得出「找不到」
        var r = Analyze(new()
        {
            ["App/App.csproj"] = AvExe,
            ["App/App.axaml"] = $"<Application {Av}><Application.Resources><ResourceDictionary/></Application.Resources></Application>",
            ["App/Code.cs"] = "class Code { void M(Application app) { var a = app.FindResourceOrDefault<IBrush>(\"Brush/Missing.Background\"); } }",
        });

        Assert.Equal(["Brush/Missing.Background"], Keys(r, FindingKind.DanglingDynamic));
    }

    [Fact]
    public void A_composed_slash_key_kept_in_a_variable_counts_as_used()
    {
        // ULogViewer：var key = $"Image/LogDataSourceProvider.{value.Name}.Light"; 之後用變數查找（規則 64 的形狀，鍵名有斜線）
        var r = Analyze(new()
        {
            ["App/App.csproj"] = AvExe,
            ["App/App.axaml"] = $"<Application {Av}><Application.Resources><ResourceDictionary>" +
                                "<x:String x:Key=\"Image/Provider.File.Light\">f</x:String><x:String x:Key=\"Image/Other\">o</x:String>" +
                                "</ResourceDictionary></Application.Resources></Application>",
            ["App/Code.cs"] = "class Code { object M(Application app, string id) { var key = $\"Image/Provider.{id}.Light\"; return app.FindResource(key); } }",
        });

        Assert.Equal(["Image/Other"], Keys(r, FindingKind.Unused));
    }

    [Fact]
    public void An_App_xaml_with_theme_dictionaries_gets_one_unseen_base_class_source()
    {
        var files = UlogLike("namespace Carina.ULog { class App : AppSuiteApplication { } }");
        files["ULog/App.axaml"] = $"<Application {Av} x:Class=\"Carina.ULog.App\"><Application.Resources><ResourceDictionary>" +
                                  "<ResourceDictionary.ThemeDictionaries><ResourceDictionary x:Key=\"Light\"><x:Double x:Key=\"Gap\">1</x:Double></ResourceDictionary>" +
                                  "<ResourceDictionary x:Key=\"Dark\"><x:Double x:Key=\"Gap\">2</x:Double></ResourceDictionary></ResourceDictionary.ThemeDictionaries>" +
                                  "</ResourceDictionary></Application.Resources></Application>";
        var r = Analyze(files);

        Assert.Equal(1, r.MergeEdges - r.MergeEdgesResolved);
    }

    [Fact]
    public void A_code_lookup_is_not_dangling_when_the_application_base_class_is_unseen()
    {
        // ULogViewer：FindResourceOrDefault<IBrush>("Brush/StatusBar.Background")，那個鍵由 AppSuite 提供
        var files = UlogLike("namespace Carina.ULog { class App : AppSuiteApplication { } }");
        files["ULog/Code.cs"] = "class Code { void M(Application app) { var b = app.FindResourceOrDefault<IBrush>(\"Brush/StatusBar.Background\"); } }";
        var r = Analyze(files);

        Assert.DoesNotContain(r.Findings, f => f.Kind == FindingKind.DanglingDynamic);
    }

    // ── 97：組出來的 ms-appx:/// 路徑 ────────────────────────────────────

    [Fact]
    public void A_composed_ms_appx_theme_path_loads_the_matching_dictionaries()
    {
        // Wino Mail：new Uri($"ms-appx:///AppThemes/{ThemeName}.xaml")
        var r = Analyze(new()
        {
            ["Mail/Mail.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType><UseWinUI>true</UseWinUI></PropertyGroup></Project>",
            ["Mail/App.xaml"] = $"<Application {Wpf}><Application.Resources><ResourceDictionary/></Application.Resources></Application>",
            ["Mail/AppThemes/Rose.xaml"] = $"<ResourceDictionary {Wpf}><Color x:Key=\"ThemeAccent\">Pink</Color></ResourceDictionary>",
            ["Mail/AppThemes/Moss.xaml"] = $"<ResourceDictionary {Wpf}><Color x:Key=\"ThemeAccent\">Green</Color></ResourceDictionary>",
            ["Mail/Styles/Orphan.xaml"] = $"<ResourceDictionary {Wpf}><Color x:Key=\"Lost\">Red</Color></ResourceDictionary>",
            ["Mail/Theme.cs"] = "class PreDefinedAppTheme { string ThemeName; async void Load() { var f = await StorageFile.GetFileFromApplicationUriAsync(new Uri($\"ms-appx:///AppThemes/{ThemeName}.xaml\")); } }",
        });

        Assert.Equal(["Orphan.xaml"], r.Findings.Where(f => f.Kind == FindingKind.OrphanDictionary).Select(f => f.Key).ToList());
    }

    [Theory]
    [InlineData("namespace Carina.ULog { partial class App : IDisposable, ISingleInstanceApp { } }")]          // Flow Launcher：基底寫在 XAML
    [InlineData("using AvApplication = Avalonia.Application; namespace Carina.ULog { class App : AvApplication { } }")]   // Avalonia 的範例
    [InlineData("namespace Carina.ULog { class App : MauiApplication { } }")]                                  // MAUI 的平台宿主
    public void Interfaces_aliases_of_Application_and_MAUI_hosts_are_not_unseen_bases(string code)
    {
        var r = Analyze(UlogLike(code));
        Assert.Equal(["TextControlThemePadding"], Keys(r, FindingKind.DanglingStatic));
    }
}
