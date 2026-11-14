# <img src="https://raw.githubusercontent.com/HSU-YU-MING/cornhsu-xamlkeys/main/icon.png" width="28" alt=""/> XamlKeys

**XAML 資源鍵的靜態作用域稽核。支援 WPF、WinUI、Avalonia 與 .NET MAUI。不開 App、全專案掃、CI 退出碼。**

[![NuGet](https://img.shields.io/nuget/v/Cornhsu.XamlKeys)](https://www.nuget.org/packages/Cornhsu.XamlKeys)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

[English](README.md)

`{StaticResource}` 指向一個不存在的鍵，執行期會丟例外。同一本字典裡放兩個一樣的 `x:Key`，
載入時 parser 一樣會丟錯 —— 而**這兩件事編譯器都不檢查**。
被合併順序默默蓋掉的鍵會一直畫錯顏色。沒人用的鍵會一直躺在那裡，直到有人刪錯那一個。

XamlKeys 解析你的 XAML，算出**每一個引用實際上會解析到哪一份定義**，有問題就讓 CI 紅燈。

```bash
dotnet tool install -g Cornhsu.XamlKeys
xamlkeys path/to/your/wpf/project
```

> 需要 **.NET 10 runtime**。解析本身是純 XML、不依賴 WPF，Linux CI 一樣跑得動 ——
> 這個 repo 自己的 CI 就是在 `ubuntu-24.04` 上建置、測試與守門的。

## 它會找出什麼

| 種類 | 分級 | 意思 |
|---|---|---|
| `dangling-static` | **fail** | `{StaticResource K}` 但沒有人定義 `K`。執行期丟例外——載入時，或寫在 Style 的 Setter 裡時要等那個屬性第一次被用到（`FocusVisualStyle` 的 Setter 要等鍵盤焦點，所以視窗能正常開、之後才當掉）。 |
| `duplicate-key` | **fail** | 同一本字典裡同一個 `x:Key` 兩次。載入時 parser 丟錯，編譯器不擋。 |
| `dangling-dynamic` | warn | `{DynamicResource K}` / `{ThemeResource K}` 找不到。默默退回預設值。 |
| `out-of-scope` | warn | `K` 在專案裡存在，但不在這個引用看得到的地方。 |
| `shadowed` | warn | 合併順序讓這份定義永遠輪不到，而且贏家的內容**不一樣**。 |
| `shadowed-identical` | info | 同上，但每一份都一模一樣 —— 多餘，不是錯。 |
| `unused` | warn | XAML 與 C# 都沒有引用這個鍵；或某個視窗自己的那一份，視窗裡沒有任何東西碰得到（複製貼上之後忘了接的樣式）。 |
| `orphan-dictionary` | warn | 沒有人合併、程式也沒有載入的資源字典。 |
| `external` | info | 這個鍵可能來自一本讀不到的合併字典。 |

預設只有 `fail` 會擋建置。`--fail-on warn` 可以把門關緊。

## 為什麼「合併順序」就是這題的全部

把所有 `x:Key` 找出來是半小時的事。價值在下一步：**哪一份定義會贏。**

WPF 查找資源的順序是：元素自己的 `Resources` → 沿樹往上每一層 → `Application.Resources`。
單一字典內部的規則更刁：字典**自己的**項目優先於合併進來的，而合併進來的字典之間
**最後宣告的贏** —— 跨合併字典的重複鍵是合法的，不會丟錯。**WinUI 的順序相反。**

所以這三個問題都不是搜文字能回答的：

- 「定義了兩次」只有在兩份都在同一個查找作用域裡才是問題
- 「沒人用」取決於誰看得到它
- 「不存在」取決於你站在哪裡

## 它不會叫你刪掉它看不見的東西

這類工具最糟的失敗，是報出「48 個鍵沒人用」，使用者照著刪，然後 App 壞掉。
所以每一塊盲區都**計數並印出來**，絕不吞掉：

- 讀不到的合併字典 → `external`，而且 **unused 的結論被宣告為不完整**
- C# 裡組出來的鍵名（`FindResource($"Icon{name}")`）→ 計入動態查找點
- 先放進變數再交給查找的鍵名（`var k = ok ? "Green" : "Red"; ...SetResourceReference(p, k)`）
  → 那個檔裡跟鍵同名的字串都算有人用，而且數量會印出來
- **函式庫**專案的鍵 → 完全不判（那是它的公開 API）
- 讀不到的字典太多 → **exit 3**，「結果不可信」

`exit 3` 是刻意的。「我看不到你的字典」跟「我看過了沒問題」不可以長成同一個綠勾。

## HTML 報告

主控台在舊專案上就是一面牆（實測 ScreenToGif 1,100 筆）。`--html` 產生一份**單一檔案**的報告：
內嵌 CSS、**沒有 script**（篩選是純 CSS）、沒有外部資源、沒有時間戳，
CI 的 artifact 直接點開就能看，也 diff 得動。

```bash
xamlkeys src --html xamlkeys-report.html
```

頁面最上面是「這次執行看不到什麼」，因為那是「unused 可不可信」的前提。

## 退出碼

| 碼 | 意思 |
|---|---|
| 0 | 通過 |
| 1 | 有達到 gate 的 finding |
| 2 | 用法、設定或輸入檔錯誤 |
| 3 | 結果不可信（什麼都沒掃到，或太多字典讀不到） |

## 導入既有專案

真實專案一開始一定有債。記錄一次，之後只擋新增的：

```bash
xamlkeys src --write-baseline xamlkeys-baseline.json   # 一次，檔案進版控
xamlkeys src --baseline xamlkeys-baseline.json         # 之後都這樣跑
```

還掉一些債之後，執行結果會提醒你（`tip: 3 baseline entr(ies) no longer occur`）。
這時把棘輪收緊，修好的項目才不會悄悄回來：

```bash
xamlkeys src --baseline xamlkeys-baseline.json --stale                                       # 看哪些不見了
xamlkeys src --baseline xamlkeys-baseline.json --write-baseline xamlkeys-baseline.json       # 收緊
```

baseline 的比對鍵**刻意不含行號** —— 否則一次格式化就讓整份失效。

要豁免單一個鍵（理由必填，而且被壓掉的項目會計入 summary）：

```xml
<!-- xamlkeys-ignore: 這個鍵由外掛在執行期取用 -->
<SolidColorBrush x:Key="PluginAccent" Color="#FF4081" />
```

## 放進 CI

```yaml
- run: dotnet tool install -g Cornhsu.XamlKeys
- run: xamlkeys src --sarif xamlkeys.sarif
- uses: github/codeql-action/upload-sarif@v3
  with:
    sarif_file: xamlkeys.sarif
```

## 設定檔

放在被掃描的資料夾旁邊、檔名 `xamlkeys.config.json`，或用 `--config <path>` 指定：

```json
{
  "dialect": "wpf",
  "failOn": "fail",
  "minCoverage": 80,
  "usedAtRuntime": ["Icon*", "Status.*"],
  "exclude": ["Generated"],
  "includeTests": false,
  "externalDictionaries": [
    { "assembly": "HandyControl", "path": "vendor/HandyControl/Themes" }
  ],
  "themeFiles": { "dark": ["Themes/Dark"], "light": ["Themes/Light"] },
  "severities": { "unused": "info", "out-of-scope": "fail" }
}
```

寫錯的屬性名會直接**報錯**，不會被默默略過 —— 一個拼錯而沒有作用的設定，比沒有設定更糟。

`externalDictionaries` 把組件名對到**掃描範圍內**的目錄（通常是 vendored 的函式庫主題副本）。範圍外的目錄一律忽略：
這個工具只讀掃描根底下的 XAML；而且設定檔是從被掃的 repo 撿的，裡面的路徑不能讓它碰到 `\\主機\分享` 或機器上其他地方。
每一格的說明在 [docs/config-schema.md](docs/config-schema.md)。

## 選項

```
--json <path>            寫出 JSON 報告
--sarif <path>           寫出 SARIF 2.1.0 報告（只含 fail／warn）
--html <path>            寫出單檔 HTML 報告（沒有 script、沒有外部檔案）
--msbuild                另外印出 MSBuild 診斷，給 VS 的錯誤清單用
--config <path>          設定檔（預設：<path> 裡的 xamlkeys.config.json）
--baseline <path>        只回報不在這份 baseline 裡的 finding
--write-baseline <path>  把目前的 finding 寫成 baseline
--stale                  搭配 --baseline，列出已經不再出現的項目
--fail-on warn|fail      守門等級（預設：fail）
--dialect wpf|winui      合併順序的方言（預設：由專案檔判定）
--min-coverage <0-100>   合併字典至少要有多少比例解析得到（預設 80）
--exclude <fragment>     略過路徑中含有這段文字的檔案（可重複）
--include-tests          連測試專案與 fixture 資料夾一起掃
--show-info              info 等級的 finding 也印出來
```

## 已知盲區

每一條都是拿十七個公開專案（WPF、WinUI、Avalonia、.NET MAUI，約 5,600 個 XAML 檔）實跑找出來的 —— 完整歸因見
[docs/實查歸因-2026-11-14.md](docs/實查歸因-2026-11-14.md)。

| 盲區 | 後果 | 工具怎麼處理 |
|---|---|---|
| 執行期組出來的鍵名 | 可能看起來沒人用 | 有 3 個字以上的固定部分（`$"Icon{n}"`、`"Icon" + n`、`n + "Brush"`）時，同專案裡符合的鍵算有人用，summary 會講有幾個；完全由變數決定的計入動態查找點，`usedAtRuntime` 可壓制 |
| 第三方組件裡的字典 | 看不到內容 | 標 `external`、計數、拉低覆蓋率。例外：工具內建鍵名清單的主題 —— WinUI 的 `XamlControlsResources`、.NET 的 WPF Fluent、Avalonia 的 Fluent／Fluent2／Simple／ColorPicker／DataGrid／TreeDataGrid、Windows Community Toolkit、Semi.Avalonia、Uno Material／Cupertino／Toolkit、ModernWpf —— 算解析得到，而且只在這棵樹真的合併了才算 |
| 程式用組出來的 URI 載入的字典 | 可能看起來是孤兒 | 認得 C# 裡的字面 `.xaml` 路徑，也認得有固定部分的（`$"Themes/{name}.xaml"` 會載入該專案的每一本 `Themes/*.xaml`）；`$"{name}.xaml"` 與用 `+` 串的路徑認不得 |
| 作用域是靜態近似 | `UserControl` 真正的父層不在它自己的檔裡 | `DynamicResource`：控制項在 XAML 裡每一個被放置的地方都有這個鍵（而且 C# 沒有另外建立它）就算解析得到。其餘鍵在別處存在時報 `out-of-scope`（warn），不報懸空（fail）。`StaticResource` 不從宿主找：它在控制項被放進去之前就解析了 |
| `ComponentResourceKey`、`{x:Static …Key}` | 不是字串鍵 | 框架的鍵（`SystemColors.…Key`、`MenuItem.SeparatorStyleKey`）認得：引用不算懸空、重新定義不算沒人用。專案自己的鍵以型別＋成員（或型別＋`ResourceId`）比對：重複、覆蓋、不在作用域、沒人用都會判。找不到的照舊計入 unresolved、不報懸空 —— 它可能定義在 C# 或別的組件裡 |
| 從別的專案連結進來的字典（`<Page Include="..\x.xaml" Link="…">`） | 合併路徑在磁碟上不存在 | 照 csproj 的 `Link` 解析。Include 有萬用字元或 `$(…)` 屬性的靜態算不出來，照舊 external |
| 函式庫專案 | 它的鍵是公開 API | 不判 `unused` 與 `orphan-dictionary` |
| 符號連結與目錄接合點 | 裡面的內容會被跳過 | 跟著走的話，指回祖先的接合點會讓掃描永遠跑不完 |
| Avalonia 的自訂主題變體（`x:Key="{x:Static …}"`） | 當成中性側 | 互斥規則仍然適用，所以它們之間不會互相覆蓋 |

## 授權

MIT。內嵌的 WinUI 框架資源鍵清單由
[microsoft/microsoft-ui-xaml](https://github.com/microsoft/microsoft-ui-xaml)（MIT）產生 ——
見 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
