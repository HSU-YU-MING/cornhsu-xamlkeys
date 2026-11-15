using XamlKeys.Core;

namespace XamlKeys.Tests;

/// <summary>
/// 第十四輪（2026-11-15）：StaticResource 在載入時解析到同檔的定義（規則 88），以及新一輪語料。
/// 歸因在 docs/實查歸因-2026-11-14.md 的「第十四輪」。
/// </summary>
public class FourteenthRoundTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("xamlkeys-round14");
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

    // ── 88：StaticResource 在載入時解析到同一個字典檔裡的那份 ─────────────

    private static Dictionary<string, string> TwoControlThemes(string refKind, string toggleValue) => new()
    {
        // Material.Avalonia：Button.axaml 與 ToggleButton.axaml 各自定義 ButtonHoveredOpacity，各自引用自己那份；
        // 兩本都合併進同一個主題 —— 依合併順序 ToggleButton 那份「蓋掉」Button 那份，但 Button 的 StaticResource 在載入時已經拿到自己的
        ["App/App.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup></Project>",
        ["App/App.axaml"] = $"<Application {Av}><Application.Resources><ResourceDictionary><ResourceDictionary.MergedDictionaries>" +
                            "<ResourceInclude Source=\"/Themes/Button.axaml\"/><ResourceInclude Source=\"/Themes/ToggleButton.axaml\"/>" +
                            "</ResourceDictionary.MergedDictionaries></ResourceDictionary></Application.Resources></Application>",
        ["App/Themes/Button.axaml"] = $"<ResourceDictionary {Av} xmlns:system=\"clr-namespace:System;assembly=netstandard\">" +
                                      "<system:Double x:Key=\"ButtonHoveredOpacity\">0.12</system:Double>" +
                                      $"<ControlTheme x:Key=\"MaterialButton\" TargetType=\"Button\"><Setter Property=\"Opacity\" Value=\"{{{refKind} ButtonHoveredOpacity}}\"/></ControlTheme>" +
                                      "</ResourceDictionary>",
        ["App/Themes/ToggleButton.axaml"] = $"<ResourceDictionary {Av} xmlns:system=\"clr-namespace:System;assembly=netstandard\">" +
                                            $"<system:Double x:Key=\"ButtonHoveredOpacity\">{toggleValue}</system:Double>" +
                                            "<ControlTheme x:Key=\"MaterialToggleButton\" TargetType=\"ToggleButton\"><Setter Property=\"Opacity\" Value=\"{StaticResource ButtonHoveredOpacity}\"/></ControlTheme>" +
                                            "</ResourceDictionary>",
        ["App/MainWindow.axaml"] = $"<Window {Av}><StackPanel><Button Theme=\"{{StaticResource MaterialButton}}\"/><ToggleButton Theme=\"{{StaticResource MaterialToggleButton}}\"/></StackPanel></Window>",
    };

    [Fact]
    public void A_definition_its_own_StaticResource_resolves_to_is_not_shadowed()
    {
        var r = Analyze(TwoControlThemes("StaticResource", "0.05"));

        Assert.DoesNotContain(r.Findings, f => f.Kind is FindingKind.Shadowed or FindingKind.ShadowedIdentical);
        Assert.Equal(1, r.ShadowedButResolvedByStatic);
    }

    [Fact]
    public void A_DynamicResource_gets_the_merged_winner_so_the_loser_is_still_shadowed()
    {
        // DynamicResource 在執行期從元素往上找 —— 拿到的是合併後的贏家（ToggleButton 那份），Button 那份真的贏不了
        var r = Analyze(TwoControlThemes("DynamicResource", "0.05"));

        Assert.Contains(r.Findings, f => f.Kind == FindingKind.Shadowed && f.Key == "ButtonHoveredOpacity"
                                         && f.Location.File.EndsWith("Button.axaml") && !f.Location.File.EndsWith("ToggleButton.axaml"));
        Assert.Equal(0, r.ShadowedButResolvedByStatic);
    }

    [Fact]
    public void An_identical_local_copy_used_by_StaticResource_is_not_called_redundant()
    {
        // MaterialDesignInXamlToolkit：CheckBox.xaml 自己留一份跟 Button.xaml 一模一樣的 FocusVisual，用 StaticResource 引用。
        // 舊版說它「多餘」—— 照做刪掉，CheckBox.xaml 的 StaticResource 在載入時就找不到（合併的兄弟字典看不到彼此）。
        var r = Analyze(new()
        {
            ["App/App.xaml"] = $"<Application {Wpf}><Application.Resources><ResourceDictionary><ResourceDictionary.MergedDictionaries>" +
                               "<ResourceDictionary Source=\"Themes/CheckBox.xaml\"/><ResourceDictionary Source=\"Themes/Button.xaml\"/>" +
                               "</ResourceDictionary.MergedDictionaries></ResourceDictionary></Application.Resources></Application>",
            ["App/Themes/Button.xaml"] = $"<ResourceDictionary {Wpf}><Style x:Key=\"FocusVisual\"><Setter Property=\"Control.Margin\" Value=\"2\"/></Style>" +
                                         "<Style x:Key=\"MdButton\" TargetType=\"Button\"><Setter Property=\"FocusVisualStyle\" Value=\"{StaticResource FocusVisual}\"/></Style></ResourceDictionary>",
            ["App/Themes/CheckBox.xaml"] = $"<ResourceDictionary {Wpf}><Style x:Key=\"FocusVisual\"><Setter Property=\"Control.Margin\" Value=\"2\"/></Style>" +
                                           "<Style x:Key=\"MdCheckBox\" TargetType=\"CheckBox\"><Setter Property=\"FocusVisualStyle\" Value=\"{StaticResource FocusVisual}\"/></Style></ResourceDictionary>",
            ["App/MainWindow.xaml"] = $"<Window {Wpf}><StackPanel><Button Style=\"{{StaticResource MdButton}}\"/><CheckBox Style=\"{{StaticResource MdCheckBox}}\"/></StackPanel></Window>",
        });

        Assert.DoesNotContain(r.Findings, f => f.Kind is FindingKind.Shadowed or FindingKind.ShadowedIdentical);
        Assert.Equal(1, r.ShadowedButResolvedByStatic);
    }

    // ── 89、90：Dopamine —— 主題由另一個專案的程式載入、語系鍵由程式組出來寫進資源 ──────

    private static Dictionary<string, string> DopamineLike(string serviceCode, string page) => new()
    {
        ["Dopamine/Dopamine.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType><UseWPF>true</UseWPF></PropertyGroup>" +
                                       "<ItemGroup><ProjectReference Include=\"..\\Dopamine.Services\\Dopamine.Services.csproj\"/></ItemGroup></Project>",
        ["Dopamine/App.xaml"] = $"<Application {Wpf}><Application.Resources><ResourceDictionary/></Application.Resources></Application>",
        ["Dopamine/Resources/Themes/Dark.xaml"] = $"<ResourceDictionary {Wpf}><SolidColorBrush x:Key=\"Brush_PrimaryText\" Color=\"White\"/></ResourceDictionary>",
        ["Dopamine/Resources/Themes/Light.xaml"] = $"<ResourceDictionary {Wpf}><SolidColorBrush x:Key=\"Brush_PrimaryText\" Color=\"Black\"/></ResourceDictionary>",
        ["Dopamine/Views/Shell.xaml"] = page,
        ["Dopamine.Services/Dopamine.Services.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><UseWPF>true</UseWPF></PropertyGroup></Project>",
        ["Dopamine.Services/Services.cs"] = serviceCode,
    };

    private const string ThemeLoader =
        "class AppearanceService { void Apply(bool light) { var themeName = light ? \"Light\" : \"Dark\";" +
        " var d = new ResourceDictionary { Source = new Uri($\"/Dopamine;component/Resources/Themes/{themeName}.xaml\", UriKind.RelativeOrAbsolute) };" +
        " Application.Current.Resources.MergedDictionaries.Add(d); } }";

    [Fact]
    public void A_composed_pack_uri_naming_another_assembly_loads_that_projects_dictionaries()
    {
        var r = Analyze(DopamineLike(ThemeLoader, $"<Window {Wpf}><TextBlock Foreground=\"{{DynamicResource Brush_PrimaryText}}\"/></Window>"));

        Assert.DoesNotContain(r.Findings, f => f.Kind is FindingKind.OrphanDictionary or FindingKind.OutOfScope or FindingKind.DanglingDynamic);
        Assert.Equal(2, r.RuntimeLoadedDictionaries);
    }

    [Fact]
    public void Keys_written_with_a_fixed_prefix_in_code_are_not_dangling()
    {
        // I18nService：Application.Current.Resources["Language_" + text.Key] = value（字串在 XML 裡）
        var code = ThemeLoader + " class I18nService { void Load(Dictionary<string, string> texts) { foreach (var text in texts)" +
                   " Application.Current.Resources[\"Language_\" + text.Key] = text.Value; } }";
        var r = Analyze(DopamineLike(code, $"<Window {Wpf}><StackPanel><TextBlock Text=\"{{DynamicResource Language_Ok}}\"/>" +
                                           "<Label Content=\"{StaticResource Language_Key_Plus}\"/><TextBlock Text=\"{DynamicResource Lang_Other}\"/></StackPanel></Window>"));

        Assert.Equal(2, r.KeysMatchingComposedWrites);
        Assert.Equal(["Lang_Other"], r.Findings.Where(f => f.Kind == FindingKind.DanglingDynamic).Select(f => f.Key).ToList());
        Assert.DoesNotContain(r.Findings, f => f.Kind == FindingKind.DanglingStatic);
    }

    [Fact]
    public void An_interpolated_prefix_write_counts_and_a_short_prefix_does_not()
    {
        var code = ThemeLoader + " class I18n { void Load(string k, string v) { Application.Current.Resources.Add($\"Language_{k}\", v);" +
                   " Application.Current.Resources[\"L_\" + k] = v; } }";
        var r = Analyze(DopamineLike(code, $"<Window {Wpf}><StackPanel><TextBlock Text=\"{{DynamicResource Language_Ok}}\"/>" +
                                           "<TextBlock Text=\"{DynamicResource L_Cancel}\"/></StackPanel></Window>"));

        Assert.Equal(1, r.KeysMatchingComposedWrites);
        Assert.Equal(["L_Cancel"], r.Findings.Where(f => f.Kind == FindingKind.DanglingDynamic).Select(f => f.Key).ToList());
    }

    [Fact]
    public void A_prefix_written_in_an_unrelated_project_does_not_count()
    {
        // 規則 90 只看引用所在專案與相關（有參考關係）的專案
        var files = DopamineLike(ThemeLoader, $"<Window {Wpf}><TextBlock Text=\"{{DynamicResource Language_Ok}}\"/></Window>");
        files["Other/Other.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><UseWPF>true</UseWPF></PropertyGroup></Project>";
        files["Other/I18n.cs"] = "class I18n { void Load(string k, string v) { Application.Current.Resources[\"Language_\" + k] = v; } }";
        var r = Analyze(files);

        Assert.Equal(0, r.KeysMatchingComposedWrites);
        Assert.Contains(r.Findings, f => f.Kind == FindingKind.DanglingDynamic && f.Key == "Language_Ok");
    }

    [Fact]
    public void Code_lookups_of_prefix_written_keys_are_not_dangling()
    {
        var code = ThemeLoader + " class I18nService { void Load(string k, string v) { Application.Current.Resources[\"Language_\" + k] = v; } }" +
                   " class Vm { string T => (string)Application.Current.FindResource(\"Language_Default\"); }";
        var r = Analyze(DopamineLike(code, $"<Window {Wpf}><TextBlock/></Window>"));

        Assert.DoesNotContain(r.Findings, f => f.Kind == FindingKind.DanglingDynamic);
        Assert.Equal(1, r.KeysMatchingComposedWrites);
    }

    // ── shadowed 的說明照檔案的命名空間判 Avalonia，不看副檔名 ──────────────

    [Fact]
    public void An_Avalonia_dictionary_with_a_xaml_extension_is_described_as_Avalonia()
    {
        // Core2D 的 Avalonia.Themes.Browser：主題檔是 .xaml，舊版的說明寫成「WPF resolves…」
        var r = Analyze(new()
        {
            ["Theme/Theme.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup></Project>",
            ["Theme/App.axaml"] = $"<Application {Av}><Application.Resources><ResourceDictionary><ResourceDictionary.MergedDictionaries>" +
                                  "<ResourceInclude Source=\"/Accents/BaseResources.xaml\"/><ResourceInclude Source=\"/BrowserTokens.axaml\"/>" +
                                  "</ResourceDictionary.MergedDictionaries></ResourceDictionary></Application.Resources></Application>",
            ["Theme/Accents/BaseResources.xaml"] = $"<ResourceDictionary {Av}><CornerRadius x:Key=\"ControlCornerRadius\">3</CornerRadius></ResourceDictionary>",
            ["Theme/BrowserTokens.axaml"] = $"<ResourceDictionary {Av}><CornerRadius x:Key=\"ControlCornerRadius\">6</CornerRadius></ResourceDictionary>",
            ["Theme/MainWindow.axaml"] = $"<Window {Av}><Border CornerRadius=\"{{DynamicResource ControlCornerRadius}}\"/></Window>",
        });

        var f = Assert.Single(r.Findings, f => f.Kind == FindingKind.Shadowed);
        Assert.Contains("Avalonia resolves", f.Message);
    }

    // ── 91：不屬於任何專案的檔，照附近（最近一層底下有專案的資料夾）的專案判方言 ──

    private static Dictionary<string, string> SampleWithDeadCopy(string projectMarker) => new()
    {
        // Windows-universal-samples：shared/xaml/ 裡有一頁沒人收的舊副本（cs 專案用自己資料夾裡那份）
        ["Samples/ContentIndexer/cs/ContentIndexer.csproj"] = $"<Project><PropertyGroup><OutputType>AppContainerExe</OutputType>{projectMarker}</PropertyGroup></Project>",
        ["Samples/ContentIndexer/cs/App.xaml"] = $"<Application {Wpf}><Application.Resources><ResourceDictionary/></Application.Resources></Application>",
        ["Samples/ContentIndexer/shared/xaml/S2_Update.xaml"] = $"<Page {Wpf}><Grid Background=\"{{StaticResource ApplicationPageBackgroundThemeBrush}}\"/></Page>",
    };

    [Fact]
    public void A_file_in_no_project_takes_the_dialect_of_the_nearby_projects()
    {
        var r = Analyze(SampleWithDeadCopy("<TargetPlatformIdentifier>UAP</TargetPlatformIdentifier>"));

        Assert.DoesNotContain(r.Findings, f => f.Kind is FindingKind.DanglingStatic or FindingKind.External);
        Assert.True(r.FrameworkProvided >= 1);
    }

    [Fact]
    public void A_file_in_no_project_next_to_WPF_projects_stays_WPF()
    {
        var r = Analyze(SampleWithDeadCopy("<UseWPF>true</UseWPF>"));

        Assert.Contains(r.Findings, f => f.Kind == FindingKind.DanglingStatic && f.Key == "ApplicationPageBackgroundThemeBrush");
    }

    [Fact]
    public void Mixed_nearby_projects_do_not_make_the_file_WinUI()
    {
        var files = SampleWithDeadCopy("<TargetPlatformIdentifier>UAP</TargetPlatformIdentifier>");
        files["Samples/ContentIndexer/wpf/Wpf.csproj"] = "<Project><PropertyGroup><OutputType>WinExe</OutputType><UseWPF>true</UseWPF></PropertyGroup></Project>";
        var r = Analyze(files);

        Assert.Contains(r.Findings, f => f.Kind == FindingKind.DanglingStatic && f.Key == "ApplicationPageBackgroundThemeBrush");
    }

    [Fact]
    public void A_merged_dictionary_in_no_project_also_takes_the_nearby_dialect()
    {
        // Uno.Toolkit 的 samples/Uno.Toolkit.Samples/Styles/*.xaml：字典本身不在專案裡，被合併之後裡面的引用照字典的方言判
        var files = SampleWithDeadCopy("<TargetPlatformIdentifier>UAP</TargetPlatformIdentifier>");
        files["Samples/ContentIndexer/shared/xaml/S2_Update.xaml"] = $"<Page {Wpf}><Page.Resources><ResourceDictionary Source=\"Styles.xaml\"/></Page.Resources>" +
                                                                     "<TextBlock Style=\"{StaticResource Caption}\"/></Page>";
        files["Samples/ContentIndexer/shared/xaml/Styles.xaml"] = $"<ResourceDictionary {Wpf}><Style x:Key=\"Caption\" TargetType=\"TextBlock\">" +
                                                                  "<Setter Property=\"Foreground\" Value=\"{StaticResource ApplicationPageBackgroundThemeBrush}\"/></Style></ResourceDictionary>";
        var r = Analyze(files);

        Assert.DoesNotContain(r.Findings, f => f.Key == "ApplicationPageBackgroundThemeBrush");
    }

    [Fact]
    public void A_dictionary_linked_into_a_WPF_project_stays_WPF_even_next_to_UWP_projects()
    {
        // 規則 77 優先：連結進專案的檔跟著連結它的專案走，不看附近
        var files = SampleWithDeadCopy("<TargetPlatformIdentifier>UAP</TargetPlatformIdentifier>");
        files["Samples/ContentIndexer/shared/xaml/Styles.xaml"] = $"<ResourceDictionary {Wpf}><Style x:Key=\"Caption\" TargetType=\"TextBlock\">" +
                                                                  "<Setter Property=\"Foreground\" Value=\"{StaticResource ApplicationPageBackgroundThemeBrush}\"/></Style></ResourceDictionary>";
        files["WpfHost/WpfHost.csproj"] = "<Project><PropertyGroup><OutputType>WinExe</OutputType><UseWPF>true</UseWPF></PropertyGroup><ItemGroup>" +
                                          "<Page Include=\"..\\Samples\\ContentIndexer\\shared\\xaml\\Styles.xaml\" Link=\"Styles.xaml\"/></ItemGroup></Project>";
        files["WpfHost/App.xaml"] = $"<Application {Wpf}><Application.Resources><ResourceDictionary Source=\"Styles.xaml\"/></Application.Resources></Application>";
        files["WpfHost/MainWindow.xaml"] = $"<Window {Wpf}><TextBlock Style=\"{{StaticResource Caption}}\"/></Window>";
        var r = Analyze(files);

        Assert.Contains(r.Findings, f => f.Key == "ApplicationPageBackgroundThemeBrush" && f.Kind == FindingKind.DanglingStatic);
    }
}
