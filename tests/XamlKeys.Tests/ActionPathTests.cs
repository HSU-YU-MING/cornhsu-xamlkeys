using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using XamlKeys.Core;

namespace XamlKeys.Tests;

/// <summary>
/// action.yml 把 finding 的 root 相對路徑補成 repo 路徑的那兩步（Annotate findings、Make SARIF paths repo-relative）。
/// 補錯了 GitHub 就對不上檔案：註記標不到 PR 的行上、code scanning 的位置是不存在的檔案 —— 而且兩者都不報錯。
/// 這裡用 runner 的方式（bash -eo pipefail）真的跑那兩步。形狀照 XamlContrast 的 ActionContractTests。
/// </summary>
public class ActionPathTests
{
    private static readonly string ActionYml = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "action.yml")).Replace("\r\n", "\n");

    private const string Annotate = "Annotate findings", Sarif = "Make SARIF paths repo-relative";

    /// <summary>（GITHUB_WORKSPACE、working-directory、root、註記該有的路徑、SARIF 該有的 uri）。finding 的檔案是 Views/Main.xaml。</summary>
    public static TheoryData<string, string, string, string, string> RootSpellings()
    {
        const string linux = "/home/runner/work/App/App", windows = @"D:\a\App\App";
        var data = new TheoryData<string, string, string, string, string>();
        void Add(string workspace, string workingDirectory, string root, string file, string? uri = null) =>
            data.Add(workspace, workingDirectory, root, file, uri ?? file);

        Add(linux, ".", ".", "Views/Main.xaml");                          // 前綴要是空的，不能接出 "./Views" 或 "/Views"
        Add(linux, ".", "src", "src/Views/Main.xaml");
        Add(linux, ".", "src/App/", "src/App/Views/Main.xaml");
        Add(linux, "./app/", "./src//x/.", "app/src/x/Views/Main.xaml");
        // 0.4.0 以前都對不上：..、反斜線、workspace 底下的絕對路徑（root 或 working-directory）
        Add(linux, "build", "../src", "src/Views/Main.xaml");
        Add(linux, "build/ci", "../../src/../app", "app/Views/Main.xaml");
        Add(linux, "app", "..", "Views/Main.xaml");
        Add(linux, ".", "src/..hidden/...", "src/..hidden/.../Views/Main.xaml"); // 以點開頭的資料夾名不是 ..
        Add(linux, ".", linux + "/src", "src/Views/Main.xaml");
        Add(linux, ".", linux, "Views/Main.xaml");
        Add(linux, ".", linux + "/", "Views/Main.xaml");
        Add(linux, "app", linux + "/src", "src/Views/Main.xaml");         // root 絕對時 working-directory 不算
        Add(linux, linux + "/build", "../src", "src/Views/Main.xaml");
        Add(linux, ".", linux + "-other/src", "home/runner/work/App/App-other/src/Views/Main.xaml"); // 只是字首相同，不在 workspace 裡
        Add(windows, ".", @"src\App", "src/App/Views/Main.xaml");
        Add(windows, @".\build", @"..\src\App\", "src/App/Views/Main.xaml");
        Add(windows, ".", windows + @"\src", "src/Views/Main.xaml");
        Add(windows, ".", windows + "/src", "src/Views/Main.xaml");       // ${{ github.workspace }}/src
        Add(windows, ".", "D:/a/App/App/src", "src/Views/Main.xaml");
        Add(windows, windows, "src", "src/Views/Main.xaml");
        // SARIF 的 uri 是 URI：空白、%、#、中文要編碼，斜線不編；註記的 file 不是 URI，跳脫照 workflow command 的規格
        Add(linux, ".", "My App", "My App/Views/Main.xaml", "My%20App/Views/Main.xaml");
        Add(linux, "src", "../My App", "My App/Views/Main.xaml", "My%20App/Views/Main.xaml");
        Add(linux, ".", linux + "/My App", "My App/Views/Main.xaml", "My%20App/Views/Main.xaml");
        Add(linux, ".", "50% #1/中文", "50%25 #1/中文/Views/Main.xaml", "50%25%20%231/%E4%B8%AD%E6%96%87/Views/Main.xaml");
        return data;
    }

    [Theory]   // jq 換成把 --arg 記下來的 stub：任何機器都跑得到
    [MemberData(nameof(RootSpellings))]
    public void Both_steps_compute_the_repository_prefix(string workspace, string workingDirectory, string root, string file, string uri)
    {
        _ = uri;   // 編碼在 jq 程式裡，由 The_steps_write_the_repository_path 看
        foreach (var (step, report) in new[] { (Annotate, "xamlkeys-report.json"), (Sarif, "xamlkeys.sarif") })
        {
            var r = Run(step, workspace, workingDirectory, root, report, "{}", stubJq: true);
            Assert.True(r.Exit == 0, r.Output);
            Assert.Equal($"root={file.Replace("%25", "%")[..^"Views/Main.xaml".Length]}\n", r.JqArgs);
        }
        Assert.Contains("""|= (($root + .) | split("/") | map(@uri) | join("/"))""", ActionYml);
    }

    [JqTheory]   // 同一件事用真的 jq 跑到底：印出來的註記與改寫後的 SARIF
    [MemberData(nameof(RootSpellings))]
    public void The_steps_write_the_repository_path(string workspace, string workingDirectory, string root, string file, string uri)
    {
        var a = Run(Annotate, workspace, workingDirectory, root, "xamlkeys-report.json", """
            { "findings": [ { "file": "Views/Main.xaml", "line": 42, "severity": "fail", "kind": "dangling-static", "message": "m" } ] }
            """);
        Assert.True(a.Exit == 0, a.Output);
        Assert.StartsWith($"::error file={file},line=42::", a.Output, StringComparison.Ordinal);

        var s = Run(Sarif, workspace, workingDirectory, root, "xamlkeys.sarif", CliSarif("Views/Main.xaml"));
        Assert.True(s.Exit == 0, s.Output);
        Assert.Equal([uri], UrisOf(s.Report));
    }

    [Fact]   // Action 把整條 uri 一起編碼，前提是 CLI 輸出的是原樣的路徑。CLI 哪天自己編碼，Action 就要改成只編前綴
             // （不然 %20 會變成 %2520）—— 這條紅了就是在提醒你
    public void The_cli_writes_sarif_paths_unencoded() => Assert.Equal(["My Views/50% #1.xaml"], UrisOf(CliSarif("My Views/50% #1.xaml")));

    private static string CliSarif(string file)
    {
        var dir = Directory.CreateTempSubdirectory("xk-action-sarif");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "App.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType><UseWPF>true</UseWPF></PropertyGroup></Project>");
            var xaml = Path.Combine(dir.FullName, file);
            Directory.CreateDirectory(Path.GetDirectoryName(xaml)!);
            File.WriteAllText(xaml, """
                <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Background="{StaticResource Missing}" />
                """);
            return ReportSarif.ToSarif(Analyzer.Run(dir.FullName, new Config()), "0.0.0-test");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static List<string?> UrisOf(string sarif) =>
        [.. JsonDocument.Parse(sarif).RootElement.GetProperty("runs")[0].GetProperty("results").EnumerateArray()
            .Select(x => x.GetProperty("locations")[0].GetProperty("physicalLocation").GetProperty("artifactLocation").GetProperty("uri").GetString())];

    /// <summary>bash 跑得到 jq 才跑。GitHub 的 runner 都有，所以在那裡不略過：沒有就該紅，不能看起來像跑過。</summary>
    public sealed class JqTheoryAttribute : TheoryAttribute
    {
        public JqTheoryAttribute()
        {
            var exe = OperatingSystem.IsWindows() ? "jq.exe" : "jq";
            if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" &&
                !(Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Any(d => File.Exists(Path.Combine(d, exe))))
                Skip = "jq is not installed here; GitHub's runners have it, so CI runs this";
        }
    }

    /// <summary>step 的 `run: |` 內容（去掉 8 格縮排）。</summary>
    private static string Script(string step)
    {
        var m = Regex.Match(ActionYml, $@"^    - name: {Regex.Escape(step)}\n(?:(?!    - name: ).*\n)*?      run: \|\n(?<body>(?:(?:        .*)?\n)*)", RegexOptions.Multiline);
        Assert.True(m.Success, $"action.yml: step `{step}` not found");
        return string.Join("\n", m.Groups["body"].Value.Split('\n').Select(l => l.Length >= 8 ? l[8..] : l.TrimStart()));
    }

    /// <summary>bash：Linux/macOS 用 PATH 上的；Windows 用 Git Bash（從 PATH 上的 git.exe 反推，避開 System32 的 WSL 啟動器）。</summary>
    private static string BashPath()
    {
        if (!OperatingSystem.IsWindows()) return "bash";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            if (File.Exists(Path.Combine(dir, "git.exe")))
                foreach (var relative in new[] { @"..\bin", "", @"..\..\bin" })   // Git\cmd、Git\bin、Git\mingw64\bin
                    if (Path.GetFullPath(Path.Combine(dir, relative, "bash.exe")) is var bash && File.Exists(bash)) return bash;
        var fallback = Path.Combine(Environment.GetEnvironmentVariable("ProgramW6432") ?? @"C:\Program Files", "Git", "bin", "bash.exe");
        return File.Exists(fallback) ? fallback : throw new InvalidOperationException("Git Bash not found; this test needs it");
    }

    /// <summary>Output：stdout 接 stderr；JqArgs：stub 記到的 --arg；Report：跑完之後那個報告檔的內容。</summary>
    private sealed record StepRun(int Exit, string Output, string JqArgs, string Report);

    private static StepRun Run(string step, string workspace, string workingDirectory, string root, string reportName, string report, bool stubJq = false)
    {
        var temp = Directory.CreateTempSubdirectory("xk-action").FullName;
        try
        {
            string Unix(string p) => p.Replace('\\', '/');
            string Text(string name) => Path.Combine(temp, name) is var f && File.Exists(f) ? File.ReadAllText(f).Replace("\r\n", "\n") : "";
            var stub = stubJq
                ? """
                  jq() {
                    while [ "$#" -gt 0 ]; do
                      if [ "$1" = --arg ]; then printf '%s=%s\n' "$2" "$3" >> "$RUNNER_TEMP/jq-args"; fi
                      shift
                    done
                  }

                  """.Replace("\r\n", "\n")
                : "";
            var script = Path.Combine(temp, "step.sh");
            File.WriteAllText(script, stub + Script(step));
            File.WriteAllText(Path.Combine(temp, reportName), report);

            var psi = new ProcessStartInfo(BashPath())
            {
                WorkingDirectory = temp,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,   // 中文 Windows 的預設是 Big5
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
            };
            foreach (var a in new[] { "--noprofile", "--norc", "-eo", "pipefail", Unix(script) }) psi.ArgumentList.Add(a);
            psi.Environment["RUNNER_TEMP"] = Unix(temp);
            psi.Environment["GITHUB_WORKSPACE"] = workspace;
            psi.Environment["XK_ROOT"] = root;
            psi.Environment["XK_WORKDIR"] = workingDirectory;
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            Assert.True(p.WaitForExit(30_000), "bash did not finish within 30 seconds");
            return new StepRun(p.ExitCode, stdout.Result.Replace("\r\n", "\n") + stderr.Result, Text("jq-args"), Text(reportName));
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch (IOException) { }
        }
    }
}
