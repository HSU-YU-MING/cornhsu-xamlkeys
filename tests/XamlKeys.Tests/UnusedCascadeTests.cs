using XamlKeys.Core;

namespace XamlKeys.Tests;

/// <summary>
/// 兩件 2026-11-14 收尾時補的 unused 判定：
///
/// 1. 連帶的「沒人用」（Analyzer.EmitUnusedOnlyViaUnused）——只被沒人用的資源引用的鍵也沒人用。
///    QuillNest 實測：AccentColor 只被 AccentBrush 引用，AccentBrush 本身沒人用；舊版要清兩輪才看得到。
/// 2. 重新定義框架內建鍵不算沒人用（WinUI 的輕量樣式覆寫）。
///    實測 Files 的 80 筆 unused 有 33 筆、WinUI Gallery 42 筆裡 16 筆是這種假警報。
/// </summary>
public class UnusedCascadeTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("xamlkeys-cascade");
    public void Dispose() => _dir.Delete(true);

    private const string Ns = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
                              "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";

    private void Put(string rel, string content)
    {
        var path = Path.Combine(_dir.FullName, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private AnalysisResult Run(string appResources, string window = "<Grid />",
                               string csproj = "<OutputType>WinExe</OutputType>")
    {
        Put("App.csproj", $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>{csproj}</PropertyGroup></Project>");
        Put("App.xaml", $"<Application {Ns}><Application.Resources>{appResources}</Application.Resources></Application>");
        Put("Main.xaml", $"<Window {Ns}>{window}</Window>");
        return Analyzer.Run(_dir.FullName, new Config());
    }

    private static List<string> Unused(AnalysisResult r) =>
        r.Findings.Where(f => f.Kind == FindingKind.Unused).Select(f => f.Key).Order(StringComparer.Ordinal).ToList();

    // ── 連帶 ───────────────────────────────────────────────────────────────

    [Fact]
    public void A_colour_used_only_by_an_unused_brush_is_unused_too_and_the_message_names_the_brush()
    {
        var r = Run("""
            <Color x:Key="AccentColor">#FF4081</Color>
            <SolidColorBrush x:Key="AccentBrush" Color="{StaticResource AccentColor}" />
            """);

        Assert.Equal(["AccentBrush", "AccentColor"], Unused(r));
        var f = r.Findings.Single(x => x.Key == "AccentColor");
        Assert.Contains("only referenced by 'AccentBrush'", f.Message);
        Assert.Equal("App.xaml", f.Related?.File);
        Assert.Equal(1, r.UnusedOnlyViaUnused);
    }

    [Fact]
    public void A_dead_alias_no_longer_keeps_its_target_alive()
    {
        // 這原本是 README「已知盲區」裡的一條：<StaticResource x:Key="A" ResourceKey="B"/>
        var r = Run("""
            <SolidColorBrush x:Key="B" Color="Red" />
            <StaticResource x:Key="A" ResourceKey="B" />
            """);

        Assert.Equal(["A", "B"], Unused(r));
    }

    [Fact]
    public void A_chain_is_followed_all_the_way_down()
    {
        var r = Run("""
            <Color x:Key="Base">#111111</Color>
            <SolidColorBrush x:Key="Fill" Color="{StaticResource Base}" />
            <Style x:Key="Card" TargetType="Border"><Setter Property="Background" Value="{StaticResource Fill}" /></Style>
            """);

        Assert.Equal(["Base", "Card", "Fill"], Unused(r));
        Assert.Equal(2, r.UnusedOnlyViaUnused);
    }

    [Fact]
    public void A_key_used_by_a_resource_that_is_itself_used_is_not_reported()
    {
        var r = Run("""
                <Color x:Key="AccentColor">#FF4081</Color>
                <SolidColorBrush x:Key="AccentBrush" Color="{StaticResource AccentColor}" />
                """,
            """<Border Background="{StaticResource AccentBrush}" />""");

        Assert.Empty(Unused(r));
    }

    [Fact]
    public void One_live_reference_anywhere_is_enough_to_keep_a_key()
    {
        var r = Run("""
                <Color x:Key="AccentColor">#FF4081</Color>
                <SolidColorBrush x:Key="AccentBrush" Color="{StaticResource AccentColor}" />
                """,
            """<TextBlock><TextBlock.Foreground><SolidColorBrush Color="{StaticResource AccentColor}" /></TextBlock.Foreground></TextBlock>""");

        Assert.Equal(["AccentBrush"], Unused(r));
    }

    [Fact]
    public void A_key_looked_up_from_code_is_not_pulled_down_with_its_owner()
    {
        Put("Main.xaml.cs", """var c = (Color)FindResource("AccentColor");""");
        var r = Run("""
            <Color x:Key="AccentColor">#FF4081</Color>
            <SolidColorBrush x:Key="AccentBrush" Color="{StaticResource AccentColor}" />
            """);

        Assert.Equal(["AccentBrush"], Unused(r));
    }

    [Fact]
    public void An_ignored_owner_keeps_what_it_references()
    {
        // ignore 的意思是「我知道它看起來沒人用，但它有用」—— 它裡面的引用當然也是活的
        var r = Run("""
            <Color x:Key="AccentColor">#FF4081</Color>
            <!-- xamlkeys-ignore: read by a plugin at runtime -->
            <SolidColorBrush x:Key="AccentBrush" Color="{StaticResource AccentColor}" />
            """);

        Assert.Empty(Unused(r));
    }

    [Fact]
    public void What_only_a_dead_copy_in_one_window_references_is_unused_too()
    {
        // 這個視窗的 ComboStyle 是死副本（別的視窗那份才在用），它裡面引用的 ItemStyle 只有它在用
        Put("Views/Editor.xaml", $$"""
            <Window {{Ns}}><Window.Resources><Style x:Key="ComboStyle" TargetType="ComboBox" /></Window.Resources>
              <ComboBox Style="{StaticResource ComboStyle}" /></Window>
            """);
        Put("Views/Labels.xaml", $$"""
            <Window {{Ns}}><Window.Resources>
              <Style x:Key="ItemStyle" TargetType="ComboBoxItem" />
              <Style x:Key="ComboStyle" TargetType="ComboBox"><Setter Property="ItemContainerStyle" Value="{StaticResource ItemStyle}" /></Style>
            </Window.Resources><Grid /></Window>
            """);

        var r = Run("");

        var f = Assert.Single(r.Findings, x => x.Key == "ItemStyle");
        Assert.Contains("only referenced by 'ComboStyle' at Views/Labels.xaml", f.Message);
    }

    // ── 框架鍵覆寫 ─────────────────────────────────────────────────────────

    [Fact]
    public void Redefining_a_winui_framework_key_is_not_unused()
    {
        // 框架的樣板用 {ThemeResource ButtonBackground} 讀它 —— 引用在框架裡，原始碼樹看不到
        var r = Run("""<SolidColorBrush x:Key="ButtonBackground" Color="Red" />""",
                    csproj: "<OutputType>WinExe</OutputType><UseWinUI>true</UseWinUI>");

        Assert.Empty(Unused(r));
        Assert.Equal(1, r.FrameworkKeyOverrides);
    }

    [Fact]
    public void The_same_key_in_a_wpf_project_is_just_an_unused_key()
    {
        // WPF 的內建主題不讀 ButtonBackground —— 只有 WinUI 才豁免
        var r = Run("""<SolidColorBrush x:Key="ButtonBackground" Color="Red" />""");

        Assert.Equal(["ButtonBackground"], Unused(r));
        Assert.Equal(0, r.FrameworkKeyOverrides);
    }

    [Fact]
    public void What_a_framework_override_references_stays_alive()
    {
        var r = Run("""
                <Color x:Key="BrandRed">#C42B1C</Color>
                <SolidColorBrush x:Key="ButtonBackground" Color="{StaticResource BrandRed}" />
                """,
            csproj: "<OutputType>WinExe</OutputType><UseWinUI>true</UseWinUI>");

        Assert.Empty(Unused(r));
    }
}
