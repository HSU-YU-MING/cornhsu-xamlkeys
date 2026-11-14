using System.Diagnostics;
using System.Text;

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
}
