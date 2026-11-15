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

    /// <summary>鍵是 <c>{x:Static …}</c> 或 <c>{ComponentResourceKey …}</c>（Key 是 MarkupKeys 正規化後的身分）。</summary>
    public bool IsMarkupKey { get; init; }
    /// <summary>鍵屬於框架（<c>MenuItem.SeparatorStyleKey</c>…）：框架的樣板會讀，重新定義它不算沒人用。</summary>
    public bool IsFrameworkKey { get; init; }
    /// <summary>markup 鍵在 C# 裡會出現的名字（成員名／ResourceId）。</summary>
    public string? MemberName { get; init; }
    /// <summary>markup 鍵的型別短名（C# 裡找不到 = 別的組件的鍵）。</summary>
    public string? MarkupType { get; init; }
    /// <summary>markup 鍵的型別的 CLR 命名空間（XAML 寫明的才有）。有的話比對型別時連命名空間一起比。</summary>
    public string? MarkupTypeNamespace { get; init; }

    /// <summary>元素上的 x:Name。code-behind 會產生同名的欄位（C#）或存取函式（C++/WinRT）。</summary>
    public string? FieldName { get; init; }

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

    /// <summary>鍵是 <c>{x:Static …}</c> 或 <c>{ComponentResourceKey …}</c>（Key 是正規化後的身分）。</summary>
    public bool IsMarkupKey { get; init; }
    /// <summary>框架自己的鍵（<c>SystemColors.ControlBrushKey</c>…）：找不到也不是懸空。</summary>
    public bool IsFrameworkKey { get; init; }

    /// <summary>C# 的 <c>Resources["K"] = v</c>：是寫入（執行期定義），不是查找。
    /// 判 unused 時照舊算引用（保守），判懸空時跳過 —— 寫入一個 XAML 沒有的鍵不是錯。</summary>
    public bool IsWrite { get; init; }

    /// <summary>這個引用所在的作用域鏈（由內而外的字典 Id）。C# 引用是空的 —— 執行期才知道。</summary>
    public required IReadOnlyList<int> ScopeChain { get; init; }

    public string? SuppressedReason { get; set; }

    /// <summary>
    /// 這個引用寫在哪一個「有鍵的資源」裡面（最內層那個）。寫在畫面本體、隱含樣式裡的是 null。
    /// </summary>
    /// <remarks>
    /// 用來判斷連帶的「沒人用」：<c>AccentColor</c> 只被 <c>AccentBrush</c> 的定義引用，
    /// 而 <c>AccentBrush</c> 本身沒人用 —— 那 <c>AccentColor</c> 其實也沒人用（QuillNest 實測）。
    /// 別名 <c>&lt;StaticResource x:Key="A" ResourceKey="B"/&gt;</c> 的 Owner 就是 A 自己。
    /// </remarks>
    public KeyDefinition? Owner { get; init; }

    /// <summary>
    /// 這個引用寫在哪些「樹裡有 x:Class 的自訂元素」裡面（含它所在的那個元素）：那些類別自己的
    /// 根層 &lt;X.Resources&gt; 字典 Id，由內而外。執行期查找會經過那些實例，它們的資源在建構時就填好了。
    /// </summary>
    /// <remarks>2026-11-15 實測 MVVM-Samples：頁面寫 &lt;controls:InteractiveSample ControlTemplate="{StaticResource InteractiveSampleTemplate}"/&gt;，
    /// 那個鍵定義在 InteractiveSample.xaml 自己的資源裡 —— 舊版報 13 筆 out-of-scope。</remarks>
    public IReadOnlyList<int>? EnclosingClassScopes { get; set; }

    public override string ToString() => $"{{{Kind}Resource {Key}}} @ {Location}";
}

