using System.Text;
using System.Text.Json;

namespace XamlKeys.Core;

/// <summary>
/// 報告輸出。
///
/// **summary 與 findings 分兩層**：CI 只消費 findings 的話，「有兩本字典沒讀到」
/// 這件事等於不存在 —— 而那正是「unused 可不可信」的前提。
/// </summary>
public static class Report
{
    /// <summary>JSON 報告的 schema 版本。⚠ 只在 major 遞增；新增欄位不遞增，
    /// 消費端要忽略不認得的欄位。</summary>
    public const int SchemaVersion = 1;

    public static string ToJson(AnalysisResult r, string toolVersion)
    {
        var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("schemaVersion", SchemaVersion);
            w.WriteString("toolVersion", toolVersion);

            w.WriteStartObject("summary");
            w.WriteString("dialect", r.Config.Dialect == XamlDialect.Wpf ? "wpf" : "winui");
            w.WriteBoolean("dialectAutoDetected", r.Config.DialectAutoDetected);
            w.WriteNumber("xamlFiles", r.XamlFiles);
            w.WriteNumber("csharpFiles", r.CSharpFiles);
            w.WriteNumber("dictionaries", r.Dictionaries);
            w.WriteNumber("mergeEdges", r.MergeEdges);
            w.WriteNumber("mergeEdgesResolved", r.MergeEdgesResolved);
            w.WriteNumber("coverage", Math.Round(r.Coverage, 1));
            w.WriteNumber("keysDefined", r.KeysDefined);
            w.WriteNumber("references", r.References);
            w.WriteNumber("suppressed", r.Suppressed);
            w.WriteNumber("parseErrors", r.ParseErrors.Count);
            w.WriteNumber("unresolvedSites", r.UnresolvedSites.Count);
            w.WriteNumber("dynamicKeySites", r.DynamicKeySites.Count);
            w.WriteBoolean("configOverrides", r.Config.HasOverrides);
            w.WriteBoolean("hasApplication", r.HasApplication);
            w.WriteNumber("projects", r.Projects);
            w.WriteNumber("applicationProjects", r.ApplicationProjects);
            // 「工具沒看到」的四個通路。⚠ 拿掉任何一個，對應的退化就變成靜默的。
            w.WriteNumber("libraryKeysNotChecked", r.LibraryKeysNotChecked);
            w.WriteNumber("variantDictionaryGroups", r.VariantDictionaryGroups);
            w.WriteNumber("frameworkProvided", r.FrameworkProvided);
            // 框架鍵清單的來源版本。⚠ 它會過期而且不會有任何東西變紅 ——
            //    放進報告讓使用者看得到它多舊（重產方式見 RELEASING.md）。
            w.WriteString("winUiKeySource", FrameworkKeys.SourceCommit);
            w.WriteNumber("winUiKeyCount", FrameworkKeys.Count);
            w.WriteNumber("runtimeLoadedDictionaries", r.RuntimeLoadedDictionaries);
            w.WriteNumber("implicitThemeDictionaries", r.ImplicitThemeDictionaries);
            w.WriteNumber("unusedDefinitions", r.UnusedDefinitions);
            w.WriteNumber("crossProjectReferences", r.CrossProjectReferences);
            w.WriteNumber("testFilesExcluded", r.TestFilesExcluded);

            w.WriteStartArray("sides");
            foreach (var s in r.Sides) w.WriteStringValue(Finding.SideSlug(s));
            w.WriteEndArray();

            w.WriteStartArray("externalSources");
            foreach (var s in r.ExternalSources) w.WriteStringValue(s);
            w.WriteEndArray();

            w.WriteStartObject("counts");
            foreach (FindingKind k in Enum.GetValues<FindingKind>())
                w.WriteNumber(JsonName(k), r.Count(k));
            w.WriteEndObject();

            w.WriteStartObject("severities");
            w.WriteNumber("fail", r.CountSeverity(Severity.Fail));
            w.WriteNumber("warn", r.CountSeverity(Severity.Warn));
            w.WriteNumber("info", r.CountSeverity(Severity.Info));
            w.WriteEndObject();

            w.WriteEndObject();   // summary

            w.WriteStartArray("findings");
            foreach (var f in r.Findings)
            {
                w.WriteStartObject();
                w.WriteString("kind", Finding.KindSlug(f.Kind));
                w.WriteString("severity", Finding.SeveritySlug(f.Severity));
                w.WriteString("key", f.Key);
                w.WriteString("file", f.Location.File);
                w.WriteNumber("line", f.Location.Line);
                w.WriteString("side", Finding.SideSlug(f.Side));
                w.WriteString("message", f.Message);
                if (f.Related is { } rel)
                {
                    w.WriteStartObject("related");
                    w.WriteString("file", rel.File);
                    w.WriteNumber("line", rel.Line);
                    w.WriteEndObject();
                }
                if (f.ExternalSource is { } ext) w.WriteString("externalSource", ext);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            // 診斷區：工具沒看到的東西。⚠ 不要拿掉 —— 它是「unused 可不可信」的證據。
            w.WriteStartArray("parseErrorDetails");
            foreach (var e in r.ParseErrors)
            {
                w.WriteStartObject();
                w.WriteString("file", e.File);
                w.WriteString("message", e.Message);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("dynamicKeySiteDetails");
            foreach (var d in r.DynamicKeySites)
            {
                w.WriteStartObject();
                w.WriteString("file", d.Location.File);
                w.WriteNumber("line", d.Location.Line);
                w.WriteString("expression", d.Expression);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>counts 區塊的欄位名（camelCase，對應 kind 的 slug）。</summary>
    private static string JsonName(FindingKind k) => k switch
    {
        FindingKind.DanglingStatic => "danglingStatic",
        FindingKind.DanglingDynamic => "danglingDynamic",
        FindingKind.DuplicateKey => "duplicateKey",
        FindingKind.OutOfScope => "outOfScope",
        FindingKind.Shadowed => "shadowed",
        FindingKind.ShadowedIdentical => "shadowedIdentical",
        FindingKind.Unused => "unused",
        FindingKind.OrphanDictionary => "orphanDictionary",
        FindingKind.External => "external",
        _ => k.ToString(),
    };

    // ── console ────────────────────────────────────────────────────────────

    public static void WriteConsole(TextWriter o, AnalysisResult r, bool showInfo)
    {
        var shown = r.Findings.Where(f => showInfo || f.Severity != Severity.Info).ToList();

        foreach (var group in shown.GroupBy(f => f.Location.File).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            o.WriteLine(group.Key);
            foreach (var f in group.OrderBy(f => f.Location.Line))
            {
                var side = f.Side == ThemeSide.Neutral ? "" : $" [{Finding.SideSlug(f.Side)}]";
                o.WriteLine($"  {f.Location.Line,5}  {Finding.SeveritySlug(f.Severity),-4} {Finding.KindSlug(f.Kind),-18} {f.Message}{side}");
            }
            o.WriteLine();
        }

        o.WriteLine($"{r.XamlFiles} XAML file(s), {r.Dictionaries} dictionar(ies), " +
                    $"{r.KeysDefined} key(s), {r.References} reference(s)");
        o.WriteLine($"merged dictionaries resolved: {r.MergeEdgesResolved}/{r.MergeEdges} ({r.Coverage:F1}%)");

        var parts = new List<string>();
        foreach (FindingKind k in Enum.GetValues<FindingKind>())
        {
            var n = r.Count(k);
            if (n > 0) parts.Add($"{Finding.KindSlug(k)} {n}");
        }
        o.WriteLine(parts.Count > 0 ? string.Join(", ", parts) : "no findings");

        // 退化要用喊的
        if (r.LibraryKeysNotChecked > 0)
            o.WriteLine($"note: {r.LibraryKeysNotChecked} key(s) in library projects were not checked for usage -- " +
                        $"a library's keys are its public API and are referenced by its consumers");
        if (r.UnusedDefinitions > r.Count(FindingKind.Unused))
            o.WriteLine($"note: the {r.Count(FindingKind.Unused)} unused key name(s) cover " +
                        $"{r.UnusedDefinitions} definition(s) across variant dictionaries");
        if (r.VariantDictionaryGroups > 0)
            o.WriteLine($"note: {r.VariantDictionaryGroups} dictionary pair(s) look like locale/theme variants " +
                        $"(near-identical key sets) and were not reported as shadowing");
        if (r.FrameworkProvided > 0)
            o.WriteLine($"note: {r.FrameworkProvided} reference(s) resolve to keys the WinUI framework provides");
        if (r.CrossProjectReferences > 0)
            o.WriteLine($"note: {r.CrossProjectReferences} reference(s) point at keys defined in another project " +
                        $"in this tree -- their scope was not checked");
        if (r.TestFilesExcluded > 0)
            o.WriteLine($"note: {r.TestFilesExcluded} XAML file(s) under test paths were skipped " +
                        $"(set includeTests in the config to scan them)");
        if (r.ExternalSources.Count > 0)
            o.WriteLine($"note: {r.ExternalSources.Count} merged dictionar(ies) could not be read -- " +
                        $"'unused' results are incomplete for keys they may define");
        if (r.DynamicKeySites.Count > 0)
            o.WriteLine($"note: {r.DynamicKeySites.Count} runtime-composed resource lookup(s) in C# -- " +
                        $"keys they reach cannot be seen statically (see 'usedAtRuntime' in the config)");
        if (r.ParseErrors.Count > 0)
            o.WriteLine($"note: {r.ParseErrors.Count} file(s) failed to parse and were skipped");
        if (r.Suppressed > 0)
            o.WriteLine($"note: {r.Suppressed} finding(s) suppressed by ignore comments or config");

        // 第一次在舊專案上跑一定是一片紅。不講這句的話,使用者看到的是一面牆,
        // 而解法（baseline）寫在 README 裡 —— 他還沒讀到那裡就已經把工具關掉了。
        if (r.Findings.Count >= 50)
            o.WriteLine($"tip: {r.Findings.Count} findings is a lot for a first run. " +
                        $"Record them once with --write-baseline <path>, then use --baseline <path> " +
                        $"so that only new findings fail.");
    }

    // ── MSBuild 診斷格式（給 Visual Studio 的錯誤清單） ─────────────────────

    public static void WriteMsBuild(TextWriter o, AnalysisResult r)
    {
        foreach (var f in r.Findings)
        {
            if (f.Severity == Severity.Info) continue;
            var level = f.Severity == Severity.Fail ? "error" : "warning";
            o.WriteLine($"{f.Location.File}({f.Location.Line}): {level} {RuleId(f.Kind)}: {f.Message}");
        }
    }

    /// <summary>⚠ 規則 ID 是凍結面：發出去之後不能改號，使用者會用它做抑制。</summary>
    public static string RuleId(FindingKind k) => k switch
    {
        FindingKind.DanglingStatic => "XK0001",
        FindingKind.DuplicateKey => "XK0002",
        FindingKind.DanglingDynamic => "XK0003",
        FindingKind.OutOfScope => "XK0004",
        FindingKind.Shadowed => "XK0005",
        FindingKind.ShadowedIdentical => "XK0009",
        FindingKind.Unused => "XK0006",
        FindingKind.OrphanDictionary => "XK0007",
        FindingKind.External => "XK0008",
        _ => "XK0000",
    };
}
