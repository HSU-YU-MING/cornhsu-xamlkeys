using System.Text.RegularExpressions;
using XamlKeys.Core;

namespace XamlKeys.Tests;

/// <summary>
/// HTML 報告的結構合約。
///
/// ⚠ 這份報告刻意**沒有 script**、**沒有外部資源**、**沒有時間戳**：
///   CI 的 artifact 常常在嚴格的 CSP 下開啟（有 script 就是一片空白），
///   而沒有時間戳才 diff 得動。這三條每一條都有測試守著。
/// </summary>
public class HtmlReportTests
{
    private static string Html() => ReportHtml.ToHtml(Analyzer.Run(TestPaths.Demo, new Config()), "0.0.0-test");

    [Fact]
    public void It_is_a_single_self_contained_file()
    {
        var h = Html();
        Assert.DoesNotContain("<script", h, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("src=", h, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("href=\"http", h, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@import", h, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("<!doctype html>", h, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<meta charset=\"utf-8\">", h);
    }

    [Fact]
    public void The_same_input_produces_the_same_file()
    {
        // 有時間戳的話就沒辦法 diff 兩次執行的結果
        Assert.Equal(Html(), Html());
    }

    [Fact]
    public void Every_finding_becomes_a_row_tagged_with_its_severity()
    {
        var r = Analyzer.Run(TestPaths.Demo, new Config());
        var h = ReportHtml.ToHtml(r, "0.0.0-test");

        Assert.Equal(r.Findings.Count, Regex.Matches(h, "<tr class=\"").Count);

        foreach (var sev in new[] { Severity.Fail, Severity.Warn, Severity.Info })
            Assert.Equal(r.CountSeverity(sev),
                         Regex.Matches(h, $"<tr class=\"{Finding.SeveritySlug(sev)}\"").Count);
    }

    [Fact]
    public void The_filter_checkboxes_come_before_main_so_the_css_can_reach_the_rows()
    {
        // 純 CSS 篩選靠兄弟選擇器（~）：checkbox 一定要在 main 前面，順序也要是 fail→warn→info
        var h = Html();
        var fail = h.IndexOf("id=\"f-fail\"", StringComparison.Ordinal);
        var warn = h.IndexOf("id=\"f-warn\"", StringComparison.Ordinal);
        var info = h.IndexOf("id=\"f-info\"", StringComparison.Ordinal);
        var main = h.IndexOf("<main>", StringComparison.Ordinal);

        Assert.True(fail > 0 && warn > fail && info > warn, "the toggles must be in fail, warn, info order");
        Assert.True(info < main, "the toggles must precede <main>");
    }

    [Fact]
    public void A_section_with_only_one_severity_is_hidden_when_that_severity_is_filtered_out()
    {
        // 篩成「只看 fail」時不該剩下一頁空的檔名 —— 這條守住那七條組合規則還在
        var h = Html();
        Assert.Contains("#f-warn:not(:checked) ~ main section:not(:has(tr.fail)):not(:has(tr.info))", h);
        Assert.Contains("#f-fail:not(:checked) ~ #f-warn:not(:checked) ~ #f-info:not(:checked) ~ main section", h);
    }

    [Fact]
    public void User_data_is_html_escaped()
    {
        // 資源鍵可以含有 < 與 &；不轉義的話報告會壞掉
        var dir = Directory.CreateTempSubdirectory("xamlkeys-html");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "App.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(dir.FullName, "App.xaml"), """
                <Application xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                  <Application.Resources><ResourceDictionary>
                    <x:String x:Key="a&lt;b&amp;c">v</x:String>
                  </ResourceDictionary></Application.Resources>
                </Application>
                """);

            var h = ReportHtml.ToHtml(Analyzer.Run(dir.FullName, new Config()), "0.0.0-test");

            Assert.Contains("a&lt;b&amp;c", h);
            Assert.DoesNotContain("<code>a<b", h);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void What_the_run_could_not_see_is_on_the_page()
    {
        // 盲區要在最前面，不是埋在最後 —— 它是「unused 可不可信」的前提
        var h = Html();
        Assert.Contains("What this run could not see", h);
        Assert.Contains("could not be read", h);
    }

    [Fact]
    public void An_untrustworthy_result_says_so_on_the_page()
    {
        var h = Html();   // demo 的覆蓋率是 2/3，低於預設的 80%
        Assert.Contains("Result is not trustworthy", h);
    }

    [Fact]
    public void A_clean_project_gets_a_clean_page()
    {
        var dir = Directory.CreateTempSubdirectory("xamlkeys-html2");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "App.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(dir.FullName, "App.xaml"), """
                <Application xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                  <Application.Resources><ResourceDictionary>
                    <x:String x:Key="Used">v</x:String>
                  </ResourceDictionary></Application.Resources>
                </Application>
                """);
            File.WriteAllText(Path.Combine(dir.FullName, "Main.xaml"), """
                <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                  <TextBlock Text="{StaticResource Used}" />
                </Window>
                """);

            var h = ReportHtml.ToHtml(Analyzer.Run(dir.FullName, new Config()), "0.0.0-test");

            Assert.Contains("No findings.", h);
            Assert.DoesNotContain("What this run could not see", h);
        }
        finally { dir.Delete(true); }
    }
}
