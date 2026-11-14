using System.Text;
using System.Text.Json;

namespace XamlKeys.Core;

public static partial class ReportSarif
{
    /// <summary>SARIF 2.1.0。GitHub code scanning 與 VS 都吃這個格式。
    /// ⚠ 只輸出 fail/warn —— info 級（external）是診斷資訊，灌進 code scanning 只會變成噪音。</summary>
    public static string ToSarif(AnalysisResult r, string toolVersion)
    {
        var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("$schema", "https://raw.githubusercontent.com/oasis-tcs/sarif-spec/master/Schemata/sarif-schema-2.1.0.json");
            w.WriteString("version", "2.1.0");

            w.WriteStartArray("runs");
            w.WriteStartObject();

            w.WriteStartObject("tool");
            w.WriteStartObject("driver");
            w.WriteString("name", "XamlKeys");
            w.WriteString("version", toolVersion);
            w.WriteString("informationUri", "https://github.com/HSU-YU-MING/cornhsu-xamlkeys");

            w.WriteStartArray("rules");
            foreach (FindingKind k in Enum.GetValues<FindingKind>())
            {
                w.WriteStartObject();
                w.WriteString("id", Report.RuleId(k));
                w.WriteString("name", Finding.KindSlug(k));
                w.WriteStartObject("shortDescription");
                w.WriteString("text", RuleText(k));
                w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteEndObject();   // driver
            w.WriteEndObject();   // tool

            w.WriteStartArray("results");
            foreach (var f in r.Findings)
            {
                if (f.Severity == Severity.Info) continue;

                w.WriteStartObject();
                w.WriteString("ruleId", Report.RuleId(f.Kind));
                w.WriteString("level", f.Severity == Severity.Fail ? "error" : "warning");

                w.WriteStartObject("message");
                w.WriteString("text", f.Message);
                w.WriteEndObject();

                w.WriteStartArray("locations");
                w.WriteStartObject();
                w.WriteStartObject("physicalLocation");
                w.WriteStartObject("artifactLocation");
                w.WriteString("uri", f.Location.File);
                w.WriteEndObject();
                w.WriteStartObject("region");
                w.WriteNumber("startLine", Math.Max(1, f.Location.Line));
                w.WriteEndObject();
                w.WriteEndObject();
                w.WriteEndObject();
                w.WriteEndArray();

                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteEndObject();   // run
            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string RuleText(FindingKind k) => k switch
    {
        FindingKind.DanglingStatic => "A {StaticResource} reference whose key is defined nowhere; throws at load time.",
        FindingKind.DanglingDynamic => "A {DynamicResource}/{ThemeResource} reference whose key is defined nowhere.",
        FindingKind.DuplicateKey => "The same x:Key is defined twice in one dictionary; the XAML parser throws at load time.",
        FindingKind.OutOfScope => "The key exists in the project but not in the scope of this reference.",
        FindingKind.Shadowed => "A definition that merge order makes unreachable, with different content.",
        FindingKind.ShadowedIdentical => "A redundant but identical duplicate definition.",
        FindingKind.Unused => "A key that nothing references.",
        FindingKind.OrphanDictionary => "A ResourceDictionary that nothing merges.",
        FindingKind.External => "The key may come from a merged dictionary that could not be read.",
        _ => "",
    };
}
