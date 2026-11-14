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
            // ⚠ 一棵樹裡可以同時有 WPF 與 WinUI 專案,兩者的合併優先序相反。
            //    每本字典用自己專案的方言,但這件事要看得見。
            w.WriteBoolean("dialectMixed", r.Config.DialectMixed);
            w.WriteNumber("xamlFiles", r.XamlFiles);
            w.WriteNumber("csharpFiles", r.CSharpFiles);
            w.WriteNumber("dictionaries", r.Dictionaries);
            w.WriteNumber("mergeEdges", r.MergeEdges);
            w.WriteNumber("mergeEdgesResolved", r.MergeEdgesResolved);
            w.WriteNumber("coverage", Math.Round(r.Coverage, 1));
            w.WriteNumber("keysDefined", r.KeysDefined);
            w.WriteNumber("references", r.References);
            w.WriteNumber("suppressed", r.Suppressed);
            w.WriteNumber("baselineSuppressed", r.BaselineSuppressed);
            w.WriteNumber("parseErrors", r.ParseErrors.Count);
            w.WriteNumber("unresolvedSites", r.UnresolvedSites.Count);
            w.WriteNumber("invalidIgnores", r.InvalidIgnores.Count);
            w.WriteNumber("dynamicKeySites", r.DynamicKeySites.Count);
            w.WriteNumber("keysNamedNearDynamicLookups", r.KeysNamedNearDynamicLookups);
            w.WriteNumber("frameworkKeyOverrides", r.FrameworkKeyOverrides);
            w.WriteNumber("unusedOnlyViaUnused", r.UnusedOnlyViaUnused);
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
            w.WriteString("winUiKeySource", FrameworkKeys.WinUiKeySource);
            w.WriteNumber("winUiKeyCount", FrameworkKeys.Count);
            w.WriteBoolean("wpfFluentThemeInUse", r.WpfFluentThemeInUse);
            w.WriteString("wpfFluentKeySource", FrameworkKeys.WpfFluentKeySource);
            w.WriteNumber("wpfFluentKeyCount", FrameworkKeys.WpfFluentCount);
            w.WriteNumber("runtimeLoadedDictionaries", r.RuntimeLoadedDictionaries);
            w.WriteNumber("implicitThemeDictionaries", r.ImplicitThemeDictionaries);
            w.WriteNumber("runtimeSelectedFamilyFiles", r.RuntimeSelectedFamilyFiles);
            w.WriteNumber("libraryUnresolvedReferences", r.LibraryUnresolvedReferences);
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

            w.WriteStartArray("invalidIgnoreDetails");
            foreach (var l in r.InvalidIgnores)
            {
                w.WriteStartObject();
                w.WriteString("file", l.File);
                w.WriteNumber("line", l.Line);
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
            o.WriteLine(LineStart(group.Key));
            foreach (var f in group.OrderBy(f => f.Location.Line))
            {
                var side = f.Side == ThemeSide.Neutral ? "" : $" [{Finding.SideSlug(f.Side)}]";
                o.WriteLine($"  {f.Location.Line,5}  {Finding.SeveritySlug(f.Severity),-4} {Finding.KindSlug(f.Kind),-18} {OneLine(f.Message)}{side}");
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
            o.WriteLine($"note: the {r.Count(FindingKind.Unused)} unused finding(s) cover {r.UnusedDefinitions} " +
                        $"definition(s) -- a key name defined in several dictionaries is reported once");
        if (r.LibraryUnresolvedReferences > 0)
            o.WriteLine($"note: {r.LibraryUnresolvedReferences} reference(s) in library projects resolve to nothing " +
                        $"in their own project; a consumer may supply them, so they are not gated");
        if (r.RuntimeSelectedFamilyFiles > 0)
            o.WriteLine($"note: {r.RuntimeSelectedFamilyFiles} unmerged dictionar(ies) look like a runtime-selected " +
                        $"family (same directory, near-identical key sets) and were not reported as orphans");
        if (r.VariantDictionaryGroups > 0)
            o.WriteLine($"note: {r.VariantDictionaryGroups} dictionary pair(s) look like locale/theme variants " +
                        $"(near-identical key sets) and were not reported as shadowing");
        if (r.FrameworkProvided > 0)
            o.WriteLine($"note: {r.FrameworkProvided} reference(s) resolve to keys the WinUI framework provides");
        if (r.Config.DialectMixed)
            o.WriteLine("note: this tree contains both WPF and WinUI projects, whose merge order is opposite; " +
                        "each dictionary was resolved with its own project's rules");
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
        if (r.FrameworkKeyOverrides > 0)
            o.WriteLine($"note: {r.FrameworkKeyOverrides} key(s) redefine a key the framework itself reads (lightweight styling) " +
                        $"-- not reported as unused");
        if (r.UnusedOnlyViaUnused > 0)
            o.WriteLine($"note: {r.UnusedOnlyViaUnused} of the unused key(s) are referenced only from inside other unused resources");
        if (r.KeysNamedNearDynamicLookups > 0)
            o.WriteLine($"note: {r.KeysNamedNearDynamicLookups} key(s) were counted as used because their name appears " +
                        $"as a string in a C# file that looks resources up by variable");
        if (r.ParseErrors.Count > 0)
            o.WriteLine($"note: {r.ParseErrors.Count} file(s) failed to parse and were skipped");
        if (r.InvalidIgnores.Count > 0)
            o.WriteLine($"warning: {r.InvalidIgnores.Count} ignore comment(s) have no reason and were ignored " +
                        $"themselves -- write '<!-- xamlkeys-ignore: why -->' (see summary.invalidIgnoreDetails)");
        if (r.Suppressed - r.BaselineSuppressed > 0)
            o.WriteLine($"note: {r.Suppressed - r.BaselineSuppressed} finding(s) suppressed by ignore comments or config");
        if (r.BaselineSuppressed > 0)
            o.WriteLine($"note: {r.BaselineSuppressed} known finding(s) hidden by the baseline");

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
            o.WriteLine($"{LineStart(f.Location.File)}({f.Location.Line}): {level} {RuleId(f.Kind)}: {OneLine(f.Message)}");
        }
    }

    // ── 文字輸出的消毒 ─────────────────────────────────────────────────────

    /// <summary>
    /// 把掃描到的文字（鍵名、檔名、訊息）壓成「一定只佔一行、而且不是 CI 指令」的樣子。
    ///
    /// ⚠ 這個工具會在 CI 上掃**別人送來的 PR**。XAML 的屬性值可以用 `&amp;#10;` 塞進真的換行，
    ///   於是一個鍵名就能在 log 裡自己開一行 —— 實測可以偽造 MSBuild 診斷、
    ///   可以讓 GitHub Actions 執行 `::error::`／`::add-mask::`，
    ///   在 Azure Pipelines 上 `##vso[task.setvariable ...]` 甚至能改後續步驟的變數。
    ///   JSON 與 SARIF 由序列化器轉義、HTML 由 HtmlEncode 處理；只有純文字輸出要自己來。
    /// </summary>
    public static string OneLine(string s)
    {
        var clean = true;
        foreach (var c in s)
            if (char.IsControl(c) || c is '\u2028' or '\u2029') { clean = false; break; }

        if (clean && !s.Contains("##", StringComparison.Ordinal)) return s;

        var sb = new StringBuilder(s.Length + 8);
        foreach (var c in s)
        {
            switch (c)
            {
                case '\n': sb.Append(@"\n"); break;
                case '\r': sb.Append(@"\r"); break;
                case '\t': sb.Append(@"\t"); break;
                default:
                    if (char.IsControl(c) || c is '\u2028' or '\u2029')
                        sb.Append(@"\u").Append(((int)c).ToString("X4"));
                    else
                        sb.Append(c);
                    break;
            }
        }

        // Azure Pipelines（`##vso[`）與 GitHub Actions 舊格式（`##[`）在一行的**任何位置**都會被當成指令
        return sb.Replace("##vso[", @"#\u0023vso[").Replace("##[", @"#\u0023[").ToString();
    }

    /// <summary>
    /// 放在一行開頭的檔名。GitHub Actions 把以 `::` 開頭的行當成指令，
    /// 而 Linux 上的資料夾可以就叫 `::error::...` —— 補一個 `./` 就不再是行首。
    /// </summary>
    public static string LineStart(string path)
    {
        var p = OneLine(path);
        return p.TrimStart().StartsWith("::", StringComparison.Ordinal) ? "./" + p : p;
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
