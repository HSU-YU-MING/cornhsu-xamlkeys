using XamlKeys.Core;

namespace XamlKeys.Tests;

/// <summary>
/// 規則 35／36（2026-11-14）：組出來、但有固定開頭或結尾的鍵名與字典路徑。
///
/// 量過 17 個外部專案：243 個用變數查找資源的地方裡，25 個寫成 <c>$"Icon{name}"</c> 或
/// <c>"Icon" + name</c>；另有 24 處用 <c>$"Themes/{name}.xaml"</c> 依名稱載入主題或語系字典。
/// 舊版把前者的鍵全報成沒人用、後者的字典全報成沒人合併。
///
/// 兩條都是「放寬」：所以樣式要夠長、只比同一個專案、而且一定留計數。
/// </summary>
public class ComposedLookupTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("xamlkeys-composed");
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
                              "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";

    private static string App(string body) =>
        $"<Application {Ns}><Application.Resources><ResourceDictionary>{body}</ResourceDictionary></Application.Resources></Application>";

    private static string Dict(string body) => $"<ResourceDictionary {Ns}>{body}</ResourceDictionary>";

    private static List<string> Unused(AnalysisResult r) =>
        r.Findings.Where(f => f.Kind == FindingKind.Unused).Select(f => f.Key).Order(StringComparer.Ordinal).ToList();

    private static List<string> Orphans(AnalysisResult r) =>
        r.Findings.Where(f => f.Kind == FindingKind.OrphanDictionary).Select(f => f.Location.File).Order(StringComparer.Ordinal).ToList();

    // ── 從查找點取出固定的開頭／結尾 ─────────────────────────────────────────

    [Theory]
    [InlineData("""FindResource($"Icon{name}")""", "Icon", "")]
    [InlineData("""TryFindResource($"Icon{name}")""", "Icon", "")]
    [InlineData("""Resources[$"{state}Brush"]""", "", "Brush")]
    [InlineData("""FindResource($"Icon{name}{size}Brush")""", "Icon", "Brush")]
    [InlineData("""FindResource($@"Icon{name}")""", "Icon", "")]
    [InlineData("""FindResource(@$"Icon{name}")""", "Icon", "")]
    [InlineData("""FindResource($"{{Icon}}{name}")""", "{Icon}", "")]
    [InlineData("""FindResource($"Icon{(big ? "L" : "S")}")""", "Icon", "")]
    [InlineData("""FindResource("Icon" + name)""", "Icon", "")]
    [InlineData("""FindResource(name + "Brush")""", "", "Brush")]
    [InlineData("""FindResource("Icon" + name + "Brush")""", "Icon", "Brush")]
    [InlineData("""TryGetResource("Icon" + name,""", "Icon", "")]
    [InlineData("""SetResourceReference(Control.BackgroundProperty, $"Brush{x}")""", "Brush", "")]
    [InlineData("""SetResourceReference(Control.BackgroundProperty, "Brush" + x)""", "Brush", "")]
    public void A_fixed_prefix_or_suffix_is_extracted(string site, string prefix, string suffix)
    {
        Assert.Equal(((string?)prefix, (string?)suffix), CSharpScanner.ComposedPattern(site));
    }

    [Theory]
    [InlineData("""FindResource(key)""")]
    [InlineData("""FindResource(GetKey("Icon"))""")]
    [InlineData("""FindResource(flag ? "A" : "B")""")]
    [InlineData("""FindResource($"Icon""")]          // 字串沒有結束（查找點被截斷）
    [InlineData("""FindResource($"Icon")""")]        // 沒有洞：不是組出來的
    [InlineData("""FindResource($"Icon{name}Brus""")] // 有洞但字串被截斷：結尾讀不準，寧可不比
    [InlineData("""SetResourceReference(Control.BackgroundProperty)""")]
    public void No_pattern_when_nothing_fixed_can_be_read(string site)
    {
        Assert.Equal(((string?)null, (string?)null), CSharpScanner.ComposedPattern(site));
    }

    [Fact]
    public void A_parenthesis_inside_a_string_in_the_first_argument_does_not_end_it()
    {
        Assert.Equal(((string?)"Brush", (string?)""),
                     CSharpScanner.ComposedPattern("""SetResourceReference(P(")"), "Brush" + x)"""));
    }

    [Fact]
    public void For_SetResourceReference_the_key_is_the_second_argument()
    {
        // 第一個參數裡的字串不能被當成鍵的開頭
        Assert.Equal(((string?)"", (string?)"Brush"),
                     CSharpScanner.ComposedPattern("""SetResourceReference(Get("Icon"), x + "Brush")"""));
    }

    [Theory]
    [InlineData("Icon", "", "IconSave", true)]
    [InlineData("Icon", "", "Icon", true)]              // 洞可以是空字串
    [InlineData("Icon", "", "SaveIcon", false)]
    [InlineData("", "Brush", "AccentBrush", true)]
    [InlineData("", "Brush", "BrushAccent", false)]
    [InlineData("Icon", "Brush", "IconSaveBrush", true)]
    [InlineData("Icon", "Brush", "IconBrush", true)]
    [InlineData("IconBr", "Brush", "IconBrush", false)] // 開頭與結尾不能重疊
    [InlineData("icon", "", "IconSave", false)]         // 資源鍵分大小寫
    [InlineData("Ic", "", "IconSave", false)]           // 樣式太短：不比
    [InlineData("I", "Br", "IconBr", true)]             // 開頭＋結尾合計 3 個字就夠
    public void A_site_matches_keys_by_its_fixed_parts(string prefix, string suffix, string key, bool expected)
    {
        var site = new DynamicKeySite(new SourceLocation("A.cs", 1), "x") { Prefix = prefix, Suffix = suffix };
        Assert.Equal(expected, site.Matches(key));
    }

    [Fact]
    public void A_site_without_a_pattern_matches_nothing()
    {
        var site = new DynamicKeySite(new SourceLocation("A.cs", 1), "x");
        Assert.False(site.HasPattern);
        Assert.False(site.Matches("Anything"));
    }

    // ── 鍵名：整個流程 ───────────────────────────────────────────────────

    [Fact]
    public void Keys_matching_a_composed_lookup_are_used_and_counted()
    {
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["App.xaml"] = App("""
                <SolidColorBrush x:Key="IconSave" Color="#111" />
                <SolidColorBrush x:Key="IconOpen" Color="#222" />
                <SolidColorBrush x:Key="Unrelated" Color="#333" />
                """),
            ["Icons.cs"] = """
                class Icons { object Get(FrameworkElement el, string name) => el.FindResource($"Icon{name}"); }
                """,
        });

        Assert.Equal(["Unrelated"], Unused(r));
        Assert.Equal(2, r.KeysMatchingComposedLookups);
    }

    [Fact]
    public void Only_keys_the_pattern_actually_saved_are_counted()
    {
        // 已經有 XAML 引用的鍵不算「靠樣式救回來」；函式庫的鍵本來就不判 unused，算進來只會灌水
        var r = Analyze(new()
        {
            ["App/App.csproj"] = AppProj,
            ["App/App.xaml"] = App("""
                <SolidColorBrush x:Key="IconSave" Color="#111" />
                <SolidColorBrush x:Key="IconOpen" Color="#222" />
                """),
            ["App/Main.xaml"] = $"<Window {Ns}><Border Background=\"{{StaticResource IconOpen}}\" /></Window>",
            ["App/Icons.cs"] = """
                class Icons { object Get(FrameworkElement el, string name) => el.FindResource($"Icon{name}"); }
                """,
            ["Lib/Lib.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Library</OutputType></PropertyGroup></Project>",
            ["Lib/Themes/Generic.xaml"] = Dict("""<SolidColorBrush x:Key="GlyphClose" Color="#333" />"""),
            ["Lib/Glyphs.cs"] = """
                class Glyphs { object Get(FrameworkElement el, string name) => el.FindResource($"Glyph{name}"); }
                """,
        });

        Assert.Empty(Unused(r));
        Assert.Equal(1, r.KeysMatchingComposedLookups);
    }

    [Fact]
    public void A_concatenation_that_starts_with_a_string_is_seen_and_rescues()
    {
        // WPF-Samples PhotoFlipperDemo：Pic011～Pic016 只由 FindResource("Pic01" + n) 取用
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["MainWindow.xaml"] = $"<Window {Ns}><Window.Resources>" +
                                  "<DiffuseMaterial x:Key=\"Pic011\" /><DiffuseMaterial x:Key=\"Pic012\" />" +
                                  "<DiffuseMaterial x:Key=\"Other\" /></Window.Resources></Window>",
            ["MainWindow.cs"] = """
                partial class MainWindow { void Flip(int n) { var m = FindResource("Pic01" + n) as DiffuseMaterial; } }
                """,
        });

        Assert.Equal(["Other"], Unused(r));
        Assert.Equal(2, r.KeysMatchingComposedLookups);
    }

    [Fact]
    public void A_concatenated_suffix_rescues_too()
    {
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["App.xaml"] = App("""
                <SolidColorBrush x:Key="ErrorBrush" Color="#111" />
                <SolidColorBrush x:Key="ErrorColorKey" Color="#222" />
                """),
            ["Status.cs"] = """
                class Status { object Get(FrameworkElement el, string state) => el.FindResource(state + "Brush"); }
                """,
        });

        Assert.Equal(["ErrorColorKey"], Unused(r));
        Assert.Equal(1, r.KeysMatchingComposedLookups);
    }

    [Fact]
    public void A_composed_lookup_only_rescues_keys_in_its_own_project()
    {
        // 跟字典路徑同一個教訓（maui-samples 的 231 個範例）：不限定專案就是一次跨專案大赦
        var r = Analyze(new()
        {
            ["A/A.csproj"] = AppProj,
            ["A/App.xaml"] = App("""<SolidColorBrush x:Key="IconA" Color="#111" />"""),
            ["A/Icons.cs"] = """
                class Icons { object Get(FrameworkElement el, string name) => el.FindResource($"Icon{name}"); }
                """,
            ["B/B.csproj"] = AppProj,
            ["B/App.xaml"] = App("""<SolidColorBrush x:Key="IconB" Color="#222" />"""),
        });

        Assert.Equal(["IconB"], Unused(r));
        Assert.Equal(1, r.KeysMatchingComposedLookups);
    }

    [Fact]
    public void A_too_short_pattern_rescues_nothing()
    {
        // $"I{x}" 只剩一個字：會把大半個專案的鍵都算成有人用，等於關掉檢查
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["App.xaml"] = App("""<SolidColorBrush x:Key="IconSave" Color="#111" />"""),
            ["Icons.cs"] = """
                class Icons { object Get(FrameworkElement el, string x) => el.FindResource($"I{x}"); }
                """,
        });

        Assert.Equal(["IconSave"], Unused(r));
        Assert.Equal(0, r.KeysMatchingComposedLookups);
    }

    // ── 字典路徑 ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Themes/{name}.xaml", "Themes/", ".xaml")]
    [InlineData("/Themes/{name}.xaml", "Themes/", ".xaml")]
    [InlineData("./Themes/{name}.xaml", "Themes/", ".xaml")]
    [InlineData("pack://application:,,,/App;component/Themes/{t}Theme.xaml", "Themes/", "Theme.xaml")]
    [InlineData("/App;component/Lang/{culture}.xaml", "Lang/", ".xaml")]
    [InlineData("avares://App/Styles/{n}.axaml", "Styles/", ".axaml")]
    [InlineData("{name}Theme.xaml", "", "Theme.xaml")]
    [InlineData("Abc{n}.xaml", "Abc", ".xaml")]
    public void A_composed_dictionary_path_becomes_a_pattern(string path, string prefix, string suffix)
    {
        Assert.Equal((prefix, suffix), Analyzer.ComposedPathPattern(path));
    }

    [Theory]
    [InlineData("Themes/Dark.xaml")]      // 不是組出來的：照舊比檔名
    [InlineData("{name}.xaml")]           // 只剩副檔名：會把每本字典都算成程式會載入
    [InlineData("/{name}.xaml")]
    [InlineData("Ab{n}.xaml")]            // 扣掉副檔名只剩 2 個字
    [InlineData("pack://application:,,,/App;component/{name}.xaml")]
    [InlineData("avares://App/{name}.axaml")]
    public void No_path_pattern_when_too_little_is_fixed(string path)
    {
        Assert.Null(Analyzer.ComposedPathPattern(path));
    }

    [Fact]
    public void Dictionaries_matching_a_composed_path_are_runtime_loaded_and_counted()
    {
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["App.xaml"] = App("""<SolidColorBrush x:Key="Own" Color="#111" />"""),
            ["ThemeService.cs"] = """
                class ThemeService { ResourceDictionary Load(string name) =>
                    new ResourceDictionary { Source = new Uri($"Themes/{name}.xaml", UriKind.Relative) }; }
                """,
            ["Themes/Dark.xaml"] = Dict("""<SolidColorBrush x:Key="Bg" Color="#000" />"""),
            ["Themes/Light.xaml"] = Dict("""<SolidColorBrush x:Key="Bg" Color="#FFF" />"""),
            ["Other/Stray.xaml"] = Dict("""<SolidColorBrush x:Key="Stray" Color="#888" />"""),
        });

        Assert.Equal(["Other/Stray.xaml"], Orphans(r));
        Assert.Equal(2, r.RuntimeLoadedDictionaries);
    }

    [Fact]
    public void A_composed_path_only_loads_dictionaries_in_its_own_project()
    {
        var r = Analyze(new()
        {
            ["A/A.csproj"] = AppProj,
            ["A/App.xaml"] = App("""<SolidColorBrush x:Key="OwnA" Color="#111" />"""),
            ["A/ThemeService.cs"] = """
                class ThemeService { Uri U(string n) => new Uri($"Themes/{n}.xaml", UriKind.Relative); }
                """,
            ["A/Themes/Dark.xaml"] = Dict("""<SolidColorBrush x:Key="InA" Color="#000" />"""),
            ["B/B.csproj"] = AppProj,
            ["B/App.xaml"] = App("""<SolidColorBrush x:Key="OwnB" Color="#111" />"""),
            ["B/Themes/Dark.xaml"] = Dict("""<SolidColorBrush x:Key="InB" Color="#000" />"""),
        });

        Assert.Equal(["B/Themes/Dark.xaml"], Orphans(r));
        Assert.Equal(1, r.RuntimeLoadedDictionaries);
    }

    [Fact]
    public void A_path_pattern_only_matches_its_fixed_parts()
    {
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["App.xaml"] = App("""<SolidColorBrush x:Key="Own" Color="#111" />"""),
            ["ThemeService.cs"] = """
                class ThemeService { Uri U(string n) => new Uri($"/App;component/Themes/{n}Theme.xaml", UriKind.Relative); }
                """,
            ["Themes/DarkTheme.xaml"] = Dict("""<SolidColorBrush x:Key="Bg" Color="#000" />"""),
            ["Themes/Icons.xaml"] = Dict("""<SolidColorBrush x:Key="Ic" Color="#000" />"""),
            ["Skins/BlueTheme.xaml"] = Dict("""<SolidColorBrush x:Key="Sk" Color="#000" />"""),
        });

        Assert.Equal(["Skins/BlueTheme.xaml", "Themes/Icons.xaml"], Orphans(r));
    }

    [Fact]
    public void A_hole_in_the_folder_does_not_turn_into_a_bare_file_name_match()
    {
        // $"Themes/{variant}/Colors.xaml"：只比檔名 Colors.xaml 的話，專案裡每一本 Colors.xaml 都會算成程式會載入
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["App.xaml"] = App("""<SolidColorBrush x:Key="Own" Color="#111" />"""),
            ["ThemeService.cs"] = """
                class ThemeService { Uri U(string v) => new Uri($"Themes/{v}/Colors.xaml", UriKind.Relative); }
                """,
            ["Themes/Dark/Colors.xaml"] = Dict("""<SolidColorBrush x:Key="Bg" Color="#000" />"""),
            ["Samples/Colors.xaml"] = Dict("""<SolidColorBrush x:Key="Sample" Color="#000" />"""),
        });

        Assert.Equal(["Samples/Colors.xaml"], Orphans(r));
    }

    [Fact]
    public void The_fixed_head_and_tail_of_a_path_cannot_overlap()
    {
        // $"Themes/Dark{x}Dark.xaml" 至少是 Themes/DarkDark.xaml —— Themes/Dark.xaml 不可能是它
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["App.xaml"] = App("""<SolidColorBrush x:Key="Own" Color="#111" />"""),
            ["ThemeService.cs"] = """
                class ThemeService { Uri U(string n) => new Uri($"Themes/Dark{n}Dark.xaml", UriKind.Relative); }
                """,
            ["Themes/Dark.xaml"] = Dict("""<SolidColorBrush x:Key="Bg" Color="#000" />"""),
        });

        Assert.Equal(["Themes/Dark.xaml"], Orphans(r));
    }
}
