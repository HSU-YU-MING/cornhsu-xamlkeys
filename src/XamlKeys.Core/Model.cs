namespace XamlKeys.Core;

/// <summary>引用的寫法。StaticResource 解析不到會在執行期丟例外；Dynamic／Theme 只是退回預設值。</summary>
public enum RefKind
{
    /// <summary>{StaticResource K} 或 &lt;StaticResource ResourceKey="K"/&gt;</summary>
    Static,
    /// <summary>{DynamicResource K}</summary>
    Dynamic,
    /// <summary>{ThemeResource K}（WinUI／UWP）</summary>
    Theme,
    /// <summary>C# 的 FindResource("K") 之類。執行期沿真實樹查找，沒有靜態作用域。</summary>
    CSharp,
}

/// <summary>主題側。沿用 XamlContrast 的概念：只在 Dark 定義的鍵，在 Light 側是解析不到的。</summary>
public enum ThemeSide
{
    /// <summary>兩側共用</summary>
    Neutral,
    Dark,
    Light,

    /// <summary>WinUI 的 ThemeDictionaries 第三本。
    /// ⚠ 它跟 Dark／Light 是**互斥**的第三種主題，不是「兩側共用」——
    /// 把它當 Neutral 會讓它的鍵跟深淺兩側的同名鍵互相「覆蓋」。
    /// 實測：WinUI Gallery 因此冒出 11 筆假 shadowed。
    /// 預設不評這一側（大多數 App 只覆寫少數幾個鍵，逐一評會是整片噪音）。</summary>
    HighContrast,

    /// <summary>ThemeDictionaries 裡的 "Default"，而且 Light 與 Dark 都已經各有一本 ——
    /// 那本在執行期永遠輪不到。獨立成一側，免得它跟兩側的同名鍵互相「覆蓋」。</summary>
    Fallback,
}

/// <summary>原始碼位置。File 一律是相對於掃描根的正斜線路徑 —— baseline 要跨 OS 可攜。</summary>
public readonly record struct SourceLocation(string File, int Line)
{
    public override string ToString() => $"{File}:{Line}";
}

/// <summary>一個 x:Key 定義。</summary>
public sealed class KeyDefinition
{
    public required string Key { get; init; }
    public required SourceLocation Location { get; init; }
    public required int DictionaryId { get; init; }

    /// <summary>元素名稱（SolidColorBrush、Style…）。只拿來讓報告好讀。</summary>
    public string? ElementName { get; init; }

    /// <summary>資源別名：&lt;StaticResource x:Key="A" ResourceKey="B"/&gt; —— 定義 A 的同時引用 B。</summary>
    public string? AliasTarget { get; init; }

    /// <summary>這個定義被 ignore 註解壓掉了（理由）。</summary>
    public string? SuppressedReason { get; set; }

    /// <summary>資源內容的正規化字串（去掉 x:Key／x:Name、壓掉空白）。
    /// 用來區分「被一個**一模一樣**的定義蓋掉」（多餘但無害）與
    /// 「被一個**不同的**定義蓋掉」（畫面真的會不一樣）。
    /// 實查：wpfui 與 MaterialDesign 在每個控制項的樣式檔裡各宣告一次同樣的轉換器，
    /// 不分這兩者的話，報出來的全是無害的重複宣告。</summary>
    public string? ContentHash { get; init; }

    public override string ToString() => $"{Key} @ {Location}";
}

/// <summary>一個資源引用。</summary>
public sealed class KeyReference
{
    public required string Key { get; init; }
    public required SourceLocation Location { get; init; }
    public required RefKind Kind { get; init; }

    /// <summary>這個引用所在的作用域鏈（由內而外的字典 Id）。C# 引用是空的 —— 執行期才知道。</summary>
    public required IReadOnlyList<int> ScopeChain { get; init; }

    public string? SuppressedReason { get; set; }

    public override string ToString() => $"{{{Kind}Resource {Key}}} @ {Location}";
}

/// <summary>合併進來的一本字典。解析不到來源時 ResolvedDictionaryId 是 null —— 那是 external。</summary>
public sealed class MergedReference
{
    public required string RawSource { get; init; }
    public required SourceLocation Location { get; init; }
    public int? ResolvedDictionaryId { get; set; }

    /// <summary>解析不到的原因（跨組件、pack URI 指向 NuGet 套件、動態組出來的 Source…）。</summary>
    public string? ExternalReason { get; set; }

    /// <summary>跨組件引用時的組件名（pack://application:,,,/MyLib;component/… 的 MyLib）。</summary>
    public string? Assembly { get; init; }

    public bool IsExternal => ResolvedDictionaryId is null;
}

/// <summary>一本資源字典。可能是獨立的 .xaml 檔，也可能是某個元素底下的 &lt;X.Resources&gt;。</summary>
public sealed class ResourceDictionaryNode
{
    public required int Id { get; init; }
    public required string File { get; init; }

    /// <summary>擁有者元素（Application.Resources、Window.Resources…）。獨立字典檔是 null。</summary>
    public string? Owner { get; init; }

    public required SourceLocation Location { get; init; }

    /// <summary>這本字典屬於哪一側。ThemeDictionaries 裡的字典、或 config 標記的主題檔才不是 Neutral。</summary>
    public ThemeSide Side { get; set; } = ThemeSide.Neutral;

    /// <summary>根元素就是 ResourceDictionary 的獨立檔（可以被 Source 合併）。</summary>
    public bool IsStandaloneFile { get; init; }

    /// <summary>這是 Application.Resources —— 作用域鏈的最外層。</summary>
    public bool IsApplicationScope { get; init; }

    /// <summary>程式在執行期把它合併進 Application.Resources（主題切換最常見的寫法）。
    /// ⚠ 不認這條的話，「用 C# 套主題」的專案會整批冒出 out-of-scope ——
    /// 實測 wpfui 385 筆、QuillNest 的 DarkTheme.xaml 也是這個形狀。</summary>
    public bool IsRuntimeLoaded { get; set; }

    public List<KeyDefinition> OwnKeys { get; } = [];

    /// <summary>⚠ 宣告順序。WPF 是「後宣告的贏」，WinUI 相反 —— 查找時由 Dialect 決定方向。</summary>
    public List<MergedReference> Merged { get; } = [];

    public override string ToString() => Owner is null ? File : $"{File} ({Owner})";
}

/// <summary>XAML 方言。合併字典的優先序在 WPF 與 WinUI 是相反的。</summary>
public enum XamlDialect
{
    /// <summary>WPF：合併字典中「最後宣告的」贏（微軟文件：the last dictionary found sequentially）。</summary>
    Wpf,
    /// <summary>WinUI／UWP：相反，依宣告順序的「反向」檢查，等於「先宣告的」贏。</summary>
    WinUi,
}

/// <summary>查找結果三態。⚠ NotFound 與 Unknown 一定要分開 ——
/// 把「看不到的外部字典」當成 NotFound，就是把第三方套件的每一個鍵都報成懸空。</summary>
public enum LookupStatus
{
    Found,
    /// <summary>作用域鏈完整走完，確定沒有。</summary>
    NotFound,
    /// <summary>路上有解析不到的外部字典 —— 不能斷言沒有。</summary>
    Unknown,
}

public sealed record LookupResult(
    LookupStatus Status,
    KeyDefinition? Definition = null,
    IReadOnlyList<KeyDefinition>? Shadowed = null,
    string? ExternalSource = null);
