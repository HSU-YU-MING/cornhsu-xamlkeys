using XamlKeys.Core;

namespace XamlKeys.Tests;

/// <summary>
/// 第十三輪語料（2026-11-15）：SourceGit、egvijayanand 的 MAUI 範例、Material.Avalonia、StabilityMatrix、ScottPlot、
/// NetSparkle、Avalonia.Xaml.Behaviors。歸因在 docs/實查歸因-2026-11-14.md 的「第十三輪語料」。
/// </summary>
public class ThirteenthRoundTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("xamlkeys-round13");
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

    private const string Av = "xmlns=\"https://github.com/avaloniaui\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";
    private const string Maui = "xmlns=\"http://schemas.microsoft.com/dotnet/2021/maui\" xmlns:x=\"http://schemas.microsoft.com/winfx/2009/xaml\"";
    private const string Exe = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup></Project>";

    private static List<string> Keys(AnalysisResult r, FindingKind k) =>
        r.Findings.Where(f => f.Kind == k).Select(f => f.Key).Distinct().Order(StringComparer.Ordinal).ToList();

    // ── 86：有 x:Key 的 ResourceInclude 是執行期挑一本的子字典 ──────────────

    private static Dictionary<string, string> SourceGitLike(string page) => new()
    {
        // SourceGit：語系檔用 <ResourceInclude x:Key="en_US" Source="…"/> 放在 Application.Resources，App.SetLocale 挑一本合併
        ["src/App.csproj"] = Exe,
        ["src/App.axaml"] = $"<Application {Av} x:Class=\"Git.App\"><Application.Resources><ResourceDictionary>" +
                            "<ResourceInclude x:Key=\"en_US\" Source=\"/Resources/Locales/en_US.axaml\"/>" +
                            "<ResourceInclude x:Key=\"zh_TW\" Source=\"/Resources/Locales/zh_TW.axaml\"/>" +
                            "</ResourceDictionary></Application.Resources></Application>",
        ["src/Resources/Locales/en_US.axaml"] = $"<ResourceDictionary {Av}><x:String x:Key=\"Text.Sure\">OK</x:String></ResourceDictionary>",
        ["src/Resources/Locales/zh_TW.axaml"] = $"<ResourceDictionary {Av}><x:String x:Key=\"Text.Sure\">確定</x:String></ResourceDictionary>",
        ["src/Views/Confirm.axaml"] = page,
    };

    [Fact]
    public void A_keyed_ResourceInclude_is_a_runtime_selected_alternative()
    {
        var r = Analyze(SourceGitLike($"<UserControl {Av}><Button Content=\"{{DynamicResource Text.Sure}}\"/></UserControl>"));

        Assert.Empty(r.Findings);
        Assert.Equal(100.0, r.Coverage);
        Assert.Equal(2, r.KeyedDictionaries);
    }

    [Fact]
    public void A_key_missing_from_every_keyed_include_is_dangling_not_external()
    {
        // 子字典的內容看得到：兩本都沒有就是真的沒有，不是「擋在看不到的東西後面」
        var r = Analyze(SourceGitLike($"<UserControl {Av}><Button Content=\"{{DynamicResource Text.Cancel}}\"/></UserControl>"));

        Assert.Equal(["Text.Cancel"], Keys(r, FindingKind.DanglingDynamic));
        Assert.DoesNotContain(r.Findings, f => f.Kind == FindingKind.External);
    }

    // ── 87：Application 類別把 new X() 加進 Application.Resources ──────────

    private static Dictionary<string, string> MauiAppCs(string appCode) => new()
    {
        // egvijayanand 的 MauiAppCS：App.cs 是 C# 寫的，依序 Resources.Add(new Colors())、Resources.Add(new Styles())
        ["MauiAppCS/MauiAppCS.csproj"] = Exe,
        ["MauiAppCS/App.cs"] = appCode,
        ["MauiAppCS/Resources/Colors.xaml"] = $"<ResourceDictionary {Maui} x:Class=\"MauiAppCS.Colors\"><Color x:Key=\"Primary\">#512BD4</Color></ResourceDictionary>",
        ["MauiAppCS/Resources/Styles.xaml"] = $"<ResourceDictionary {Maui} x:Class=\"MauiAppCS.Styles\"><Style TargetType=\"Button\">" +
                                              "<Setter Property=\"BackgroundColor\" Value=\"{StaticResource Primary}\"/></Style></ResourceDictionary>",
    };

    [Fact]
    public void Dictionaries_the_Application_class_adds_are_in_Application_scope()
    {
        var r = Analyze(MauiAppCs("namespace MauiAppCS { public partial class App : Application { public App() {" +
                                  " Resources.Add(new Colors()); Resources.Add(new Styles()); } } }"));

        Assert.DoesNotContain(r.Findings, f => f.Kind is FindingKind.OutOfScope or FindingKind.OrphanDictionary or FindingKind.DanglingStatic);
        Assert.Equal(2, r.RuntimeLoadedDictionaries);
    }

    [Fact]
    public void MergedDictionaries_Add_in_the_Application_class_counts_too()
    {
        // egvijayanand 的 C#-Samples（App.designer.cs）：Resources.MergedDictionaries.Add(new Styles.Colors())
        var r = Analyze(MauiAppCs("namespace MauiAppCS { public partial class App : Microsoft.Maui.Controls.Application { public App() {" +
                                  " Resources.MergedDictionaries.Add(new Resources.Colors()); Resources.MergedDictionaries.Add(new Styles()); } } }"));

        Assert.DoesNotContain(r.Findings, f => f.Kind is FindingKind.OutOfScope or FindingKind.DanglingStatic);
        Assert.Equal(2, r.RuntimeLoadedDictionaries);
    }

    [Fact]
    public void A_page_adding_a_dictionary_to_its_own_Resources_is_not_Application_scope()
    {
        // 頁面的 Resources.Add(new Colors()) 只進那一頁 —— 不知道是哪一頁，照規則 66 不放進任何作用域
        var r = Analyze(MauiAppCs("namespace MauiAppCS { public partial class MainPage : ContentPage { public MainPage() {" +
                                  " Resources.Add(new Colors()); Resources.Add(new Styles()); } } }"));

        Assert.Equal(0, r.RuntimeLoadedDictionaries);
        Assert.Contains(r.Findings, f => f.Key == "Primary" && f.Kind is FindingKind.OutOfScope or FindingKind.DanglingStatic);
    }

    [Fact]
    public void Another_projects_Application_does_not_pull_in_a_same_named_dictionary()
    {
        // egvijayanand 一個 repo 裡幾十個 App 都有自己的 Colors／Styles —— 只算同一個專案的
        var files = MauiAppCs("namespace MauiAppCS { public partial class App : Application { public App() {" +
                              " Resources.Add(new Colors()); Resources.Add(new Styles()); } } }");
        files["Other/Other.csproj"] = Exe;
        files["Other/Styles.xaml"] = $"<ResourceDictionary {Maui} x:Class=\"Other.Styles\"><Style TargetType=\"Label\">" +
                                     "<Setter Property=\"TextColor\" Value=\"{StaticResource Accent}\"/></Style></ResourceDictionary>";
        var r = Analyze(files);

        Assert.Equal(2, r.RuntimeLoadedDictionaries);
        Assert.Contains(r.Findings, f => f.Key == "Accent");
    }

    // ── 86 的後果：選用的子字典蓋掉基底，不是「基底永遠贏不了」 ────────────

    private static Dictionary<string, string> FluentLike(string compact) => new()
    {
        // Avalonia 的 FluentTheme：基底 MergeResourceInclude 進來，<ResourceInclude x:Key="CompactStyles"> 要 DensityStyle=Compact 才合併
        ["Themes/Themes.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Library</OutputType></PropertyGroup></Project>",
        ["Themes/FluentTheme.xaml"] = $"<Styles {Av} x:Class=\"Themes.FluentTheme\"><Styles.Resources><ResourceDictionary>" +
                                      "<ResourceDictionary.MergedDictionaries><MergeResourceInclude Source=\"/Accents/BaseResources.xaml\"/></ResourceDictionary.MergedDictionaries>" +
                                      "<ResourceInclude x:Key=\"CompactStyles\" Source=\"/DensityStyles/Compact.xaml\"/>" +
                                      "</ResourceDictionary></Styles.Resources>" +
                                      "<Style Selector=\"Button\"><Setter Property=\"Padding\" Value=\"{DynamicResource ButtonPadding}\"/></Style></Styles>",
        ["Themes/Accents/BaseResources.xaml"] = $"<ResourceDictionary {Av}><Thickness x:Key=\"ButtonPadding\">8,5</Thickness></ResourceDictionary>",
        ["Themes/DensityStyles/Compact.xaml"] = compact,
    };

    [Fact]
    public void A_base_value_overridden_only_by_an_optional_keyed_include_is_not_shadowed()
    {
        var r = Analyze(FluentLike($"<ResourceDictionary {Av}><Thickness x:Key=\"ButtonPadding\">6,3</Thickness></ResourceDictionary>"));

        Assert.DoesNotContain(r.Findings, f => f.Kind is FindingKind.Shadowed or FindingKind.ShadowedIdentical);
        Assert.Equal(1, r.LayeredOverrides);
    }

    [Fact]
    public void Shadowing_inside_the_optional_dictionary_is_still_reported()
    {
        // 選用的那本自己合併一本又重定義：那一本裡的值不管挑不挑都贏不了
        var files = FluentLike($"<ResourceDictionary {Av}><ResourceDictionary.MergedDictionaries><ResourceInclude Source=\"/DensityStyles/CompactBase.xaml\"/>" +
                               "</ResourceDictionary.MergedDictionaries><Thickness x:Key=\"ButtonPadding\">6,3</Thickness></ResourceDictionary>");
        files["Themes/DensityStyles/CompactBase.xaml"] = $"<ResourceDictionary {Av}><Thickness x:Key=\"ButtonPadding\">4,2</Thickness></ResourceDictionary>";
        var r = Analyze(files);

        Assert.Contains(r.Findings, f => f.Kind == FindingKind.Shadowed && f.Location.File.EndsWith("CompactBase.xaml"));
    }
}
