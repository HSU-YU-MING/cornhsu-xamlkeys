using System.Text;
using System.Text.Json;

namespace XamlKeys.Core;

/// <summary>
/// 已知債清單（ratchet）。
///
/// 真實專案第一次跑一定是一片紅 —— 「零 finding 才給過」的 gate 第一天就會被關掉。
/// baseline 讓既有的債被記下來，之後只擋**新增的**。
///
/// ⚠ 比對鍵刻意不含行號：行號會隨無關的編輯漂移，一次格式化就讓整份 baseline 失效，
///   使用者只好重產，於是真正的新債也一起被洗白。
/// </summary>
public sealed class Baseline
{
    /// <summary>比對鍵的寫法版本。鍵的組成改了就遞增 —— 舊 baseline 要能被認出來並要求重產。</summary>
    public const int CurrentKeyVersion = 1;

    public int KeyVersion { get; init; } = CurrentKeyVersion;
    public HashSet<string> Entries { get; init; } = new(StringComparer.Ordinal);

    public static Baseline FromFindings(IEnumerable<Finding> findings) => new()
    {
        Entries = findings.Select(f => f.BaselineKey).ToHashSet(StringComparer.Ordinal),
    };

    public static Baseline Load(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (IOException ex) { throw new ConfigException($"cannot read baseline '{path}': {ex.Message}"); }

        JsonDocument doc;
        try { doc = JsonDocument.Parse(text); }
        catch (JsonException ex) { throw new ConfigException($"baseline is not valid JSON: {ex.Message}"); }

        using (doc)
        {
            var root = doc.RootElement;
            var version = root.TryGetProperty("keyVersion", out var kv) && kv.TryGetInt32(out var v) ? v : 0;

            if (version != CurrentKeyVersion)
                throw new ConfigException(
                    $"baseline keyVersion {version} was written by a different version of the tool " +
                    $"(this build writes {CurrentKeyVersion}); regenerate it with --write-baseline");

            var entries = new HashSet<string>(StringComparer.Ordinal);
            if (root.TryGetProperty("entries", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var e in arr.EnumerateArray())
                    if (e.GetString() is { Length: > 0 } s) entries.Add(s);

            return new Baseline { KeyVersion = version, Entries = entries };
        }
    }

    public string ToJson(string toolVersion)
    {
        var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("keyVersion", KeyVersion);
            w.WriteString("toolVersion", toolVersion);
            w.WriteStartArray("entries");
            foreach (var e in Entries.OrderBy(e => e, StringComparer.Ordinal)) w.WriteStringValue(e);
            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>把已知債從 findings 裡挑掉。回傳被挑掉的筆數（要進 summary.suppressed）。</summary>
    public int Apply(AnalysisResult result)
    {
        var hidden = result.Findings.Where(f => Entries.Contains(f.BaselineKey)).ToList();
        result.Findings.RemoveAll(f => Entries.Contains(f.BaselineKey));

        result.Suppressed += hidden.Count;
        result.BaselineSuppressed += hidden.Count;
        result.UnusedDefinitions -= hidden.Where(f => f.Kind == FindingKind.Unused).Sum(f => f.Definitions);
        return hidden.Count;
    }

    /// <summary>baseline 裡有、這次沒看到的項目 —— 債還掉了，可以重產 baseline。</summary>
    public IEnumerable<string> Stale(IEnumerable<Finding> current)
    {
        var now = current.Select(f => f.BaselineKey).ToHashSet(StringComparer.Ordinal);
        return Entries.Where(e => !now.Contains(e));
    }
}
