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
    private static readonly Lazy<HashSet<string>> WinUi =
        new(() => Load("XamlKeys.Core.WinUiFrameworkKeys.json", s => WinUiSource = s));

    /// <summary>.NET 9 起 WPF 內建的 Fluent 主題（PresentationFramework.Fluent）的鍵。</summary>
    private static readonly Lazy<HashSet<string>> WpfFluent =
        new(() => Load("XamlKeys.Core.WpfFluentKeys.json", s => WpfFluentSource = s));

    /// <summary>執行期由系統決定的鍵：使用者的強調色、系統色。
    /// 清單產不出來（它們不寫在 XAML 裡），用前綴認。</summary>
    private static readonly string[] RuntimePrefixes =
    [
        "SystemAccentColor",
        "SystemColor",
        "SystemControl",
        "SystemChrome",
    ];

    private static string WinUiSource = "unknown";
    private static string WpfFluentSource = "unknown";

    public static string WinUiKeySource { get { _ = WinUi.Value; return WinUiSource; } }
    public static string WpfFluentKeySource { get { _ = WpfFluent.Value; return WpfFluentSource; } }
    public static int WpfFluentCount => WpfFluent.Value.Count;

    /// <summary>
    /// 這個鍵是不是框架／平台提供的？
    ///
    /// 兩段判斷：
    ///   - **執行期前綴**（SystemAccentColor…／SystemColor…）在**每一種方言**都算。
    ///     那是作業系統給的值（使用者的強調色、系統色），不是誰的 repo 裡的東西。
    ///     實測（Avalonia）：只對 WinUI 生效的話，`SystemAccentColor` 會在
    ///     Avalonia 專案裡冒出 161 筆假 dangling。WPF 的系統色是
    ///     <c>{x:Static SystemColors.…}</c>（不是字串鍵），所以沒有誤殺的風險。
    ///   - **WinUI 的 3,573 個具名鍵**只在 WinUI 方言查 —— 那些名字在別的生態系
    ///     可能是使用者自己定義的。
    /// </summary>
    /// <param name="wpfFluentInUse">這棵樹有沒有真的引用 .NET 內建的 Fluent 主題
    /// （<c>PresentationFramework.Fluent</c>）。
    /// ⚠ WPF 的 Fluent 主題是**選用**的，WinUI 的框架資源則是一定在 ——
    /// 所以 Fluent 的 955 個鍵只在真的有引用時才豁免。
    /// 不分條件地豁免，會把沒用 Fluent 的 WPF 專案裡的真懸空一起遮掉。</param>
    public static bool IsProvidedByFramework(string key, XamlDialect dialect, bool wpfFluentInUse = false)
    {
        foreach (var p in RuntimePrefixes)
            if (key.StartsWith(p, StringComparison.Ordinal)) return true;

        if (dialect == XamlDialect.WinUi && WinUi.Value.Contains(key)) return true;

        return wpfFluentInUse && WpfFluent.Value.Contains(key);
    }

    /// <summary>合併來源字串看起來是在引用 .NET 內建的 Fluent 主題嗎？</summary>
    public static bool IsWpfFluentSource(string source) =>
        source.Contains("PresentationFramework.Fluent", StringComparison.OrdinalIgnoreCase);

    public static int Count => WinUi.Value.Count;


    private static HashSet<string> Load(string resourceName, Action<string> setSource)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);

        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        if (stream is null) return set;

        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;

        if (root.TryGetProperty("source", out var src) && src.GetString() is { } s)
            setSource(s);

        if (root.TryGetProperty("keys", out var keys) && keys.ValueKind == JsonValueKind.Array)
            foreach (var k in keys.EnumerateArray())
                if (k.GetString() is { Length: > 0 } key) set.Add(key);

        return set;
    }
}
