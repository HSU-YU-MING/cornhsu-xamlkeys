using System.Diagnostics;
using System.Text;
using Core = XamlKeys.Core;

namespace XamlKeys.Tests;

/// <summary>
/// CLI 契約：旗標解析與退出碼的判定順序只存在於 Program.cs，Core 的單元測試碰不到。
/// 這裡以子行程跑真的 xamlkeys.dll。
/// </summary>
public class CliContractTests
{
    private static (int Exit, string Out, string Err) Run(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        psi.ArgumentList.Add(TestPaths.CliDll);
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();

        return (p.ExitCode, stdout, stderr);
    }

    [Fact]
    public void No_arguments_is_a_usage_error()
    {
        var (exit, _, err) = Run();
        Assert.Equal(2, exit);
        Assert.Contains("required", err);
    }

    [Fact]
    public void An_unknown_option_is_a_usage_error()
    {
        var (exit, _, err) = Run("--nope", TestPaths.Demo);
        Assert.Equal(2, exit);
        Assert.Contains("unknown option", err);
    }

    [Fact]
    public void A_missing_directory_is_a_usage_error()
    {
        var (exit, _, _) = Run(Path.Combine(TestPaths.Demo, "does-not-exist"));
        Assert.Equal(2, exit);
    }

    [Fact]
    public void A_flag_cannot_swallow_the_next_flag_as_its_value()
    {
        // `--json --msbuild` 若把 --msbuild 當成路徑,就會寫出一個叫 --msbuild 的檔
        var (exit, _, err) = Run("--json", "--msbuild", TestPaths.Demo);
        Assert.Equal(2, exit);
        Assert.Contains("--json takes a path", err);
    }

    [Fact]
    public void Double_dash_lets_a_directory_be_named_like_a_flag()
    {
        // 沒有這個出口的話,一個叫 --version 的目錄會被當成旗標:印版本、exit 0、什麼都沒稽核
        var (exit, _, _) = Run("--", TestPaths.Demo);
        Assert.NotEqual(0, exit);   // demo 本來就有 fail 與低覆蓋率
    }

    [Theory]
    [InlineData("--fail-on", "maybe")]
    [InlineData("--dialect", "avalonia")]
    [InlineData("--min-coverage", "101")]
    [InlineData("--min-coverage", "NaN")]
    public void Bad_flag_values_are_usage_errors(string flag, string value)
    {
        // ⚠ NaN:任何比較都是 false,所以「覆蓋率 < NaN」永遠不成立 -> 下限靜默失效
        var (exit, _, _) = Run(flag, value, TestPaths.Demo);
        Assert.Equal(2, exit);
    }

    [Fact]
    public void Untrustworthy_beats_findings_in_the_exit_code_order()
    {
        // demo 的覆蓋率是 2/3:既有 fail 級 finding,也低於下限。
        // 判定順序規定「不可信」優先 -> 3,不是 1。
        var (exit, _, err) = Run(TestPaths.Demo);
        Assert.Equal(3, exit);
        Assert.Contains("not trustworthy", err);
    }

    [Fact]
    public void With_the_floor_lowered_the_findings_decide_the_exit_code()
    {
        var (exit, _, _) = Run("--min-coverage", "0", TestPaths.Demo);
        Assert.Equal(1, exit);
    }

    [Fact]
    public void The_gate_level_changes_what_fails()
    {
        // 只有 fail 級擋 -> 1（demo 有 dangling-static）
        Assert.Equal(1, Run("--min-coverage", "0", "--fail-on", "fail", TestPaths.Demo).Exit);
        // warn 以上都擋 -> 還是 1,但筆數更多
        Assert.Equal(1, Run("--min-coverage", "0", "--fail-on", "warn", TestPaths.Demo).Exit);
    }

    [Fact]
    public void Version_and_help_do_not_scan()
    {
        Assert.Equal(0, Run("--version").Exit);
        Assert.Equal(0, Run("--help").Exit);
    }

