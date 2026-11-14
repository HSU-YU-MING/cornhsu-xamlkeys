using XamlKeys.Core;

namespace XamlKeys.Tests;

/// <summary>
/// 各畫面自己的 &lt;X.Resources&gt; 裡，名字別處有人用、這一份卻誰都碰不到的定義（Analyzer.EmitDeadLocalCopies）。
///
/// 2026-11-14 第一次真的拿 XamlKeys 去用（QuillNest）時看到的盲點：unused 以名字算一次，
/// 所以複製貼上到各個視窗的同名樣式，只要有一份在用，其他死掉的副本全部隱形。
///
/// 下面「不能報」的每一條，都是語料裡實際會出現、而且執行期真的找得到的寫法。
/// </summary>
public class DeadLocalCopyTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("xamlkeys-deadcopy");
    public void Dispose() => _dir.Delete(true);

    private const string Ns = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
                              "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";

    private void Put(string rel, string content)
    {
        var path = Path.Combine(_dir.FullName, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private List<Finding> DeadCopies()
    {
        if (!File.Exists(Path.Combine(_dir.FullName, "App.csproj")))
            Put("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup></Project>");
        if (!File.Exists(Path.Combine(_dir.FullName, "App.xaml")))
            Put("App.xaml", $"<Application {Ns}><Application.Resources /></Application>");

        return Analyzer.Run(_dir.FullName, new Config()).Findings
                       .Where(f => f.Kind == FindingKind.Unused && f.Message.Contains("nothing that can see"))
                       .ToList();
    }

    private static string Window(string resources, string body = "") =>
        $"<Window {Ns}><Window.Resources>{resources}</Window.Resources><Grid>{body}</Grid></Window>";

    private const string Style = """<Style x:Key="DarkComboBoxStyle" TargetType="ComboBox" />""";
    private const string UsesStyle = """<ComboBox Style="{StaticResource DarkComboBoxStyle}" />""";

    [Fact]
    public void A_copy_nobody_in_its_window_uses_is_reported_and_points_at_the_copy_in_use()
    {
        Put("Views/Editor.xaml", Window(Style, UsesStyle));
        Put("Views/LabelDialog.xaml", Window(Style));        // 這個視窗連下拉選單都沒有

        var f = Assert.Single(DeadCopies());
        Assert.Equal("Views/LabelDialog.xaml", f.Location.File);
        Assert.Equal("Views/Editor.xaml", f.Related?.File);
        Assert.Contains("Window.Resources", f.Message);
    }

    [Fact]
    public void Copies_that_each_window_uses_are_not_reported()
    {
        Put("Views/Editor.xaml", Window(Style, UsesStyle));
        Put("Views/LabelDialog.xaml", Window(Style, UsesStyle));

        Assert.Empty(DeadCopies());
    }

    [Fact]
    public void A_local_override_of_a_key_a_shared_style_looks_up_dynamically_is_in_use()
    {
        // 輕量樣式覆寫：共用樣式的 {DynamicResource K} 從被套用的控制項往上找，
        // 視窗自己的 K 會蓋過全域的值。Avalonia／MahApps 的範例大量這樣寫。
        Put("App.xaml", $$"""
            <Application {{Ns}}>
              <Application.Resources>
                <SolidColorBrush x:Key="TabHeaderBrush" Color="Gray" />
                <Style TargetType="TabItem"><Setter Property="Foreground" Value="{DynamicResource TabHeaderBrush}" /></Style>
              </Application.Resources>
            </Application>
            """);
        Put("Views/Fancy.xaml", Window("""<SolidColorBrush x:Key="TabHeaderBrush" Color="Gold" />""", "<TabControl />"));

        Assert.Empty(DeadCopies());
    }

    [Fact]
    public void A_key_a_shared_template_expects_from_its_host_is_not_reported()
    {
        // 共用字典裡樣板的 {StaticResource HostAccent} 靜態解析不到 —— 樣板套用時才沿樹往上，
        // 從宿主視窗拿到。⚠ 這裡刻意用 Static：Dynamic 會先被「輕量樣式覆寫」那條擋掉，測不到這條。
        Put("Themes/Shared.xaml", $$"""
            <ResourceDictionary {{Ns}}>
              <DataTemplate x:Key="BadgeTemplate"><Border Background="{StaticResource HostAccent}" /></DataTemplate>
            </ResourceDictionary>
            """);
        Put("App.xaml", $$"""
            <Application {{Ns}}>
              <Application.Resources>
                <ResourceDictionary>
                  <ResourceDictionary.MergedDictionaries><ResourceDictionary Source="Themes/Shared.xaml" /></ResourceDictionary.MergedDictionaries>
                </ResourceDictionary>
              </Application.Resources>
            </Application>
            """);
        Put("Views/Host.xaml", Window("""<SolidColorBrush x:Key="HostAccent" Color="Red" />"""));
        Put("Views/Other.xaml", Window("""<SolidColorBrush x:Key="HostAccent" Color="Red" />""",
                                       """<Border Background="{StaticResource HostAccent}" />"""));

        Assert.Empty(DeadCopies());
    }

    [Theory]
    [InlineData("Views/Dialog.xaml.cs")]
    [InlineData("Views/Dialog.cs")]          // WPF-Samples 的 HexSphereDemo 是這種命名
    public void A_copy_the_code_behind_uses_by_name_is_not_reported(string codeBehind)
    {
        Put("Views/Editor.xaml", Window(Style, UsesStyle));
        Put("Views/Dialog.xaml", Window(Style));
        Put(codeBehind, "public partial class Dialog { void Init() { combo.Style = DarkComboBoxStyle; } }");

        Assert.Empty(DeadCopies());
    }

    [Fact]
    public void A_name_any_csharp_looks_up_is_not_reported()
    {
        // C# 可以對任何元素呼叫 FindResource —— 哪一份會被找到，要看執行期是從哪個元素出發
        Put("Views/Editor.xaml", Window(Style, UsesStyle));
        Put("Views/Dialog.xaml", Window(Style));
        Put("Services/ComboStyler.cs", """static void Apply(FrameworkElement e) => e.Style = (Style)e.FindResource("DarkComboBoxStyle");""");

        Assert.Empty(DeadCopies());
    }

    [Fact]
    public void A_copy_in_a_window_whose_code_behind_looks_resources_up_by_variable_is_not_reported()
    {
        Put("Views/Editor.xaml", Window(Style, UsesStyle));
        Put("Views/Dialog.xaml", Window(Style));
        Put("Views/Dialog.xaml.cs", "void Pick(string k) { combo.Style = (Style)FindResource(k); }");

        Assert.Empty(DeadCopies());
    }

    [Fact]
    public void Theme_dictionary_copies_are_not_reported()
    {
        // 高對比那一份永遠不會被深、淺兩側「解析到」，但執行期切到高對比時就是它
        Put("Views/Editor.xaml", Window(
            """
            <ResourceDictionary>
              <ResourceDictionary.ThemeDictionaries>
                <ResourceDictionary x:Key="Default"><SolidColorBrush x:Key="CardBrush" Color="Gray" /></ResourceDictionary>
                <ResourceDictionary x:Key="HighContrast"><SolidColorBrush x:Key="CardBrush" Color="Black" /></ResourceDictionary>
              </ResourceDictionary.ThemeDictionaries>
            </ResourceDictionary>
            """,
            """<Border Background="{ThemeResource CardBrush}" />"""));

        Assert.Empty(DeadCopies());
    }

    [Fact]
    public void Resources_of_an_application_subclass_root_are_application_wide()
    {
        // MAUI 允許 App.xaml 的根元素是自己的 Application 子類別（<local:App>）
        Put("Views/Editor.xaml", Window(Style, UsesStyle));
        Put("TestApp.xaml", $"""
            <local:App {Ns} xmlns:local="clr-namespace:Demo">
              <local:App.Resources>{Style}</local:App.Resources>
            </local:App>
            """);

        Assert.Empty(DeadCopies());
    }

    [Fact]
    public void An_ignore_comment_silences_a_dead_copy_and_is_counted()
    {
        Put("Views/Editor.xaml", Window(Style, UsesStyle));
        Put("Views/Dialog.xaml", Window("<!-- xamlkeys-ignore: kept for the next release -->" + Style));

        Assert.Empty(DeadCopies());
        Assert.True(Analyzer.Run(_dir.FullName, new Config()).Suppressed > 0);
    }
}
