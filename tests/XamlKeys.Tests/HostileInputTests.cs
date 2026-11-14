using XamlKeys.Core;

namespace XamlKeys.Tests;

/// <summary>
/// 惡意輸入。這個工具的主要用途是在 CI 上掃 **別人送來的 PR**，
/// 所以 XAML 的內容、檔名、baseline 檔都要當成不可信的。
///
/// 2026-11-14 發版前的安全檢查實測到兩件事，都在這裡釘住：
///   1. 鍵名裡的 `&amp;#10;` 會在 log 裡自己開一行 —— 可以偽造診斷、可以下 CI 指令
///   2. 巢狀幾千層的檔會讓整個行程 stack overflow
/// </summary>
public class HostileInputTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("xamlkeys-hostile");
    public void Dispose() => _dir.Delete(true);

    private const string Ns = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
                              "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";

    private void Put(string rel, string content)
    {
        var path = Path.Combine(_dir.FullName, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private AnalysisResult Analyze()
    {
        Put("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup></Project>");
        return Analyzer.Run(_dir.FullName, new Config());
    }

    // ── 一行就是一行 ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("中文鍵名 #1 ## ok", "中文鍵名 #1 ## ok")]          // 一般內容原樣通過
    [InlineData("a\nb", @"a\nb")]
    [InlineData("a\r\nb", @"a\r\nb")]
    [InlineData("a\u001b[31mb", @"a\u001B[31mb")]                   // ANSI 跳脫序列
    [InlineData("a\u2028b", @"a\u2028b")]                           // Unicode 的行分隔字元
    [InlineData("x ##vso[task.setvariable variable=A]1", @"x #\u0023vso[task.setvariable variable=A]1")]
    [InlineData("x ##[error]boo", @"x #\u0023[error]boo")]
    public void OneLine_never_lets_scanned_text_start_a_line_or_a_ci_command(string input, string expected) =>
        Assert.Equal(expected, Report.OneLine(input));

    [Theory]
    [InlineData("Themes/Dark.xaml", "Themes/Dark.xaml")]
    [InlineData("::error::pwn/A.xaml", "./::error::pwn/A.xaml")]    // Linux 上的資料夾可以叫這個名字
    [InlineData("  ::warning::x.xaml", "./  ::warning::x.xaml")]    // GitHub 會先去掉行首空白再判斷
    public void A_path_at_the_start_of_a_line_cannot_be_a_workflow_command(string input, string expected) =>
        Assert.Equal(expected, Report.LineStart(input));

    [Fact]
    public void A_key_containing_a_newline_cannot_forge_lines_in_text_output()
    {
        // `&#10;` 是 XML 的字元參照：解析之後鍵名裡是一個真的換行
        Put("Main.xaml", $$"""
            <Window {{Ns}}>
              <Border Tag="{StaticResource Nope&#10;::error::INJECTED&#10;Fake.xaml(1): error XK0001: forged}" />
              <Border><Border.Resources>
                <SolidColorBrush x:Key="Dup&#10;##vso[task.setvariable variable=X]1" Color="#1" />
                <SolidColorBrush x:Key="Dup&#10;##vso[task.setvariable variable=X]1" Color="#2" />
              </Border.Resources></Border>
            </Window>
            """);

        var r = Analyze();
        Assert.Contains(r.Findings, f => f.Kind == FindingKind.DanglingStatic);
        Assert.Contains(r.Findings, f => f.Kind == FindingKind.DuplicateKey);

        var console = new StringWriter();
        Report.WriteConsole(console, r, showInfo: true);
        var msbuild = new StringWriter();
        Report.WriteMsBuild(msbuild, r);

        foreach (var line in (console.ToString() + msbuild).Split('\n'))
        {
            Assert.False(line.TrimStart().StartsWith("::", StringComparison.Ordinal), $"workflow command at line start: {line}");
            Assert.False(line.StartsWith("Fake.xaml(", StringComparison.Ordinal), $"forged diagnostic: {line}");
            Assert.DoesNotContain("##vso[", line);
        }

        // MSBuild 格式：一筆 finding 恰好一行
        var diagnostics = msbuild.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(r.Findings.Count(f => f.Severity != Severity.Info), diagnostics.Length);
    }

    [Fact]
    public void The_json_report_keeps_the_key_exactly_as_it_is()
    {
        // 消毒只發生在「給人和 CI log 看的純文字」。機器讀的報告要保留原值 ——
        // JSON 序列化器本來就會轉義，改了反而讓 baseline 的比對鍵對不上。
        Put("Main.xaml", $$"""<Window {{Ns}}><Border Tag="{StaticResource A&#10;B}" /></Window>""");

        var json = Report.ToJson(Analyze(), "test");

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var key = doc.RootElement.GetProperty("findings").EnumerateArray()
                     .Single(f => f.GetProperty("kind").GetString() == "dangling-static")
                     .GetProperty("key").GetString();
        Assert.Equal("A\nB", key);
    }

    // ── 巢狀深度 ───────────────────────────────────────────────────────────

    private static string Nested(int depth, string innermost) =>
        $"<Window {Ns}>" + string.Concat(Enumerable.Repeat("<Grid>", depth)) + innermost
                         + string.Concat(Enumerable.Repeat("</Grid>", depth)) + "</Window>";

    [Fact]
    public void A_pathologically_nested_file_is_skipped_and_counted_instead_of_crashing_the_run()
    {
        // 修之前：8,000 層就 stack overflow，行程直接死掉，其他檔的結果一起消失
        Put("Deep.xaml", Nested(20_000, ""));
        Put("Main.xaml", $$"""<Window {{Ns}}><Border Tag="{StaticResource Missing}" /></Window>""");

        var r = Analyze();

        var err = Assert.Single(r.ParseErrors);
        Assert.Contains("Deep.xaml", err.File);
        Assert.Contains("nested", err.Message);

        // 其他檔照常稽核
        Assert.Contains(r.Findings, f => f.Kind == FindingKind.DanglingStatic && f.Key == "Missing");
    }

    [Fact]
    public void Deep_but_plausible_nesting_is_still_scanned()
    {
        // 上限不能誤傷真的檔：300 層遠超過任何手寫 XAML，裡面的引用還是要看得到
        Put("Main.xaml", Nested(300, """<Border Tag="{StaticResource Missing}" />"""));

        var r = Analyze();

        Assert.Empty(r.ParseErrors);
        Assert.Contains(r.Findings, f => f.Kind == FindingKind.DanglingStatic && f.Key == "Missing");
    }

    // ── action.yml ─────────────────────────────────────────────────────────

    [Fact]
    public void The_action_never_interpolates_an_input_into_a_shell_script()
    {
        // `${{ inputs.x }}` 寫在 run: 裡，是在 shell 看到腳本**之前**做的文字代換 ——
        // 呼叫端把 PR 作者控制得到的值接上來，就是任意指令執行。
        // 唯一允許的形式是 env 區塊裡的一行 `XK_NAME: ${{ inputs.name }}`。
        var yml = File.ReadAllLines(Path.Combine(TestPaths.RepoRoot, "action.yml"));
        var envLine = new System.Text.RegularExpressions.Regex(@"^\s+XK_[A-Z_]+: \$\{\{ inputs\.[a-z-]+ \}\}$");

        var interpolations = yml.Where(l => l.Contains("${{", StringComparison.Ordinal)
                                         && !l.TrimStart().StartsWith('#')).ToList();

        Assert.NotEmpty(interpolations);
        Assert.All(interpolations, l => Assert.Matches(envLine, l));
    }

    // ── 掃描範圍外的路徑 ────────────────────────────────────────────────
    // 合併路徑與 csproj 的 Include 都是被掃的 repo 寫的。指到範圍外（絕對路徑、\\主機\分享）的一律不碰：
    // 查檔案存不存在本身就會讓 Windows 對 UNC 主機發 SMB 連線、送出 NTLM 認證。

    [Theory]
    [InlineData(@"\\evil-host\share\Theme.xaml")]
    [InlineData(@"\\?\UNC\evil-host\share\Theme.xaml")]
    [InlineData(@"//evil-host/share/Theme.xaml")]
    [InlineData(@"C:\Windows\System32\drivers\etc\Theme.xaml")]
    [InlineData(@"..\..\..\outside\Theme.xaml")]
    public void The_file_system_is_never_asked_about_a_path_outside_the_scan_root(string hostile)
    {
        Put("App.csproj", $"<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><Page Include=\"{hostile}\" Link=\"Themes\\Linked.xaml\" /></ItemGroup></Project>");
        Put("App.xaml", "<Application />");
        var asked = new List<string>();
        var resolver = new SourceResolver(_dir.FullName, [Path.Combine(_dir.FullName, "App.csproj")])
        {
            Probe = p => { asked.Add(p); return false; },
        };
        var from = Path.Combine(_dir.FullName, "App.xaml");

        Assert.True(resolver.Resolve(hostile, from).IsExternal);
        Assert.True(resolver.Resolve("/Themes/Linked.xaml", from).IsExternal);   // 經由 csproj 的 Link
        Assert.True(resolver.Resolve("Themes/Linked.xaml", from).IsExternal);

        var root = Path.GetFullPath(_dir.FullName) + Path.DirectorySeparatorChar;
        Assert.All(asked, p => Assert.StartsWith(root, p, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(@"\\evil-host\share")]
    [InlineData(@"//evil-host/share")]
    [InlineData(@"C:\Users")]
    [InlineData(@"..\..\..\packages\Vendor\Themes")]
    public void An_externalDictionaries_entry_outside_the_scan_root_is_never_probed(string hostile)
    {
        // 反斜線、磁碟代號只在 Windows 是路徑：在 Linux 上那只是掃描根底下一個奇怪的檔名，查了也無害
        if (!OperatingSystem.IsWindows() && (hostile.Contains('\\') || hostile.Contains(':'))) return;

        // config 是從被掃的 repo 自動撿的：對方可以把目錄指到 \\主機\分享
        var asked = new List<string>();
        var resolver = new SourceResolver(_dir.FullName, [], new Dictionary<string, string> { ["Vendor"] = hostile },
                                          p => { asked.Add(p); return true; });

        Assert.Empty(asked);
        Assert.True(resolver.Resolve("/Vendor;component/Themes/Generic.xaml", Path.Combine(_dir.FullName, "App.xaml")).IsExternal);
    }

    [Theory]
    [InlineData("Theme.xaml:stream")]
    [InlineData("Themes::$INDEX_ALLOCATION/Theme.xaml")]
    public void A_colon_after_the_root_is_never_probed(string tricky)
    {
        Put("App.xaml", "<Application />");
        var asked = new List<string>();
        var resolver = new SourceResolver(_dir.FullName, []) { Probe = p => { asked.Add(p); return true; } };

        Assert.True(resolver.Resolve(tricky, Path.Combine(_dir.FullName, "App.xaml")).IsExternal);
        Assert.Empty(asked);
    }

    [Fact]
    public void A_component_whose_attributes_cannot_be_read_counts_as_a_link()
    {
        // fail closed：讀不到屬性（權限不足之類）不能當成「不是連結」
        Put("App.xaml", "<Application />");
        var asked = new List<string>();
        var resolver = new SourceResolver(_dir.FullName, [])
        {
            Probe = p => { asked.Add(p); return true; },
            Attributes = _ => throw new UnauthorizedAccessException(),
        };

        Assert.True(resolver.Resolve("Themes/Theme.xaml", Path.Combine(_dir.FullName, "App.xaml")).IsExternal);
        Assert.Empty(asked);
    }

    [Fact]
    public void On_a_case_sensitive_file_system_a_differently_cased_root_is_outside()
    {
        // Linux：/tmp/repo 與 /tmp/REPO 是兩個目錄。不分大小寫比的話，後者會被當成在範圍內
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) return;

        Put("App.xaml", "<Application />");
        var asked = new List<string>();
        var resolver = new SourceResolver(_dir.FullName, []) { Probe = p => { asked.Add(p); return true; } };
        var other = Path.Combine(Path.GetDirectoryName(_dir.FullName)!, Path.GetFileName(_dir.FullName).ToUpperInvariant(), "Theme.xaml");

        Assert.True(resolver.Resolve(other, Path.Combine(_dir.FullName, "App.xaml")).IsExternal);
        Assert.DoesNotContain(asked, p => p.StartsWith(Path.GetDirectoryName(other)!, StringComparison.Ordinal));
    }

    [Fact]
    public void A_component_that_does_not_exist_is_not_a_link()
    {
        Put("App.xaml", "<Application />");
        var resolver = new SourceResolver(_dir.FullName, [])
        {
            Probe = _ => true,
            Attributes = _ => throw new DirectoryNotFoundException(),
        };

        Assert.False(resolver.Resolve("Themes/Theme.xaml", Path.Combine(_dir.FullName, "App.xaml")).IsExternal);
    }

    [Fact]
    public void An_externalDictionaries_entry_inside_the_scan_root_works()
    {
        // 正當用法：vendored 的函式庫副本（沒有 csproj，靠 config 對上組件名）
        Put("vendor/Vendor/Themes/Generic.xaml", $"<ResourceDictionary {Ns}><SolidColorBrush x:Key=\"VendorBrush\" Color=\"Red\" /></ResourceDictionary>");
        Put("App.xaml", $"""
            <Application {Ns}><Application.Resources><ResourceDictionary>
              <ResourceDictionary.MergedDictionaries><ResourceDictionary Source="pack://application:,,,/Vendor;component/Themes/Generic.xaml" /></ResourceDictionary.MergedDictionaries>
            </ResourceDictionary></Application.Resources></Application>
            """);
        Put("Main.xaml", $"<Window {Ns}><Border Background=\"{{StaticResource VendorBrush}}\" /></Window>");
        Put("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup></Project>");
        var cfg = new Config();
        cfg.ExternalDictionaries["Vendor"] = "vendor/Vendor";

        var r = Analyzer.Run(_dir.FullName, cfg);

        Assert.Empty(r.ExternalSources);
        Assert.DoesNotContain(r.Findings, f => f.Kind is FindingKind.DanglingStatic or FindingKind.External);
    }

    [Fact]
    public void A_path_through_a_symlink_or_junction_inside_the_root_is_never_probed()
    {
        var outside = Directory.CreateTempSubdirectory("xamlkeys-outside");
        try
        {
            File.WriteAllText(Path.Combine(outside.FullName, "Theme.xaml"), "<ResourceDictionary />");
            var link = Path.Combine(_dir.FullName, "Themes");
            if (!TryLink(link, outside.FullName)) return;   // 這台機器建不了連結（沒有權限）：無從驗證

            Put("App.xaml", "<Application />");
            var asked = new List<string>();
            var resolver = new SourceResolver(_dir.FullName, []) { Probe = p => { asked.Add(p); return true; } };

            Assert.True(resolver.Resolve("Themes/Theme.xaml", Path.Combine(_dir.FullName, "App.xaml")).IsExternal);
            Assert.DoesNotContain(asked, p => p.Contains("Themes", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            var link = Path.Combine(_dir.FullName, "Themes");
            if (Directory.Exists(link)) Directory.Delete(link);   // 只刪連結本身，不碰它指到的地方
            outside.Delete(true);
        }

        static bool TryLink(string link, string target)
        {
            try { Directory.CreateSymbolicLink(link, target); return true; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            if (!OperatingSystem.IsWindows()) return false;
            // Windows 沒有開發人員模式時建不了符號連結，但目錄接合點不需要權限
            var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
            p!.WaitForExit();
            return p.ExitCode == 0 && Directory.Exists(link);
        }
    }

    [Fact]
    public void A_Source_pointing_outside_the_scan_root_is_never_read()
    {
        var outside = Directory.CreateTempSubdirectory("xamlkeys-outside");
        try
        {
            var secret = Path.Combine(outside.FullName, "Secret.xaml");
            File.WriteAllText(secret, $"<ResourceDictionary {Ns}><SolidColorBrush x:Key=\"LeakedKeyName\" Color=\"Red\" /></ResourceDictionary>");

            Put("App.xaml", $"""
                <Application {Ns}><Application.Resources><ResourceDictionary>
                  <ResourceDictionary.MergedDictionaries><ResourceDictionary Source="{secret}" /></ResourceDictionary.MergedDictionaries>
                </ResourceDictionary></Application.Resources></Application>
                """);
            var r = Analyze();

            Assert.Contains(secret, r.ExternalSources);
            Assert.DoesNotContain(r.Findings, f => f.Key == "LeakedKeyName");
            Assert.Equal(0, r.KeysDefined);
        }
        finally { outside.Delete(true); }
    }

    [Fact]
    public void A_csproj_Link_pointing_outside_the_scan_root_is_never_read()
    {
        var outside = Directory.CreateTempSubdirectory("xamlkeys-outside");
        try
        {
            var secret = Path.Combine(outside.FullName, "Secret.xaml");
            File.WriteAllText(secret, $"<ResourceDictionary {Ns}><SolidColorBrush x:Key=\"LeakedKeyName\" Color=\"Red\" /></ResourceDictionary>");

            Put("App.xaml", $"""
                <Application {Ns}><Application.Resources><ResourceDictionary>
                  <ResourceDictionary.MergedDictionaries><ResourceDictionary Source="/Themes/Secret.xaml" /></ResourceDictionary.MergedDictionaries>
                </ResourceDictionary></Application.Resources></Application>
                """);
            Put("App.csproj", $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup><ItemGroup><Page Include=\"{secret}\" Link=\"Themes\\Secret.xaml\" /></ItemGroup></Project>");
            var r = Analyzer.Run(_dir.FullName, new Config());

            Assert.Contains("/Themes/Secret.xaml", r.ExternalSources);
            Assert.Equal(0, r.KeysDefined);
        }
        finally { outside.Delete(true); }
    }
}
