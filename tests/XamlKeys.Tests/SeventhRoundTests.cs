using XamlKeys.Core;

namespace XamlKeys.Tests;

/// <summary>
/// 第七輪語料（2026-11-15）：Notepads、LiveCharts2、Avalonia.Samples、Uno Chefs、XamlFlair、MVVM-Samples、AvalonDock，
/// 加上 README 列的已知限制「UserControl 真正的父層不知道」。歸因在 docs/實查歸因-2026-11-14.md 的「第七輪語料」。
/// </summary>
public class SeventhRoundTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("xamlkeys-round7");
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
    private const string AppProj = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup></Project>";
    private const string MauiProj = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><UseMaui>true</UseMaui></PropertyGroup></Project>";

    private static List<string> Keys(AnalysisResult r, FindingKind k) =>
        r.Findings.Where(f => f.Kind == k).Select(f => f.Key).Distinct().Order(StringComparer.Ordinal).ToList();

    private const string App = "<Application {0} x:Class=\"Demo.App\"><Application.Resources><SolidColorBrush x:Key=\"Global\" Color=\"Red\"/></Application.Resources></Application>";

    /// <summary>AvalonDock 的形狀：視窗的資源裡用 DataTemplate 放 &lt;views:SearchView/&gt;，SearchView 用 DynamicResource 取視窗的色票。</summary>
    private static string Window(string body, string resources = "<SolidColorBrush x:Key=\"PanelBg\" Color=\"#252526\"/>") =>
        $"<Window {Wpf} xmlns:views=\"clr-namespace:Demo.Views\" x:Class=\"Demo.MainWindow\" Background=\"{{DynamicResource Global}}\">" +
        $"<Window.Resources>{resources}<DataTemplate x:Key=\"T\"><views:SearchView/></DataTemplate></Window.Resources>{body}</Window>";

    private static string Control(string refKind = "DynamicResource", string cls = "Demo.Views.SearchView") =>
        $"<UserControl {Wpf} x:Class=\"{cls}\" Background=\"{{{refKind} PanelBg}}\"/>";

    // ── 57：UserControl 的 DynamicResource 在每一個放置處都找得到 ──────────────

    [Fact]
    public void A_UserControl_reading_its_hosts_resources_dynamically_is_resolved()
    {
        var r = Analyze(new()
        {
            ["App/App.csproj"] = AppProj,
            ["App/App.xaml"] = string.Format(App, Wpf),
            ["App/MainWindow.xaml"] = Window("<ContentControl ContentTemplate=\"{StaticResource T}\"/>"),
            ["App/Views/SearchView.xaml"] = Control(),
        });

        Assert.Empty(Keys(r, FindingKind.OutOfScope));
        Assert.Equal(1, r.KeysResolvedThroughHosts);
        Assert.DoesNotContain(r.Findings, f => f.Kind == FindingKind.Unused && f.Key == "PanelBg");
    }

    [Fact]
    public void A_StaticResource_is_not_resolved_through_the_host()
    {
        // StaticResource 在控制項自己 InitializeComponent() 時就解析 —— 那時還沒被放進宿主
        var r = Analyze(new()
        {
            ["App/App.csproj"] = AppProj,
            ["App/App.xaml"] = string.Format(App, Wpf),
            ["App/MainWindow.xaml"] = Window("<ContentControl ContentTemplate=\"{StaticResource T}\"/>"),
            ["App/Views/SearchView.xaml"] = Control("StaticResource"),
        });

        Assert.Equal(["PanelBg"], Keys(r, FindingKind.OutOfScope));
        Assert.Equal(0, r.KeysResolvedThroughHosts);
    }

    [Fact]
    public void One_host_without_the_key_keeps_the_finding_and_names_that_host()
    {
        var r = Analyze(new()
        {
            ["App/App.csproj"] = AppProj,
            ["App/App.xaml"] = string.Format(App, Wpf),
            ["App/MainWindow.xaml"] = Window("<ContentControl ContentTemplate=\"{StaticResource T}\"/>"),
            ["App/Other.xaml"] = $"<Window {Wpf} xmlns:v=\"clr-namespace:Demo.Views\" x:Class=\"Demo.Other\"><v:SearchView/></Window>",
            ["App/Views/SearchView.xaml"] = Control(),
        });

        var f = Assert.Single(r.Findings, f => f.Kind == FindingKind.OutOfScope);
        Assert.Contains("Other.xaml", f.Message);
        Assert.Equal(0, r.KeysResolvedThroughHosts);
    }

    [Fact]
    public void A_control_never_placed_in_XAML_keeps_the_finding()
    {
        var r = Analyze(new()
        {
            ["App/App.csproj"] = AppProj,
            ["App/App.xaml"] = string.Format(App, Wpf),
            ["App/MainWindow.xaml"] = $"<Window {Wpf} x:Class=\"Demo.MainWindow\"><Window.Resources><SolidColorBrush x:Key=\"PanelBg\" Color=\"Red\"/></Window.Resources><Border Background=\"{{DynamicResource PanelBg}}\"/></Window>",
            ["App/Views/SearchView.xaml"] = Control(),
        });

        Assert.Equal(["PanelBg"], Keys(r, FindingKind.OutOfScope));
    }

    [Theory]
    [InlineData("var v = new SearchView();")]
    [InlineData("var v = new Views.SearchView { };")]
    [InlineData("services.AddTransient<SearchView>();")]
    [InlineData("Register(typeof(SearchView));")]
    public void A_control_also_created_in_code_keeps_the_finding(string code)
    {
        // C# 也會建立它：那些宿主我們看不到
        var r = Analyze(new()
        {
            ["App/App.csproj"] = AppProj,
            ["App/App.xaml"] = string.Format(App, Wpf),
            ["App/MainWindow.xaml"] = Window("<ContentControl ContentTemplate=\"{StaticResource T}\"/>"),
            ["App/Views/SearchView.xaml"] = Control(),
            ["App/Views/SearchView.xaml.cs"] = "namespace Demo.Views; public partial class SearchView : UserControl { public SearchView() { InitializeComponent(); } }",
            ["App/Shell.cs"] = $"namespace Demo; class Shell {{ void Go() {{ {code} }} }}",
        });

        Assert.Equal(["PanelBg"], Keys(r, FindingKind.OutOfScope));
    }

    [Fact]
    public void The_control_s_own_code_behind_does_not_count_as_creating_it()
    {
        var r = Analyze(new()
        {
            ["App/App.csproj"] = AppProj,
            ["App/App.xaml"] = string.Format(App, Wpf),
            ["App/MainWindow.xaml"] = Window("<ContentControl ContentTemplate=\"{StaticResource T}\"/>"),
            ["App/Views/SearchView.xaml"] = Control(),
            ["App/Views/SearchView.xaml.cs"] = "namespace Demo.Views; public partial class SearchView : UserControl { public SearchView() { InitializeComponent(); } }",
        });

        Assert.Empty(Keys(r, FindingKind.OutOfScope));
    }

    [Fact]
    public void A_host_that_is_itself_a_placed_control_is_followed_up()
    {
        // SearchView 放在 Panel 裡，Panel 放在視窗裡，鍵在視窗
        var r = Analyze(new()
        {
            ["App/App.csproj"] = AppProj,
            ["App/App.xaml"] = string.Format(App, Wpf),
            ["App/MainWindow.xaml"] = $"<Window {Wpf} xmlns:v=\"clr-namespace:Demo.Views\" x:Class=\"Demo.MainWindow\"><Window.Resources><SolidColorBrush x:Key=\"PanelBg\" Color=\"Red\"/></Window.Resources><v:Panel/></Window>",
            ["App/Views/Panel.xaml"] = $"<UserControl {Wpf} xmlns:v=\"clr-namespace:Demo.Views\" x:Class=\"Demo.Views.Panel\"><v:SearchView/></UserControl>",
            ["App/Views/SearchView.xaml"] = Control(),
        });

        Assert.Empty(Keys(r, FindingKind.OutOfScope));
        Assert.Equal(1, r.KeysResolvedThroughHosts);
    }

    [Fact]
    public void Placements_in_an_unrelated_project_do_not_count()
    {
        // 同名的類別在不相干的專案裡（MAUI 版與 Xamarin.Forms 版的同一個範例）：那邊的放置處不算
        var r = Analyze(new()
        {
            ["A/A.csproj"] = AppProj,
            ["A/App.xaml"] = string.Format(App, Wpf),
            ["A/Views/SearchView.xaml"] = Control(),
            ["A/Holder.xaml"] = $"<Window {Wpf} x:Class=\"Demo.Holder\"><Window.Resources><SolidColorBrush x:Key=\"PanelBg\" Color=\"Red\"/></Window.Resources></Window>",
            ["B/B.csproj"] = AppProj,
            ["B/MainWindow.xaml"] = Window(""),
        });

        Assert.Equal(["PanelBg"], Keys(r, FindingKind.OutOfScope));
    }

    // ── 58：寫在自訂控制項上的引用，由控制項自己的資源提供 ──────────────────

    [Fact]
    public void A_reference_on_a_custom_control_resolves_in_that_control_s_own_resources()
    {
        // MVVM-Samples：<controls:InteractiveSample ControlTemplate="{StaticResource InteractiveSampleTemplate}"/>
        var r = Analyze(new()
        {
            ["App/App.csproj"] = MauiProj,
            ["App/App.xaml"] = string.Format(App, Maui),
            ["App/Controls/InteractiveSample.xaml"] = $"<ContentView {Maui} x:Class=\"Demo.Controls.InteractiveSample\"><ContentView.Resources><ControlTemplate x:Key=\"SampleTemplate\"><Grid/></ControlTemplate></ContentView.Resources></ContentView>",
            ["App/Views/Page.xaml"] = $"<ContentPage {Maui} xmlns:controls=\"clr-namespace:Demo.Controls\" x:Class=\"Demo.Views.Page\"><controls:InteractiveSample ControlTemplate=\"{{StaticResource SampleTemplate}}\"/></ContentPage>",
        });

        Assert.Empty(Keys(r, FindingKind.OutOfScope));
        Assert.Equal(1, r.KeysFromControlResources);
    }

    [Fact]
    public void A_reference_next_to_the_custom_control_does_not_see_its_resources()
    {
        var r = Analyze(new()
        {
            ["App/App.csproj"] = MauiProj,
            ["App/App.xaml"] = string.Format(App, Maui),
            ["App/Controls/InteractiveSample.xaml"] = $"<ContentView {Maui} x:Class=\"Demo.Controls.InteractiveSample\"><ContentView.Resources><ControlTemplate x:Key=\"SampleTemplate\"><Grid/></ControlTemplate></ContentView.Resources></ContentView>",
            ["App/Views/Page.xaml"] = $"<ContentPage {Maui} xmlns:controls=\"clr-namespace:Demo.Controls\" x:Class=\"Demo.Views.Page\"><StackLayout><controls:InteractiveSample/><ContentView ControlTemplate=\"{{StaticResource SampleTemplate}}\"/></StackLayout></ContentPage>",
        });

        Assert.Equal(["SampleTemplate"], Keys(r, FindingKind.OutOfScope));
        Assert.Equal(0, r.KeysFromControlResources);
    }

    [Fact]
    public void A_same_named_control_in_an_unrelated_project_does_not_lend_its_resources()
    {
        // 頁面的 <controls:Card> 在 A 專案找不到類別；B 專案（不相干）剛好有同名的 Card，資源裡有這個鍵
        var r = Analyze(new()
        {
            ["A/A.csproj"] = MauiProj,
            ["A/App.xaml"] = string.Format(App, Maui),
            ["A/Holder.xaml"] = $"<ContentPage {Maui} x:Class=\"Demo.Holder\"><ContentPage.Resources><ControlTemplate x:Key=\"CardTemplate\"><Grid/></ControlTemplate></ContentPage.Resources></ContentPage>",
            ["A/Page.xaml"] = $"<ContentPage {Maui} xmlns:controls=\"clr-namespace:Demo.Controls\" x:Class=\"Demo.Page\"><controls:Card ControlTemplate=\"{{StaticResource CardTemplate}}\"/></ContentPage>",
            ["B/B.csproj"] = MauiProj,
            ["B/Card.xaml"] = $"<ContentView {Maui} x:Class=\"Demo.Controls.Card\"><ContentView.Resources><ControlTemplate x:Key=\"CardTemplate\"><Grid/></ControlTemplate></ContentView.Resources></ContentView>",
        });

        Assert.Equal(["CardTemplate"], Keys(r, FindingKind.OutOfScope));
        Assert.Equal(0, r.KeysFromControlResources);
    }

    [Fact]
    public void A_page_deriving_from_a_XAML_base_page_sees_the_base_page_s_resources()
    {
        // maui-samples ControlTemplateDemos：根元素就是 <controls:HeaderFooterPage>（繼承），基底的資源在建構時就填好了
        var r = Analyze(new()
        {
            ["App/App.csproj"] = MauiProj,
            ["App/App.xaml"] = string.Format(App, Maui),
            ["App/Controls/HeaderFooterPage.xaml"] = $"<ContentPage {Maui} x:Class=\"Demo.Controls.HeaderFooterPage\"><ContentPage.Resources><ControlTemplate x:Key=\"Secondary\"><Grid/></ControlTemplate></ContentPage.Resources></ContentPage>",
            ["App/Views/Page.xaml"] = $"<controls:HeaderFooterPage {Maui} xmlns:controls=\"clr-namespace:Demo.Controls\" x:Class=\"Demo.Views.Page\" ControlTemplate=\"{{StaticResource Secondary}}\"/>",
        });

        Assert.Empty(Keys(r, FindingKind.OutOfScope));
        Assert.Equal(1, r.KeysFromControlResources);
    }

    [Fact]
    public void A_reference_on_a_custom_control_whose_resources_lack_the_key_stays_out_of_scope()
    {
        var r = Analyze(new()
        {
            ["App/App.csproj"] = MauiProj,
            ["App/App.xaml"] = string.Format(App, Maui),
            ["App/Controls/InteractiveSample.xaml"] = $"<ContentView {Maui} x:Class=\"Demo.Controls.InteractiveSample\"><ContentView.Resources><ControlTemplate x:Key=\"SampleTemplate\"><Grid/></ControlTemplate></ContentView.Resources></ContentView>",
            ["App/Holder.xaml"] = $"<ContentPage {Maui} x:Class=\"Demo.Holder\"><ContentPage.Resources><ControlTemplate x:Key=\"Elsewhere\"><Grid/></ControlTemplate></ContentPage.Resources></ContentPage>",
            ["App/Views/Page.xaml"] = $"<ContentPage {Maui} xmlns:controls=\"clr-namespace:Demo.Controls\" x:Class=\"Demo.Views.Page\"><controls:InteractiveSample ControlTemplate=\"{{StaticResource Elsewhere}}\"/></ContentPage>",
        });

        Assert.Equal(["Elsewhere"], Keys(r, FindingKind.OutOfScope));
        Assert.Equal(0, r.KeysFromControlResources);
    }

    // ── 59：Uno.Sdk 單一專案是 WinUI ───────────────────────────────────────

    [Fact]
    public void An_Uno_Sdk_project_is_WinUI_so_the_Uno_theme_lists_apply()
    {
        // Uno Chefs：<Project Sdk="Uno.Sdk">，沒有 UseWinUI；樣式檔 BasedOn Uno Material 的 FilledButtonStyle
        var r = Analyze(new()
        {
            ["Chefs/Chefs.csproj"] = "<Project Sdk=\"Uno.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><UnoFeatures>Material;Toolkit</UnoFeatures></PropertyGroup></Project>",
            ["Chefs/App.xaml"] = $"<Application {Wpf} x:Class=\"Chefs.App\"><Application.Resources><ResourceDictionary><ResourceDictionary.MergedDictionaries>" +
                                 "<XamlControlsResources xmlns=\"using:Microsoft.UI.Xaml.Controls\"/><MaterialTheme xmlns=\"using:Uno.Material\"/>" +
                                 "<ResourceDictionary Source=\"ms-appx:///Styles/Button.xaml\"/></ResourceDictionary.MergedDictionaries></ResourceDictionary></Application.Resources></Application>",
            ["Chefs/Styles/Button.xaml"] = $"<ResourceDictionary {Wpf}><Style x:Key=\"ChefsButton\" TargetType=\"Button\" BasedOn=\"{{StaticResource FilledButtonStyle}}\"/></ResourceDictionary>",
            ["Chefs/MainPage.xaml"] = $"<Page {Wpf}><Button Style=\"{{StaticResource ChefsButton}}\"/></Page>",
        });

        Assert.DoesNotContain(r.Findings, f => f.Key == "FilledButtonStyle");
        Assert.Contains("uno-material", r.ThemePacksInUse);
    }

    // ── 63：Uno 主題物件用屬性帶進的覆寫字典 ───────────────────────────────

    private const string UnoProj = "<Project Sdk=\"Uno.Sdk\"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>";

    [Fact]
    public void Uno_theme_override_sources_are_merged()
    {
        // Uno Chefs：<MaterialToolkitTheme ColorOverrideSource="…" FontOverrideSource="…"/>
        var r = Analyze(new()
        {
            ["Chefs/Chefs.csproj"] = UnoProj,
            ["Chefs/App.xaml"] = $"<Application {Wpf} x:Class=\"Chefs.App\"><Application.Resources><ResourceDictionary><ResourceDictionary.MergedDictionaries>" +
                                 "<MaterialToolkitTheme xmlns=\"using:Uno.Toolkit.UI.Material\" ColorOverrideSource=\"ms-appx:///Styles/Colors.xaml\" FontOverrideSource=\"ms-appx:///Styles/Fonts.xaml\"/>" +
                                 "</ResourceDictionary.MergedDictionaries></ResourceDictionary></Application.Resources></Application>",
            ["Chefs/Styles/Colors.xaml"] = $"<ResourceDictionary {Wpf}><x:String x:Key=\"Logo\">a.png</x:String></ResourceDictionary>",
            ["Chefs/Styles/Fonts.xaml"] = $"<ResourceDictionary {Wpf}><x:String x:Key=\"BodyFont\">Inter</x:String></ResourceDictionary>",
            ["Chefs/MainPage.xaml"] = $"<Page {Wpf}><StackPanel><Image Source=\"{{ThemeResource Logo}}\"/><TextBlock FontFamily=\"{{StaticResource BodyFont}}\"/></StackPanel></Page>",
        });

        Assert.Empty(Keys(r, FindingKind.OrphanDictionary));
        Assert.Empty(Keys(r, FindingKind.External));
        Assert.Empty(Keys(r, FindingKind.OutOfScope));
    }

    [Fact]
    public void Other_attributes_of_a_theme_object_are_not_merge_sources()
    {
        // <XamlControlsResources ControlsResourcesVersion="Version2"/>：不是路徑，不能當成一本看不到的字典
        var r = Analyze(new()
        {
            ["Chefs/Chefs.csproj"] = UnoProj,
            ["Chefs/App.xaml"] = $"<Application {Wpf} x:Class=\"Chefs.App\"><Application.Resources><ResourceDictionary><ResourceDictionary.MergedDictionaries>" +
                                 "<XamlControlsResources xmlns=\"using:Microsoft.UI.Xaml.Controls\" ControlsResourcesVersion=\"Version2\"/>" +
                                 "</ResourceDictionary.MergedDictionaries><x:String x:Key=\"Own\">a</x:String></ResourceDictionary></Application.Resources></Application>",
            ["Chefs/MainPage.xaml"] = $"<Page {Wpf}><TextBlock Text=\"{{StaticResource Own}}\" Tag=\"{{StaticResource Typo}}\"/></Page>",
        });

        Assert.Equal(1, r.MergeEdges);
        Assert.DoesNotContain(r.Findings, f => f.ExternalSource == "Version2");
    }

    [Fact]
    public void An_external_finding_for_a_key_defined_elsewhere_does_not_claim_it_is_undefined()
    {
        var r = Analyze(new()
        {
            ["Chefs/Chefs.csproj"] = UnoProj,
            ["Chefs/App.xaml"] = $"<Application {Wpf} x:Class=\"Chefs.App\"><Application.Resources><ResourceDictionary><ResourceDictionary.MergedDictionaries>" +
                                 "<ResourceDictionary Source=\"ms-appx:///Missing/Lib.xaml\"/></ResourceDictionary.MergedDictionaries></ResourceDictionary></Application.Resources></Application>",
            ["Chefs/Styles/Loose.xaml"] = $"<ResourceDictionary {Wpf}><x:String x:Key=\"Logo\">a.png</x:String></ResourceDictionary>",
            ["Chefs/MainPage.xaml"] = $"<Page {Wpf}><Image Source=\"{{ThemeResource Logo}}\"/></Page>",
        });

        var f = Assert.Single(r.Findings, f => f.Kind == FindingKind.External);
        Assert.Contains("not in the scope of this reference", f.Message);
    }

    // ── 60：markup 鍵的型別連命名空間一起比 ─────────────────────────────────

    [Fact]
    public void A_library_key_override_is_not_unused_when_the_tree_has_a_same_named_type_in_another_namespace()
    {
        // AvalonDock：MLibTest 覆寫 ColorPickerLib.Themes.ResourceKeys 的鍵（套件），樹裡另有 AvalonDock 自己的 ResourceKeys
        var r = Analyze(new()
        {
            ["App/App.csproj"] = AppProj,
            ["App/App.xaml"] = $"<Application {Wpf}><Application.Resources><ResourceDictionary Source=\"Brushes.xaml\"/></Application.Resources></Application>",
            ["App/Brushes.xaml"] = $"<ResourceDictionary {Wpf} xmlns:cp=\"clr-namespace:ColorPickerLib.Themes;assembly=ColorPickerLib\">" +
                                   "<SolidColorBrush x:Key=\"{ComponentResourceKey TypeInTargetAssembly={x:Type cp:ResourceKeys}, ResourceId=PopUpOpenBorderBrushKey}\" Color=\"Red\"/></ResourceDictionary>",
            ["Dock/Dock.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><UseWPF>true</UseWPF></PropertyGroup></Project>",
            ["Dock/ResourceKeys.cs"] = "namespace AvalonDock.Themes { public static class ResourceKeys { } }",
        });

        Assert.Empty(Keys(r, FindingKind.Unused));
        Assert.Equal(1, r.LibraryKeyOverrides);
    }

    [Fact]
    public void A_key_whose_type_is_declared_in_the_tree_with_that_namespace_is_still_checked()
    {
        var r = Analyze(new()
        {
            ["App/App.csproj"] = AppProj,
            ["App/App.xaml"] = $"<Application {Wpf}><Application.Resources><ResourceDictionary Source=\"Brushes.xaml\"/></Application.Resources></Application>",
            ["App/Brushes.xaml"] = $"<ResourceDictionary {Wpf} xmlns:k=\"clr-namespace:Demo.Themes\">" +
                                   "<SolidColorBrush x:Key=\"{ComponentResourceKey TypeInTargetAssembly={x:Type k:ResourceKeys}, ResourceId=Accent}\" Color=\"Red\"/></ResourceDictionary>",
            ["App/ResourceKeys.cs"] = "namespace Demo.Themes;\npublic static class ResourceKeys { }",
        });

        Assert.Single(Keys(r, FindingKind.Unused));
        Assert.Equal(0, r.LibraryKeyOverrides);
    }

    // ── 61：Caliburn.Micro 的啟動器 ─────────────────────────────────────────

    [Fact]
    public void A_Caliburn_bootstrapper_in_App_xaml_is_not_unused()
    {
        var r = Analyze(new()
        {
            ["App/App.csproj"] = AppProj,
            ["App/App.xaml"] = $"<Application {Wpf} xmlns:local=\"clr-namespace:Demo\" x:Class=\"Demo.App\"><Application.Resources><ResourceDictionary><ResourceDictionary.MergedDictionaries>" +
                               "<ResourceDictionary><local:Bootstrapper x:Key=\"Bootstrapper\"/></ResourceDictionary></ResourceDictionary.MergedDictionaries>" +
                               "<SolidColorBrush x:Key=\"Unused\" Color=\"Red\"/></ResourceDictionary></Application.Resources></Application>",
        });

        Assert.Equal(["Unused"], Keys(r, FindingKind.Unused));
        Assert.Equal(1, r.StartupResources);
    }

    [Fact]
    public void A_bootstrapper_outside_App_xaml_is_still_checked()
    {
        var r = Analyze(new()
        {
            ["App/App.csproj"] = AppProj,
            ["App/App.xaml"] = $"<Application {Wpf} x:Class=\"Demo.App\"><Application.Resources><SolidColorBrush x:Key=\"Used\" Color=\"Red\"/></Application.Resources></Application>",
            ["App/Main.xaml"] = $"<Window {Wpf} xmlns:local=\"clr-namespace:Demo\" Background=\"{{StaticResource Used}}\"><Window.Resources><local:Bootstrapper x:Key=\"Bootstrapper\"/></Window.Resources></Window>",
        });

        Assert.Equal(["Bootstrapper"], Keys(r, FindingKind.Unused));
        Assert.Equal(0, r.StartupResources);
    }

    // ── 62：死副本的「在用的那份」先找同一個專案 ─────────────────────────────

    [Fact]
    public void A_dead_local_copy_points_at_the_copy_in_use_in_the_same_project()
    {
        var r = Analyze(new()
        {
            // 另一個專案排在前面（檔名順序），舊版會指到它
            ["A/A.csproj"] = AppProj,
            ["A/Main.xaml"] = $"<Window {Wpf}><Window.Resources><BooleanToVisibilityConverter x:Key=\"B2V\"/></Window.Resources><Grid Visibility=\"{{Binding X, Converter={{StaticResource B2V}}}}\"/></Window>",
            ["Z/Z.csproj"] = AppProj,
            ["Z/Main.xaml"] = $"<Window {Wpf}><Window.Resources><BooleanToVisibilityConverter x:Key=\"B2V\"/></Window.Resources><Grid/></Window>",
            ["Z/Other.xaml"] = $"<Window {Wpf}><Window.Resources><BooleanToVisibilityConverter x:Key=\"B2V\"/></Window.Resources><Grid Visibility=\"{{Binding X, Converter={{StaticResource B2V}}}}\"/></Window>",
        });

        var f = Assert.Single(r.Findings, f => f.Kind == FindingKind.Unused);
        Assert.Equal("Z/Main.xaml", f.Location.File);
        Assert.Equal("Z/Other.xaml", f.Related!.Value.File);
    }

    // ── 55：out-of-scope 指到同一個專案的定義 ───────────────────────────────

    [Fact]
    public void Out_of_scope_points_at_the_definition_in_the_same_project()
    {
        // maui-samples：沒合併的 Styles.xaml 引用自己專案 Colors.xaml 的鍵，舊版指到另一個範例的 Colors.xaml
        var r = Analyze(new()
        {
            ["A/A.csproj"] = MauiProj,
            ["A/Colors.xaml"] = $"<ResourceDictionary {Maui}><Color x:Key=\"Primary\">Red</Color></ResourceDictionary>",
            ["A/Page.xaml"] = $"<ContentPage {Maui}><ContentPage.Resources><ResourceDictionary Source=\"Colors.xaml\"/></ContentPage.Resources><Label TextColor=\"{{StaticResource Primary}}\"/></ContentPage>",
            ["B/B.csproj"] = MauiProj,
            ["B/Colors.xaml"] = $"<ResourceDictionary {Maui}><Color x:Key=\"Primary\">Blue</Color></ResourceDictionary>",
            ["B/Styles.xaml"] = $"<ResourceDictionary {Maui}><Style x:Key=\"S\" TargetType=\"Label\"><Setter Property=\"TextColor\" Value=\"{{StaticResource Primary}}\"/></Style></ResourceDictionary>",
        });

        var f = Assert.Single(r.Findings, f => f.Kind == FindingKind.OutOfScope);
        Assert.StartsWith("B/", f.Related!.Value.File);
    }

    // ── 56：兩個專案同一個組件名 ─────────────────────────────────────────

    [Fact]
    public void A_shared_assembly_name_resolves_to_the_referencing_project_first()
    {
        // WPF-Samples：入門教學與完整範例都叫 ExpenseIt9
        const string proj = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType><AssemblyName>ExpenseIt9</AssemblyName></PropertyGroup></Project>";
        var r = Analyze(new()
        {
            ["Intro/Intro.csproj"] = proj,
            ["Intro/App.xaml"] = $"<Application {Wpf}><Application.Resources><ResourceDictionary><ResourceDictionary.MergedDictionaries><ResourceDictionary Source=\"/ExpenseIt9;component/Styles.xaml\"/></ResourceDictionary.MergedDictionaries></ResourceDictionary></Application.Resources></Application>",
            ["Intro/Styles.xaml"] = $"<ResourceDictionary {Wpf}><Style x:Key=\"Header\" TargetType=\"Label\"/></ResourceDictionary>",
            ["Intro/Home.xaml"] = $"<Page {Wpf}><Label Style=\"{{StaticResource Header}}\"/></Page>",
            ["Zdemo/Zdemo.csproj"] = proj,
            ["Zdemo/App.xaml"] = $"<Application {Wpf}><Application.Resources><ResourceDictionary><ResourceDictionary.MergedDictionaries><ResourceDictionary Source=\"/ExpenseIt9;component/Styles.xaml\"/></ResourceDictionary.MergedDictionaries></ResourceDictionary></Application.Resources></Application>",
            ["Zdemo/Styles.xaml"] = $"<ResourceDictionary {Wpf}><Style x:Key=\"Other\" TargetType=\"Label\"/></ResourceDictionary>",
            ["Zdemo/Home.xaml"] = $"<Page {Wpf}><Label Style=\"{{StaticResource Other}}\"/></Page>",
        });

        Assert.Empty(Keys(r, FindingKind.OutOfScope));
        Assert.Empty(Keys(r, FindingKind.DanglingStatic));
        Assert.Empty(Keys(r, FindingKind.OrphanDictionary));
    }
}
