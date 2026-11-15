using XamlKeys.Core;

namespace XamlKeys.Tests;

/// <summary>
/// 第十一輪語料（2026-11-15）：Auto Dark Mode、FileConverter、ModernFlyouts、RX-Explorer、Stylophone、NAudio、
/// Windows Community Toolkit、Microsoft Calculator、Pixeval、SyncTrayzor。歸因在 docs/實查歸因-2026-11-14.md 的「第十一輪語料」。
/// </summary>
public class EleventhRoundTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("xamlkeys-round11");
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
    private const string Uwp = "<Project><PropertyGroup><OutputType>AppContainerExe</OutputType><TargetPlatformIdentifier>UAP</TargetPlatformIdentifier></PropertyGroup></Project>";
    private const string AppProj = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup></Project>";
    private const string Cond =
        "xmlns:c7=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation?IsApiContractPresent(Windows.Foundation.UniversalApiContract,7)\" " +
        "xmlns:c7Not=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation?IsApiContractNotPresent(Windows.Foundation.UniversalApiContract,7)\" " +
        "xmlns:c8=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation?IsApiContractPresent(Windows.Foundation.UniversalApiContract,8)\" " +
        "xmlns:c8Not=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation?IsApiContractNotPresent(Windows.Foundation.UniversalApiContract,8)\"";

    private static List<string> Keys(AnalysisResult r, FindingKind k) =>
        r.Findings.Where(f => f.Kind == k).Select(f => f.Key).Distinct().Order(StringComparer.Ordinal).ToList();

    // ── 82：條件式 XAML 的互補定義 ───────────────────────────────────────

    [Fact]
    public void Complementary_conditional_definitions_are_not_duplicates()
    {
        // Microsoft Calculator：<contract7NotPresent:Style x:Key="K"/> 與 <contract7Present:Style x:Key="K"/>
        var r = Analyze(new()
        {
            ["App/App.csproj"] = Uwp,
            ["App/App.xaml"] = $"<Application {Wpf} {Cond}><Application.Resources>" +
                               "<c7Not:Style x:Key=\"ResultStyle\" TargetType=\"TextBlock\"/>" +
                               "<c7:Style x:Key=\"ResultStyle\" TargetType=\"TextBlock\"/>" +
                               "</Application.Resources></Application>",
            ["App/MainPage.xaml"] = $"<Page {Wpf}><TextBlock Style=\"{{StaticResource ResultStyle}}\"/></Page>",
        });

        Assert.Empty(Keys(r, FindingKind.DuplicateKey));
        Assert.Empty(Keys(r, FindingKind.Shadowed));
        Assert.Equal(1, r.ConditionalAlternatives);
    }

    [Theory]
    [InlineData("c7:Style", "c7:Style")]   // 同一個條件
    [InlineData("c7:Style", "c8:Style")]   // 不同版本，不互補
    [InlineData("Style", "c7Not:Style")]   // 一個沒條件
    [InlineData("c7:Style", "c8Not:Style")] // 一正一反，但不是同一個 API 版本
    public void Definitions_that_are_not_complementary_are_still_duplicates(string first, string second)
    {
        var r = Analyze(new()
        {
            ["App/App.csproj"] = Uwp,
            ["App/App.xaml"] = $"<Application {Wpf} {Cond}><Application.Resources>" +
                               $"<{first} x:Key=\"ResultStyle\" TargetType=\"TextBlock\"/>" +
                               $"<{second} x:Key=\"ResultStyle\" TargetType=\"TextBlock\"/>" +
                               "</Application.Resources></Application>",
            ["App/MainPage.xaml"] = $"<Page {Wpf}><TextBlock Style=\"{{StaticResource ResultStyle}}\"/></Page>",
        });

        Assert.Equal(["ResultStyle"], Keys(r, FindingKind.DuplicateKey));
        Assert.Equal(0, r.ConditionalAlternatives);
    }

    // ── 83：ResourceDictionary 子類別當 X.Resources 的根 ─────────────────

    [Fact]
    public void A_ResourceDictionary_subclass_as_the_resources_root_has_its_merged_dictionaries_read()
    {
        // Stylophone：<Application.Resources><controls:XamlControlsResources><controls:XamlControlsResources.MergedDictionaries>
        var r = Analyze(new()
        {
            ["App/App.csproj"] = Uwp,
            ["App/App.xaml"] = $"<Application {Wpf} xmlns:controls=\"using:Microsoft.UI.Xaml.Controls\"><Application.Resources>" +
                               "<controls:XamlControlsResources><controls:XamlControlsResources.MergedDictionaries>" +
                               "<ResourceDictionary Source=\"/Styles/Thickness.xaml\"/>" +
                               "</controls:XamlControlsResources.MergedDictionaries></controls:XamlControlsResources>" +
                               "</Application.Resources></Application>",
            ["App/Styles/Thickness.xaml"] = $"<ResourceDictionary {Wpf}><Thickness x:Key=\"PageMargin\">12</Thickness></ResourceDictionary>",
            ["App/MainPage.xaml"] = $"<Page {Wpf}><Grid Margin=\"{{StaticResource PageMargin}}\" Background=\"{{ThemeResource ApplicationPageBackgroundThemeBrush}}\"/></Page>",
        });

        Assert.Empty(r.Findings);
        Assert.Equal(100.0, r.Coverage);   // XamlControlsResources 是 WinUI 一定有的已知主題
    }

    [Fact]
    public void An_unknown_ResourceDictionary_subclass_root_is_still_counted_as_unreadable()
    {
        // SyncTrayzor：Stylet 的 <s:ApplicationLoader> —— 它的 MergedDictionaries 讀得到，但它自己帶什麼看不到
        var r = Analyze(new()
        {
            ["App/App.csproj"] = AppProj,
            ["App/App.xaml"] = $"<Application {Wpf} xmlns:s=\"https://github.com/canton7/Stylet\"><Application.Resources>" +
                               "<s:ApplicationLoader><s:ApplicationLoader.MergedDictionaries>" +
                               "<ResourceDictionary Source=\"Xaml/Resources.xaml\"/>" +
                               "</s:ApplicationLoader.MergedDictionaries></s:ApplicationLoader>" +
                               "</Application.Resources></Application>",
            ["App/Xaml/Resources.xaml"] = $"<ResourceDictionary {Wpf}><Style x:Key=\"Header\" TargetType=\"TextBlock\"/></ResourceDictionary>",
            ["App/Main.xaml"] = $"<Window {Wpf}><TextBlock Style=\"{{StaticResource Header}}\"/></Window>",
        });

        Assert.Empty(Keys(r, FindingKind.OutOfScope));
        Assert.Empty(Keys(r, FindingKind.OrphanDictionary));
        Assert.Contains(r.ExternalSources, s => s.Contains("ApplicationLoader"));
    }
}
