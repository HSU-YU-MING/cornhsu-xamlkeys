using XamlKeys.Core;

namespace XamlKeys.Tests;

/// <summary>
/// markup extension 掃描。每一條都對應一個真實寫法 —— 漏掉任何一種，
/// 那個鍵就會被報成「沒人用」，而使用者會照著刪。
/// </summary>
public class MarkupExtensionTests
{
    private static List<string?> Keys(string value) =>
        MarkupExtensions.Scan(value).Select(r => r.Key).ToList();

    [Theory]
    [InlineData("{StaticResource Foo}", "Foo")]
    [InlineData("{DynamicResource Foo}", "Foo")]
    [InlineData("{ThemeResource Foo}", "Foo")]
    [InlineData("{StaticResource ResourceKey=Foo}", "Foo")]
    [InlineData("{StaticResource  Foo }", "Foo")]
    [InlineData("{StaticResourceExtension Foo}", "Foo")]
    public void Reads_the_simple_forms(string value, string expected) =>
        Assert.Equal([expected], Keys(value));

    [Fact]
    public void Finds_a_reference_nested_in_a_binding()
    {
        // 轉換器寫在 Binding 裡是最常見的巢狀形狀
        var keys = Keys("{Binding Name, Converter={StaticResource BoolToVis}}");
        Assert.Equal(["BoolToVis"], keys);
    }

    [Fact]
    public void Finds_several_references_in_one_value()
    {
        var keys = Keys("{Binding X, Converter={StaticResource A}, ConverterParameter={StaticResource B}}");
        Assert.Equal(["A", "B"], keys);
    }

    [Fact]
    public void A_nested_extension_as_the_key_is_not_a_string_key()
    {
        // {StaticResource {x:Type Button}} 是隱含樣式的顯式寫法,鍵不是字串 -> 不可知,不是懸空
        var refs = MarkupExtensions.Scan("{StaticResource {x:Type Button}}");
        Assert.Single(refs);
        Assert.Null(refs[0].Key);
    }

    [Fact]
    public void A_static_member_key_is_not_a_string_key()
    {
        var refs = MarkupExtensions.Scan("{StaticResource {x:Static SystemColors.ControlBrushKey}}");
        Assert.Single(refs);
        Assert.Null(refs[0].Key);
    }

    [Fact]
    public void The_escape_sequence_makes_the_whole_value_literal()
    {
        // XAML 的 {} 跳脫:整個值是字面文字
        Assert.Empty(Keys("{}{StaticResource NotAReference}"));
    }

    [Fact]
    public void Quoted_keys_lose_their_quotes()
    {
        Assert.Equal(["My Brush"], Keys("{StaticResource 'My Brush'}"));
    }

    [Fact]
    public void Other_named_arguments_are_not_mistaken_for_the_key()
    {
        // TargetType= 不是鍵;ResourceKey= 才是
        Assert.Equal(["Foo"], Keys("{StaticResource TargetType=Button, ResourceKey=Foo}"));
    }

    [Fact]
    public void A_plain_value_is_not_a_reference()
    {
        Assert.Empty(Keys("#FF0000"));
        Assert.Empty(Keys("{Binding Path=Name}"));
        Assert.Empty(Keys(""));
    }

    [Fact]
    public void An_unclosed_extension_is_ignored_rather_than_guessed()
    {
        Assert.Empty(Keys("{StaticResource Foo"));
    }
}
