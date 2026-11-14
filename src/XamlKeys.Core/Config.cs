using System.Text.Json;

namespace XamlKeys.Core;

/// <summary>設定檔讀取失敗。訊息要指到欄位 —— 使用者才知道改哪一行。</summary>
public sealed class ConfigException(string message) : Exception(message);

/// <summary>
/// xamlkeys.config.json。
///
/// 設計原則：**每一個會讓結果變寬鬆的設定，都要在 summary 留下痕跡。**
/// 否則 config 就是一條靜默退化通道（XamlContrast 8.1 規則 3）。
/// </summary>
public sealed class Config
{
    public const string DefaultFileName = "xamlkeys.config.json";

    public XamlDialect Dialect { get; set; } = XamlDialect.Wpf;

    /// <summary>true 表示 dialect 沒有明寫、由偵測決定。要寫進 summary。</summary>
    public bool DialectAutoDetected { get; set; } = true;

    /// <summary>這棵樹裡同時有 WPF 與 WinUI 專案（兩者的合併優先序相反）。
    /// 每本字典用自己專案的方言，但這件事要在 summary 講出來。</summary>
    public bool DialectMixed { get; set; }

    /// <summary>額外排除的路徑片段（除了內建的 bin/obj/點開頭目錄）。</summary>
    public List<string> Exclude { get; } = [];

    /// <summary>連測試專案與測試素材資料夾一起掃。預設關 ——
    /// 測試素材常常是**刻意寫壞的** XAML，報出來只是噪音。</summary>
    public bool IncludeTests { get; set; }

    /// <summary>視為「執行期會用到」的鍵樣式（支援結尾 * 萬用）。被蓋到的不報 unused，但計入 suppressed。</summary>
    public List<string> UsedAtRuntime { get; } = [];

