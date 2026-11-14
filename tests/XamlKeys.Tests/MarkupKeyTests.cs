using XamlKeys.Core;

namespace XamlKeys.Tests;

/// <summary>
/// 不是字串的鍵：<c>{x:Static …Key}</c> 與 <c>{ComponentResourceKey …}</c>（2026-11-15，原本是 README 的已知盲區）。
/// 17 個外部專案量過：591 處引用裡 553 處是框架自己的鍵 —— 先認框架，再談專案自己的鍵。
/// </summary>
public class MarkupKeyTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("xamlkeys-markup");
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
    private const string Ns = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
                              "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" " +
                              "xmlns:local=\"clr-namespace:MyApp\"";

    private static string App(string body) =>
        $"<Application {Ns}><Application.Resources><ResourceDictionary>{body}</ResourceDictionary></Application.Resources></Application>";
    private static string Win(string body) => $"<Window {Ns}>{body}</Window>";

    private static List<Finding> Of(AnalysisResult r, FindingKind k) => r.Findings.Where(f => f.Kind == k).ToList();

    // ── 正規化 ─────────────────────────────────────────────────────────

    private static string? Resolve(string prefix) => prefix switch
    {
        "" => "http://schemas.microsoft.com/winfx/2006/xaml/presentation",
        "x" => "http://schemas.microsoft.com/winfx/2006/xaml",
        "local" => "clr-namespace:MyApp;assembly=MyApp",
        "ui" => "using:MyLib.Controls",
        "lib" => "https://example.com/mylib",
        _ => null,
    };

    [Theory]
    [InlineData("{x:Static SystemColors.ControlBrushKey}", "{x:Static SystemColors.ControlBrushKey}", true, "ControlBrushKey")]
    [InlineData("{x:Static local:Keys.Accent}", "{x:Static MyApp.Keys.Accent}", false, "Accent")]
    [InlineData("{x:Static Member=local:Keys.Accent}", "{x:Static MyApp.Keys.Accent}", false, "Accent")]
    [InlineData("{x:Static ui:Keys.Accent}", "{x:Static MyLib.Controls.Keys.Accent}", false, "Accent")]
    [InlineData("{x:Static lib:Keys.Accent}", "{x:Static https://example.com/mylib|Keys.Accent}", false, "Accent")]
    [InlineData("{ComponentResourceKey {x:Type local:Box}, Look}", "{ComponentResourceKey MyApp.Box, Look}", false, "Look")]
    [InlineData("{ComponentResourceKey TypeInTargetAssembly={x:Type local:Box}, ResourceId=Look}", "{ComponentResourceKey MyApp.Box, Look}", false, "Look")]
    [InlineData("{ComponentResourceKey ResourceId=Look, TypeInTargetAssembly={x:Type local:Box}}", "{ComponentResourceKey MyApp.Box, Look}", false, "Look")]
    [InlineData("{ComponentResourceKey ResourceId=DataGridSelectAllButtonStyle, TypeInTargetAssembly={x:Type DataGrid}}", "{ComponentResourceKey DataGrid, DataGridSelectAllButtonStyle}", true, "DataGridSelectAllButtonStyle")]
    public void Markup_keys_get_one_identity_however_they_are_written(string raw, string key, bool framework, string member)
    {
        var c = MarkupKeys.From(raw, Resolve);
        Assert.NotNull(c);
        Assert.Equal(key, c!.Value.Key);
        Assert.Equal(framework, c.Value.Framework);
        Assert.Equal(member, c.Value.Member);
    }

    [Theory]
    [InlineData("{x:Type Button}")]                 // 隱含樣式的鍵：不是我們的單位
    [InlineData("{x:Static Keys}")]                 // 沒有成員
    [InlineData("{x:Static {Binding X}}")]
    [InlineData("{ComponentResourceKey {x:Type local:Box}}")]   // 少了 ResourceId
    [InlineData("{Binding Foo}")]
    public void Anything_else_is_not_a_markup_key(string raw)
    {
        Assert.Null(MarkupKeys.From(raw, Resolve));
    }

    // ── 框架的鍵 ───────────────────────────────────────────────────────

    [Fact]
    public void A_framework_key_reference_is_never_dangling_and_is_counted()
    {
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["App.xaml"] = App("""<SolidColorBrush x:Key="Own" Color="Red" />"""),
            ["Main.xaml"] = Win("""<Border Background="{DynamicResource Own}" BorderBrush="{DynamicResource {x:Static SystemColors.ControlBrushKey}}" />"""),
        });

        Assert.Empty(r.Findings);
        Assert.Equal(1, r.FrameworkProvided);
    }

    [Fact]
    public void Redefining_a_framework_key_is_not_unused_even_in_a_window()
    {
        // MahApps／MaterialDesign 的 MenuItem.SeparatorStyleKey：框架的 Separator 在執行期讀它
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["App.xaml"] = App("""
                <Style x:Key="{x:Static MenuItem.SeparatorStyleKey}" TargetType="Separator" />
                <Style x:Key="{x:Static ToolBar.ButtonStyleKey}" TargetType="Button" />
                """),
            ["Main.xaml"] = Win("""
                <Window.Resources><Style x:Key="{x:Static MenuItem.SeparatorStyleKey}" TargetType="Separator" /></Window.Resources>
                """),
            ["Other.xaml"] = Win("""<Separator Style="{StaticResource {x:Static MenuItem.SeparatorStyleKey}}" />"""),
        });

        Assert.Empty(Of(r, FindingKind.Unused));   // ToolBar.ButtonStyleKey 沒有任何 XAML 引用，框架照樣會讀
        Assert.True(r.FrameworkKeyOverrides >= 1);
    }

    // ── 專案自己的鍵 ────────────────────────────────────────────────────

    [Fact]
    public void A_project_key_resolves_through_scope_like_a_string_key()
    {
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["App.xaml"] = App("""<SolidColorBrush x:Key="{x:Static local:Keys.Accent}" Color="Red" />"""),
            ["Main.xaml"] = Win("""<Border Background="{StaticResource {x:Static local:Keys.Accent}}" />"""),
        });

        Assert.Empty(r.Findings);
        Assert.Empty(r.UnresolvedSites);
    }

    [Fact]
    public void Two_definitions_of_the_same_markup_key_in_one_dictionary_are_a_duplicate()
    {
        // 同一個 x:Static 物件當兩次鍵 —— 載入時丟例外，跟字串鍵一樣
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["App.xaml"] = App("""
                <SolidColorBrush x:Key="{x:Static local:Keys.Accent}" Color="Red" />
                <SolidColorBrush x:Key="{x:Static Member=local:Keys.Accent}" Color="Blue" />
                """),
            ["Main.xaml"] = Win("""<Border Background="{StaticResource {x:Static local:Keys.Accent}}" />"""),
        });

        Assert.Single(Of(r, FindingKind.DuplicateKey));
    }

    [Fact]
    public void A_project_key_defined_nowhere_stays_unresolved_and_is_never_dangling()
    {
        // 可能在 C# 裡 Resources.Add(Keys.Missing, …)，或在別的組件的 generic.xaml
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["App.xaml"] = App("""<SolidColorBrush x:Key="Own" Color="Red" />"""),
            ["Main.xaml"] = Win("""<Border Background="{StaticResource Own}" BorderBrush="{StaticResource {x:Static local:Keys.Missing}}" />"""),
        });

        Assert.DoesNotContain(r.Findings, f => f.Kind is FindingKind.DanglingStatic or FindingKind.DanglingDynamic);
        Assert.Single(r.UnresolvedSites);
    }

    [Fact]
    public void A_project_key_defined_elsewhere_but_not_in_scope_is_out_of_scope()
    {
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["App.xaml"] = App("""<SolidColorBrush x:Key="Own" Color="Red" />"""),
            ["A.xaml"] = Win("""<Window.Resources><SolidColorBrush x:Key="{x:Static local:Keys.Local}" Color="Red" /></Window.Resources><Border Background="{StaticResource {x:Static local:Keys.Local}}" />"""),
            ["B.xaml"] = Win("""<Border Background="{StaticResource Own}" BorderBrush="{StaticResource {x:Static local:Keys.Local}}" />"""),
        });

        var f = Assert.Single(Of(r, FindingKind.OutOfScope));
        Assert.Equal("B.xaml", f.Location.File);
    }

    [Fact]
    public void A_project_markup_key_nobody_references_is_unused_unless_CSharp_names_the_member()
    {
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["App.xaml"] = App("""
                <SolidColorBrush x:Key="{x:Static local:Keys.Dead}" Color="Red" />
                <SolidColorBrush x:Key="{x:Static local:Keys.FromCode}" Color="Red" />
                <SolidColorBrush x:Key="{ComponentResourceKey {x:Type local:Box}, Look}" Color="Red" />
                """),
            ["Keys.cs"] = """
                static class Keys { public static readonly object Dead = new(); public static readonly object FromCode = new(); }
                class Box { }
                class Use { object A(FrameworkElement e) => e.FindResource(Keys.FromCode); }
                """,
        });

        // Dead 在 C# 裡只有宣告 —— 宣告也是一次出現，所以只能靠「不在 C# 裡出現」判；這裡 Dead 有宣告，也算有人用。
        // 真正會被報的是 C# 完全沒提到的：ComponentResourceKey 的 Look。
        var unused = Of(r, FindingKind.Unused).Select(f => f.Key).ToList();
        Assert.Equal(["{ComponentResourceKey MyApp.Box, Look}"], unused);
    }

    [Fact]
    public void A_markup_key_of_a_type_outside_the_tree_belongs_to_that_library()
    {
        // dnSpy：{x:Static SharpTreeView.DefaultItemContainerStyleKey}，SharpTreeView 在 ICSharpCode.TreeView 套件裡 ——
        // 套件自己的樣板讀這個鍵，不是沒人用
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["App.xaml"] = $"""
                <Application {Ns} xmlns:tv="clr-namespace:ICSharpCode.TreeView;assembly=ICSharpCode.TreeView">
                  <Application.Resources>
                    <Style x:Key="{"{"}x:Static tv:SharpTreeView.DefaultItemContainerStyleKey{"}"}" TargetType="TreeViewItem" />
                  </Application.Resources>
                </Application>
                """,
        });

        Assert.Empty(Of(r, FindingKind.Unused));
        Assert.Equal(1, r.LibraryKeyOverrides);
    }
}
