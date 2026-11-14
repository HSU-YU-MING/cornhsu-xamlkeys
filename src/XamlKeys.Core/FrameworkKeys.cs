using System.Reflection;
using System.Text.Json;

namespace XamlKeys.Core;

/// <summary>
/// 框架自己提供的資源鍵。
///
/// 為什麼需要：WinUI 專案的 XAML 大量引用框架內建的鍵
/// （<c>TextFillColorPrimaryBrush</c>、<c>ControlCornerRadius</c>、<c>BodyTextBlockStyle</c>…），
/// 那些定義不在使用者的 repo 裡。
///
/// **實測（2026-11-14，WinUI Gallery）**：沒有這份清單時，工具報出 195 個不重複的
/// 假懸空（309 筆 dangling-static ＋ 666 筆 dangling-dynamic）。那種第一天就滿螢幕紅字的
/// 工具不會有第二天。
///
/// 資料來源與重新產生方式見 <c>scripts/regenerate-framework-keys.mjs</c>（microsoft-ui-xaml，MIT）。
/// </summary>
public static class FrameworkKeys
{
    private static readonly Lazy<HashSet<string>> WinUi = new(Load);

    /// <summary>執行期由系統決定的鍵：使用者的強調色、系統色。
    /// 清單產不出來（它們不寫在 XAML 裡），用前綴認。</summary>
    private static readonly string[] RuntimePrefixes =
    [
        "SystemAccentColor",
        "SystemColor",
        "SystemControl",
        "SystemChrome",
    ];

    public static string SourceCommit { get; private set; } = "unknown";

    /// <summary>這個鍵是不是框架提供的？只有 WinUI 方言才查 ——
    /// WPF 的框架資源幾乎都是型別鍵或 <c>{x:Static SystemColors.…}</c>，不是字串鍵。</summary>
    public static bool IsProvidedByFramework(string key, XamlDialect dialect)
    {
        if (dialect != XamlDialect.WinUi) return false;
        if (WinUi.Value.Contains(key)) return true;

        foreach (var p in RuntimePrefixes)
            if (key.StartsWith(p, StringComparison.Ordinal)) return true;

        return false;
    }

    public static int Count => WinUi.Value.Count;

    /// <summary>
    /// 這份清單會過期：WinUI 每出一版就多幾個框架鍵，而清單沒跟上的話，
    /// 那些新鍵會被報成懸空 —— **而且不會有任何東西變紅**，只會慢慢多出假警報。
    ///
    /// 沒有辦法在離線的情況下知道上游有沒有變，所以這裡退而求其次：
    /// 把「這份資料是什麼時候的」放進報告，讓使用者自己看得到它老了。
    /// 重新產生的方式見 RELEASING.md。
    /// </summary>
    public const string RegenerateHint = "scripts/regenerate-framework-keys.mjs";

    private static HashSet<string> Load()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);

        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("XamlKeys.Core.WinUiFrameworkKeys.json");
        if (stream is null) return set;

        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;

        if (root.TryGetProperty("source", out var src) && src.GetString() is { } s)
            SourceCommit = s;

        if (root.TryGetProperty("keys", out var keys) && keys.ValueKind == JsonValueKind.Array)
            foreach (var k in keys.EnumerateArray())
                if (k.GetString() is { Length: > 0 } key) set.Add(key);

        return set;
    }
}
