using System.Text.Json;
using XamlKeys.Core;

namespace XamlKeys.Tests;

/// <summary>輸出合約：JSON 形狀、ASCII、SARIF、MSBuild 格式。</summary>
public class OutputContractTests
{
    private static AnalysisResult Demo() => Analyzer.Run(TestPaths.Demo, new Config());

    [Fact]
    public void All_console_and_report_text_is_ascii()
    {
        // ⚠ 回歸守門。第一版在訊息裡用了破折號,Windows 主控台（cp950）把它印成亂碼。
        //   規劃書的規矩是「輸出一律英文」—— 這條測試把它收緊成「一律 ASCII」。
        var r = Demo();

        var console = new StringWriter();
        Report.WriteConsole(console, r, showInfo: true);
        AssertAscii(console.ToString(), "console");

        var msbuild = new StringWriter();
        Report.WriteMsBuild(msbuild, r);
        AssertAscii(msbuild.ToString(), "msbuild");

        AssertAscii(Report.ToJson(r, "0.0.0-test"), "json");
        AssertAscii(ReportSarif.ToSarif(r, "0.0.0-test"), "sarif");
        // Markdown 用 GitHub 的 :x: 短碼，不放 emoji（跟其他輸出同一條規矩）
        AssertAscii(ReportMarkdown.ToMarkdown(r, "0.0.0-test", 1), "markdown");
        AssertAscii(ReportMarkdown.ToMarkdown(r, "0.0.0-test", 3), "markdown");
    }

    private static void AssertAscii(string text, string what)
    {
        foreach (var (line, i) in text.Split('\n').Select((l, i) => (l, i)))
            foreach (var c in line)
                Assert.True(c < 128,
                    $"{what} line {i + 1} contains a non-ASCII character U+{(int)c:X4}: {line.Trim()}");
    }

    [Fact]
    public void Json_has_both_a_summary_and_findings()
    {
        // CI 只消費 findings 的話,「有一本字典沒讀到」等於不存在 ——
        // 而那正是「unused 可不可信」的前提。
        using var doc = JsonDocument.Parse(Report.ToJson(Demo(), "0.0.0-test"));
        var root = doc.RootElement;

        Assert.Equal(Report.SchemaVersion, root.GetProperty("schemaVersion").GetInt32());
        Assert.True(root.TryGetProperty("summary", out var summary));
        Assert.True(root.TryGetProperty("findings", out _));

        foreach (var field in new[] { "coverage", "keysDefined", "references", "suppressed",
                                      "externalSources", "dynamicKeySites", "counts", "dialect" })
            Assert.True(summary.TryGetProperty(field, out _), $"summary.{field} is part of the contract");
    }

    [Fact]
    public void Json_counts_match_the_findings_array()
    {
        var r = Demo();
        using var doc = JsonDocument.Parse(Report.ToJson(r, "0.0.0-test"));

        var counts = doc.RootElement.GetProperty("summary").GetProperty("counts");
        var total = counts.EnumerateObject().Sum(p => p.Value.GetInt32());

        Assert.Equal(r.Findings.Count, total);
        Assert.Equal(r.Findings.Count, doc.RootElement.GetProperty("findings").GetArrayLength());
    }

    [Fact]
    public void Sarif_omits_info_level_findings()
    {
        // info 級（external）是診斷資訊。灌進 GitHub code scanning 只會變成噪音。
        var r = Demo();
        using var doc = JsonDocument.Parse(ReportSarif.ToSarif(r, "0.0.0-test"));

        var results = doc.RootElement.GetProperty("runs")[0].GetProperty("results");
        Assert.Equal(r.Findings.Count(f => f.Severity != Severity.Info), results.GetArrayLength());
    }

    [Fact]
    public void Every_finding_kind_has_a_distinct_rule_id()
    {
        var ids = Enum.GetValues<FindingKind>().Select(Report.RuleId).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(ids, id => Assert.StartsWith("XK", id));
    }

    [Fact]
    public void MsBuild_lines_carry_file_line_level_and_rule_id()
    {
        var w = new StringWriter();
        Report.WriteMsBuild(w, Demo());

        var line = w.ToString().Split('\n').First(l => l.Contains("XK0001"));
        Assert.Matches(@"^.+\(\d+\): error XK0001: .+$", line.Trim());
    }

    [Fact]
    public void File_paths_in_findings_are_relative_and_use_forward_slashes()
    {
        // baseline 要跨 OS 可攜
        foreach (var f in Demo().Findings)
        {
            Assert.False(Path.IsPathRooted(f.Location.File), $"{f.Location.File} should be relative");
            Assert.DoesNotContain('\\', f.Location.File);
        }
    }

    [Fact]
    public void Baseline_keys_do_not_contain_line_numbers()
    {
        // 行號會隨無關的編輯漂移,一次格式化就讓整份 baseline 失效
        foreach (var f in Demo().Findings)
            Assert.DoesNotContain($":{f.Location.Line}", f.BaselineKey);
    }
}
