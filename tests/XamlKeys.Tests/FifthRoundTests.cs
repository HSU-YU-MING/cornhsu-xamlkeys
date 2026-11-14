using XamlKeys.Core;

namespace XamlKeys.Tests;

/// <summary>
/// 第五輪語料（2026-11-15）：七個這之前沒跑過的公開專案 —— EarTrumpet、dnSpy、WalletWasabi、SukiUI、
/// Dev Home、dotnet-podcasts、PowerToys。每一條測試對應一條規則，註解寫來源專案與不做會怎樣。
/// 完整歸因在 docs/實查歸因-2026-11-14.md 的「第五輪語料」。
/// </summary>
public class FifthRoundTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("xamlkeys-round5");
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

    private const string AppProj = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup></Project>";
    private const string LibProj = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Library</OutputType></PropertyGroup></Project>";
    private static string AppRefs(params string[] refs) =>
        "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup><ItemGroup>" +
        string.Concat(refs.Select(r => $"<ProjectReference Include=\"{r}\" />")) + "</ItemGroup></Project>";

    private const string Wpf = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
                               "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";
    private const string Ava = "xmlns=\"https://github.com/avaloniaui\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";
    private const string Maui = "xmlns=\"http://schemas.microsoft.com/dotnet/2021/maui\" xmlns:x=\"http://schemas.microsoft.com/winfx/2009/xaml\"";
    private const string WinUi = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
                                 "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";

    private static string App(string ns, string body) =>
        $"<Application {ns}><Application.Resources><ResourceDictionary>{body}</ResourceDictionary></Application.Resources></Application>";
    private static string Dict(string ns, string body) => $"<ResourceDictionary {ns}>{body}</ResourceDictionary>";
    private static string Win(string ns, string body) => $"<Window {ns}>{body}</Window>";

    private static List<string> Keys(AnalysisResult r, FindingKind kind) =>
        r.Findings.Where(f => f.Kind == kind).Select(f => f.Key).Distinct().Order(StringComparer.Ordinal).ToList();

    // ── 38：有 x:Key 的子字典 ──────────────────────────────────────────────

    [Fact]
    public void Keyed_sub_dictionaries_are_runtime_alternatives_whose_keys_are_visible_and_do_not_shadow_each_other()
    {
        // SukiUI：Normal／Compact／Big 各定義一份 SmallCornerRadius，程式依設定挑一本合併
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["App.xaml"] = App(Wpf, """
                <ResourceDictionary x:Key="Normal"><CornerRadius x:Key="SmallCornerRadius">8</CornerRadius></ResourceDictionary>
                <ResourceDictionary x:Key="Compact"><CornerRadius x:Key="SmallCornerRadius">5</CornerRadius></ResourceDictionary>
                """),
            ["Main.xaml"] = Win(Wpf, """<Border CornerRadius="{DynamicResource SmallCornerRadius}" />"""),
        });

        Assert.Empty(Keys(r, FindingKind.DanglingDynamic));
        Assert.Empty(Keys(r, FindingKind.Shadowed));
        Assert.Empty(Keys(r, FindingKind.Unused));   // 外層的 "Normal"／"Compact" 不是一個個資源鍵
        Assert.Equal(2, r.KeyedDictionaries);
    }

    // ── 39：根是 <Styles>／<Style> 的 Avalonia 樣式檔 ───────────────────────

    [Fact]
    public void A_style_file_with_a_Styles_root_is_a_dictionary_that_StyleInclude_can_merge()
    {
        // WalletWasabi：App 用 StyleInclude 合併 Styles.axaml，舊版判成「不是字典、讀不到」
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["App.axaml"] = $"""
                <Application {Ava}>
                  <Application.Styles><StyleInclude Source="avares://App/Styles/Styles.axaml" /></Application.Styles>
                </Application>
                """,
            ["Styles/Styles.axaml"] = $"""
                <Styles {Ava}>
                  <Styles.Resources><Thickness x:Key="Pad">4</Thickness></Styles.Resources>
                  <Style Selector="Button"><Setter Property="Padding" Value="{"{"}StaticResource Pad{"}"}" /></Style>
                </Styles>
                """,
        });

        Assert.Equal(100, r.Coverage);
        Assert.DoesNotContain(r.Findings, f => f.Kind is FindingKind.DanglingStatic or FindingKind.External or FindingKind.Unused);
    }

    [Fact]
    public void A_theme_object_at_the_root_of_a_Styles_file_makes_missing_keys_external_not_dangling()
    {
        // SukiUI：SukiTheme.axaml 根層放 <SimpleTheme/>，它帶的鍵（SimpleDateTimePickerUpButton）不是懸空
        var r = Analyze(new()
        {
            ["Lib/Lib.csproj"] = LibProj,
            ["Lib/Theme.axaml"] = $"""
                <Styles {Ava}>
                  <SimpleTheme />
                  <Styles.Resources><ResourceDictionary><ResourceDictionary.MergedDictionaries>
                    <ResourceInclude Source="/Picker.axaml" />
                  </ResourceDictionary.MergedDictionaries></ResourceDictionary></Styles.Resources>
                </Styles>
                """,
            ["Lib/Picker.axaml"] = Dict(Ava, """
                <ControlTheme x:Key="PickerTheme" TargetType="Button">
                  <Setter Property="Theme" Value="{StaticResource SimpleDateTimePickerUpButton}" />
                  <Setter Property="Tag" Value="{StaticResource NotOnAnyList}" />
                </ControlTheme>
                """),
        });

        Assert.Empty(Keys(r, FindingKind.DanglingStatic));
        // Simple 主題的鍵在內建清單裡（2026-11-15 起）：算框架提供；不在清單的才是 external
        Assert.DoesNotContain("SimpleDateTimePickerUpButton", Keys(r, FindingKind.External));
        Assert.Contains("NotOnAnyList", Keys(r, FindingKind.External));
        Assert.Contains("avalonia-simple", r.ThemePacksInUse);
    }

    [Fact]
    public void A_style_file_with_a_single_Style_root_contributes_its_resources()
    {
        // SukiUI 的 ColorTheme/ColorsStyles.xaml：根是一個 <Style>，資源在 <Style.Resources>；
        // <Setter> 不能被當成主題物件（那會讓整個作用域變成「看不到」）
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["App.axaml"] = $"""
                <Application {Ava}>
                  <Application.Styles><StyleInclude Source="/Colors.axaml" /></Application.Styles>
                </Application>
                """,
            ["Colors.axaml"] = $"""
                <Style {Ava}>
                  <Style.Resources><SolidColorBrush x:Key="ThemeBrush" Color="Red" /></Style.Resources>
                  <Setter Property="Opacity" Value="1" />
                </Style>
                """,
            ["Main.axaml"] = $"""<Window {Ava}><Border Background="{"{"}DynamicResource ThemeBrush{"}"}" /><Border Background="{"{"}DynamicResource Nowhere{"}"}" /></Window>""",
        });

        Assert.Equal(["Nowhere"], Keys(r, FindingKind.DanglingDynamic));
        Assert.Empty(Keys(r, FindingKind.External));
    }

    [Fact]
    public void An_Avalonia_Styles_root_file_is_never_reported_as_an_orphan()
    {
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["App.axaml"] = $"<Application {Ava} />",
            ["Loose.axaml"] = $"""<Styles {Ava}><Styles.Resources><Thickness x:Key="T">1</Thickness></Styles.Resources></Styles>""",
        });

        Assert.Empty(Keys(r, FindingKind.OrphanDictionary));
    }

    [Fact]
    public void Resources_win_over_what_Styles_bring_in_on_the_same_element()
    {
        // FluentAvalonia 的範例 App：Application.Resources 刻意覆寫函式庫主題的 NavigationViewContentMargin。
        // Avalonia 先查 Resources 再查 Styles —— 反過來的話會把 App 的覆寫判成「被函式庫蓋掉」。
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["App.axaml"] = $"""
                <Application {Ava}>
                  <Application.Resources><ResourceDictionary><ResourceDictionary.MergedDictionaries>
                    <ResourceInclude Source="/Overrides.axaml" />
                  </ResourceDictionary.MergedDictionaries></ResourceDictionary></Application.Resources>
                  <Application.Styles><StyleInclude Source="/Theme.axaml" /></Application.Styles>
                </Application>
                """,
            // 覆寫放在合併進來的字典裡（跟 FluentAvalonia 一樣）—— 這時候只有合併順序決定誰贏
            ["Overrides.axaml"] = $"""<ResourceDictionary {Ava}><Thickness x:Key="Margin">0,48,0,0</Thickness></ResourceDictionary>""",
            ["Theme.axaml"] = $"""<Styles {Ava}><Styles.Resources><Thickness x:Key="Margin">0</Thickness></Styles.Resources></Styles>""",
            ["Main.axaml"] = $"""<Window {Ava}><Border Margin="{"{"}StaticResource Margin{"}"}" /></Window>""",
        });

        var s = r.Findings.Single(f => f.Kind == FindingKind.Shadowed);
        Assert.Equal("Theme.axaml", s.Location.File);
    }

    // ── 40：MAUI 字典裡沒有 x:Key 的 <ResourceDictionary Source> ─────────────

    [Fact]
    public void An_unkeyed_ResourceDictionary_with_a_Source_inside_a_dictionary_merges_it()
    {
        // dotnet-podcasts：整份主題這樣載入。舊版 83 筆 out-of-scope、主題檔被報成孤兒
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["App.xaml"] = App(Maui, """<ResourceDictionary Source="Themes/Default.xaml" />"""),
            ["Themes/Default.xaml"] = Dict(Maui, """<Color x:Key="Primary">#512BD4</Color>"""),
            ["Main.xaml"] = $"""<ContentPage {Maui}><Label TextColor="{"{"}StaticResource Primary{"}"}" /></ContentPage>""",
        });

        Assert.Empty(r.Findings);
    }

    // ── 41：ms-appx:///組件名/…、/組件名/… ───────────────────────────────

    [Theory]
    [InlineData("ms-appx:///Lib.Styles/Styles/Shared.xaml")]
    [InlineData("/Lib.Styles/Styles/Shared.xaml")]
    public void A_WinUI_path_that_starts_with_an_assembly_name_resolves_into_that_project(string source)
    {
        // Dev Home（ms-appx:///DevHome.Dashboard/…）、PowerToys（AssemblyName 覆寫、或不寫 ms-appx:）
        var r = Analyze(new()
        {
            ["App/App.csproj"] = AppRefs("..\\Styles\\Styles.csproj"),
            ["App/App.xaml"] = App(WinUi, $"""<ResourceDictionary.MergedDictionaries><ResourceDictionary Source="{source}" /></ResourceDictionary.MergedDictionaries>"""),
            ["App/Main.xaml"] = Win(WinUi, """<Border Background="{StaticResource SharedBrush}" />"""),
            ["Styles/Styles.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Library</OutputType><AssemblyName>Lib.Styles</AssemblyName></PropertyGroup></Project>",
            ["Styles/Styles/Shared.xaml"] = Dict(WinUi, """<SolidColorBrush x:Key="SharedBrush" Color="Red" />"""),
        });

        Assert.Equal(100, r.Coverage);
        Assert.DoesNotContain(r.Findings, f => f.Kind is FindingKind.DanglingStatic or FindingKind.External);
    }

    // ── 42：C# 在執行期寫進資源的鍵 ───────────────────────────────────────

    [Theory]
    [InlineData("""Application.Current.Resources["AccentBrush"] = brush;""")]                         // wpfui
    [InlineData("""static readonly Info[] All = { new Info { ResourceKey = "AccentBrush" } };""")]  // dnSpy ColorInfos
    [InlineData("""void Add(string n) => Res.Add($"{n}Brush", null); void Init() => Add("Accent");""")] // SukiUI
    [InlineData("""static string K(string n) => $"Acc{n}Brush";""")]                                 // dnSpy TEInherited…
    public void A_DynamicResource_key_that_CSharp_names_is_not_dangling_and_is_counted(string code)
    {
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["App.xaml"] = App(Wpf, """<SolidColorBrush x:Key="Own" Color="Red" />"""),
            ["Main.xaml"] = Win(Wpf, """<Border Background="{DynamicResource Own}" BorderBrush="{DynamicResource AccentBrush}" />"""),
            ["Theme.cs"] = $"class Theme {{ {code} }}",
        });

        Assert.Empty(Keys(r, FindingKind.DanglingDynamic));
        // Resources["K"] = … 是明確的定義，2026-11-15 起另外計在 keysAddedInCode
        Assert.Equal(1, r.DynamicKeysNamedInCode + r.KeysAddedInCode);
    }

    [Fact]
    public void A_StaticResource_is_never_loosened_by_names_in_code()
    {
        // 只放寬 DynamicResource：StaticResource 是 gate 擋的那一級
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["App.xaml"] = App(Wpf, """<SolidColorBrush x:Key="Own" Color="Red" />"""),
            ["Main.xaml"] = Win(Wpf, """<Border Background="{DynamicResource Own}" BorderBrush="{StaticResource AccentBrush}" />"""),
            // 一般字串（不是 Resources.Add／Resources[…] = 這種明確的定義）
            ["Theme.cs"] = """class Theme { object I = new Info { ResourceKey = "AccentBrush" }; }""",
        });

        Assert.Equal(["AccentBrush"], Keys(r, FindingKind.DanglingStatic));
    }

    [Theory]
    [InlineData("""el.FindResource("AccentBrush");""")]                 // 讀取不是定義
    [InlineData("""static string K(string n) => $"{n}Brush";""")]       // 只有結尾、沒有固定開頭：太寬
    [InlineData("""static string K(string n) => $"Ac{n}Brush";""")]     // 開頭不到 3 個字
    public void Reads_and_too_loose_patterns_do_not_define_a_key(string code)
    {
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["App.xaml"] = App(Wpf, """<SolidColorBrush x:Key="Own" Color="Red" />"""),
            ["Main.xaml"] = Win(Wpf, """<Border Background="{DynamicResource Own}" BorderBrush="{DynamicResource AccentBrush}" />"""),
            ["Theme.cs"] = $"class Theme {{ void A(FrameworkElement el) {{ {code} }} }}",
        });

        Assert.Contains("AccentBrush", Keys(r, FindingKind.DanglingDynamic));
        Assert.Equal(0, r.DynamicKeysNamedInCode);
    }

    [Fact]
    public void A_CSharp_lookup_of_a_key_that_CSharp_also_writes_is_not_dangling()
    {
        // dnSpy：SetResourceReference(…, "ToolTipBackground") 讀，ColorInfos.cs 的 ResourceKey = "ToolTipBackground" 寫
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["App.xaml"] = App(Wpf, """<SolidColorBrush x:Key="Own" Color="Red" />"""),
            ["Main.xaml"] = Win(Wpf, """<Border Background="{DynamicResource Own}" />"""),
            ["Tip.cs"] = """class Tip { void A(Border b) { b.SetResourceReference(Border.BackgroundProperty, "ToolTipBackground"); } }""",
            ["Colors.cs"] = """class Colors { object I = new Info { ResourceKey = "ToolTipBackground" }; }""",
        });

        Assert.Empty(Keys(r, FindingKind.DanglingDynamic));
    }

    // ── 43：名字交給輔助方法 —— 同一個專案或以 ProjectReference 連著的專案 ─────

    [Fact]
    public void A_key_name_handed_to_a_helper_method_counts_as_used()
    {
        // dnSpy 的 SetStyle(… ?? "FileTabGroupTabControlStyle")、SukiUI 的 BindBrush(p, this, "FnBlade")
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["App.xaml"] = App(Wpf, """
                <Style x:Key="FileTabStyle" TargetType="TabControl" />
                <Style x:Key="Dead" TargetType="TabControl" />
                """),
            ["TabGroup.cs"] = """class TabGroup { void A(Options o) { tabControl.SetStyle(o.Style ?? "FileTabStyle"); } }""",
        });

        Assert.Equal(["Dead"], Keys(r, FindingKind.Unused));
        Assert.Equal(1, r.KeysNamedInCode);
    }

    [Fact]
    public void Names_in_a_file_that_writes_resources_by_variable_are_definitions_not_uses()
    {
        // Kindling 的 ThemeService：整張色盤寫進資源，色盤裡的 AmberBorder 其實沒人讀
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["App.xaml"] = App(Wpf, """<SolidColorBrush x:Key="AmberBorder" Color="Red" />"""),
            ["ThemeService.cs"] = """
                class ThemeService {
                    static readonly (string Key, string Dark)[] Palette = { ("AmberBorder", "#5A4A1E") };
                    void Apply() { foreach (var (key, dark) in Palette) Application.Current.Resources[key] = Make(dark); }
                }
                """,
        });

        Assert.Equal(["AmberBorder"], Keys(r, FindingKind.Unused));
    }

    [Fact]
    public void Names_in_code_only_rescue_definitions_in_related_projects()
    {
        // maui-samples：一個範例的 C# 有 "Success"，另一個獨立範例真的沒人用的 Success 不能跟著消失。
        // Flow Launcher：主題檔在 App，讀它們的 Theme.cs 在 App 參考的 Core —— 這要算。
        var r = Analyze(new()
        {
            ["A/A.csproj"] = AppRefs("..\\Core\\Core.csproj"),
            ["A/App.xaml"] = App(Wpf, """<x:String x:Key="SystemBG">Auto</x:String><x:String x:Key="Success">ok</x:String>"""),
            ["Core/Core.csproj"] = LibProj,
            ["Core/Theme.cs"] = """class Theme { string A(ResourceDictionary dict) => dict["SystemBG"] as string; }""",
            ["B/B.csproj"] = AppProj,
            ["B/App.xaml"] = App(Wpf, """<x:String x:Key="Success">ok</x:String>"""),
            ["B/Log.cs"] = """class Log { string S = "Success"; }""",
        });

        // 只有 A 的那一份 Success 被報（B 的那份有人用）—— 以定義為單位，不是以名字
        var unused = r.Findings.Where(f => f.Kind == FindingKind.Unused).ToList();
        Assert.Equal("Success", Assert.Single(unused).Key);
        Assert.Equal("A/App.xaml", unused[0].Location.File);
    }

    // ── 44：合併我的上層合併了看不到的字典 ──────────────────────────────

    [Fact]
    public void An_unreadable_merge_in_a_parent_of_the_same_project_makes_a_missing_key_external()
    {
        var r = Analyze(new()
        {
            ["Lib/Lib.csproj"] = LibProj,
            ["Lib/Generic.xaml"] = Dict(Wpf, """
                <ResourceDictionary.MergedDictionaries>
                  <ResourceDictionary Source="pack://application:,,,/Vendor;component/Theme.xaml" />
                  <ResourceDictionary Source="Picker.xaml" />
                </ResourceDictionary.MergedDictionaries>
                """),
            ["Lib/Picker.xaml"] = Dict(Wpf, """<Style x:Key="P" TargetType="Button"><Setter Property="Style" Value="{StaticResource VendorButton}" /></Style>"""),
        });

        Assert.Empty(Keys(r, FindingKind.DanglingStatic));
        Assert.Contains("VendorButton", Keys(r, FindingKind.External));
    }

    [Fact]
    public void A_parent_in_another_project_does_not_count()
    {
        // MaterialDesign：MahApps 整合專案同時合併核心庫的字典與 <MahAppsBundledTheme/>，
        // 核心庫自己的真懸空不能因此降級
        var r = Analyze(new()
        {
            ["Core/Core.csproj"] = LibProj,
            ["Core/Picker.xaml"] = Dict(Wpf, """<Style x:Key="P" TargetType="Button"><Setter Property="Style" Value="{StaticResource Missing}" /></Style>"""),
            ["Mash/Mash.csproj"] = LibProj,
            ["Mash/Generic.xaml"] = Dict(Wpf, """
                <ResourceDictionary.MergedDictionaries>
                  <ResourceDictionary Source="pack://application:,,,/Vendor;component/Theme.xaml" />
                  <ResourceDictionary Source="pack://application:,,,/Core;component/Picker.xaml" />
                </ResourceDictionary.MergedDictionaries>
                """),
        });

        Assert.Contains("Missing", Keys(r, FindingKind.DanglingStatic));
    }

    // ── 45：主題元素接回 x:Class 檔 ────────────────────────────────────

    [Theory]
    [InlineData("xmlns:suki=\"clr-namespace:SukiUI\"", "")]
    [InlineData("xmlns:suki=\"https://github.com/kikipoulet/SukiUI\"", """[assembly: XmlnsDefinition("https://github.com/kikipoulet/SukiUI", "SukiUI")]""")]
    public void A_theme_element_whose_class_is_in_the_tree_resolves_to_that_file(string xmlns, string assemblyInfo)
    {
        // SukiUI 的示範 App 用 <suki:SukiTheme/> 載入主題；SukiTheme.axaml 的 x:Class 就是 SukiUI.SukiTheme
        var r = Analyze(new()
        {
            ["Demo/Demo.csproj"] = AppRefs("..\\Lib\\Lib.csproj"),
            ["Demo/App.axaml"] = $"""
                <Application {Ava} {xmlns}>
                  <Application.Styles><suki:SukiTheme /></Application.Styles>
                </Application>
                """,
            ["Demo/Main.axaml"] = $"""<Window {Ava}><Border Background="{"{"}DynamicResource SukiLowText{"}"}" /></Window>""",
            ["Lib/Lib.csproj"] = LibProj,
            ["Lib/AssemblyInfo.cs"] = assemblyInfo,
            ["Lib/SukiTheme.axaml"] = $"""
                <Styles {Ava} x:Class="SukiUI.SukiTheme">
                  <Styles.Resources><SolidColorBrush x:Key="SukiLowText" Color="Gray" /></Styles.Resources>
                </Styles>
                """,
        });

        Assert.Equal(1, r.ClassBackedDictionaries);
        Assert.DoesNotContain(r.Findings, f => f.Kind is FindingKind.External or FindingKind.DanglingDynamic);
    }

    // ── 46：App 刻意蓋掉函式庫的鍵 ─────────────────────────────────────

    [Fact]
    public void An_app_overriding_a_library_key_is_counted_not_reported_as_shadowed()
    {
        var r = Analyze(new()
        {
            ["App/App.csproj"] = AppRefs("..\\Lib\\Lib.csproj"),
            ["App/App.xaml"] = App(Wpf, """
                <ResourceDictionary.MergedDictionaries>
                  <ResourceDictionary Source="pack://application:,,,/Lib;component/Theme.xaml" />
                </ResourceDictionary.MergedDictionaries>
                <Thickness x:Key="ContentMargin">0,48,0,0</Thickness>
                """),
            ["App/Main.xaml"] = Win(Wpf, """<Border Margin="{StaticResource ContentMargin}" />"""),
            ["Lib/Lib.csproj"] = LibProj,
            ["Lib/Theme.xaml"] = Dict(Wpf, """<Thickness x:Key="ContentMargin">0</Thickness>"""),
        });

        Assert.Empty(Keys(r, FindingKind.Shadowed));
        Assert.Equal(1, r.LibraryKeyOverrides);
    }
}
