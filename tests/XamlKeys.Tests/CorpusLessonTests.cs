using XamlKeys.Core;

namespace XamlKeys.Tests;

/// <summary>
/// 外部語料教的每一課，各一條測試。
///
/// 2026-11-14 拿七個公開專案（ScreenToGif、HandyControl、MahApps.Metro、wpfui、
/// MaterialDesignInXamlToolkit、WinUI Gallery、ILSpy，合計 1,593 個 XAML 檔）實跑，
/// 第一版吐出約 36,000 筆假警報。每一類的成因都寫在對應的測試上。
///
/// 這批測試的意義跟一般單元測試不同：它們是「憲法：每條規則來自實查」的存檔。
/// 自家的四個專案**一條都測不到**（全是 WPF 應用程式、沒有第三方字典、沒有
/// ThemeDictionaries、沒有 pack URI）—— 這正是 XamlContrast §8.3 的教訓。
/// </summary>
public class CorpusLessonTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("xamlkeys-corpus");

    public void Dispose() => _dir.Delete(true);

    /// <summary>用一組 {相對路徑: 內容} 造一個小專案，跑完回傳結果。</summary>
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
    private const string LibProj = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Library</OutputType></PropertyGroup></Project>";

    private static string Dict(string body) => $"""
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
        {body}
        </ResourceDictionary>
        """;

    private static string App(string body) => $"""
        <Application xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Application.Resources>
            <ResourceDictionary>
        {body}
            </ResourceDictionary>
          </Application.Resources>
        </Application>
        """;

    private static string Window(string body, string extraNs = "") => $"""
        <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" {extraNs}>
        {body}
        </Window>
        """;

    // ── WinUI Gallery ──────────────────────────────────────────────────────

    [Fact]
    public void A_resource_declared_with_x_Name_is_a_key()
    {
        // 教訓來源：WinUI Gallery 的 ImplicitAnimationSet／轉換器／Layout 都用 x:Name。
        // XAML 規格：資源字典項目沒有 x:Key 時，x:Name 就是它的鍵。
        // 漏掉的話，引用端報懸空、定義端算不出鍵。
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["App.xaml"] = App("""<SolidColorBrush x:Name="NamedNotKeyed" Color="#111" />"""),
            ["Main.xaml"] = Window("""<Border Background="{StaticResource NamedNotKeyed}" />"""),
        });

        Assert.DoesNotContain(r.Findings, f => f.Kind is FindingKind.DanglingStatic or FindingKind.DanglingDynamic);
        Assert.Equal(1, r.KeysDefined);
    }

    [Fact]
    public void A_markup_extension_mentioned_inside_prose_is_not_a_reference()
    {
        // 教訓來源：WinUI Gallery 的說明文字
        //   Text="...is the code equivalent of {ThemeResource Key}. Switch the theme..."
        // XAML 只在屬性值的**開頭**是 { 時才解析成 markup extension。
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["App.xaml"] = App("""<SolidColorBrush x:Key="Real" Color="#111" />"""),
            ["Main.xaml"] = Window("""<TextBlock Text="the code equivalent of {ThemeResource Key}. Switch it." />"""),
        });

        Assert.DoesNotContain(r.Findings, f => f.Key == "Key");
    }

    [Fact]
    public void Framework_provided_WinUI_keys_are_not_dangling()
    {
        // 教訓來源：WinUI Gallery。沒有框架鍵清單時，195 個不重複的框架鍵
        // 被報成 975 筆懸空（309 static + 666 dynamic）。
        var r = Analyze(new()
        {
            ["App.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType><UseWinUI>true</UseWinUI></PropertyGroup></Project>",
            ["App.xaml"] = App("""<SolidColorBrush x:Key="Mine" Color="#111" />"""),
            ["Main.xaml"] = Window("""<Border Background="{ThemeResource TextFillColorPrimaryBrush}" />"""),
        });

        Assert.DoesNotContain(r.Findings, f => f.Key == "TextFillColorPrimaryBrush");
        Assert.True(r.FrameworkProvided > 0);
    }

    [Fact]
    public void A_key_the_project_defines_itself_is_not_counted_as_framework_provided()
    {
        // ⚠ 框架鍵的檢查必須排在查找**之後**。排在前面的那一版，
        //   wpfui（WPF 函式庫，刻意沿用 WinUI 的鍵名）有 3021 筆本來解析得到的引用被短路。
        var r = Analyze(new()
        {
            ["App.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType><UseWinUI>true</UseWinUI></PropertyGroup></Project>",
            ["App.xaml"] = App("""<SolidColorBrush x:Key="TextFillColorPrimaryBrush" Color="#111" />"""),
            ["Main.xaml"] = Window("""<Border Background="{ThemeResource TextFillColorPrimaryBrush}" />"""),
        });

        Assert.Equal(0, r.FrameworkProvided);
    }

    [Fact]
    public void A_HighContrast_theme_dictionary_does_not_collide_with_dark_and_light()
    {
        // 教訓來源：WinUI Gallery 的 App.xaml。HighContrast 是**互斥的第三種主題**，
        // 當成 Neutral 的話，它的鍵會跟深淺兩側的同名鍵互相覆蓋 -> 11 筆假 shadowed。
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["App.xaml"] = App("""
                <ResourceDictionary.ThemeDictionaries>
                  <ResourceDictionary x:Key="Light"><SolidColorBrush x:Key="Bg" Color="#FFF" /></ResourceDictionary>
                  <ResourceDictionary x:Key="Dark"><SolidColorBrush x:Key="Bg" Color="#000" /></ResourceDictionary>
                  <ResourceDictionary x:Key="HighContrast"><SolidColorBrush x:Key="Bg" Color="#F00" /></ResourceDictionary>
                </ResourceDictionary.ThemeDictionaries>
                """),
            ["Main.xaml"] = Window("""<Border Background="{StaticResource Bg}" />"""),
        });

        Assert.DoesNotContain(r.Findings, f => f.Kind is FindingKind.Shadowed or FindingKind.ShadowedIdentical);
    }

    [Fact]
    public void Default_means_light_when_a_Dark_dictionary_is_present()
    {
        // 教訓來源：WinUI Gallery 的 XamlResourcesPage —— 一組 Default＋Dark，
        // Default 裡寫的是淺色值。把 Default 一律當深色會判成兩本互相覆蓋。
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["App.xaml"] = App("""
                <ResourceDictionary.ThemeDictionaries>
                  <ResourceDictionary x:Key="Default"><SolidColorBrush x:Key="Bg" Color="#EEE" /></ResourceDictionary>
                  <ResourceDictionary x:Key="Dark"><SolidColorBrush x:Key="Bg" Color="#333" /></ResourceDictionary>
                </ResourceDictionary.ThemeDictionaries>
                """),
            ["Main.xaml"] = Window("""<Border Background="{StaticResource Bg}" />"""),
        });

        Assert.DoesNotContain(r.Findings, f => f.Kind is FindingKind.Shadowed or FindingKind.ShadowedIdentical);
    }

    // ── HandyControl / MaterialDesign / wpfui（函式庫） ─────────────────────

    [Fact]
    public void A_library_projects_keys_are_not_reported_unused()
    {
        // 教訓來源：MaterialDesignInXamlToolkit —— 3372 個鍵被報掉 1767 個。
        // 函式庫的鍵是它的公開 API，引用它的是**使用者的**專案。
        var r = Analyze(new()
        {
            ["Lib/Lib.csproj"] = LibProj,
            ["Lib/Themes/Styles.xaml"] = Dict("""<SolidColorBrush x:Key="PublicApiBrush" Color="#111" />"""),
        });

        Assert.DoesNotContain(r.Findings, f => f.Kind == FindingKind.Unused);
        Assert.True(r.LibraryKeysNotChecked > 0, "the skipped keys must be visible in the summary");
    }

    [Fact]
    public void A_library_dictionary_nobody_merges_is_not_an_orphan()
    {
        // 教訓來源：HandyControl 140 筆、MaterialDesign 103 筆。
        // 函式庫的字典本來就靠使用者去合併。
        var r = Analyze(new()
        {
            ["Lib/Lib.csproj"] = LibProj,
            ["Lib/Themes/Styles.xaml"] = Dict("""<SolidColorBrush x:Key="PublicApiBrush" Color="#111" />"""),
        });

        Assert.DoesNotContain(r.Findings, f => f.Kind == FindingKind.OrphanDictionary);
    }

    [Fact]
    public void A_reference_to_a_key_in_another_project_is_not_out_of_scope()
    {
        // 教訓來源：wpfui 的範例 App 引用函式庫的色票 -> 421 筆假 out-of-scope。
        // 使用者的 App 用什麼方式把那本字典合併進來（pack URI／generic.xaml／程式組路徑）追不完。
        var r = Analyze(new()
        {
            ["Lib/Lib.csproj"] = LibProj,
            ["Lib/Themes/Styles.xaml"] = Dict("""<SolidColorBrush x:Key="LibBrush" Color="#111" />"""),
            ["App/App.csproj"] = AppProj,
            ["App/App.xaml"] = App("""<SolidColorBrush x:Key="Own" Color="#222" />"""),
            ["App/Main.xaml"] = Window("""<Border Background="{StaticResource LibBrush}" />"""),
        });

        Assert.DoesNotContain(r.Findings, f => f.Kind == FindingKind.OutOfScope);
        Assert.True(r.CrossProjectReferences > 0, "cross-project references must be visible in the summary");
    }

    [Fact]
    public void Out_of_scope_still_fires_inside_one_project()
    {
        // 跨專案不猜，但**同一個專案裡**用錯別的畫面的區域資源，正是這一類的價值所在。
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["App.xaml"] = App("""<SolidColorBrush x:Key="Global" Color="#111" />"""),
            ["Other.xaml"] = Window("""<Window.Resources><SolidColorBrush x:Key="OnlyHere" Color="#222" /></Window.Resources>"""),
            ["Main.xaml"] = Window("""<Border Background="{StaticResource OnlyHere}" />"""),
        });

        Assert.Contains(r.Findings, f => f.Kind == FindingKind.OutOfScope && f.Key == "OnlyHere");
    }

    // ── ScreenToGif ────────────────────────────────────────────────────────

    [Fact]
    public void Locale_variant_dictionaries_are_not_shadowing_each_other()
    {
        // 教訓來源：ScreenToGif 用 XAML 做在地化,四十個語系檔各定義同一組字串鍵,
        // 全部合併進 App.xaml -> 28,704 筆假 shadowed。
        // 判準：兩本字典的鍵集合重疊度夠高 = 刻意的變體,不是意外的覆蓋。
        var keys = string.Join('\n', Enumerable.Range(0, 10)
            .Select(i => $"""<x:String x:Key="S{i}">v</x:String>"""));

        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["App.xaml"] = App("""
                <ResourceDictionary.MergedDictionaries>
                  <ResourceDictionary Source="Loc/en.xaml" />
                  <ResourceDictionary Source="Loc/de.xaml" />
                </ResourceDictionary.MergedDictionaries>
                """),
            ["Loc/en.xaml"] = Dict(keys),
            ["Loc/de.xaml"] = Dict(keys.Replace(">v<", ">w<")),
            ["Main.xaml"] = Window("""<TextBlock Text="{StaticResource S0}" />"""),
        });

        Assert.DoesNotContain(r.Findings, f => f.Kind == FindingKind.Shadowed);
        Assert.True(r.VariantDictionaryGroups > 0, "the variant pairing must be visible in the summary");
    }

    [Fact]
    public void An_accidental_collision_between_unrelated_dictionaries_is_still_reported()
    {
        // 變體規則不能把真正的意外覆蓋一起吃掉：兩本內容完全不同、只撞到一個鍵。
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["App.xaml"] = App("""
                <ResourceDictionary.MergedDictionaries>
                  <ResourceDictionary Source="A.xaml" />
                  <ResourceDictionary Source="B.xaml" />
                </ResourceDictionary.MergedDictionaries>
                """),
            ["A.xaml"] = Dict("""
                <SolidColorBrush x:Key="Clash" Color="#111" />
                <SolidColorBrush x:Key="A1" Color="#1" /><SolidColorBrush x:Key="A2" Color="#2" />
                <SolidColorBrush x:Key="A3" Color="#3" /><SolidColorBrush x:Key="A4" Color="#4" />
                <SolidColorBrush x:Key="A5" Color="#5" /><SolidColorBrush x:Key="A6" Color="#6" />
                """),
            ["B.xaml"] = Dict("""
                <SolidColorBrush x:Key="Clash" Color="#999" />
                <SolidColorBrush x:Key="B1" Color="#1" /><SolidColorBrush x:Key="B2" Color="#2" />
                <SolidColorBrush x:Key="B3" Color="#3" /><SolidColorBrush x:Key="B4" Color="#4" />
                <SolidColorBrush x:Key="B5" Color="#5" /><SolidColorBrush x:Key="B6" Color="#6" />
                """),
            ["Main.xaml"] = Window("""<Border Background="{StaticResource Clash}" />"""),
        });

        Assert.Contains(r.Findings, f => f.Kind == FindingKind.Shadowed && f.Key == "Clash");
    }

    [Fact]
    public void An_unused_key_defined_in_many_variants_is_reported_once()
    {
        // 教訓來源：ScreenToGif。逐一報是 14,468 筆,背後只有 1,094 個鍵名。
        // 一萬四千行的報告沒有人會讀 —— 等於工具沒做事。
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["App.xaml"] = App("""
                <ResourceDictionary.MergedDictionaries>
                  <ResourceDictionary Source="Loc/en.xaml" />
                  <ResourceDictionary Source="Loc/de.xaml" />
                </ResourceDictionary.MergedDictionaries>
                """),
            ["Loc/en.xaml"] = Dict("""<x:String x:Key="Ghost">a</x:String>"""),
            ["Loc/de.xaml"] = Dict("""<x:String x:Key="Ghost">b</x:String>"""),
            ["Main.xaml"] = Window("<Border />"),
        });

        var unused = Assert.Single(r.Findings, f => f.Kind == FindingKind.Unused);
        Assert.Equal("Ghost", unused.Key);
        Assert.Equal(2, r.UnusedDefinitions);
        Assert.Contains("2 definitions", unused.Message);
    }

    [Fact]
    public void Themes_Generic_xaml_is_never_an_orphan()
    {
        // 教訓來源：ScreenToGif。Themes/Generic.xaml 是 WPF 自動載入的隱含主題字典,
        // 永遠不會出現在任何 MergedDictionaries 裡 —— 而它往往是整個專案最重要的一本。
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["App.xaml"] = App("""<SolidColorBrush x:Key="Own" Color="#111" />"""),
            ["Themes/Generic.xaml"] = Dict("""<SolidColorBrush x:Key="InGeneric" Color="#222" />"""),
            ["Main.xaml"] = Window("""<Border Background="{StaticResource InGeneric}" />"""),
        });

        Assert.DoesNotContain(r.Findings, f => f.Kind == FindingKind.OrphanDictionary);
        Assert.True(r.ImplicitThemeDictionaries > 0);
    }

    // ── wpfui / QuillNest ──────────────────────────────────────────────────

    [Fact]
    public void A_dictionary_loaded_from_code_joins_the_application_scope()
    {
        // 教訓來源：wpfui 與 QuillNest 都用 C# 在執行期套主題,
        // 那本字典沒有任何靜態合併邊 —— 不認這條,它的鍵全部變成 out-of-scope 或孤兒。
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["App.xaml"] = App("""<SolidColorBrush x:Key="Own" Color="#111" />"""),
            ["Themes/DarkTheme.xaml"] = Dict("""<SolidColorBrush x:Key="SwitchedIn" Color="#222" />"""),
            ["ThemeService.cs"] = """
                var d = new ResourceDictionary { Source = new Uri("Themes/DarkTheme.xaml", UriKind.Relative) };
                """,
            ["Main.xaml"] = Window("""<Border Background="{StaticResource SwitchedIn}" />"""),
        });

        Assert.DoesNotContain(r.Findings, f => f.Kind is FindingKind.OrphanDictionary or FindingKind.OutOfScope);
        Assert.True(r.RuntimeLoadedDictionaries > 0);
    }

    // ── ILSpy ──────────────────────────────────────────────────────────────

    [Fact]
    public void Test_fixture_xaml_is_skipped_by_default()
    {
        // 教訓來源：ILSpy 的 BamlDecompiler 測試案例資料夾裡放著**刻意寫壞的** XAML
        // （同一本字典裡重複的 x:Key、不被合併的字典）。那是測試資料,不是產品程式碼。
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["App.xaml"] = App("""<SolidColorBrush x:Key="Own" Color="#111" />"""),
            ["Foo.Tests.Windows/Cases/Broken.xaml"] = Dict("""
                <SolidColorBrush x:Key="Dup" Color="#1" />
                <SolidColorBrush x:Key="Dup" Color="#2" />
                """),
        });

        Assert.DoesNotContain(r.Findings, f => f.Kind == FindingKind.DuplicateKey);
        Assert.True(r.TestFilesExcluded > 0, "the skipped files must be visible in the summary");
    }

    [Fact]
    public void Test_fixture_xaml_can_be_opted_back_in()
    {
        var files = new Dictionary<string, string>
        {
            ["App.csproj"] = AppProj,
            ["App.xaml"] = App("""<SolidColorBrush x:Key="Own" Color="#111" />"""),
            ["Foo.Tests/Broken.xaml"] = Dict("""
                <SolidColorBrush x:Key="Dup" Color="#1" />
                <SolidColorBrush x:Key="Dup" Color="#2" />
                """),
        };

        Assert.Contains(Analyze(files, new Config { IncludeTests = true }).Findings,
                        f => f.Kind == FindingKind.DuplicateKey);
    }

    // ── MahApps ────────────────────────────────────────────────────────────

    [Fact]
    public void A_pack_uri_into_a_project_in_the_tree_resolves()
    {
        var r = Analyze(new()
        {
            ["Lib/MyLib.csproj"] = LibProj,
            ["Lib/Themes/Styles.xaml"] = Dict("""<SolidColorBrush x:Key="LibBrush" Color="#111" />"""),
            ["App/App.csproj"] = AppProj,
            ["App/App.xaml"] = App("""
                <ResourceDictionary.MergedDictionaries>
                  <ResourceDictionary Source="pack://application:,,,/MyLib;component/Themes/Styles.xaml" />
                </ResourceDictionary.MergedDictionaries>
                """),
            ["App/Main.xaml"] = Window("""<Border Background="{StaticResource LibBrush}" />"""),
        });

        Assert.Equal(100.0, r.Coverage);
        Assert.Empty(r.ExternalSources);
    }

    [Fact]
    public void A_pack_uri_to_a_build_generated_file_stays_external()
    {
        // 教訓來源：MahApps 的 Styles/Themes/*.xaml 是建置時從 Theme.Template.xaml 產生的,
        // repo 裡根本沒有那些檔。external 是**正確**答案 —— 而且要拉低覆蓋率,
        // 因為我們確實看不到它們定義了什麼。
        var r = Analyze(new()
        {
            ["App/App.csproj"] = AppProj,
            ["App/App.xaml"] = App("""
                <ResourceDictionary.MergedDictionaries>
                  <ResourceDictionary Source="pack://application:,,,/Generated;component/Themes/Light.Blue.xaml" />
                </ResourceDictionary.MergedDictionaries>
                """),
            ["App/Main.xaml"] = Window("""<Border Background="{StaticResource FromGenerated}" />"""),
        });

        Assert.NotEmpty(r.ExternalSources);
        Assert.DoesNotContain(r.Findings, f => f.Kind == FindingKind.DanglingStatic);
        Assert.Contains(r.Findings, f => f.Kind == FindingKind.External);
    }

    [Fact]
    public void ExternalDictionaries_config_turns_an_external_merge_into_a_resolved_one()
    {
        // 設定讀得到卻沒有作用，比沒有這個設定更糟 —— 使用者會以為自己設了。
        var files = new Dictionary<string, string>
        {
            ["App/App.csproj"] = AppProj,
            ["App/App.xaml"] = App("""
                <ResourceDictionary.MergedDictionaries>
                  <ResourceDictionary Source="pack://application:,,,/ThirdParty;component/Themes/Styles.xaml" />
                </ResourceDictionary.MergedDictionaries>
                """),
            ["vendor/Themes/Styles.xaml"] = Dict("""<SolidColorBrush x:Key="VendorBrush" Color="#111" />"""),
            ["App/Main.xaml"] = Window("""<Border Background="{StaticResource VendorBrush}" />"""),
        };

        var without = Analyze(files);
        Assert.NotEmpty(without.ExternalSources);
        Assert.Contains(without.Findings, f => f.Kind == FindingKind.External);

        var cfg = new Config();
        cfg.ExternalDictionaries["ThirdParty"] = "vendor";

        var with = Analyze(files, cfg);
        Assert.Empty(with.ExternalSources);
        Assert.Equal(100.0, with.Coverage);
        Assert.DoesNotContain(with.Findings, f => f.Kind == FindingKind.External);
        Assert.True(with.Config.HasOverrides, "a config that changes the result must be visible in the summary");
    }

    // ── 第二輪語料（Files / Flow Launcher / ModernWpf / Fluent.Ribbon / WPF-Samples / QuickLook） ──

    [Fact]
    public void A_ResourceDictionary_subclass_in_MergedDictionaries_counts_as_external()
    {
        // ⚠ 教訓來源：QuickLook 與 Flow Launcher 的 App.xaml 合併的是
        //   <ui:ThemesDictionary /> / <ui:ControlsDictionary /> —— ResourceDictionary 的**子類別**,
        //   內容在組件裡。舊版直接無視它,等於把一本看不到的字典當成「不存在」,
        //   正好違反第一不變量。實測共 420 筆假 dangling。
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["App.xaml"] = """
                <Application xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                             xmlns:ui="clr-namespace:Some.Ui;assembly=Some.Ui">
                  <Application.Resources>
                    <ResourceDictionary>
                      <ResourceDictionary.MergedDictionaries>
                        <ui:ThemesDictionary />
                      </ResourceDictionary.MergedDictionaries>
                    </ResourceDictionary>
                  </Application.Resources>
                </Application>
                """,
            ["Main.xaml"] = Window("""<Border Background="{StaticResource MaybeFromTheLibrary}" />"""),
        });

        Assert.DoesNotContain(r.Findings, f => f.Kind == FindingKind.DanglingStatic);
        Assert.Contains(r.Findings, f => f.Kind == FindingKind.External);
        Assert.Contains(r.ExternalSources, s => s.Contains("ThemesDictionary"));
    }

    [Fact]
    public void The_dialect_is_decided_per_project_not_per_tree()
    {
        // ⚠ 教訓來源：ModernWpf 是 WPF 函式庫,但 repo 裡一個 tools/ 底下的輔助工具
        //   寫了 <UseWinUI>true</UseWinUI> —— 整次掃描的合併優先序因此被翻過來,
        //   shadowed 的贏家與輸家全部對調。
        var winUiProj = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType><UseWinUI>true</UseWinUI></PropertyGroup></Project>";

        var r = Analyze(new()
        {
            // WPF 函式庫：兩本合併字典都定義 K,WPF 規則是「後宣告的贏」-> B 贏
            ["Lib/Lib.csproj"] = LibProj,
            ["Lib/Themes/Host.xaml"] = Dict("""
                <ResourceDictionary.MergedDictionaries>
                  <ResourceDictionary Source="A.xaml" />
                  <ResourceDictionary Source="B.xaml" />
                </ResourceDictionary.MergedDictionaries>
                """),
            ["Lib/Themes/A.xaml"] = Dict("""<SolidColorBrush x:Key="K" Color="#A00" />"""),
            ["Lib/Themes/B.xaml"] = Dict("""<SolidColorBrush x:Key="K" Color="#B00" />"""),
            ["Lib/Use.xaml"] = Window("""<Border Background="{StaticResource K}" />"""),

            // 同一棵樹裡的 WinUI 輔助工具,不該影響上面那個專案
            ["tools/Capture/Capture.csproj"] = winUiProj,
            ["tools/Capture/App.xaml"] = App("""<SolidColorBrush x:Key="Unrelated" Color="#111" />"""),
        });

        var shadow = r.Findings.FirstOrDefault(f => f.Kind is FindingKind.Shadowed && f.Key == "K");
        Assert.NotNull(shadow);
        // WPF：A 是輸家、B 是贏家。判成 WinUI 的話兩者會對調。
        Assert.Contains("A.xaml", shadow!.Location.File);
        Assert.Contains("B.xaml", shadow.Related!.Value.File);
    }

    [Fact]
    public void Two_dictionaries_in_one_ThemeDictionaries_group_never_shadow_each_other()
    {
        // ⚠ 教訓來源：Files 的 GridSplitter.xaml 是 Default ＋ HighContrast 的組合。
        //   靠鍵名（Dark／Light／Default／HighContrast）去推哪一側會判錯;
        //   真正的規則是「同一組裡執行期只會選中一本,所以本質互斥」。
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["App.xaml"] = App("""
                <ResourceDictionary.ThemeDictionaries>
                  <ResourceDictionary x:Key="Default"><SolidColorBrush x:Key="Bg" Color="#EEE" /></ResourceDictionary>
                  <ResourceDictionary x:Key="HighContrast"><SolidColorBrush x:Key="Bg" Color="#F00" /></ResourceDictionary>
                </ResourceDictionary.ThemeDictionaries>
                """),
            ["Main.xaml"] = Window("""<Border Background="{StaticResource Bg}" />"""),
        });

        Assert.DoesNotContain(r.Findings, f => f.Kind is FindingKind.Shadowed or FindingKind.ShadowedIdentical);
    }

    [Fact]
    public void A_family_of_unmerged_near_identical_dictionaries_is_not_a_pile_of_orphans()
    {
        // ⚠ 教訓來源：Flow Launcher 的 Languages/ 底下 289 個語系檔,**每一個都剛好 606 個鍵**,
        //   沒有任何 MergedDictionaries 合併它們 —— 程式用完全組出來的路徑在執行期挑一本,
        //   而那行程式還在預設被排除的測試專案裡。報 289 筆孤兒沒有任何幫助。
        var keys = string.Join('\n', Enumerable.Range(0, 8).Select(i => $"""<x:String x:Key="S{i}">v</x:String>"""));
        var files = new Dictionary<string, string>
        {
            ["App.csproj"] = AppProj,
            ["App.xaml"] = App("""<SolidColorBrush x:Key="Own" Color="#111" />"""),
            ["Main.xaml"] = Window("""<TextBlock Text="{StaticResource S0}" />"""),
        };
        foreach (var loc in new[] { "en", "de", "ja", "ar" })
            files[$"Languages/{loc}.xaml"] = Dict(keys);

        var r = Analyze(files);

        Assert.DoesNotContain(r.Findings, f => f.Kind == FindingKind.OrphanDictionary);
        Assert.True(r.RuntimeSelectedFamilyFiles >= 4, "the family must be visible in the summary");
    }

    [Fact]
    public void A_lone_unmerged_dictionary_is_still_an_orphan()
    {
        // 家族規則不能把真正的孤兒一起吃掉：同目錄只有一本、沒有同伴
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["App.xaml"] = App("""<SolidColorBrush x:Key="Own" Color="#111" />"""),
            ["Stranded/Alone.xaml"] = Dict("""
                <SolidColorBrush x:Key="A" Color="#1" /><SolidColorBrush x:Key="B" Color="#2" />
                <SolidColorBrush x:Key="C" Color="#3" /><SolidColorBrush x:Key="D" Color="#4" />
                <SolidColorBrush x:Key="E" Color="#5" /><SolidColorBrush x:Key="F" Color="#6" />
                """),
        });

        Assert.Contains(r.Findings, f => f.Kind == FindingKind.OrphanDictionary);
    }

    [Fact]
    public void A_ResourceKey_attribute_holding_a_markup_extension_is_not_a_key_name()
    {
        // ⚠ 教訓來源：ModernWpf 的 HighContrast.xaml 寫
        //   <StaticResource ResourceKey="{x:Static SystemColors.HighlightColorKey}"/>
        //   舊版把整串當成鍵名,報出 10 筆叫「{x:Static SystemColors.…}」的假懸空。
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["App.xaml"] = App("""
                <Style x:Key="S" TargetType="Button">
                  <Setter Property="Background">
                    <Setter.Value>
                      <StaticResource ResourceKey="{x:Static SystemColors.HighlightColorKey}" />
                    </Setter.Value>
                  </Setter>
                </Style>
                """),
            ["Main.xaml"] = Window("""<Button Style="{StaticResource S}" />"""),
        });

        Assert.DoesNotContain(r.Findings, f => f.Key.StartsWith('{'));
        Assert.NotEmpty(r.UnresolvedSites);
    }

    [Fact]
    public void The_application_scope_belongs_to_its_own_project()
    {
        // ⚠ 教訓來源：wpfui。把全樹的 Application.Resources 都接到每一次查找上,
        //   結果是 samples/ 底下某個範例 App 合併的一本看不到的字典,
        //   污染了 src/Wpf.Ui/ 每一個檔案的作用域 —— 一筆**真的**懸空引用被降級成 external。
        var r = Analyze(new()
        {
            // 範例 App 合併了一本看不到的字典
            ["samples/Demo/Demo.csproj"] = AppProj,
            ["samples/Demo/App.xaml"] = """
                <Application xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                             xmlns:ui="clr-namespace:Some.Ui;assembly=Some.Ui">
                  <Application.Resources>
                    <ResourceDictionary>
                      <ResourceDictionary.MergedDictionaries><ui:ThemesDictionary /></ResourceDictionary.MergedDictionaries>
                    </ResourceDictionary>
                  </Application.Resources>
                </Application>
                """,
            // 應用程式專案裡一筆真的懸空,不該被另一個專案的外部字典遮住
            ["app/App2.csproj"] = AppProj,
            ["app/App.xaml"] = App("""<SolidColorBrush x:Key="Own" Color="#111" />"""),
            ["app/Main.xaml"] = Window("""<Border Background="{StaticResource ReallyMissing}" />"""),
        });

        var f = Assert.Single(r.Findings, x => x.Kind == FindingKind.DanglingStatic);
        Assert.Equal("ReallyMissing", f.Key);
        Assert.Equal(Severity.Fail, f.Severity);
    }

    [Fact]
    public void An_unresolved_reference_in_a_library_is_reported_but_not_gated()
    {
        // ⚠ 教訓來源：ModernWpf 48 筆、QuickLook 的外掛 99 筆、Wpf.Ui 30 筆。
        //   函式庫的字典是給**別人**合併的,而那個「別人」會先合併什麼我們不可能知道。
        //   **不隱藏,只降級**：還是報、還是進 JSON,但不進 gate。
        var r = Analyze(new()
        {
            ["Lib/Lib.csproj"] = LibProj,
            ["Lib/Themes/Styles.xaml"] = Dict("""
                <Style x:Key="MyStyle" TargetType="Button">
                  <Setter Property="Background" Value="{DynamicResource ConsumerSupplies}" />
                  <Setter Property="Foreground" Value="{StaticResource AlsoConsumerSupplies}" />
                </Style>
                """),
        });

        var dyn = Assert.Single(r.Findings, f => f.Kind == FindingKind.DanglingDynamic);
        var sta = Assert.Single(r.Findings, f => f.Kind == FindingKind.DanglingStatic);

        // DynamicResource 是「讓使用者覆寫」的標準寫法 -> info
        Assert.Equal(Severity.Info, dyn.Severity);
        // StaticResource 會在載入期丟例外,風險不同 -> 留在 warn（實測 wpfui 的真 bug 就是這樣）
        Assert.Equal(Severity.Warn, sta.Severity);
        Assert.Equal(2, r.LibraryUnresolvedReferences);
    }

    [Fact]
    public void The_same_reference_in_an_application_is_gated()
    {
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["App.xaml"] = App("""<SolidColorBrush x:Key="Own" Color="#111" />"""),
            ["Main.xaml"] = Window("""<Border Background="{StaticResource Missing}" />"""),
        });

        Assert.Equal(Severity.Fail, Assert.Single(r.Findings, f => f.Kind == FindingKind.DanglingStatic).Severity);
        Assert.Equal(0, r.LibraryUnresolvedReferences);
    }

    // ── 第三輪語料：Avalonia（Avalonia / FluentAvalonia / Semi.Avalonia） ───

    private static string Ax(string body) => $"""
        <ResourceDictionary xmlns="https://github.com/avaloniaui"
                            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
        {body}
        </ResourceDictionary>
        """;

    [Fact]
    public void Avalonia_ResourceInclude_is_a_real_merge_not_an_unknown_element()
    {
        // Avalonia 用 <ResourceInclude Source="…"/>，不是 <ResourceDictionary Source="…"/>。
        // 不認的話它會被當成「函式庫提供的子類別」-> 整個作用域變成不可知。
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["Themes/Colors.axaml"] = Ax("""<SolidColorBrush x:Key="Brand" Color="#111" />"""),
            ["App.axaml"] = """
                <Application xmlns="https://github.com/avaloniaui"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                  <Application.Resources>
                    <ResourceDictionary>
                      <ResourceDictionary.MergedDictionaries>
                        <ResourceInclude Source="Themes/Colors.axaml" />
                      </ResourceDictionary.MergedDictionaries>
                    </ResourceDictionary>
                  </Application.Resources>
                </Application>
                """,
            ["Main.axaml"] = """
                <Window xmlns="https://github.com/avaloniaui"
                        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                  <Border Background="{StaticResource Brand}" />
                </Window>
                """,
        });

        Assert.Equal(100.0, r.Coverage);
        Assert.Empty(r.ExternalSources);
        Assert.DoesNotContain(r.Findings, f => f.Kind is FindingKind.DanglingStatic or FindingKind.External);
    }

    [Fact]
    public void An_avares_uri_resolves_to_a_project_in_the_tree()
    {
        var r = Analyze(new()
        {
            ["Lib/MyLib.csproj"] = LibProj,
            ["Lib/Themes/Colors.axaml"] = Ax("""<SolidColorBrush x:Key="LibBrand" Color="#111" />"""),
            ["App/App.csproj"] = AppProj,
            ["App/App.axaml"] = """
                <Application xmlns="https://github.com/avaloniaui"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                  <Application.Resources>
                    <ResourceDictionary>
                      <ResourceDictionary.MergedDictionaries>
                        <ResourceInclude Source="avares://MyLib/Themes/Colors.axaml" />
                      </ResourceDictionary.MergedDictionaries>
                    </ResourceDictionary>
                  </Application.Resources>
                </Application>
                """,
        });

        Assert.Equal(100.0, r.Coverage);
        Assert.Empty(r.ExternalSources);
    }

    [Fact]
    public void A_theme_object_in_Application_Styles_is_an_external_merge()
    {
        // Avalonia 的函式庫主題是放在 <Application.Styles> 裡的物件
        // （<semi:SemiTheme/>、<FluentAvaloniaTheme/>）,它帶進來的資源靜態看不到。
        // 不認的話那些資源等於不存在 -> 一整批假 dangling。
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["App.axaml"] = """
                <Application xmlns="https://github.com/avaloniaui"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                             xmlns:semi="https://irihi.tech/semi">
                  <Application.Styles>
                    <semi:SemiTheme />
                  </Application.Styles>
                </Application>
                """,
            ["Main.axaml"] = """
                <Window xmlns="https://github.com/avaloniaui"
                        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                  <Border Background="{StaticResource FromTheTheme}" />
                </Window>
                """,
        });

        Assert.DoesNotContain(r.Findings, f => f.Kind == FindingKind.DanglingStatic);
        Assert.Contains(r.ExternalSources, s => s.Contains("SemiTheme"));
    }

    [Fact]
    public void Theme_sides_reach_the_dictionaries_a_theme_entry_includes()
    {
        // ⚠ 教訓來源：Semi.Avalonia 用**目錄**分主題 ——
        //   ThemeDictionaries 的 Dark 那本只放一行 ResourceInclude，真正的鍵在別的檔。
        //   側別不往下傳的話，Dark 與 Light 兩整套鍵會被判成互相覆蓋（66 筆假 shadowed）。
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["Themes/Dark/_index.axaml"] = Ax("""<SolidColorBrush x:Key="Bg" Color="#000" />"""),
            ["Themes/Light/_index.axaml"] = Ax("""<SolidColorBrush x:Key="Bg" Color="#FFF" />"""),
            ["App.axaml"] = """
                <Application xmlns="https://github.com/avaloniaui"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                  <Application.Resources>
                    <ResourceDictionary>
                      <ResourceDictionary.ThemeDictionaries>
                        <ResourceInclude x:Key="Light" Source="Themes/Light/_index.axaml" />
                        <ResourceInclude x:Key="Dark" Source="Themes/Dark/_index.axaml" />
                      </ResourceDictionary.ThemeDictionaries>
                    </ResourceDictionary>
                  </Application.Resources>
                </Application>
                """,
            ["Main.axaml"] = """
                <Window xmlns="https://github.com/avaloniaui"
                        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                  <Border Background="{StaticResource Bg}" />
                </Window>
                """,
        });

        Assert.DoesNotContain(r.Findings, f => f.Kind is FindingKind.Shadowed or FindingKind.ShadowedIdentical);
        Assert.DoesNotContain(r.Findings, f => f.Kind == FindingKind.DanglingStatic);
    }

    [Fact]
    public void The_same_theme_group_declared_twice_still_means_mutually_exclusive()
    {
        // ⚠ 教訓來源：Semi.Avalonia 把同一套 ThemeDictionaries 在 SemiTheme.axaml 與
        //   Index.axaml 各宣告了一次 —— Dark 那本因此是從**兩個**群組被合併到的。
        //   用「同一個群組 id」去判會整批漏掉。
        var themeDecl = """
                  <Application.Resources>
                    <ResourceDictionary>
                      <ResourceDictionary.ThemeDictionaries>
                        <ResourceInclude x:Key="Light" Source="Themes/Light/_index.axaml" />
                        <ResourceInclude x:Key="Dark" Source="Themes/Dark/_index.axaml" />
                      </ResourceDictionary.ThemeDictionaries>
                    </ResourceDictionary>
                  </Application.Resources>
            """;

        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["Themes/Dark/_index.axaml"] = Ax("""<SolidColorBrush x:Key="Bg" Color="#000" />"""),
            ["Themes/Light/_index.axaml"] = Ax("""<SolidColorBrush x:Key="Bg" Color="#FFF" />"""),
            ["App.axaml"] = $"""
                <Application xmlns="https://github.com/avaloniaui"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                {themeDecl}
                </Application>
                """,
            ["Second.axaml"] = $"""
                <Application xmlns="https://github.com/avaloniaui"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                {themeDecl}
                </Application>
                """,
            ["Main.axaml"] = """
                <Window xmlns="https://github.com/avaloniaui"
                        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                  <Border Background="{StaticResource Bg}" />
                </Window>
                """,
        });

        Assert.DoesNotContain(r.Findings, f => f.Kind is FindingKind.Shadowed or FindingKind.ShadowedIdentical);
    }

    [Fact]
    public void Platform_supplied_system_keys_are_not_dangling_in_any_dialect()
    {
        // ⚠ 教訓來源：Avalonia 的 SystemAccentColor（作業系統給的強調色）——
        //   執行期前綴只對 WinUI 生效的話，Avalonia 專案會冒出 161 筆假 dangling,
        //   ModernWpf 的 SystemColor* 也是 48 筆。
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["App.axaml"] = """
                <Application xmlns="https://github.com/avaloniaui"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                  <Application.Resources><ResourceDictionary>
                    <SolidColorBrush x:Key="Own" Color="#111" />
                  </ResourceDictionary></Application.Resources>
                </Application>
                """,
            ["Main.axaml"] = """
                <Window xmlns="https://github.com/avaloniaui"
                        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                  <Border Background="{DynamicResource SystemAccentColor}" />
                  <Border Background="{DynamicResource SystemColorHighlightColor}" />
                </Window>
                """,
        });

        Assert.DoesNotContain(r.Findings, f => f.Key.StartsWith("System", StringComparison.Ordinal));
        Assert.Equal(2, r.FrameworkProvided);
    }

    // ── 第四輪語料：.NET MAUI（maui-samples / eShop） ──────────────────────

    [Fact]
    public void The_2009_xaml_namespace_is_recognised()
    {
        // ⚠ 教訓來源：.NET MAUI 用 http://schemas.microsoft.com/winfx/2009/xaml
        //   （dotnet/maui-samples 的 1,238 個檔裡有 1,122 個是 2009），
        //   WPF／WinUI／Avalonia 則是 2006。只認 2006 的話,MAUI 專案的 x:Key
        //   一個都看不到 —— 實測是 0 個鍵、19,157 筆假懸空。
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["App.xaml"] = """
                <Application xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
                             xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml">
                  <Application.Resources>
                    <ResourceDictionary>
                      <ResourceDictionary.MergedDictionaries>
                        <ResourceDictionary Source="Resources/Styles/Colors.xaml" />
                      </ResourceDictionary.MergedDictionaries>
                    </ResourceDictionary>
                  </Application.Resources>
                </Application>
                """,
            ["Resources/Styles/Colors.xaml"] = """
                <ResourceDictionary xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
                                    xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml">
                  <Color x:Key="Primary">#512BD4</Color>
                </ResourceDictionary>
                """,
            ["MainPage.xaml"] = """
                <ContentPage xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
                             xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml">
                  <Label TextColor="{StaticResource Primary}" />
                </ContentPage>
                """,
        });

        Assert.Equal(1, r.KeysDefined);
        Assert.DoesNotContain(r.Findings, f => f.Kind is FindingKind.DanglingStatic or FindingKind.Unused);
    }

    [Fact]
    public void A_dictionary_loaded_from_code_is_only_exempt_inside_its_own_project()
    {
        // ⚠ 教訓來源：dotnet/maui-samples 有 231 個獨立的範例專案。
        //   只比對檔名的話，任何一個範例的 C# 提到 "Colors.xaml"，
        //   其餘 230 份 Colors.xaml 都會被當成「程式會載入」而豁免孤兒檢查。
        var r = Analyze(new()
        {
            ["A/A.csproj"] = AppProj,
            ["A/App.xaml"] = App("""<SolidColorBrush x:Key="OwnA" Color="#111" />"""),
            ["A/Loader.cs"] = """
                var d = new ResourceDictionary { Source = new Uri("Themes/Colors.xaml", UriKind.Relative) };
                """,
            ["A/Themes/Colors.xaml"] = Dict("""<SolidColorBrush x:Key="InA" Color="#222" />"""),

            // B 專案有一本同名的字典,但 B 的程式碼沒有載入它
            ["B/B.csproj"] = AppProj,
            ["B/App.xaml"] = App("""<SolidColorBrush x:Key="OwnB" Color="#333" />"""),
            ["B/Themes/Colors.xaml"] = Dict("""<SolidColorBrush x:Key="InB" Color="#444" />"""),
        });

        var orphans = r.Findings.Where(f => f.Kind == FindingKind.OrphanDictionary).ToList();
        Assert.Single(orphans);
        Assert.Contains("B/", orphans[0].Location.File);
    }

    // ── 對抗性輸入（這個工具會在 CI 上掃別人的 repo） ───────────────────────

    [Fact]
    public void A_DTD_is_refused_rather_than_expanded()
    {
        // ⚠ XDocument.Load 的預設設定會展開內部 DTD 實體 —— 實測:用 <!ENTITY> 定義的
        //   鍵名真的被解出來。外部實體則只靠「我們剛好不印資源的值」擋著,
        //   哪天有人把值加進報告就變成任意讀檔。XAML 從來不用 DTD,直接禁掉。
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["App.xaml"] = App("""<SolidColorBrush x:Key="Own" Color="#111" />"""),
            ["Evil.xaml"] = """
                <?xml version="1.0"?>
                <!DOCTYPE foo [ <!ENTITY k "EntityExpandedKey"> ]>
                <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                  <x:String x:Key="&k;">v</x:String>
                </ResourceDictionary>
                """,
        });

        Assert.DoesNotContain(r.Findings, f => f.Key == "EntityExpandedKey");
        Assert.Contains(r.ParseErrors, e => e.File.Contains("Evil.xaml"));
    }

    [Fact]
    public void Too_many_unparsable_files_make_the_result_untrustworthy()
    {
        // 讀不進來的檔跟讀不到的字典一樣危險:它裡面的引用我們沒看到,
        // 於是它用到的鍵會被報成「沒人用」。只印一行 note、退出碼照樣 0,
        // 就是另一條「謊報健康」的通道。
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["App.xaml"] = App("""<SolidColorBrush x:Key="Own" Color="#111" />"""),
            ["Broken1.xaml"] = "<ResourceDictionary",
            ["Broken2.xaml"] = "<ResourceDictionary",
        });

        Assert.True(r.Untrustworthy(out var reason));
        Assert.Contains("could not be parsed", reason);
    }

    [Fact]
    public void Non_ascii_keys_and_paths_work()
    {
        // 中文鍵名、emoji、以及帶空格的路徑都是真實會遇到的
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["with space/App.xaml"] = App("""
                <x:String x:Key="標題.主要">中文鍵</x:String>
                <x:String x:Key="emoji🔑">v</x:String>
                """),
            ["with space/Main.xaml"] = Window("""
                <TextBlock Text="{StaticResource 標題.主要}" />
                <TextBlock Text="{StaticResource emoji🔑}" />
                """),
        });

        Assert.DoesNotContain(r.Findings, f => f.Kind is FindingKind.DanglingStatic
                                                       or FindingKind.DanglingDynamic
                                                       or FindingKind.Unused);
    }

    // ── 自家專案（.claude/worktrees） ───────────────────────────────────────

    [Fact]
    public void Dot_directories_are_excluded()
    {
        // 教訓來源：自家專案底下的 .claude/worktrees/ 有整份副本,
        // 不排除的話每個鍵都被數三次,重複定義也會整批假警報。
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["App.xaml"] = App("""<SolidColorBrush x:Key="Own" Color="#111" />"""),
            [".claude/worktrees/copy/App.xaml"] = App("""<SolidColorBrush x:Key="Own" Color="#111" />"""),
        });

        Assert.Equal(1, r.XamlFiles);
        Assert.Equal(1, r.KeysDefined);
    }
}
