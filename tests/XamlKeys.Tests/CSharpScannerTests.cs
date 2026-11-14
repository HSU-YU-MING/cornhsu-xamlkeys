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
    public void Reads_literal_lookups(string code, string expected)
    {
        var (refs, _) = Scan(code);
        Assert.Equal(expected, Assert.Single(refs).Key);
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

            Assert.Contains("Themes/DarkTheme.xaml", s.LoadedDictionaryPaths);
        }
        finally { dir.Delete(true); }
    }
}
