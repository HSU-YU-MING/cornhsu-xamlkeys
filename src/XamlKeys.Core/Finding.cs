namespace XamlKeys.Core;

/// <summary>分級。JSON 與 console 一律用這三個英文字 —— CI 腳本 match 的是它。</summary>
public enum Severity
{
    /// <summary>只計數，不影響退出碼。</summary>
    Info,
    Warn,
    Fail,
}

/// <summary>
/// Finding 的種類。
///
/// ⚠ 排序即嚴重度：改動既有項目的語意要升 major（JSON 的 kind 是凍結面）。
/// 新增 kind 是加法 —— 消費端要忽略不認得的值。
/// </summary>
public enum FindingKind
{
    /// <summary>{StaticResource K} 在作用域裡找不到 K，而且全專案都沒有這個鍵。
    /// 執行期 WPF 會丟 ResourceReferenceKeyNotFoundException —— App 直接掛。</summary>
    DanglingStatic,

    /// <summary>{DynamicResource K} / {ThemeResource K} 找不到。退回預設值，不爆但畫面會怪。</summary>
    DanglingDynamic,

    /// <summary>同一本字典裡同一個鍵定義了兩次。這是**非法 XAML** ——
    /// 微軟文件明寫鍵在單一字典內必須唯一，載入時由 parser 丟錯。編譯器不擋。</summary>
    DuplicateKey,

    /// <summary>鍵在專案裡找得到，但不在這個引用的作用域鏈上。
    /// ⚠ 本工具的作用域是靜態近似（看不到 UserControl 在執行期被放進誰底下），
    /// 所以這一類刻意比 DanglingStatic 輕 —— 它有可能是我們看漏，不是使用者寫錯。</summary>
    OutOfScope,

    /// <summary>同一條合併鏈上同一個鍵被定義多次，而且內容**不一樣** ——
    /// 後者（WPF）默默贏，畫面會跟某一份定義寫的不同。</summary>
    Shadowed,

    /// <summary>同一條合併鏈上被定義多次，但每一份**一模一樣**。
    /// 結果正確，只是多餘。實查：wpfui 與 MaterialDesignInXamlToolkit 在每個控制項的
    /// 樣式檔裡各宣告一次同樣的轉換器 —— 報成 warn 會淹掉真正有問題的那幾筆。</summary>
    ShadowedIdentical,

    /// <summary>定義了，但沒有任何引用解析到它。</summary>
    Unused,

    /// <summary>一本獨立的資源字典檔沒有被任何地方合併，也沒有被 C# 載入 —— 整本都進不了作用域。</summary>
    OrphanDictionary,

    /// <summary>引用的鍵只可能來自解析不到的外部字典。不是錯，是看不到。</summary>
    External,
}

public sealed class Finding
{
    public required FindingKind Kind { get; init; }
    public required Severity Severity { get; set; }
    public required string Key { get; init; }
    public required SourceLocation Location { get; init; }

    /// <summary>給人看的一句話，英文（JSON 與 console 共用）。</summary>
    public required string Message { get; init; }

    /// <summary>相關的另一個位置：Shadowed 的贏家、DuplicateKey 的第一次定義、Unused 的定義處。</summary>
    public SourceLocation? Related { get; init; }

    /// <summary>外部字典的來源字串（External 專用）。</summary>
    public string? ExternalSource { get; init; }

    public ThemeSide Side { get; init; } = ThemeSide.Neutral;

    /// <summary>unused 一筆背後有幾份定義（同一個鍵名在幾本字典裡各定義一次）。只用來算 summary，不輸出。
    /// ⚠ baseline 濾掉一筆 unused 時要跟著扣，否則「0 個鍵名對應 22 份定義」這種自相矛盾的句子就會出現。</summary>
    public int Definitions { get; init; } = 1;

    /// <summary>baseline 的比對鍵。⚠ 刻意不含行號 —— 行號會隨無關的編輯漂移，
    /// 一次格式化就讓整份 baseline 失效。</summary>
    public string BaselineKey => $"{KindSlug(Kind)}|{Location.File}|{Key}|{SideSlug(Side)}";

    public static string KindSlug(FindingKind k) => k switch
    {
        FindingKind.DanglingStatic => "dangling-static",
        FindingKind.DanglingDynamic => "dangling-dynamic",
        FindingKind.DuplicateKey => "duplicate-key",
        FindingKind.OutOfScope => "out-of-scope",
        FindingKind.Shadowed => "shadowed",
        FindingKind.ShadowedIdentical => "shadowed-identical",
        FindingKind.Unused => "unused",
        FindingKind.OrphanDictionary => "orphan-dictionary",
        FindingKind.External => "external",
        _ => k.ToString().ToLowerInvariant(),
    };

    public static string SideSlug(ThemeSide s) => s switch
    {
        ThemeSide.Dark => "dark",
        ThemeSide.Light => "light",
        ThemeSide.HighContrast => "high-contrast",
        _ => "neutral",
    };

    public static string SeveritySlug(Severity s) => s switch
    {
        Severity.Fail => "fail",
        Severity.Warn => "warn",
        _ => "info",
    };
}