/// <summary>自訂控制項在 XAML 裡被放置的一處（&lt;views:SearchView/&gt;），與那個位置的作用域鏈。</summary>
public sealed record ElementUse(string ClassName, SourceLocation Location, IReadOnlyList<int> Chain);

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

    /// <summary>這本字典所屬的專案目錄（不在任何專案底下就是 null）。
    /// ⚠ Application.Resources 的作用域是**該專案的**，不是整棵樹的。
    /// 實測（wpfui）：把全樹的 Application.Resources 都接到每一次查找上，
    /// 結果是某個範例 App 合併的一本看不到的字典，污染了函式庫裡每一個檔案的作用域 ——
    /// 一筆真的懸空引用因此被降級成 external。</summary>
    public string? ProjectDir { get; set; }

    /// <summary>不在任何專案目錄底下、但被專案用 Link 連進去的字典：那些專案（例如 Windows-universal-samples 的 SharedContent/xaml/App.xaml）。</summary>
    public IReadOnlyList<string> LinkedProjects { get; set; } = [];

    /// <summary>同一個 &lt;ResourceDictionary.ThemeDictionaries&gt; 群組的識別碼（不是主題字典就是 null）。
    /// ⚠ 同一組裡的字典**本質上互斥**（執行期只會選中一本），所以它們之間永遠不是「覆蓋」。
    /// 這條比用鍵名（Dark／Light／Default／HighContrast）去推穩健得多 ——
    /// 實測 Files 的 GridSplitter.xaml 是 Default ＋ HighContrast 的組合，
    /// 靠鍵名推會判成互相覆蓋。</summary>
    public int? ThemeGroupId { get; set; }

    /// <summary>根元素是 &lt;Styles&gt; 的 Avalonia 樣式檔。它不是 ResourceDictionary，
    /// 不判 orphan-dictionary（以元素寫法或程式載入的樣式檔很常見，也不在舊版的檢查範圍）。</summary>
    public bool IsStylesFile { get; set; }

    /// <summary>不在任何 Resources／MergedDictionaries 裡的 &lt;ResourceDictionary&gt; 元素（圖表的 Palette 項目）。鍵由那個控制項照慣例讀，不判 unused。</summary>
    public bool IsInlineCollectionItem { get; set; }

    /// <summary>這本字典依哪一套合併優先序解析。
    /// ⚠ 逐**專案**決定，不是整次掃描一個值：一棵樹裡可以同時有 WPF 與 WinUI 專案
    /// （ModernWpf 就是 WPF 函式庫配一個 WinUI 的輔助工具）。</summary>
    public XamlDialect Dialect { get; set; } = XamlDialect.Wpf;

    /// <summary>根元素就是 ResourceDictionary 的獨立檔（可以被 Source 合併）。</summary>
    public bool IsStandaloneFile { get; init; }

    /// <summary>這是 Application.Resources —— 作用域鏈的最外層。</summary>
    public bool IsApplicationScope { get; init; }

    /// <summary>程式在執行期把它合併進 Application.Resources（主題切換最常見的寫法）。
    /// ⚠ 不認這條的話，「用 C# 套主題」的專案會整批冒出 out-of-scope ——
    /// 實測 wpfui 385 筆、QuillNest 的 DarkTheme.xaml 也是這個形狀。</summary>
    public bool IsRuntimeLoaded { get; set; }

    /// <summary>x:Class 字典在 C# 裡被 new 出來（<c>MergedDictionaries.Add(new DarkTheme())</c>）。只代表「不是孤兒」——
    /// 不知道它被放進哪個作用域，所以**不**加進 Application 作用域（那是 IsRuntimeLoaded 的事）。</summary>
    /// <remarks>⚠ 2026-11-15 第八輪：第一版把它當成 IsRuntimeLoaded，Avalonia 自己的 repo 有 new FluentTheme()、
    ///   SukiUI 有 new 主題類別 —— 它們整本（含看不到的 &lt;SimpleTheme/&gt;）變成全專案的作用域，
    ///   SukiUI 已回報上游的真 bug（CWhite-）因此被降級成「也許在看不到的那本裡」。</remarks>
    public bool IsCreatedInCode { get; set; }

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
    string? ExternalSource = null);
