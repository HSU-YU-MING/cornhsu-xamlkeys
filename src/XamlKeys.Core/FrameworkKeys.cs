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
    private static ThemePack WpfFluentPack => Packs[0];

    /// <summary>執行期由系統決定的鍵：使用者的強調色、系統色。
    /// 清單產不出來（它們不寫在 XAML 裡），用前綴認。</summary>
    private static readonly string[] RuntimePrefixes =
    [
        "SystemAccentColor",
        "SystemColor",
        "SystemControl",
        "SystemChrome",
    ];

    /// <summary>
    /// 選用的主題套件：只有這棵樹真的合併了那個主題時，它的鍵才算「別人提供」。
    /// </summary>
    /// <remarks>
    /// ⚠ 2026-11-15 第五輪語料：Avalonia 專案的 external 大多是 Fluent／Simple 主題的鍵
    ///   （WalletWasabi 131 筆、SukiUI 116 筆），PowerToys 的 exit 3 有一部分是 CommunityToolkit。
    ///   這些主題的鍵名是公開的（MIT），跟 WinUI 一樣產成清單；產生方式見 scripts/regenerate-framework-keys.mjs。
    /// ⚠ 不分條件地豁免，會把沒用那個主題的專案裡的真懸空一起遮掉（同 WPF Fluent 的理由）。
    /// </remarks>
    public sealed class ThemePack(string name, string resource, Func<string, bool> matches)
    {
        public string Name { get; } = name;
        public Func<string, bool> Matches { get; } = matches;
        private readonly Lazy<(HashSet<string> Keys, string Source)> _data =
            new(() => { var src = "unknown"; var set = Load(resource, x => src = x); return (set, src); });
        public HashSet<string> Keys => _data.Value.Keys;
        public string Source => _data.Value.Source;
    }

    private static bool Has(string s, string part) => s.Contains(part, StringComparison.OrdinalIgnoreCase);

    public static readonly ThemePack[] Packs =
    [
        new("wpf-fluent", "XamlKeys.Core.WpfFluentKeys.json", s => Has(s, "PresentationFramework.Fluent")),
        new("avalonia-fluent", "XamlKeys.Core.AvaloniaFluentKeys.json",
            s => s == "<FluentTheme/>" || Has(s, "avares://Avalonia.Themes.Fluent/")),
        new("avalonia-fluent2", "XamlKeys.Core.AvaloniaFluent2Keys.json",
            s => s == "<Fluent2Theme/>" || Has(s, "avares://Avalonia.Themes.Fluent2/")),
        new("avalonia-simple", "XamlKeys.Core.AvaloniaSimpleKeys.json",
            s => s == "<SimpleTheme/>" || Has(s, "avares://Avalonia.Themes.Simple/")),
        new("avalonia-colorpicker", "XamlKeys.Core.AvaloniaColorPickerKeys.json", s => Has(s, "avares://Avalonia.Controls.ColorPicker/")),
        new("avalonia-datagrid", "XamlKeys.Core.AvaloniaDataGridKeys.json", s => Has(s, "avares://Avalonia.Controls.DataGrid/")),
        new("avalonia-treedatagrid", "XamlKeys.Core.AvaloniaTreeDataGridKeys.json", s => Has(s, "avares://Avalonia.Controls.TreeDataGrid/")),
        new("community-toolkit", "XamlKeys.Core.CommunityToolkitKeys.json", s => Has(s, "ms-appx:///CommunityToolkit.WinUI.")),
    ];

    /// <summary>這個合併來源是內容已知的主題嗎（某個選用套件、或 WinUI 一定在的 XamlControlsResources）？
    /// 是的話不算「讀不到」：鍵名都在清單裡。</summary>
    public static bool IsKnownThemeSource(string source) =>
        source == "<XamlControlsResources/>" || Packs.Any(p => p.Matches(source));

    /// <summary>合併來源用到了哪些選用套件。</summary>
    public static IReadOnlySet<string> PacksUsedBy(IEnumerable<string> sources)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var src in sources)
            foreach (var p in Packs)
                if (p.Matches(src)) used.Add(p.Name);
        return used;
    }

    private static string WinUiSource = "unknown";

    public static string WinUiKeySource { get { _ = WinUi.Value; return WinUiSource; } }
    public static string WpfFluentKeySource => WpfFluentPack.Source;
    public static int WpfFluentCount => WpfFluentPack.Keys.Count;

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
    public static bool IsProvidedByFramework(string key, XamlDialect dialect, bool wpfFluentInUse = false) =>
        IsProvidedByFramework(key, dialect, wpfFluentInUse ? new HashSet<string> { "wpf-fluent" } : EmptyPacks);

    private static readonly HashSet<string> EmptyPacks = [];

    /// <param name="packsInUse">這棵樹合併了的選用套件（<see cref="PacksUsedBy"/>）。</param>
    public static bool IsProvidedByFramework(string key, XamlDialect dialect, IReadOnlySet<string> packsInUse)
    {
        foreach (var p in RuntimePrefixes)
            if (key.StartsWith(p, StringComparison.Ordinal)) return true;

        if (dialect == XamlDialect.WinUi && WinUi.Value.Contains(key)) return true;

        foreach (var pack in Packs)
            if (packsInUse.Contains(pack.Name) && pack.Keys.Contains(key)) return true;
        return false;
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
