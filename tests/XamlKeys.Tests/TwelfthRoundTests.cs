using XamlKeys.Core;

namespace XamlKeys.Tests;

/// <summary>
/// 第十二輪語料（2026-11-15）：MAUIsland、Telepathy、MauiPlanets、Dots、DevWinUI、MicaForEveryone、SidebarDiagnostics。
/// 歸因在 docs/實查歸因-2026-11-14.md 的「第十二輪語料」。
/// </summary>
public class TwelfthRoundTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("xamlkeys-round12");
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

    private static List<string> Keys(AnalysisResult r, FindingKind k) =>
        r.Findings.Where(f => f.Kind == k).Select(f => f.Key).Distinct().Order(StringComparer.Ordinal).ToList();

    private static Dictionary<string, string> WinUiLibrary(string props, string libProject) => new()
    {
        ["Directory.Build.props"] = props,
        ["dev/Lib/Lib.csproj"] = libProject,
        ["dev/Lib/Themes/Generic.xaml"] = $"<ResourceDictionary {Wpf}><Style x:Key=\"CardStyle\" TargetType=\"Border\">" +
                                          "<Setter Property=\"Background\" Value=\"{ThemeResource CardBackgroundFillColorDefaultBrush}\"/></Style></ResourceDictionary>",
    };

    private const string Lib = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Library</OutputType></PropertyGroup></Project>";
    private const string WinUiProps = "<Project><PropertyGroup><UseWinUI Condition=\"'$(MSBuildProjectName)' != 'Native'\">true</UseWinUI></PropertyGroup></Project>";

    // ── 84：最近的 Directory.Build.props 宣告 WinUI ───────────────────────

    [Fact]
    public void A_project_inherits_WinUI_from_the_nearest_Directory_Build_props()
    {
        // DevWinUI：<UseWinUI> 寫在 repo 根的 Directory.Build.props
        var r = Analyze(WinUiLibrary(WinUiProps, Lib));

        Assert.DoesNotContain(r.Findings, f => f.Kind is FindingKind.DanglingDynamic or FindingKind.DanglingStatic);
        Assert.True(r.FrameworkProvided >= 1);
    }

    [Fact]
    public void A_project_that_says_UseWPF_is_not_turned_into_WinUI_by_the_props()
    {
        var r = Analyze(WinUiLibrary(WinUiProps,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Library</OutputType><UseWPF>true</UseWPF></PropertyGroup></Project>"));

        Assert.Equal(["CardBackgroundFillColorDefaultBrush"], Keys(r, FindingKind.DanglingDynamic));
    }

    [Fact]
    public void Only_the_nearest_Directory_Build_props_counts()
    {
        // MSBuild 只自動匯入最近的那一個：dev/ 底下的 props 沒有 WinUI，根目錄的有 —— 用 dev/ 的
        var files = WinUiLibrary(WinUiProps, Lib);
        files["dev/Directory.Build.props"] = "<Project><PropertyGroup><LangVersion>latest</LangVersion></PropertyGroup></Project>";
        var r = Analyze(files);

        Assert.Equal(["CardBackgroundFillColorDefaultBrush"], Keys(r, FindingKind.DanglingDynamic));
    }

    // ── 85：AssemblyName 是展不開的屬性時退回專案檔名 ──────────────────────

    [Fact]
    public void An_unexpandable_AssemblyName_falls_back_to_the_project_file_name()
    {
        // DevWinUI：<AssemblyName>$(BaseNamespace)</AssemblyName>，Gallery 合併 ms-appx:///DevWinUI/Themes/Generic.xaml
        var r = Analyze(new()
        {
            ["Directory.Build.props"] = "<Project><PropertyGroup><UseWinUI>true</UseWinUI><BaseNamespace>DevWinUI</BaseNamespace></PropertyGroup></Project>",
            ["dev/DevWinUI/DevWinUI.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><AssemblyName>$(BaseNamespace)</AssemblyName></PropertyGroup></Project>",
            ["dev/DevWinUI/Themes/Generic.xaml"] = $"<ResourceDictionary {Wpf}><x:Double x:Key=\"SettingActionControlMinWidth\">240</x:Double></ResourceDictionary>",
            ["dev/Gallery/Gallery.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup></Project>",
            ["dev/Gallery/App.xaml"] = $"<Application {Wpf}><Application.Resources><ResourceDictionary><ResourceDictionary.MergedDictionaries>" +
                                       "<XamlControlsResources xmlns=\"using:Microsoft.UI.Xaml.Controls\"/><ResourceDictionary Source=\"ms-appx:///DevWinUI/Themes/Generic.xaml\"/>" +
                                       "</ResourceDictionary.MergedDictionaries></ResourceDictionary></Application.Resources></Application>",
            ["dev/Gallery/MainPage.xaml"] = $"<Page {Wpf}><Grid MinWidth=\"{{StaticResource SettingActionControlMinWidth}}\"/></Page>",
        });

        Assert.DoesNotContain(r.Findings, f => f.Kind is FindingKind.External or FindingKind.DanglingStatic);
        Assert.Equal(100.0, r.Coverage);
    }
}
