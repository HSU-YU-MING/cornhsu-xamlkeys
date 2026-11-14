using XamlKeys.Core;

namespace XamlKeys.Tests;

public class CSharpScannerTests
{
    private static (List<KeyReference> Refs, List<DynamicKeySite> Dyn) Scan(string code)
    {
        var dir = Directory.CreateTempSubdirectory("xamlkeys-cs");
        try
        {
            var file = Path.Combine(dir.FullName, "T.cs");
            File.WriteAllText(file, code);

            var s = new CSharpScanner(dir.FullName);
            s.ScanFile(file);
            return (s.References.ToList(), s.DynamicSites.ToList());
        }
        finally { dir.Delete(true); }
    }

    [Theory]
    [InlineData("""var b = el.FindResource("Key1");""", "Key1")]
    [InlineData("""var b = el.TryFindResource("Key1");""", "Key1")]
    [InlineData("""var b = Application.Current.Resources["Key1"];""", "Key1")]
    [InlineData("""el.SetResourceReference(Control.BackgroundProperty, "Key1");""", "Key1")]
    // Avalonia
    [InlineData("""if (this.TryFindResource("Key1", out var v)) { }""", "Key1")]
    [InlineData("""if (this.TryFindResource("Key1", ThemeVariant.Dark, out var v)) { }""", "Key1")]
    [InlineData("""if (res.TryGetResource("Key1", ThemeVariant.Dark, out var v)) { }""", "Key1")]
    [InlineData("""var o = this.GetResourceObservable("Key1");""", "Key1")]
    [InlineData("""el[!Border.BackgroundProperty] = new DynamicResourceExtension("Key1");""", "Key1")]
    // .NET MAUI
    [InlineData("""label.SetDynamicResource(Label.TextColorProperty, "Key1");""", "Key1")]
    [InlineData("""Application.Current.Resources.TryGetValue("Key1", out var c);""", "Key1")]
    // 左括號後面有空白：還是字面查找，不能被算成執行期查找
    [InlineData("""var b = el.FindResource( "Key1" );""", "Key1")]
    public void Reads_literal_lookups(string code, string expected)
    {
        var (refs, dyn) = Scan(code);
        Assert.Equal(expected, Assert.Single(refs).Key);
        Assert.Empty(dyn);
    }

    [Theory]
    [InlineData("IconText.SetResourceReference(TextBlock.ForegroundProperty, ic.BrushKey);")]
    [InlineData("label.SetDynamicResource(Label.TextColorProperty, key);")]
    [InlineData("if (this.TryFindResource(key, out var v)) { }")]
    [InlineData("if (res.TryGetResource(name, ThemeVariant.Dark, out var v)) { }")]
    [InlineData("Application.Current.Resources.TryGetValue(key, out var c);")]
    [InlineData("var b = Application.Current.Resources[key];")]
    public void A_lookup_by_variable_is_a_dynamic_site(string code)
    {
        var (refs, dyn) = Scan(code);
        Assert.Empty(refs);
        Assert.Single(dyn);
    }

    [Theory]
    [InlineData("SetResourceReference(StyleProperty, typeof(ScrollViewer));")]     // 隱含樣式：鍵是型別
    [InlineData("Application.Current.Resources[key] = brush;")]                    // 寫入，不是查找
    public void Things_that_look_like_lookups_but_are_not(string code)
    {
        var (refs, dyn) = Scan(code);
        Assert.Empty(refs);
        Assert.Empty(dyn);
    }

    [Fact]
    public void A_composed_key_is_a_dynamic_site_not_a_reference()
    {
        // ⚠ 這一條的反面是本工具最危險的失敗:把 $"Icon{n}" 當成「沒有引用」,
        //   於是 IconSave / IconOpen… 全部被報成沒人用,使用者照著刪就炸了。
        var (refs, dyn) = Scan("""var b = el.FindResource($"Icon{name}");""");

        Assert.Empty(refs);
        Assert.Single(dyn);
    }

    [Fact]
    public void A_variable_key_is_a_dynamic_site()
    {
        var (refs, dyn) = Scan("var b = el.FindResource(keyName);");
        Assert.Empty(refs);
        Assert.Single(dyn);
    }

    [Fact]
    public void Commented_out_lookups_are_not_references()
    {
        var (refs, _) = Scan("""
            // el.FindResource("InAComment");
            /* el.FindResource("InABlockComment"); */
            var real = el.FindResource("Real");
            """);

        Assert.Equal("Real", Assert.Single(refs).Key);
    }

