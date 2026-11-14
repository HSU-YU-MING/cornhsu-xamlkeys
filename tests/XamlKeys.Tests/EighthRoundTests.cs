using XamlKeys.Core;

namespace XamlKeys.Tests;

/// <summary>
/// 第八輪語料（2026-11-15）：dnGrep、Prism-Samples-Wpf、WoWDatabaseEditor、Dock、Windows Terminal、
/// Windows App SDK 範例、xamarin-forms-samples。歸因在 docs/實查歸因-2026-11-14.md 的「第八輪語料」。
/// </summary>
public class EighthRoundTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("xamlkeys-round8");
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
    private const string Xf = "xmlns=\"http://xamarin.com/schemas/2014/forms\" xmlns:x=\"http://schemas.microsoft.com/winfx/2009/xaml\"";
    private const string Maui = "xmlns=\"http://schemas.microsoft.com/dotnet/2021/maui\" xmlns:x=\"http://schemas.microsoft.com/winfx/2009/xaml\"";
    private const string Ava = "xmlns=\"https://github.com/avaloniaui\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";
    private const string AppProj = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup></Project>";
    private const string LibProj = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Library</OutputType></PropertyGroup></Project>";
    private const string CppApp = "<Project><PropertyGroup Label=\"Globals\"><ConfigurationType>Application</ConfigurationType></PropertyGroup></Project>";

    private static List<string> Keys(AnalysisResult r, FindingKind k) =>
        r.Findings.Where(f => f.Kind == k).Select(f => f.Key).Distinct().Order(StringComparer.Ordinal).ToList();

    // ── 64：前後都有固定部分的組出來字串（先存進變數）─────────────────────

    [Fact]
    public void A_composed_literal_with_a_fixed_head_and_tail_counts_as_using_matching_keys()
    {
        // dnGrep：bgColor = $"Match.Group.{groupIdx}.Highlight.Background"; run.SetResourceReference(…, bgColor);
        var r = Analyze(new()
        {
            ["App/App.csproj"] = AppProj,
            ["App/App.xaml"] = $"<Application {Wpf}><Application.Resources>" +
                "<SolidColorBrush x:Key=\"Match.Group.1.Highlight.Background\" Color=\"Red\"/>" +
                "<SolidColorBrush x:Key=\"Match.Group.2.Highlight.Background\" Color=\"Red\"/>" +
                "<SolidColorBrush x:Key=\"Match.Group.Unrelated\" Color=\"Red\"/>" +
                "</Application.Resources></Application>",
            ["App/Line.cs"] = "class Line { void A(Run run, int groupIdx) { var bgColor = $\"Match.Group.{groupIdx}.Highlight.Background\"; run.SetResourceReference(Run.BackgroundProperty, bgColor); } }",
        });

        Assert.Equal(["Match.Group.Unrelated"], Keys(r, FindingKind.Unused));
        Assert.Equal(2, r.KeysMatchingComposedLookups);
    }

    [Fact]
    public void A_composed_literal_with_only_a_head_does_not_rescue_keys()
    {
        // $"Icon{x}" 這種只有一頭的字串太寬（log 訊息、檔名都長這樣）—— 不是查找點就不認
        var r = Analyze(new()
        {
            ["App/App.csproj"] = AppProj,
            ["App/App.xaml"] = $"<Application {Wpf}><Application.Resources><SolidColorBrush x:Key=\"IconBrush\" Color=\"Red\"/></Application.Resources></Application>",
            ["App/Log.cs"] = "class Log { string A(int x) => $\"Icon{x}\"; }",
        });

        Assert.Equal(["IconBrush"], Keys(r, FindingKind.Unused));
    }

    [Fact]
    public void A_composed_literal_in_an_unrelated_project_does_not_rescue_keys()
    {
        var r = Analyze(new()
        {
            ["A/A.csproj"] = AppProj,
            ["A/App.xaml"] = $"<Application {Wpf}><Application.Resources><SolidColorBrush x:Key=\"Match.Group.1.Highlight.Background\" Color=\"Red\"/></Application.Resources></Application>",
            ["B/B.csproj"] = AppProj,
            ["B/Line.cs"] = "class Line { string A(int i) => $\"Match.Group.{i}.Highlight.Background\"; }",
        });

        Assert.Equal(["Match.Group.1.Highlight.Background"], Keys(r, FindingKind.Unused));
    }

    // ── 65：Xamarin.Forms 的裝置樣式 ─────────────────────────────────────

    [Fact]
    public void Xamarin_Forms_device_styles_are_provided_by_the_framework()
    {
        var r = Analyze(new()
        {
            ["App/App.csproj"] = AppProj,
            ["App/App.xaml"] = $"<Application {Xf}><Application.Resources><Color x:Key=\"Own\">Red</Color></Application.Resources></Application>",
            ["App/Page.xaml"] = $"<ContentPage {Xf}><StackLayout BackgroundColor=\"{{StaticResource Own}}\"><Label Style=\"{{DynamicResource TitleStyle}}\"/><Label Style=\"{{DynamicResource SubtitleTextStyle}}\"/></StackLayout></ContentPage>",
            ["App/PageCS.cs"] = "class PageCS { void A(Label l) { l.SetDynamicResource(Label.StyleProperty, \"BodyStyle\"); } }",
        });

        Assert.Equal(["SubtitleTextStyle"], Keys(r, FindingKind.DanglingDynamic));
    }

    [Fact]
    public void MAUI_pages_do_not_get_the_Xamarin_Forms_device_styles()
    {
        var r = Analyze(new()
        {
            ["App/App.csproj"] = AppProj,
            ["App/App.xaml"] = $"<Application {Maui}><Application.Resources><Color x:Key=\"Own\">Red</Color></Application.Resources></Application>",
            ["App/Page.xaml"] = $"<ContentPage {Maui}><Label TextColor=\"{{StaticResource Own}}\" Style=\"{{DynamicResource TitleStyle}}\"/></ContentPage>",
        });

        Assert.Equal(["TitleStyle"], Keys(r, FindingKind.DanglingDynamic));
    }

    // ── 66：C# 裡 new 出來的字典類別 ─────────────────────────────────────

    [Fact]
    public void A_dictionary_class_created_in_code_is_runtime_loaded()
    {
        // Xamarin.Forms ThemingDemo：mergedDictionaries.Add(new DarkTheme());
        var r = Analyze(new()
        {
            ["App/App.csproj"] = AppProj,
            ["App/App.xaml"] = $"<Application {Xf} xmlns:local=\"clr-namespace:Demo\"><Application.Resources><ResourceDictionary Source=\"Themes/LightTheme.xaml\"/></Application.Resources></Application>",
            ["App/Themes/LightTheme.xaml"] = $"<ResourceDictionary {Xf} x:Class=\"Demo.LightTheme\"><Color x:Key=\"PageBg\">White</Color></ResourceDictionary>",
            ["App/Themes/DarkTheme.xaml"] = $"<ResourceDictionary {Xf} x:Class=\"Demo.DarkTheme\"><Color x:Key=\"PageBg\">Black</Color></ResourceDictionary>",
            ["App/Page.xaml"] = $"<ContentPage {Xf} BackgroundColor=\"{{DynamicResource PageBg}}\"/>",
            ["App/Switch.cs"] = "namespace Demo; class Switch { void A() { Application.Current.Resources.MergedDictionaries.Add(new DarkTheme()); } }",
        });

        Assert.Empty(Keys(r, FindingKind.OrphanDictionary));
        Assert.Equal(1, r.DictionariesCreatedInCode);
        Assert.Equal(0, r.RuntimeLoadedDictionaries);
    }

    [Fact]
    public void A_dictionary_created_in_code_does_not_join_any_scope()
    {
        // SukiUI：主題類別在 C# 裡被 new 出來，裡面有看不到的 <SimpleTheme/>。
        // 第一版把它加進整個專案的作用域，真的懸空（CWhite-）被降級成 external。
        var r = Analyze(new()
        {
            ["Lib/Lib.csproj"] = AppProj,
            ["Lib/Theme.axaml"] = $"<Styles {Ava} x:Class=\"Suki.Theme\"><SimpleTheme/><Styles.Resources><Color x:Key=\"Accent\">Red</Color></Styles.Resources></Styles>",
            ["Lib/Picker.axaml"] = $"<UserControl {Ava}><Border Background=\"{{DynamicResource CWhite-}}\"/></UserControl>",
            ["Lib/Setup.cs"] = "namespace Suki; class Setup { void A(Application app) { app.Styles.Add(new Theme()); } }",
        });

        Assert.Equal(["CWhite-"], Keys(r, FindingKind.DanglingDynamic));
    }

    [Fact]
    public void A_dictionary_class_created_in_an_unrelated_project_does_not_count()
    {
        // maui-samples 有好幾個範例都有自己的 DarkTheme：一個範例的 new DarkTheme() 不能赦免另一個
        var r = Analyze(new()
        {
            ["A/A.csproj"] = AppProj,
            ["A/App.xaml"] = $"<Application {Xf}><Application.Resources><Color x:Key=\"Own\">Red</Color></Application.Resources></Application>",
            ["A/Page.xaml"] = $"<ContentPage {Xf} BackgroundColor=\"{{StaticResource Own}}\"/>",
            ["A/Themes/DarkTheme.xaml"] = $"<ResourceDictionary {Xf} x:Class=\"Demo.DarkTheme\"><Color x:Key=\"PageBg\">Black</Color></ResourceDictionary>",
            ["B/B.csproj"] = AppProj,
            ["B/Switch.cs"] = "namespace Demo; class Switch { void A() { Resources.MergedDictionaries.Add(new DarkTheme()); } }",
        });

        Assert.Equal(["DarkTheme.xaml"], Keys(r, FindingKind.OrphanDictionary));
    }

    // ── 67：分層主題（基底＋覆寫）────────────────────────────────────────

    private static Dictionary<string, string> LayeredThemes(string win10Usage) => new()
    {
        ["App/App.csproj"] = AppProj,
        ["App/Styles/Win10/Colors.axaml"] = $"<Styles {Ava}><Styles.Resources><SolidColorBrush x:Key=\"ContentBackground\" Color=\"#202020\"/></Styles.Resources></Styles>",
        ["App/Styles/Win11/Colors.axaml"] = $"<Styles {Ava}><StyleInclude Source=\"avares://App/Styles/Win10/Colors.axaml\"/><Styles.Resources><SolidColorBrush x:Key=\"ContentBackground\" Color=\"#2B2B2B\"/></Styles.Resources></Styles>",
        ["App/Styles/Win11Dark.axaml"] = $"<Styles {Ava}><StyleInclude Source=\"avares://App/Styles/Win11/Colors.axaml\"/></Styles>",
        ["App/Styles/Win10Dark.axaml"] = win10Usage,
        ["App/Views/Main.axaml"] = $"<UserControl {Ava} Background=\"{{DynamicResource ContentBackground}}\"/>",
    };

    [Fact]
    public void A_base_definition_overridden_by_a_layered_theme_is_not_shadowed_when_another_theme_uses_it()
    {
        var r = Analyze(LayeredThemes($"<Styles {Ava}><StyleInclude Source=\"avares://App/Styles/Win10/Colors.axaml\"/></Styles>"));

        Assert.Empty(Keys(r, FindingKind.Shadowed));
        Assert.Equal(1, r.LayeredOverrides);
    }

    [Fact]
    public void A_base_definition_overridden_everywhere_is_still_shadowed_and_the_message_names_Avalonia()
    {
        // Win10 主題沒有直接用 Win10 的色票 —— 那份真的永遠贏不了
        var r = Analyze(LayeredThemes($"<Styles {Ava}><StyleInclude Source=\"avares://App/Styles/Win11/Colors.axaml\"/></Styles>"));

        var f = Assert.Single(r.Findings, f => f.Kind == FindingKind.Shadowed);
        Assert.Contains("Avalonia", f.Message);
        Assert.Equal(0, r.LayeredOverrides);
    }

    // ── 68、72：C++/WinRT 專案 ───────────────────────────────────────────

    [Fact]
    public void A_vcxproj_is_a_WinUI_project_so_WinUI_keys_resolve()
    {
        // Windows Terminal、Windows App SDK 的 cpp-winui 範例
        var r = Analyze(new()
        {
            ["Term/Term.vcxproj"] = CppApp,
            ["Term/App.xaml"] = $"<Application {Wpf}><Application.Resources><ResourceDictionary><ResourceDictionary.MergedDictionaries>" +
                                "<XamlControlsResources xmlns=\"using:Microsoft.UI.Xaml.Controls\"/><ResourceDictionary Source=\"ms-appx:///Styles.xaml\"/>" +
                                "</ResourceDictionary.MergedDictionaries></ResourceDictionary></Application.Resources></Application>",
            ["Term/Styles.xaml"] = $"<ResourceDictionary {Wpf}><Style x:Key=\"Caption\" TargetType=\"TextBlock\"/></ResourceDictionary>",
            ["Term/MainPage.xaml"] = $"<Page {Wpf} Background=\"{{ThemeResource ApplicationPageBackgroundThemeBrush}}\"><TextBlock Style=\"{{StaticResource Caption}}\" Foreground=\"{{ThemeResource TextFillColorPrimaryBrush}}\"/></Page>",
        });

        Assert.DoesNotContain(r.Findings, f => f.Kind is FindingKind.External or FindingKind.DanglingStatic or FindingKind.DanglingDynamic);
        Assert.Equal(100.0, r.Coverage);
    }

    [Fact]
    public void A_wide_string_key_in_CPlusPlus_code_counts_as_used()
    {
        var r = Analyze(new()
        {
            ["Term/Term.vcxproj"] = CppApp,
            ["Term/App.xaml"] = $"<Application {Wpf}><Application.Resources><SolidColorBrush x:Key=\"TabBrush\" Color=\"Red\"/><SolidColorBrush x:Key=\"Unused\" Color=\"Red\"/></Application.Resources></Application>",
            ["Term/TabBase.cpp"] = "void TabBase::Paint() { auto brush = Application::Current().Resources().Lookup(winrt::box_value(L\"TabBrush\")); }",
        });

        Assert.Equal(["Unused"], Keys(r, FindingKind.Unused));
    }

    // ── 71：x:Name 的資源在 code-behind 當欄位用 ───────────────────────────

    [Theory]
    [InlineData("AnimationPage.cpp", "void AnimationPage::Start() { myStoryBoard().Begin(); }")]
    [InlineData("AnimationPage.xaml.cpp", "void AnimationPage::Start() { myStoryBoard().Begin(); }")]
    [InlineData("AnimationPage.xaml.cs", "partial class AnimationPage { void Start() { myStoryBoard.Begin(); } }")]
    public void A_named_resource_used_as_a_field_in_its_code_behind_is_not_unused(string codeBehind, string code)
    {
        var r = Analyze(new()
        {
            ["App/App.vcxproj"] = CppApp,
            ["App/App.xaml"] = $"<Application {Wpf}><Application.Resources><SolidColorBrush x:Key=\"Global\" Color=\"Red\"/></Application.Resources></Application>",
            ["App/AnimationPage.xaml"] = $"<Page {Wpf} Background=\"{{StaticResource Global}}\"><Page.Resources><Storyboard x:Name=\"myStoryBoard\"/><Storyboard x:Name=\"otherStoryBoard\"/></Page.Resources></Page>",
            [$"App/{codeBehind}"] = code,
        });

        Assert.Equal(["otherStoryBoard"], Keys(r, FindingKind.Unused));
        Assert.Equal(1, r.KeysUsedAsFields);
    }

    [Fact]
    public void A_name_mentioned_in_another_file_s_code_behind_does_not_count()
    {
        var r = Analyze(new()
        {
            ["App/App.csproj"] = AppProj,
            ["App/App.xaml"] = $"<Application {Wpf}><Application.Resources><SolidColorBrush x:Key=\"Global\" Color=\"Red\"/></Application.Resources></Application>",
            ["App/AnimationPage.xaml"] = $"<Page {Wpf} Background=\"{{StaticResource Global}}\"><Page.Resources><Storyboard x:Name=\"Fade\"/></Page.Resources></Page>",
            ["App/OtherPage.xaml.cs"] = "partial class OtherPage { void Start() { Fade.Begin(); } }",
        });

        Assert.Equal(["Fade"], Keys(r, FindingKind.Unused));
    }

    // ── 73：放在 Resources 裡的 TeachingTip ─────────────────────────────

    [Theory]
    [InlineData("Page.Resources")]
    [InlineData("TextBox.Resources")]
    public void A_teaching_tip_placed_in_resources_is_not_unused(string where)
    {
        // Files：<TextBox.Resources><TeachingTip x:Name="InvalidNameWarning" IsOpen="{x:Bind …}"/></TextBox.Resources>
        var inner = where == "Page.Resources"
            ? "<Page.Resources><TeachingTip x:Name=\"Tip\" IsOpen=\"{x:Bind Vm.Show}\"/><SolidColorBrush x:Key=\"Dead\" Color=\"Red\"/></Page.Resources><Grid/>"
            : "<StackPanel><TextBox><TextBox.Resources><TeachingTip x:Name=\"Tip\" IsOpen=\"{x:Bind Vm.Show}\"/><SolidColorBrush x:Key=\"Dead\" Color=\"Red\"/></TextBox.Resources></TextBox></StackPanel>";
        var r = Analyze(new()
        {
            ["App/App.csproj"] = AppProj,
            ["App/Page.xaml"] = $"<Page {Wpf}>{inner}</Page>",
        });

        Assert.Equal(["Dead"], Keys(r, FindingKind.Unused));
        Assert.Equal(1, r.PlacedTeachingTips);
    }

    // ── 69：萬用字元的 csproj Link ───────────────────────────────────────

    [Fact]
    public void A_wildcard_link_with_RecursiveDir_resolves_the_merge()
    {
        // Dock：Simple 主題用 Fluent 主題的 Controls/**/*.axaml，Link="Controls\%(RecursiveDir)%(Filename)%(Extension)"
        var r = Analyze(new()
        {
            ["Fluent/Fluent.csproj"] = LibProj,
            ["Fluent/Controls/ControlStrings.axaml"] = $"<ResourceDictionary {Ava}><x:String x:Key=\"CloseString\">Close</x:String></ResourceDictionary>",
            ["Simple/Simple.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><AvaloniaResource Include=\"..\\Fluent\\Controls\\**\\*.axaml\"><Link>Controls\\%(RecursiveDir)%(Filename)%(Extension)</Link></AvaloniaResource></ItemGroup></Project>",
            ["Simple/Theme.axaml"] = $"<Styles {Ava}><Styles.Resources><ResourceDictionary><ResourceDictionary.MergedDictionaries><ResourceInclude Source=\"/Controls/ControlStrings.axaml\"/></ResourceDictionary.MergedDictionaries></ResourceDictionary></Styles.Resources></Styles>",
        });

        Assert.DoesNotContain(r.Findings, f => f.Kind == FindingKind.External);
        Assert.Equal(100.0, r.Coverage);
    }

    [Theory]
    [InlineData(@"..\Fluent\Controls\**\*.axaml", @"Controls\%(RecursiveDir)%(Filename)%(Extension)", "Controls/a/b.axaml", true)]
    [InlineData(@"..\Fluent\Controls\*.axaml", @"Controls\%(RecursiveDir)%(Filename)%(Extension)", "Controls/a/b.axaml", false)]
    [InlineData(@"..\Fluent\Controls\*.axaml", @"Controls\%(RecursiveDir)%(Filename)%(Extension)", "Controls/b.axaml", true)]
    [InlineData(@"..\Fluent\Controls\**\*.axaml", @"Controls\%(RecursiveDir)%(Filename)%(Extension)", "Controls/b.xaml", false)]
    [InlineData(@"..\Fluent\Controls\**\*.axaml", @"Controls\%(RecursiveDir)%(Filename)%(Extension)", "Controls/../b.axaml", false)]
    public void Wildcard_links_only_match_their_own_shape(string include, string link, string request, bool resolves)
    {
        var r = Analyze(new()
        {
            ["Fluent/Fluent.csproj"] = LibProj,
            ["Fluent/Controls/b.axaml"] = $"<ResourceDictionary {Ava}><x:String x:Key=\"K\">x</x:String></ResourceDictionary>",
            ["Fluent/Controls/a/b.axaml"] = $"<ResourceDictionary {Ava}><x:String x:Key=\"K2\">x</x:String></ResourceDictionary>",
            ["Fluent/Controls/b.xaml"] = $"<ResourceDictionary {Wpf}><x:String x:Key=\"K3\">x</x:String></ResourceDictionary>",
            ["Fluent/b.axaml"] = $"<ResourceDictionary {Ava}><x:String x:Key=\"K4\">x</x:String></ResourceDictionary>",
            ["Simple/Simple.csproj"] = $"<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><AvaloniaResource Include=\"{include}\"><Link>{link}</Link></AvaloniaResource></ItemGroup></Project>",
            ["Simple/Theme.axaml"] = $"<ResourceDictionary {Ava}><ResourceDictionary.MergedDictionaries><ResourceInclude Source=\"/{request}\"/></ResourceDictionary.MergedDictionaries></ResourceDictionary>",
        });

        Assert.Equal(resolves ? 1 : 0, r.MergeEdgesResolved);
    }

    // ── 70：Dark／Light 二選一的 include 元素 ───────────────────────────

    [Fact]
    public void A_dark_light_include_element_merges_both_sources_as_alternatives()
    {
        // WoWDatabaseEditor：<styles:StyleIncludeColorAware DarkSource="…" LightSource="…"/>
        var r = Analyze(new()
        {
            ["App/App.csproj"] = AppProj,
            ["App/Views/Dark.axaml"] = $"<Styles {Ava}><Styles.Resources><SolidColorBrush x:Key=\"WarningBrush\" Color=\"Orange\"/><SolidColorBrush x:Key=\"DarkOnly\" Color=\"Black\"/></Styles.Resources></Styles>",
            ["App/Views/Light.axaml"] = $"<Styles {Ava}><Styles.Resources><SolidColorBrush x:Key=\"WarningBrush\" Color=\"Red\"/><SolidColorBrush x:Key=\"DarkOnly\" Color=\"White\"/></Styles.Resources></Styles>",
            ["App/Views/Viewer.axaml"] = $"<UserControl {Ava} xmlns:styles=\"clr-namespace:App.Styles\"><UserControl.Styles>" +
                                         "<styles:StyleIncludeColorAware DarkSource=\"avares://App/Views/Dark.axaml\" LightSource=\"avares://App/Views/Light.axaml\"/>" +
                                         "</UserControl.Styles><Border BorderBrush=\"{DynamicResource WarningBrush}\" Background=\"{DynamicResource DarkOnly}\"/></UserControl>",
        });

        Assert.DoesNotContain(r.Findings, f => f.Kind is FindingKind.External or FindingKind.Shadowed or FindingKind.OrphanDictionary or FindingKind.DanglingDynamic);
        Assert.Equal(100.0, r.Coverage);
    }
}
