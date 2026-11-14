using XamlKeys.Core;

namespace XamlKeys.Tests;

public class ConfigAndBaselineTests
{
    private static Config LoadConfig(string json)
    {
        var dir = Directory.CreateTempSubdirectory("xamlkeys-cfg");
        try
        {
            var p = Path.Combine(dir.FullName, Config.DefaultFileName);
            File.WriteAllText(p, json);
            return Config.Load(p);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void An_unknown_property_is_an_error_not_a_shrug()
    {
        // ⚠ 打錯字的設定靜默失效,使用者會以為自己設了,實際上沒有。
        var ex = Assert.Throws<ConfigException>(() => LoadConfig("""{ "faiOn": "warn" }"""));
        Assert.Contains("faiOn", ex.Message);
    }

    [Theory]
    [InlineData("""{ "failOn": "nope" }""")]
    [InlineData("""{ "dialect": "avalonia" }""")]
    [InlineData("""{ "minCoverage": 101 }""")]
    [InlineData("""{ "minCoverage": "80" }""")]
    [InlineData("""{ "severities": { "unused": "critical" } }""")]
    [InlineData("""{ "severities": { "no-such-kind": "warn" } }""")]
    [InlineData("""{ "exclude": "not-an-array" }""")]
    [InlineData("""{ "externalDictionaries": [ { "assembly": "X" } ] }""")]
    public void Bad_values_name_the_field(string json)
    {
        var ex = Assert.Throws<ConfigException>(() => LoadConfig(json));
        Assert.NotEmpty(ex.Message);
    }

    [Fact]
    public void Severity_overrides_apply()
    {
        var cfg = LoadConfig("""{ "severities": { "unused": "info", "out-of-scope": "fail" } }""");

        Assert.Equal(Severity.Info, cfg.SeverityFor(FindingKind.Unused));
        Assert.Equal(Severity.Fail, cfg.SeverityFor(FindingKind.OutOfScope));
        Assert.Equal(Severity.Fail, cfg.SeverityFor(FindingKind.DanglingStatic));   // 沒覆寫的照預設
        Assert.True(cfg.HasOverrides);
    }

    [Fact]
    public void A_default_config_reports_no_overrides()
    {
        Assert.False(new Config().HasOverrides);
    }

    [Fact]
    public void Dialect_set_explicitly_is_not_auto_detected()
    {
        Assert.False(LoadConfig("""{ "dialect": "winui" }""").DialectAutoDetected);
        Assert.True(new Config().DialectAutoDetected);
    }

    [Theory]
    [InlineData("IconSave", "Icon*", true)]
    [InlineData("Icon", "Icon*", true)]
    [InlineData("Ico", "Icon*", false)]
    [InlineData("Exact", "Exact", true)]
    [InlineData("Exactly", "Exact", false)]
    public void Glob_matching(string value, string pattern, bool expected) =>
        Assert.Equal(expected, Config.MatchesGlob(value, pattern));

    // ── baseline ───────────────────────────────────────────────────────────

    [Fact]
    public void A_baseline_hides_known_debt_and_counts_it()
    {
        var first = Analyzer.Run(TestPaths.Demo, new Config());
        var baseline = Baseline.FromFindings(first.Findings);

        var second = Analyzer.Run(TestPaths.Demo, new Config());
        var before = second.Findings.Count;
        var removed = baseline.Apply(second);

        Assert.Equal(before, removed);
        Assert.Empty(second.Findings);
        Assert.True(second.Suppressed >= removed);
    }

    [Fact]
    public void A_baseline_from_a_different_key_version_is_rejected()
    {
        // 悄悄接受舊 baseline = 悄悄把真正的新債一起洗白
        var dir = Directory.CreateTempSubdirectory("xamlkeys-bl");
        try
        {
            var p = Path.Combine(dir.FullName, "b.json");
            File.WriteAllText(p, """{ "keyVersion": 0, "entries": [] }""");

            var ex = Assert.Throws<ConfigException>(() => Baseline.Load(p));
            Assert.Contains("keyVersion", ex.Message);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void A_baseline_round_trips()
    {
        var r = Analyzer.Run(TestPaths.Demo, new Config());
        var dir = Directory.CreateTempSubdirectory("xamlkeys-bl2");
        try
        {
            var p = Path.Combine(dir.FullName, "b.json");
            File.WriteAllText(p, Baseline.FromFindings(r.Findings).ToJson("0.0.0-test"));

            Assert.Equal(r.Findings.Count, Baseline.Load(p).Entries.Count);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void Stale_entries_are_reported_so_the_baseline_can_shrink()
    {
        var r = Analyzer.Run(TestPaths.Demo, new Config());
        var baseline = new Baseline { Entries = { "unused|Nowhere.xaml|Ghost|neutral" } };

        Assert.Contains("unused|Nowhere.xaml|Ghost|neutral", baseline.Stale(r.Findings));
    }

    [Fact]
    public void The_summary_says_what_the_baseline_hid_and_does_not_contradict_itself()
    {
        // 2026-11-14 在 QuillNest 照 README 導入時看到的兩句：
        //   "note: the 0 unused key name(s) cover 22 definition(s) ..."   ← 用的是過濾前的數字
        //   "note: 19 finding(s) suppressed by ignore comments or config" ← 其實是 baseline 濾掉的
        // 第二句會讓人去找根本不存在的 ignore 註解。
        var r = Analyzer.Run(TestPaths.Demo, new Config());
        var unused = r.Findings.Where(f => f.Kind == FindingKind.Unused).ToList();
        Assert.True(r.UnusedDefinitions > unused.Count, "the demo needs a key defined in more than one dictionary");

        Baseline.FromFindings(unused).Apply(r);

        Assert.Equal(unused.Count, r.BaselineSuppressed);
        Assert.Equal(0, r.UnusedDefinitions);

        var console = new StringWriter();
        Report.WriteConsole(console, r, showInfo: false);
        var text = console.ToString();

        Assert.DoesNotContain("unused finding(s) cover", text);
        Assert.Contains($"{unused.Count} known finding(s) hidden by the baseline", text);
        Assert.DoesNotContain($"{unused.Count} finding(s) suppressed by ignore comments", text);
    }
}