    /// <summary>外部字典的補路徑：組件名 → 目錄。指得到就解析進來，精度升級。</summary>
    public Dictionary<string, string> ExternalDictionaries { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>主題檔對應：把特定檔案標成深／淺側。</summary>
    public List<string> DarkFiles { get; } = [];
    public List<string> LightFiles { get; } = [];

    /// <summary>gate：fail（只擋 Fail 級）或 warn（Warn 以上都擋）。</summary>
    public Severity FailOn { get; set; } = Severity.Fail;

    /// <summary>合併字典解析成功率下限（百分比）。低於它 → exit 3「結果不可信」。</summary>
    public double MinCoverage { get; set; } = 80.0;

    /// <summary>每一類 finding 的分級覆寫。非預設值要寫進 summary。</summary>
    public Dictionary<FindingKind, Severity> SeverityOverrides { get; } = [];

    public static Severity DefaultSeverity(FindingKind kind) => kind switch
    {
        FindingKind.DanglingStatic => Severity.Fail,
        FindingKind.DuplicateKey => Severity.Fail,
        FindingKind.DanglingDynamic => Severity.Warn,
        FindingKind.OutOfScope => Severity.Warn,
        FindingKind.Shadowed => Severity.Warn,
        FindingKind.ShadowedIdentical => Severity.Info,
        FindingKind.Unused => Severity.Warn,
        FindingKind.OrphanDictionary => Severity.Warn,
        FindingKind.External => Severity.Info,
        _ => Severity.Info,
    };

    public Severity SeverityFor(FindingKind kind) =>
        SeverityOverrides.TryGetValue(kind, out var s) ? s : DefaultSeverity(kind);

    /// <summary>有任何非預設的設定嗎？summary 要據此標記。</summary>
    public bool HasOverrides =>
        SeverityOverrides.Count > 0 || UsedAtRuntime.Count > 0 ||
        ExternalDictionaries.Count > 0 || Exclude.Count > 0 || IncludeTests ||
        Math.Abs(MinCoverage - 80.0) > double.Epsilon;

    public static Config Load(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (IOException ex) { throw new ConfigException($"cannot read config '{path}': {ex.Message}"); }

        JsonDocument doc;
        try { doc = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException ex) { throw new ConfigException($"config is not valid JSON: {ex.Message}"); }

        using (doc)
        {
            var cfg = new Config();
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object)
                throw new ConfigException("config root must be an object");

            foreach (var prop in r.EnumerateObject())
            {
                switch (prop.Name)
                {
                    case "dialect":
                        cfg.Dialect = ReadString(prop) switch
                        {
                            "wpf" => XamlDialect.Wpf,
                            "winui" => XamlDialect.WinUi,
                            var other => throw new ConfigException($"dialect must be 'wpf' or 'winui' (got '{other}')"),
                        };
                        cfg.DialectAutoDetected = false;
                        break;

                    case "exclude": ReadStrings(prop, cfg.Exclude); break;

                    case "includeTests":
                        if (prop.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                            throw new ConfigException("includeTests must be true or false");
                        cfg.IncludeTests = prop.Value.GetBoolean();
                        break;
                    case "usedAtRuntime": ReadStrings(prop, cfg.UsedAtRuntime); break;

                    case "externalDictionaries":
                        if (prop.Value.ValueKind != JsonValueKind.Array)
                            throw new ConfigException("externalDictionaries must be an array");
                        foreach (var e in prop.Value.EnumerateArray())
                        {
                            var asm = e.TryGetProperty("assembly", out var a) ? a.GetString() : null;
                            var p = e.TryGetProperty("path", out var pp) ? pp.GetString() : null;
                            if (string.IsNullOrWhiteSpace(asm) || string.IsNullOrWhiteSpace(p))
                                throw new ConfigException("each externalDictionaries entry needs 'assembly' and 'path'");
                            cfg.ExternalDictionaries[asm] = p;
                        }
                        break;

                    case "themeFiles":
                        if (prop.Value.ValueKind != JsonValueKind.Object)
                            throw new ConfigException("themeFiles must be an object with 'dark' and 'light'");
                        if (prop.Value.TryGetProperty("dark", out var dk)) ReadStringsInto(dk, cfg.DarkFiles, "themeFiles.dark");
                        if (prop.Value.TryGetProperty("light", out var lt)) ReadStringsInto(lt, cfg.LightFiles, "themeFiles.light");
                        break;

                    case "failOn":
                        cfg.FailOn = ReadString(prop) switch
                        {
                            "fail" => Severity.Fail,
                            "warn" => Severity.Warn,
                            var other => throw new ConfigException($"failOn must be 'fail' or 'warn' (got '{other}')"),
                        };
                        break;

                    case "minCoverage":
                        if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetDouble(out var mc)
                            || !double.IsFinite(mc) || mc is < 0 or > 100)
                            throw new ConfigException("minCoverage must be a number between 0 and 100");
                        cfg.MinCoverage = mc;
                        break;

                    case "severities":
                        if (prop.Value.ValueKind != JsonValueKind.Object)
                            throw new ConfigException("severities must be an object");
                        foreach (var s in prop.Value.EnumerateObject())
                        {
                            var kind = ParseKind(s.Name)
                                ?? throw new ConfigException($"severities: unknown finding kind '{s.Name}'");
                            var sev = s.Value.GetString() switch
                            {
                                "fail" => Severity.Fail,
                                "warn" => Severity.Warn,
                                "info" => Severity.Info,
                                var o => throw new ConfigException($"severities.{s.Name} must be fail/warn/info (got '{o}')"),
                            };
                            cfg.SeverityOverrides[kind] = sev;
                        }
                        break;

                    default:
                        // ⚠ 不認得的屬性是錯，不是忽略：打錯字的設定靜默失效，
                        //    使用者會以為自己設了，實際上沒有。
                        throw new ConfigException($"unknown config property '{prop.Name}'");
                }
            }

            return cfg;
        }
    }

    public static FindingKind? ParseKind(string slug) => slug switch
    {
        "dangling-static" => FindingKind.DanglingStatic,
        "dangling-dynamic" => FindingKind.DanglingDynamic,
        "duplicate-key" => FindingKind.DuplicateKey,
        "out-of-scope" => FindingKind.OutOfScope,
        "shadowed" => FindingKind.Shadowed,
        "shadowed-identical" => FindingKind.ShadowedIdentical,
        "unused" => FindingKind.Unused,
        "orphan-dictionary" => FindingKind.OrphanDictionary,
        "external" => FindingKind.External,
        _ => null,
    };

    private static string ReadString(JsonProperty p) =>
        p.Value.ValueKind == JsonValueKind.String
            ? p.Value.GetString() ?? ""
            : throw new ConfigException($"{p.Name} must be a string");

    private static void ReadStrings(JsonProperty p, List<string> into) => ReadStringsInto(p.Value, into, p.Name);

    private static void ReadStringsInto(JsonElement e, List<string> into, string name)
    {
        if (e.ValueKind != JsonValueKind.Array)
            throw new ConfigException($"{name} must be an array of strings");

        foreach (var item in e.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new ConfigException($"{name} must be an array of strings");
            if (item.GetString() is { Length: > 0 } s) into.Add(s);
        }
    }

    /// <summary>結尾萬用比對：Icon* 比對 IconSave；完全相等也算。</summary>
    public static bool MatchesGlob(string value, string pattern)
    {
        if (pattern.EndsWith('*'))
            return value.StartsWith(pattern[..^1], StringComparison.Ordinal);
        return string.Equals(value, pattern, StringComparison.Ordinal);
    }
}
