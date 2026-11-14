using XamlKeys.Core;

namespace XamlKeys.Tests;

/// <summary>
/// 第六輪語料（2026-11-15）：Uno Gallery、Lively、PixiEditor、Ursa.Avalonia、CommunityToolkit.Maui、Snoop、
/// WeatherTwentyOne。每一條測試對應一條規則；歸因在 docs/實查歸因-2026-11-14.md 的「第六輪語料」。
/// </summary>
public class SixthRoundTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("xamlkeys-round6");
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
    private const string MauiProj = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><UseMaui>true</UseMaui></PropertyGroup></Project>";
    private const string WinUiProj = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType><UseWinUI>true</UseWinUI></PropertyGroup></Project>";

    private static List<string> Keys(AnalysisResult r, FindingKind k) =>
        r.Findings.Where(f => f.Kind == k).Select(f => f.Key).Distinct().Order(StringComparer.Ordinal).ToList();

    // ── 50：C# 加進資源的鍵是定義 ───────────────────────────────────────

    [Theory]
    [InlineData("""Resources.Add(nameof(StringToListConverter), new StringToListConverter());""")]
    [InlineData("""Resources.Add("StringToListConverter", new StringToListConverter());""")]
    [InlineData("""Resources["StringToListConverter"] = new StringToListConverter();""")]
    [InlineData("""this.Resources[nameof(Converters.StringToListConverter)] = new StringToListConverter();""")]
    public void A_key_added_from_the_code_behind_is_defined_even_for_a_StaticResource(string add)
    {
        // CommunityToolkit.Maui 的範例頁：InitializeComponent() 之前在 code-behind 加，XAML 用 StaticResource
        var r = Analyze(new()
        {
            ["App.csproj"] = MauiProj,
            ["App.xaml"] = $"<Application {Maui}><Application.Resources><Color x:Key=\"Own\">Red</Color></Application.Resources></Application>",
            ["Page.xaml"] = $"<ContentPage {Maui}><Label TextColor=\"{{StaticResource Own}}\" Text=\"{{Binding X, Converter={{StaticResource StringToListConverter}}}}\" /></ContentPage>",
            ["Page.xaml.cs"] = $"partial class Page {{ public Page() {{ {add} InitializeComponent(); }} }}",
        });

        Assert.Empty(Keys(r, FindingKind.DanglingStatic));
        Assert.Equal(1, r.KeysAddedInCode);
    }

    [Fact]
    public void A_key_added_by_an_unrelated_project_does_not_count()
    {
        var r = Analyze(new()
        {
            ["A/A.csproj"] = MauiProj,
            ["A/App.xaml"] = $"<Application {Maui}><Application.Resources><Color x:Key=\"Own\">Red</Color></Application.Resources></Application>",
            ["A/Page.xaml"] = $"<ContentPage {Maui}><Label TextColor=\"{{StaticResource Own}}\" BackgroundColor=\"{{StaticResource Added}}\" /></ContentPage>",
            ["B/B.csproj"] = MauiProj,
            ["B/Setup.cs"] = """class Setup { void A(Application app) { app.Resources.Add("Added", null); } }""",
        });

        Assert.Equal(["Added"], Keys(r, FindingKind.DanglingStatic));
    }

    // ── 51：非 WinUI 專案裡的 WinUI 檔 ───────────────────────────────────

    [Fact]
    public void A_WinUI_dictionary_inside_a_MAUI_project_uses_the_WinUI_framework_keys()
    {
        // CommunityToolkit.Maui 的 MediaElement：ResourceDictionary.windows.xaml
        var r = Analyze(new()
        {
            ["Lib/Lib.csproj"] = MauiProj,
            ["Lib/ResourceDictionary.windows.xaml"] = $"""
                <ResourceDictionary {Wpf} xmlns:muxc="using:Microsoft.UI.Xaml.Controls">
                  <Style x:Key="Transport" TargetType="muxc:MediaTransportControls">
                    <Setter Property="UseSystemFocusVisuals" Value="{"{"}StaticResource UseSystemFocusVisuals{"}"}" />
                  </Style>
                </ResourceDictionary>
                """,
        });

        Assert.Empty(Keys(r, FindingKind.DanglingStatic));
    }

    [Fact]
    public void The_Windows_platform_App_xaml_of_a_MAUI_app_is_WinUI()
    {
        // NavigationViewContentBackground 是 WinUI 的輕量樣式鍵：重新定義它不是沒人用
        var r = Analyze(new()
        {
            ["App.csproj"] = MauiProj,
            ["App.xaml"] = $"<Application {Maui}><Application.Resources><Color x:Key=\"Own\">Red</Color></Application.Resources></Application>",
            ["Page.xaml"] = $"<ContentPage {Maui}><Label TextColor=\"{{StaticResource Own}}\" /></ContentPage>",
            ["Platforms/Windows/App.xaml"] = $"<Application {Wpf}><Application.Resources><SolidColorBrush x:Key=\"NavigationViewContentBackground\" Color=\"Transparent\" /></Application.Resources></Application>",
        });

        Assert.Empty(Keys(r, FindingKind.Unused));
    }

    [Fact]
    public void A_WinUI_key_name_in_an_ordinary_MAUI_file_is_still_dangling()
    {
        var r = Analyze(new()
        {
            ["App.csproj"] = MauiProj,
            ["App.xaml"] = $"<Application {Maui}><Application.Resources><Color x:Key=\"Own\">Red</Color></Application.Resources></Application>",
            ["Page.xaml"] = $"<ContentPage {Maui}><Label TextColor=\"{{StaticResource Own}}\" BackgroundColor=\"{{StaticResource UseSystemFocusVisuals}}\" /></ContentPage>",
        });

        Assert.Equal(["UseSystemFocusVisuals"], Keys(r, FindingKind.DanglingStatic));
    }

    // ── 52：測試專案的名字 ────────────────────────────────────────────

    [Theory]
    [InlineData("TestHarnesses/BasicTestHarness")]
    [InlineData("src/CommunityToolkit.Maui.UnitTests")]
    [InlineData("src/App.DeviceTests")]
    public void Test_projects_with_these_names_are_skipped(string dir)
    {
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["App.xaml"] = $"<Application {Wpf}><Application.Resources><SolidColorBrush x:Key=\"Own\" Color=\"Red\" /></Application.Resources></Application>",
            ["Main.xaml"] = $"<Window {Wpf}><Border Background=\"{{StaticResource Own}}\" /></Window>",
            [$"{dir}/Window1.xaml"] = $"<Window {Wpf}><Border Background=\"{{DynamicResource MissingDynamicResource}}\" /></Window>",
        });

        Assert.Empty(r.Findings);
        Assert.Equal(1, r.TestFilesExcluded);
    }

    [Fact]
    public void A_word_that_merely_ends_in_tests_is_not_a_test_folder()
    {
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["App.xaml"] = $"<Application {Wpf}><Application.Resources><SolidColorBrush x:Key=\"Own\" Color=\"Red\" /></Application.Resources></Application>",
            ["Contests/Main.xaml"] = $"<Window {Wpf}><Border Background=\"{{StaticResource Own}}\" BorderBrush=\"{{StaticResource Missing}}\" /></Window>",
        });

        Assert.Equal(["Missing"], Keys(r, FindingKind.DanglingStatic));
        Assert.Equal(0, r.TestFilesExcluded);
    }

    // ── 54：產生出來的在地化存取器；相關專案不含兄弟 ─────────────────────

    [Theory]
    [InlineData("Localize.GameMode()")]
    [InlineData("Localization.GameMode")]
    [InlineData("ShellLocalization.GameMode")]
    public void A_key_read_through_a_generated_localization_accessor_is_used(string access)
    {
        // Flow Launcher：source generator 依語系檔產生 Localize.<鍵名>()，359 筆 unused 裡 158 筆是這樣用的
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["App.xaml"] = $"<Application {Wpf} xmlns:system=\"clr-namespace:System;assembly=mscorlib\"><Application.Resources><system:String x:Key=\"GameMode\">Game Mode</system:String><system:String x:Key=\"Dead\">x</system:String></Application.Resources></Application>",
            ["MainWindow.cs"] = $"class MainWindow {{ string H() => {access}; }}",
        });

        Assert.Equal(["Dead"], Keys(r, FindingKind.Unused));
    }

    [Fact]
    public void A_Localized_suffix_is_not_an_accessor()
    {
        // BrowserTypeLocalized.Value 之類是列舉屬性，不是在地化類別
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["App.xaml"] = $"<Application {Wpf} xmlns:system=\"clr-namespace:System;assembly=mscorlib\"><Application.Resources><system:String x:Key=\"Value\">x</system:String></Application.Resources></Application>",
            ["Settings.cs"] = "class Settings { object V() => BrowserTypeLocalized.Value; }",
        });

        Assert.Equal(["Value"], Keys(r, FindingKind.Unused));
    }

    [Fact]
    public void Two_projects_that_only_share_a_library_are_not_related()
    {
        // CommunityToolkit.Maui：範例 App 的鍵被單元測試專案裡碰巧同名的字串救了回去 —— 兩者只是都參考同一個函式庫
        var r = Analyze(new()
        {
            ["Lib/Lib.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Library</OutputType></PropertyGroup></Project>",
            ["App/App.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup><ItemGroup><ProjectReference Include=\"..\\Lib\\Lib.csproj\" /></ItemGroup></Project>",
            ["App/App.xaml"] = $"<Application {Wpf}><Application.Resources><SolidColorBrush x:Key=\"TextColor\" Color=\"Red\" /></Application.Resources></Application>",
            ["Other/Other.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup><ItemGroup><ProjectReference Include=\"..\\Lib\\Lib.csproj\" /></ItemGroup></Project>",
            ["Other/Check.cs"] = """class Check { string S = "TextColor"; }""",
        });

        Assert.Equal(["TextColor"], Keys(r, FindingKind.Unused));
    }

    [Fact]
    public void A_reference_in_a_page_of_a_WinUI_file_inside_a_MAUI_project_uses_the_WinUI_list()
    {
        // 畫面檔沒有自己的字典時，看檔案本身判方言（不是整棵樹的多數決）
        var r = Analyze(new()
        {
            ["App.csproj"] = MauiProj,
            ["App.xaml"] = $"<Application {Maui}><Application.Resources><Color x:Key=\"Own\">Red</Color></Application.Resources></Application>",
            ["Page.xaml"] = $"<ContentPage {Maui}><Label TextColor=\"{{StaticResource Own}}\" /></ContentPage>",
            ["Platforms/Windows/Shell.xaml"] = $"<Page {Wpf}><TextBlock Style=\"{{StaticResource BodyTextBlockStyle}}\" /></Page>",
        });

        Assert.Empty(Keys(r, FindingKind.DanglingStatic));
    }

    // ── 53：Semi.Avalonia、Uno 的主題清單 ───────────────────────────────

    [Fact]
    public void Semi_Avalonia_keys_are_framework_provided_when_SemiTheme_is_merged()
    {
        // Ursa.Avalonia：建在 Semi 上的函式庫，747＋913 筆是 Semi 的鍵
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["App.axaml"] = $"<Application {Ava} xmlns:semi=\"https://irihi.tech/semi\"><Application.Styles><semi:SemiTheme /></Application.Styles></Application>",
            ["Main.axaml"] = $"<Window {Ava}><Border Background=\"{{DynamicResource SemiColorPrimary}}\" /></Window>",
        });

        Assert.DoesNotContain(r.Findings, f => f.Key == "SemiColorPrimary");
        Assert.Contains("semi-avalonia", r.ThemePacksInUse);
    }

    [Fact]
    public void Uno_Material_keys_are_framework_provided_in_a_WinUI_app()
    {
        // Uno Gallery：PrimaryBrush、HoverOpacity… 1,768 筆 external
        var r = Analyze(new()
        {
            ["App.csproj"] = WinUiProj,
            ["App.xaml"] = $"""
                <Application {Wpf} xmlns:utum="using:Uno.Toolkit.UI.Material"><Application.Resources><ResourceDictionary>
                  <ResourceDictionary.MergedDictionaries><XamlControlsResources /><utum:MaterialToolkitTheme /></ResourceDictionary.MergedDictionaries>
                </ResourceDictionary></Application.Resources></Application>
                """,
            ["Main.xaml"] = $"<Page {Wpf}><Border Background=\"{{ThemeResource PrimaryBrush}}\" Opacity=\"{{StaticResource HoverOpacity}}\" /></Page>",
        });

        Assert.DoesNotContain(r.Findings, f => f.Key is "PrimaryBrush" or "HoverOpacity");
        Assert.Contains("uno-material", r.ThemePacksInUse);
        Assert.Equal(100, r.Coverage);
    }

    [Fact]
    public void The_Uno_lists_do_not_apply_to_an_Avalonia_app_with_a_same_named_theme()
    {
        // Material.Avalonia 也有 <MaterialTheme/>；Uno 是 WinUI，不能把 Avalonia 專案的鍵算成 Uno 的
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["App.axaml"] = $"<Application {Ava} xmlns:themes=\"clr-namespace:Material.Styles.Themes;assembly=Material.Styles\"><Application.Styles><themes:MaterialTheme /></Application.Styles></Application>",
            ["Main.axaml"] = $"<Window {Ava}><Border Background=\"{{DynamicResource PrimaryBrush}}\" /></Window>",
        });

        Assert.DoesNotContain("uno-material", r.ThemePacksInUse);
        Assert.Contains("<MaterialTheme/>", r.ExternalSources);
        Assert.Contains(r.Findings, f => f.Key == "PrimaryBrush" && f.Kind == FindingKind.External);
    }
}