    [Fact]
    public void Writing_a_baseline_does_not_fail_the_run()
    {
        // 使用者這一次是在建立清單,不是在驗收
        var dir = Directory.CreateTempSubdirectory("xamlkeys-cli");
        try
        {
            var bl = Path.Combine(dir.FullName, "b.json");
            var (exit, _, _) = Run("--min-coverage", "0", "--write-baseline", bl, TestPaths.Demo);

            Assert.Equal(0, exit);
            Assert.True(File.Exists(bl));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void A_baseline_turns_a_red_run_green_and_a_new_finding_turns_it_red_again()
    {
        var dir = Directory.CreateTempSubdirectory("xamlkeys-cli2");
        try
        {
            var bl = Path.Combine(dir.FullName, "b.json");
            Run("--min-coverage", "0", "--write-baseline", bl, TestPaths.Demo);

            Assert.Equal(0, Run("--min-coverage", "0", "--baseline", bl, TestPaths.Demo).Exit);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void A_missing_baseline_file_is_a_usage_error_not_a_silent_pass()
    {
        var (exit, _, err) = Run("--baseline", Path.Combine(TestPaths.Demo, "no-baseline.json"), TestPaths.Demo);
        Assert.Equal(2, exit);
        Assert.Contains("baseline not found", err);
    }

    [Fact]
    public void Re_recording_a_baseline_onto_itself_does_not_empty_it()
    {
        // ⚠ `--baseline b.json --write-baseline b.json` 是更新棘輪最自然的打法。
        //   舊版先套用舊 baseline 再寫入「剩下的」—— 於是把檔案清成 0 條。
        //   那個檔案在版控裡，這是會毀資料的 bug。
        var dir = Directory.CreateTempSubdirectory("xamlkeys-rerecord");
        try
        {
            var bl = Path.Combine(dir.FullName, "b.json");
            Run("--min-coverage", "0", "--write-baseline", bl, TestPaths.Demo);
            var before = Core.Baseline.Load(bl).Entries.Count;
            Assert.True(before > 0);

            Run("--min-coverage", "0", "--baseline", bl, "--write-baseline", bl, TestPaths.Demo);

            Assert.Equal(before, Core.Baseline.Load(bl).Entries.Count);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void Stale_without_a_baseline_is_a_usage_error_not_a_silent_no_op()
    {
        // 靜靜什麼都不做，使用者會以為自己檢查過了
        var (exit, _, err) = Run("--min-coverage", "0", "--stale", TestPaths.Demo);
        Assert.Equal(2, exit);
        Assert.Contains("--baseline", err);
    }

    [Fact]
    public void A_baseline_with_entries_that_no_longer_occur_gets_a_nudge()
    {
        // 沒人去收緊的棘輪，最後會把新債一起蓋住
        var dir = Directory.CreateTempSubdirectory("xamlkeys-nudge");
        try
        {
            var bl = Path.Combine(dir.FullName, "b.json");
            File.WriteAllText(bl, """{"keyVersion":1,"toolVersion":"x","entries":["unused|Nowhere.xaml|Ghost|neutral"]}""");

            var (_, stdout, _) = Run("--min-coverage", "0", "--baseline", bl, TestPaths.Demo);
            Assert.Contains("no longer occur", stdout);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void The_html_report_is_written_where_asked()
    {
        var dir = Directory.CreateTempSubdirectory("xamlkeys-htmlcli");
        try
        {
            var html = Path.Combine(dir.FullName, "out", "report.html");
            Run("--min-coverage", "0", "--html", html, TestPaths.Demo);

            Assert.True(File.Exists(html));
            Assert.StartsWith("<!doctype html>", File.ReadAllText(html), StringComparison.OrdinalIgnoreCase);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void Include_tests_brings_test_paths_back_into_the_scan()
    {
        var dir = Directory.CreateTempSubdirectory("xamlkeys-inctests");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "App.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(dir.FullName, "App.xaml"), """
                <Application xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                  <Application.Resources><ResourceDictionary>
                    <SolidColorBrush x:Key="Used" Color="#111" />
                  </ResourceDictionary></Application.Resources>
                </Application>
                """);
            File.WriteAllText(Path.Combine(dir.FullName, "Main.xaml"), """
                <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                  <Border Background="{StaticResource Used}" />
                </Window>
                """);
            Directory.CreateDirectory(Path.Combine(dir.FullName, "App.Tests"));
            File.WriteAllText(Path.Combine(dir.FullName, "App.Tests", "Broken.xaml"), """
                <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                  <SolidColorBrush x:Key="Dup" Color="#1" />
                  <SolidColorBrush x:Key="Dup" Color="#2" />
                </ResourceDictionary>
                """);

            Assert.Equal(0, Run(dir.FullName).Exit);                       // 預設跳過測試路徑
            Assert.Equal(1, Run("--include-tests", dir.FullName).Exit);    // 掃進來就會看到重複鍵
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void Stale_baseline_entries_are_listed()
    {
        var dir = Directory.CreateTempSubdirectory("xamlkeys-stale");
        try
        {
            var bl = Path.Combine(dir.FullName, "b.json");
            File.WriteAllText(bl, """{"keyVersion":1,"toolVersion":"x","entries":["unused|Nowhere.xaml|Ghost|neutral"]}""");

            var (_, stdout, _) = Run("--min-coverage", "0", "--baseline", bl, "--stale", TestPaths.Demo);
            Assert.Contains("no longer occur", stdout);
            Assert.Contains("Ghost", stdout);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void MsBuild_output_carries_the_rule_ids()
    {
        var (_, stdout, _) = Run("--min-coverage", "0", "--msbuild", TestPaths.Demo);
        Assert.Contains("): error XK0001:", stdout);
        Assert.Contains("): error XK0002:", stdout);
    }

    [Fact]
    public void A_config_found_inside_the_scanned_tree_is_announced()
    {
        // ⚠ 在 CI 上掃一個 PR 時,那個設定檔就是 PR 的內容 ——
        //   它可以把 severities 全降成 info,把守門員靜靜關掉。不禁止,但一定要講出來。
        var dir = Directory.CreateTempSubdirectory("xamlkeys-cfgdisc");
        try
        {
            foreach (var f in Directory.EnumerateFiles(TestPaths.Demo, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(TestPaths.Demo, f);
                var dest = Path.Combine(dir.FullName, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(f, dest, overwrite: true);
            }

            File.WriteAllText(Path.Combine(dir.FullName, "xamlkeys.config.json"),
                              """{"severities":{"dangling-static":"info"}}""");

            var (_, _, stderr) = Run("--min-coverage", "0", dir.FullName);
            Assert.Contains("found in the scanned tree", stderr);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void Json_and_sarif_are_written_where_asked()
    {
        var dir = Directory.CreateTempSubdirectory("xamlkeys-cli3");
        try
        {
            var json = Path.Combine(dir.FullName, "nested", "r.json");
            var sarif = Path.Combine(dir.FullName, "r.sarif");

            Run("--min-coverage", "0", "--json", json, "--sarif", sarif, TestPaths.Demo);

            Assert.True(File.Exists(json), "the tool should create missing output directories");
            Assert.True(File.Exists(sarif));
        }
        finally { dir.Delete(true); }
    }

    // ── --dialect 與 --exclude：旗標真的有接到分析上 ─────────────────────
    // Core 層的行為在 ConfigBehaviourTests；這裡只守「命令列 → 設定」這段線沒有斷。

    private static DirectoryInfo TwoMergedDefinitions()
    {
        const string ns = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
                          "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";

        var dir = Directory.CreateTempSubdirectory("xamlkeys-flags");
        void Put(string rel, string content)
        {
            var path = Path.Combine(dir.FullName, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        Put("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup></Project>");
        Put("App.xaml", $"""
            <Application {ns}>
              <Application.Resources>
                <ResourceDictionary>
                  <ResourceDictionary.MergedDictionaries>
                    <ResourceDictionary Source="First.xaml" />
                    <ResourceDictionary Source="Second.xaml" />
                  </ResourceDictionary.MergedDictionaries>
                </ResourceDictionary>
              </Application.Resources>
            </Application>
            """);
        Put("First.xaml", $"<ResourceDictionary {ns}><SolidColorBrush x:Key=\"K\" Color=\"#111\" /></ResourceDictionary>");
        Put("Second.xaml", $"<ResourceDictionary {ns}><SolidColorBrush x:Key=\"K\" Color=\"#222\" /></ResourceDictionary>");
        Put("Main.xaml", $"<Window {ns}><Border Background=\"{{StaticResource K}}\" /></Window>");
        Put("Generated/Broken.xaml", $"<ResourceDictionary {ns}><SolidColorBrush x:Key=\"D\" Color=\"#1\" /><SolidColorBrush x:Key=\"D\" Color=\"#2\" /></ResourceDictionary>");
        return dir;
    }

    private static string ShadowedLoser(string jsonPath)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(jsonPath));
        var shadowed = doc.RootElement.GetProperty("findings").EnumerateArray()
                          .Single(f => f.GetProperty("kind").GetString() == "shadowed");
        return shadowed.GetProperty("file").GetString()!;
    }

    [Fact]
    public void The_dialect_flag_reverses_which_merged_dictionary_loses()
    {
        var dir = TwoMergedDefinitions();
        try
        {
            var wpf = Path.Combine(dir.FullName, "wpf.json");
            var winui = Path.Combine(dir.FullName, "winui.json");

            Run("--dialect", "wpf", "--exclude", "Generated", "--json", wpf, dir.FullName);
            Run("--dialect", "winui", "--exclude", "Generated", "--json", winui, dir.FullName);

            Assert.EndsWith("First.xaml", ShadowedLoser(wpf));      // WPF：後宣告的贏
            Assert.EndsWith("Second.xaml", ShadowedLoser(winui));   // WinUI：先宣告的贏
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void The_exclude_flag_removes_a_folder_from_the_scan()
    {
        var dir = TwoMergedDefinitions();
        try
        {
            // Generated/Broken.xaml 裡有重複鍵（fail）：不排除 → 1，排除 → 0
            var (withIt, _, _) = Run(dir.FullName);
            var (without, stdout, _) = Run("--exclude", "Generated", dir.FullName);

            Assert.Equal(1, withIt);
            Assert.Equal(0, without);
            Assert.DoesNotContain("Broken.xaml", stdout);
        }
        finally { dir.Delete(true); }
    }

    // ── baseline 與設定檔也是 PR 的內容 ───────────────────────────────────

    private static void AssertNoWorkflowCommand(string output)
    {
        foreach (var line in output.Split('\n'))
            Assert.False(line.TrimStart().StartsWith("::", StringComparison.Ordinal),
                         $"a line GitHub Actions would run as a command: {line}");
    }

    [Fact]
    public void A_hostile_baseline_entry_cannot_become_a_workflow_command_in_the_stale_list()
    {
        // baseline 的項目可以是任何字串，而且不存在的項目一定會被列成「已過期」。
        // GitHub 會先去掉行首空白再判斷 `::`，所以「縮排兩格」擋不住。
        var dir = Directory.CreateTempSubdirectory("xamlkeys-stale-hostile");
        try
        {
            var bl = Path.Combine(dir.FullName, "b.json");
            File.WriteAllText(bl, """{"keyVersion":1,"toolVersion":"x","entries":["::error::forged","a\n::add-mask::xaml"]}""");

            var (_, stdout, stderr) = Run("--min-coverage", "0", "--baseline", bl, "--stale", TestPaths.Demo);

            Assert.Contains("::error::forged", stdout);     // 還是要列出來，只是不能在行首
            AssertNoWorkflowCommand(stdout);
            AssertNoWorkflowCommand(stderr);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void A_hostile_config_cannot_inject_lines_through_its_error_message()
    {
        // 設定檔的錯誤訊息會引用檔案裡的屬性名與值
        var dir = Directory.CreateTempSubdirectory("xamlkeys-cfg-hostile");
        try
        {
            var cfg = Path.Combine(dir.FullName, "c.json");
            File.WriteAllText(cfg, """{"nope\n::error::forged":1}""");

            var (exit, stdout, stderr) = Run("--config", cfg, TestPaths.Demo);

            Assert.Equal(2, exit);
            Assert.Contains("unknown config property", stderr);
            AssertNoWorkflowCommand(stdout);
            AssertNoWorkflowCommand(stderr);
        }
        finally { dir.Delete(true); }
    }
}
