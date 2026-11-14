using System.Text.Json;
using System.Text.RegularExpressions;
using XamlKeys.Core;

namespace XamlKeys.Tests;

/// <summary>
/// 凍結面的快照。
///
/// 1.0 的意思是「這些東西不再改」。這組測試把它們一條一條寫死：
/// 任何人（包括作者自己）不小心動到，CI 立刻紅，必須先回來改這個檔，
/// 也就是必須先**承認自己正在破壞契約**。
///
/// ⚠ 這個檔不是用來「跟上程式碼」的。測試紅了的正確反應是問
/// 「我該不該做這個變更」，而不是把期望值改成新的。
/// 0.x 期間可以改（要寫進 CHANGELOG）；1.0 之後改任何一條就是 major。
///
/// 九個面的清單見 CLAUDE.md 與 ROADMAP.md。
/// </summary>
public class FrozenContractTests
{
    // ── 1. CLI 旗標與位置參數 ──────────────────────────────────────────────

    private static readonly string[] Flags =
    [
        "--baseline", "--config", "--dialect", "--exclude", "--fail-on", "--help", "--html",
        "--include-tests", "--json", "--min-coverage", "--msbuild", "--sarif", "--show-info",
        "--stale", "--version", "--write-baseline",
    ];

    [Fact]
    public void The_cli_accepts_exactly_these_flags()
    {
        var program = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "src/XamlKeys.Cli/Program.cs"));

        var actual = Regex.Matches(program, @"case ""(--[a-z-]+)""")
                          .Select(m => m.Groups[1].Value)
                          .Concat(program.Contains(@"case ""-h"" or ""--help""") ? ["--help"] : Array.Empty<string>())
                          .Distinct()
                          .OrderBy(s => s, StringComparer.Ordinal)
                          .ToArray();

        Assert.Equal(Flags, actual);
    }

    [Fact]
    public void Every_flag_is_documented_in_the_help_text()
    {
        // 說明沒寫的旗標等於不存在；說明寫了卻不接受的旗標更糟。
        var (_, help, _) = RunCli("--help");

        foreach (var f in Flags)
            Assert.True(help.Contains(f, StringComparison.Ordinal), $"--help does not mention {f}");
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("README.zh-Hant.md")]
    public void Every_flag_is_listed_in_both_readmes(string readme)
    {
        // 2026-11-14 發現：中文 README 少了十個旗標，而且沒有任何東西會發現。
        // --help 與 --version 不必列（它們是「怎麼問工具」，不是「怎麼用工具」）。
        var text = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, readme));

        foreach (var f in Flags.Except(["--help", "--version"]))
            Assert.True(text.Contains(f, StringComparison.Ordinal), $"{readme} does not mention {f}");
    }

    [Fact]
    public void Only_one_positional_argument_is_accepted()
    {
        // ⚠ 多收一個位置參數就是改掉了 CLI 的形狀。
        var (exit, _, err) = RunCli(TestPaths.Demo, TestPaths.Demo);
        Assert.Equal(2, exit);
        Assert.Contains("only one root path", err);
    }

    [Fact]
    public void Double_dash_ends_the_flags()
    {
        // 沒有這個出口，一個叫 --version 的資料夾會被當成旗標
        var (exit, _, _) = RunCli("--min-coverage", "0", "--", TestPaths.Demo);
        Assert.Equal(1, exit);
    }

    // ── 2. 退出碼的語意與判定順序 ──────────────────────────────────────────

    [Theory]
    [InlineData(0, "clean")]
    [InlineData(1, "findings at or above the gate")]
    [InlineData(2, "usage, config or input error")]
    [InlineData(3, "result is not trustworthy")]
    public void The_four_exit_codes_are_documented(int code, string meaning)
    {
        var (_, help, _) = RunCli("--help");
        Assert.Contains($"{code}  ", help);
        Assert.Contains(meaning, help, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Untrustworthy_is_decided_before_findings()
    {
        // demo 同時「有 fail 級 finding」與「覆蓋率低於下限」。順序規定不可信優先。
        Assert.Equal(3, RunCli(TestPaths.Demo).Exit);
        Assert.Equal(1, RunCli("--min-coverage", "0", TestPaths.Demo).Exit);
    }

    [Fact]
    public void Writing_a_baseline_always_exits_zero()
    {
        var dir = Directory.CreateTempSubdirectory("xk-contract-bl");
        try
        {
            var bl = Path.Combine(dir.FullName, "b.json");
            Assert.Equal(0, RunCli("--min-coverage", "0", "--write-baseline", bl, TestPaths.Demo).Exit);
        }
        finally { dir.Delete(true); }
    }

    // ── 3. JSON 報告 ───────────────────────────────────────────────────────

    [Fact]
    public void The_json_schema_version_is_one()
    {
        using var doc = JsonDocument.Parse(Json());
        Assert.Equal(1, doc.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(1, Report.SchemaVersion);
    }

    [Fact]
    public void The_json_top_level_shape_is_frozen()
    {
        using var doc = JsonDocument.Parse(Json());
        Assert.Equal(
            ["schemaVersion", "toolVersion", "summary", "findings",
             "parseErrorDetails", "invalidIgnoreDetails", "dynamicKeySiteDetails"],
            doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public void Every_finding_carries_the_same_required_fields()
    {
        using var doc = JsonDocument.Parse(Json());

        foreach (var f in doc.RootElement.GetProperty("findings").EnumerateArray())
            foreach (var required in new[] { "kind", "severity", "key", "file", "line", "side", "message" })
                Assert.True(f.TryGetProperty(required, out _), $"a finding is missing '{required}'");
    }

    [Fact]
    public void The_finding_kinds_are_frozen()
    {
        // ⚠ 新增一個 kind 是加法（消費端要忽略不認得的值）；
        //    改名或刪除就是破壞性變更。
        Assert.Equal(
            ["dangling-static", "dangling-dynamic", "duplicate-key", "out-of-scope",
             "shadowed", "shadowed-identical", "unused", "orphan-dictionary", "external"],
            Enum.GetValues<FindingKind>().Select(Finding.KindSlug).ToArray());
    }

    [Fact]
    public void The_severity_names_are_frozen()
    {
        Assert.Equal(["info", "warn", "fail"],
                     Enum.GetValues<Severity>().Select(Finding.SeveritySlug).ToArray());
    }

    [Fact]
    public void The_theme_side_names_are_frozen()
    {
        Assert.Equal(["neutral", "dark", "light", "high-contrast"],
                     Enum.GetValues<ThemeSide>().Select(Finding.SideSlug).ToArray());
    }

    [Fact]
    public void The_summary_fields_are_frozen()
    {
        // 新增欄位是加法；拿掉或改名是破壞性變更 —— 尤其是那些「工具沒看到什麼」的欄位，
        // 消費端可能正靠它們判斷結果可不可信。
        using var doc = JsonDocument.Parse(Json());
        var actual = doc.RootElement.GetProperty("summary").EnumerateObject().Select(p => p.Name).ToHashSet();

        foreach (var field in new[]
        {
            "dialect", "dialectAutoDetected", "dialectMixed", "xamlFiles", "csharpFiles",
            "dictionaries", "mergeEdges", "mergeEdgesResolved", "coverage", "keysDefined",
            "references", "suppressed", "baselineSuppressed", "parseErrors", "unresolvedSites", "invalidIgnores",
            "dynamicKeySites", "keysNamedNearDynamicLookups", "keysMatchingComposedLookups", "keysNamedInCode", "dynamicKeysNamedInCode", "keysAddedInCode", "keysResolvedThroughHosts", "keysFromControlResources", "startupResources", "layeredOverrides", "keysUsedAsFields", "dictionariesCreatedInCode", "placedTeachingTips", "keyedDictionaries", "classBackedDictionaries", "libraryKeyOverrides", "frameworkKeyOverrides", "unusedOnlyViaUnused", "configOverrides", "hasApplication", "projects", "applicationProjects",
            "libraryKeysNotChecked", "variantDictionaryGroups", "frameworkProvided",
            "runtimeLoadedDictionaries", "implicitThemeDictionaries", "runtimeSelectedFamilyFiles",
            "libraryUnresolvedReferences", "unusedDefinitions", "crossProjectReferences",
            "testFilesExcluded", "winUiKeySource", "winUiKeyCount", "wpfFluentThemeInUse",
            "wpfFluentKeySource", "wpfFluentKeyCount", "sides", "externalSources", "knownThemeSources", "themePacksInUse", "counts", "severities",
        })
            Assert.True(actual.Contains(field), $"summary.{field} is part of the frozen contract");
    }

    // ── 4. SARIF 規則 ID ───────────────────────────────────────────────────

    [Theory]
    [InlineData(FindingKind.DanglingStatic, "XK0001")]
    [InlineData(FindingKind.DuplicateKey, "XK0002")]
    [InlineData(FindingKind.DanglingDynamic, "XK0003")]
    [InlineData(FindingKind.OutOfScope, "XK0004")]
    [InlineData(FindingKind.Shadowed, "XK0005")]
    [InlineData(FindingKind.Unused, "XK0006")]
    [InlineData(FindingKind.OrphanDictionary, "XK0007")]
    [InlineData(FindingKind.External, "XK0008")]
    [InlineData(FindingKind.ShadowedIdentical, "XK0009")]
    public void Rule_ids_never_move(FindingKind kind, string id)
    {
        // ⚠ 使用者會用這些 ID 做抑制。發出去之後改號 = 把別人的設定弄壞。
        Assert.Equal(id, Report.RuleId(kind));
    }

    // ── 5. config schema ───────────────────────────────────────────────────

    [Fact]
    public void The_config_accepts_exactly_these_properties()
    {
        var source = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "src/XamlKeys.Core/Config.cs"));
        var actual = Regex.Matches(source, @"case ""([a-zA-Z]+)"":")
                          .Select(m => m.Groups[1].Value)
                          .Distinct().OrderBy(s => s, StringComparer.Ordinal).ToArray();

        Assert.Equal(
            ["dialect", "exclude", "externalDictionaries", "failOn", "includeTests",
             "minCoverage", "severities", "themeFiles", "usedAtRuntime"],
            actual);
    }

    [Fact]
    public void An_unknown_config_property_is_still_an_error()
    {
        var dir = Directory.CreateTempSubdirectory("xk-contract-cfg");
        try
        {
            var p = Path.Combine(dir.FullName, Config.DefaultFileName);
            File.WriteAllText(p, """{ "notAThing": 1 }""");
            Assert.Throws<ConfigException>(() => Config.Load(p));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void The_default_severities_are_frozen()
    {
        // 預設 gate 只擋 fail 級。改這個會改變所有人的 CI 結果。
        Assert.Equal(Severity.Fail, Config.DefaultSeverity(FindingKind.DanglingStatic));
        Assert.Equal(Severity.Fail, Config.DefaultSeverity(FindingKind.DuplicateKey));
        Assert.Equal(Severity.Warn, Config.DefaultSeverity(FindingKind.DanglingDynamic));
        Assert.Equal(Severity.Warn, Config.DefaultSeverity(FindingKind.OutOfScope));
        Assert.Equal(Severity.Warn, Config.DefaultSeverity(FindingKind.Shadowed));
        Assert.Equal(Severity.Info, Config.DefaultSeverity(FindingKind.ShadowedIdentical));
        Assert.Equal(Severity.Warn, Config.DefaultSeverity(FindingKind.Unused));
        Assert.Equal(Severity.Warn, Config.DefaultSeverity(FindingKind.OrphanDictionary));
        Assert.Equal(Severity.Info, Config.DefaultSeverity(FindingKind.External));

        Assert.Equal(Severity.Fail, new Config().FailOn);
        Assert.Equal(80.0, new Config().MinCoverage);
        Assert.False(new Config().IncludeTests);
    }

    // ── 6. baseline 檔格式 ─────────────────────────────────────────────────

    [Fact]
    public void The_baseline_key_version_is_one()
    {
        Assert.Equal(1, Baseline.CurrentKeyVersion);
    }

    [Fact]
    public void The_baseline_entry_shape_is_frozen()
    {
        // kind|file|key|side，不含行號
        var r = Analyzer.Run(TestPaths.Demo, new Config());
        foreach (var f in r.Findings)
        {
            var parts = f.BaselineKey.Split('|');
            Assert.Equal(4, parts.Length);
            Assert.Equal(Finding.KindSlug(f.Kind), parts[0]);
            Assert.Equal(f.Location.File, parts[1]);
            Assert.Equal(f.Key, parts[2]);
            Assert.Equal(Finding.SideSlug(f.Side), parts[3]);
        }
    }

    [Fact]
    public void A_baseline_file_has_these_fields()
    {
        var json = Baseline.FromFindings(Analyzer.Run(TestPaths.Demo, new Config()).Findings).ToJson("x");
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(["keyVersion", "toolVersion", "entries"],
                     doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
    }

    // ── 7. action.yml 的 inputs ────────────────────────────────────────────

    [Fact]
    public void The_action_inputs_are_frozen()
    {
        var yml = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "action.yml"));
        var inputs = yml[yml.IndexOf("inputs:", StringComparison.Ordinal)..];
        inputs = inputs[..inputs.IndexOf("\nruns:", StringComparison.Ordinal)];

        var actual = Regex.Matches(inputs, @"^  ([a-z-]+):", RegexOptions.Multiline)
                          .Select(m => m.Groups[1].Value).ToArray();

        Assert.Equal(
            ["path", "config", "baseline", "fail-on", "min-coverage", "sarif", "json", "version"],
            actual);
    }

    // ── 8. ignore 註解的語法 ───────────────────────────────────────────────

    [Fact]
    public void The_ignore_marker_is_frozen()
    {
        Assert.Equal("xamlkeys-ignore", XamlScanner.IgnoreMarker);
    }

    [Fact]
    public void An_ignore_without_a_reason_suppresses_nothing_and_is_reported()
    {
        // 理由必填。沒理由的 ignore 是一條沒有交代的靜默壓制通道。
        var dir = Directory.CreateTempSubdirectory("xk-contract-ig");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "App.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(dir.FullName, "App.xaml"), """
                <Application xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                  <Application.Resources><ResourceDictionary>
                    <!-- xamlkeys-ignore -->
                    <SolidColorBrush x:Key="NoReason" Color="#111" />
                    <!-- xamlkeys-ignore: a real reason -->
                    <SolidColorBrush x:Key="WithReason" Color="#222" />
                  </ResourceDictionary></Application.Resources>
                </Application>
                """);

            var r = Analyzer.Run(dir.FullName, new Config());

            Assert.Contains(r.Findings, f => f.Key == "NoReason");
            Assert.DoesNotContain(r.Findings, f => f.Key == "WithReason");
            Assert.Single(r.InvalidIgnores);
        }
        finally { dir.Delete(true); }
    }

    // ── 9. 稽核範圍的硬規則 ────────────────────────────────────────────────

    [Fact]
    public void The_default_exclusions_are_frozen()
    {
        // 改這個會讓同一個 repo 在新版掃到不同的檔案數。
        var dir = Directory.CreateTempSubdirectory("xk-contract-scope");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "App.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup></Project>");

            foreach (var d in new[] { "bin", "obj", "node_modules", "packages", "artifacts",
                                      "TestResults", ".hidden", "Foo.Tests", "TestData" })
            {
                Directory.CreateDirectory(Path.Combine(dir.FullName, d));
                File.WriteAllText(Path.Combine(dir.FullName, d, "X.xaml"),
                    "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" />");
            }

            File.WriteAllText(Path.Combine(dir.FullName, "Kept.xaml"),
                "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" />");

            Assert.Equal(1, Analyzer.Run(dir.FullName, new Config()).XamlFiles);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void Both_xaml_and_axaml_are_scanned()
    {
        var source = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "src/XamlKeys.Core/Analyzer.cs"));
        Assert.Contains("\"*.xaml\"", source);
        Assert.Contains("\"*.axaml\"", source);
    }

    // ── 規格文件與實際輸出要對得上 ─────────────────────────────────────────

    [Fact]
    public void The_report_schema_describes_the_report_the_tool_actually_writes()
    {
        // ⚠ 沒人驗過的 schema 只是裝飾。這裡逐欄比對：
        //    schema 宣告的欄位實際上要存在，實際輸出的欄位 schema 也要寫到。
        using var schema = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "docs/report.schema.json")));
        using var report = JsonDocument.Parse(Json());

        var schemaProps = schema.RootElement.GetProperty("properties");
        var reportProps = report.RootElement;

        foreach (var p in schemaProps.EnumerateObject())
            Assert.True(reportProps.TryGetProperty(p.Name, out _),
                        $"docs/report.schema.json describes '{p.Name}' but the report has no such field");

        foreach (var p in reportProps.EnumerateObject())
            Assert.True(schemaProps.TryGetProperty(p.Name, out _),
                        $"the report writes '{p.Name}' but docs/report.schema.json does not describe it");

        // summary 也要逐欄對
        var schemaSummary = schemaProps.GetProperty("summary").GetProperty("properties");
        var reportSummary = reportProps.GetProperty("summary");

        foreach (var p in schemaSummary.EnumerateObject())
            Assert.True(reportSummary.TryGetProperty(p.Name, out _),
                        $"the schema describes summary.{p.Name} but the report has no such field");

        foreach (var p in reportSummary.EnumerateObject())
            Assert.True(schemaSummary.TryGetProperty(p.Name, out _),
                        $"the report writes summary.{p.Name} but the schema does not describe it");
    }

    [Fact]
    public void The_schema_lists_the_same_finding_kinds_and_severities_as_the_code()
    {
        using var schema = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "docs/report.schema.json")));

        var item = schema.RootElement.GetProperty("properties").GetProperty("findings").GetProperty("items");

        var kinds = item.GetProperty("properties").GetProperty("kind").GetProperty("enum")
                        .EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(Enum.GetValues<FindingKind>().Select(Finding.KindSlug).ToArray(), kinds);

        var sides = item.GetProperty("properties").GetProperty("side").GetProperty("enum")
                        .EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(Enum.GetValues<ThemeSide>().Select(Finding.SideSlug).ToArray(), sides);
    }

    [Fact]
    public void The_config_doc_lists_every_accepted_property()
    {
        // 設定檔的規格文件漏寫一個選項,等於那個選項不存在
        var doc = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "docs/config-schema.md"));
        var source = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "src/XamlKeys.Core/Config.cs"));

        foreach (Match m in Regex.Matches(source, @"case ""([a-zA-Z]+)"":"))
            Assert.True(doc.Contains(m.Groups[1].Value, StringComparison.Ordinal),
                        $"docs/config-schema.md does not mention the '{m.Groups[1].Value}' option");
    }

    // ── 輔助 ───────────────────────────────────────────────────────────────

    private static string Json() => Report.ToJson(Analyzer.Run(TestPaths.Demo, new Config()), "0.0.0-test");

    private static (int Exit, string Out, string Err) RunCli(params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };

        psi.ArgumentList.Add(TestPaths.CliDll);
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = System.Diagnostics.Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd();
        var e = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, o, e);
    }
}
