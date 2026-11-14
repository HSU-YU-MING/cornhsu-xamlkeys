using XamlKeys.Core;

namespace XamlKeys.Tests;

/// <summary>
/// 對示範專案下計數斷言。
///
/// 這份示範是**刻意做壞的**：八類 finding 各有實例。對計數下斷言同時驗證了
/// 「工具真的會擋」跟「擋的數量沒有莫名其妙變動」—— 改了規則導致誤判變多，這裡立刻紅。
/// （做法照 XamlContrast 的 CI：exit code 加計數斷言。）
/// </summary>
public class DemoTests
{
    private static AnalysisResult Run(Config? cfg = null) => Analyzer.Run(TestPaths.Demo, cfg ?? new Config());

    [Fact]
    public void Every_finding_kind_has_at_least_one_instance()
    {
        var r = Run();
        foreach (FindingKind k in Enum.GetValues<FindingKind>())
            Assert.True(r.Count(k) > 0, $"the demo should exercise {Finding.KindSlug(k)} but produced none");
    }

    [Theory]
    [InlineData(FindingKind.DanglingStatic, 1)]
    [InlineData(FindingKind.DanglingDynamic, 1)]
    [InlineData(FindingKind.DuplicateKey, 1)]
    [InlineData(FindingKind.OutOfScope, 1)]
    [InlineData(FindingKind.Shadowed, 3)]
    [InlineData(FindingKind.ShadowedIdentical, 1)]
    [InlineData(FindingKind.Unused, 5)]
    [InlineData(FindingKind.OrphanDictionary, 1)]
    [InlineData(FindingKind.External, 1)]
    public void Counts_are_stable(FindingKind kind, int expected) =>
        Assert.Equal(expected, Run().Count(kind));

    [Fact]
    public void The_dangling_static_is_the_one_we_planted()
    {
        var f = Assert.Single(Run().Findings, f => f.Kind == FindingKind.DanglingStatic);
        Assert.Equal("MissingBrush", f.Key);
        Assert.Equal(Severity.Fail, f.Severity);
    }

    [Fact]
    public void The_shadowed_winner_is_the_last_merged_dictionary()
    {
        var f = Run().Findings.First(f => f.Kind == FindingKind.Shadowed && f.Key == "SharedBrush");
        Assert.Contains("Base.xaml", f.Location.File);          // 輸家
        Assert.Contains("Override.xaml", f.Related!.Value.File); // 贏家
    }

    [Fact]
    public void An_identical_duplicate_is_info_not_a_warning()
    {
        // 被一份一模一樣的定義蓋掉:結果正確,只是多餘。
        // 報成 warn 會淹掉真正有問題的那幾筆（實測 MaterialDesign 11 筆裡有 6 筆是這種）。
        var f = Assert.Single(Run().Findings, f => f.Kind == FindingKind.ShadowedIdentical);
        Assert.Equal("IdenticalInBoth", f.Key);
        Assert.Equal(Severity.Info, f.Severity);
    }

    [Fact]
    public void A_shadowed_definition_with_different_content_stays_a_warning()
    {
        var f = Run().Findings.First(f => f.Kind == FindingKind.Shadowed && f.Key == "SharedBrush");
        Assert.Equal(Severity.Warn, f.Severity);
    }

    [Fact]
    public void A_key_only_reached_from_code_is_not_reported_unused()
    {
        // CodeBehind.cs 用字面字串取 CodeOnlyBrush
        Assert.DoesNotContain(Run().Findings, f => f.Kind == FindingKind.Unused && f.Key == "CodeOnlyBrush");
    }

    [Fact]
    public void A_design_time_only_reference_is_ignored()
    {
        // d:Background="{StaticResource DesignOnlyBrush}" 不會進到執行期
        Assert.DoesNotContain(Run().Findings, f => f.Key == "DesignOnlyBrush");
    }

    [Fact]
    public void An_ignore_comment_suppresses_and_is_counted()
    {
        var r = Run();
        Assert.DoesNotContain(r.Findings, f => f.Key == "SuppressedUnused");
        Assert.True(r.Suppressed > 0, "a suppressed finding must still be counted in the summary");
    }

    [Fact]
    public void UsedAtRuntime_suppresses_the_composed_key_family()
    {
        var cfg = new Config();
        cfg.UsedAtRuntime.Add("Icon*");

        var r = Run(cfg);
        Assert.DoesNotContain(r.Findings, f => f.Kind == FindingKind.Unused && f.Key == "IconSave");
    }

    [Fact]
    public void The_unreadable_dictionary_pushes_coverage_below_the_floor()
    {
        var r = Run();
        Assert.True(r.Untrustworthy(out var reason), "an unreadable merged dictionary must make the result untrustworthy");
        Assert.Contains("resolved", reason);
    }

    [Fact]
    public void Raising_the_coverage_floor_off_makes_the_result_trustworthy_again()
    {
        var cfg = new Config { MinCoverage = 0 };
        Assert.False(Run(cfg).Untrustworthy(out _));
    }

    [Fact]
    public void A_composed_key_lookup_in_code_is_surfaced()
    {
        Assert.NotEmpty(Run().DynamicKeySites);
    }

    [Fact]
    public void A_non_string_key_is_counted_but_not_reported()
    {
        var r = Run();
        Assert.NotEmpty(r.UnresolvedSites);
        Assert.DoesNotContain(r.Findings, f => f.Key.Contains("x:Type"));
    }
}