    [Fact]
    public void A_url_inside_a_string_is_not_treated_as_a_comment()
    {
        // StripComments 必須看得懂字串:否則 "https://x" 的 // 會把後面整行吃掉
        var (refs, _) = Scan("""
            var url = "https://example.com/a";
            var b = el.FindResource("AfterTheUrl");
            """);

        Assert.Equal("AfterTheUrl", Assert.Single(refs).Key);
    }

    [Fact]
    public void Verbatim_strings_survive_comment_stripping()
    {
        var (refs, _) = Scan("""
            var p = @"C:\x\y";
            var b = el.FindResource("AfterVerbatim");
            """);

        Assert.Equal("AfterVerbatim", Assert.Single(refs).Key);
    }

    [Fact]
    public void Line_numbers_survive_comment_stripping()
    {
        // 註解換成等長空白而不是刪掉,就是為了讓行號不漂
        var (refs, _) = Scan("""
            // line 1
            /* line 2
               line 3 */
            var b = el.FindResource("OnLineFour");
            """);

        Assert.Equal(4, Assert.Single(refs).Location.Line);
    }

    [Fact]
    public void Xaml_paths_in_code_are_collected()
    {
        var dir = Directory.CreateTempSubdirectory("xamlkeys-cs2");
        try
        {
            var file = Path.Combine(dir.FullName, "T.cs");
            File.WriteAllText(file, """
                var d = new ResourceDictionary { Source = new Uri("Themes/DarkTheme.xaml", UriKind.Relative) };
                """);

            var s = new CSharpScanner(dir.FullName);
            s.ScanFile(file);

            Assert.Contains(s.LoadedDictionaryPaths, e => e.Path == "Themes/DarkTheme.xaml");
        }
        finally { dir.Delete(true); }
    }

    // ── 用變數查找的檔裡，跟鍵同名的字串 ─────────────────────────────────

    [Fact]
    public void A_key_handed_to_a_variable_lookup_in_the_same_file_is_not_reported_as_unused()
    {
        // Kindling 實際用的時候抓到的：Blue 被說成沒人用，照做的話資訊對話框的圖示就沒顏色了。
        // 同時守住反面：主題服務把整張色盤**寫進**資源，那些鍵名不能因此全部變成「有人用」。
        var dir = Directory.CreateTempSubdirectory("xamlkeys-near");
        try
        {
            void Put(string rel, string content)
            {
                var path = Path.Combine(dir.FullName, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, content);
            }

            const string ns = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
                              "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";

            Put("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup></Project>");
            Put("App.xaml", $"""
                <Application {ns}>
                  <Application.Resources>
                    <SolidColorBrush x:Key="Blue" Color="#378ADD" />
                    <SolidColorBrush x:Key="Red" Color="#E24B4A" />
                    <SolidColorBrush x:Key="AmberBorder" Color="#5A4A1E" />
                  </Application.Resources>
                </Application>
                """);
            Put("Main.xaml", $$"""<Window {{ns}}><Border Background="{DynamicResource Red}" /></Window>""");
            Put("MessageDialog.xaml.cs", """
                (string Glyph, string BrushKey)? icon = image switch
                {
                    MessageBoxImage.Error       => ("x", "Red"),
                    MessageBoxImage.Information => ("i", "Blue"),
                    _ => null,
                };
                IconText.SetResourceReference(TextBlock.ForegroundProperty, icon.Value.BrushKey);
                """);
            Put("ThemeService.cs", """
                static readonly (string Key, string Dark, string Light)[] Palette =
                {
                    ("Blue", "#378ADD", "#1D6FC4"),
                    ("AmberBorder", "#5A4A1E", "#E6D8A8"),
                };
                foreach (var (key, dark, light) in Palette) Application.Current.Resources[key] = Make(dark);
                """);

            var r = Analyzer.Run(dir.FullName, new Config());

            Assert.DoesNotContain(r.Findings, f => f.Kind == FindingKind.Unused && f.Key == "Blue");
            Assert.Contains(r.Findings, f => f.Kind == FindingKind.Unused && f.Key == "AmberBorder");
            // Red 本來就被 XAML 引用了，不算這條規則救回來的
            Assert.Equal(1, r.KeysNamedNearDynamicLookups);
            Assert.Single(r.DynamicKeySites);
        }
        finally { dir.Delete(true); }
    }
}
