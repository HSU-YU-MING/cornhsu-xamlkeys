using XamlKeys.Core;

namespace XamlKeys.Tests;

/// <summary>
/// 近似鍵名的建議。
///
/// ⚠ 這組測試裡**不該建議**的那幾條，比該建議的那幾條重要。
/// 錯的建議比沒有建議更糟 —— 它看起來像答案，使用者會照著改。
/// 全部來自真實語料（ScreenToGif、maui-samples、QuillNest）。
/// </summary>
public class NearMissTests
{
    [Theory]
    // 改名加後綴：QuillNest 的真實案例
    [InlineData("InverseBoolToVis", new[] { "InverseBoolToVisibilityConverter", "BoolToVisibilityConverter" },
                "InverseBoolToVisibilityConverter")]
    // 改名去後綴：ScreenToGif
    [InlineData("Bool2VisibilityConverter", new[] { "Bool2Visibility", "Vector.Save" }, "Bool2Visibility")]
    // maui-samples：引用寫 CardBackgroundColor，專案定義的是 CardBackground
    [InlineData("CardBackgroundColor", new[] { "CardBackground", "PageBackgroundColor" }, "CardBackground")]
    // 單純打錯一個字
    [InlineData("PrimaryBrsuh", new[] { "PrimaryBrush", "SecondaryBrush" }, "PrimaryBrush")]
    // 大小寫錯（XAML 的鍵區分大小寫，這是常見陷阱）
    [InlineData("primaryBrush", new[] { "PrimaryBrush" }, "PrimaryBrush")]
    public void Suggests_the_obvious_rename(string key, string[] candidates, string expected) =>
        Assert.Equal(expected, NearMiss.Suggest(key, candidates));

    [Theory]
    // ⚠ 以下全部**不該**建議。都是真實語料裡，收緊前會誤報的案例。
    //
    // 同一個命名空間底下的兄弟鍵：共同前綴把編輯距離壓小了
    [InlineData("Vector.Back", new[] { "Vector.Clock", "Vector.Fit" })]
    [InlineData("Style.Button.Horizontal.Fade", new[] { "Style.Button.Horizontal.White" })]
    // 共同後綴同理
    [InlineData("TextPrimaryColor", new[] { "LightPrimaryColor", "DarkPrimaryColor" })]
    // 完全不像
    [InlineData("AccentBrush", new[] { "WindowBackground", "FontSizeLarge" })]
    // 太短的鍵不給建議（雜訊比訊號多）
    [InlineData("Bg", new[] { "Fg" })]
    public void Stays_quiet_when_the_match_is_only_superficial(string key, string[] candidates) =>
        Assert.Null(NearMiss.Suggest(key, candidates));

    [Fact]
    public void Never_suggests_the_key_itself()
    {
        Assert.Null(NearMiss.Suggest("PrimaryBrush", ["PrimaryBrush"]));
    }

    [Fact]
    public void Picks_the_closest_candidate()
    {
        // Vector.Save.Project 跟 Vector.Save 的差距比跟 Vector 小
        Assert.Equal("Vector.Save",
                     NearMiss.Suggest("Vector.Save.Project", ["Vector.Save", "Vector.Save.Project.As.Something"]));
    }

    [Fact]
    public void An_empty_candidate_set_is_fine()
    {
        Assert.Null(NearMiss.Suggest("Anything", []));
    }
}
