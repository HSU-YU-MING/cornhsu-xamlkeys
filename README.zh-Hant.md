# XamlKeys

**XAML 資源鍵的靜態作用域稽核。不開 App、全專案掃、CI 退出碼。**

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

> 需要 **.NET 10 runtime**。解析本身是純 XML，Linux CI 也跑得動。

## 它會找出什麼

| 種類 | 分級 | 意思 |
|---|---|---|
| `dangling-static` | **fail** | `{StaticResource K}` 但沒有人定義 `K`。執行期丟例外。 |
| `duplicate-key` | **fail** | 同一本字典裡同一個 `x:Key` 兩次。載入時 parser 丟錯，編譯器不擋。 |
| `dangling-dynamic` | warn | `{DynamicResource K}` / `{ThemeResource K}` 找不到。默默退回預設值。 |
| `out-of-scope` | warn | `K` 在專案裡存在，但不在這個引用看得到的地方。 |
| `shadowed` | warn | 合併順序讓這份定義永遠輪不到，而且贏家的內容**不一樣**。 |
| `shadowed-identical` | info | 同上，但每一份都一模一樣 —— 多餘，不是錯。 |
| `unused` | warn | XAML 與 C# 都沒有引用這個鍵。 |
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
- **函式庫**專案的鍵 → 完全不判（那是它的公開 API）
- 讀不到的字典太多 → **exit 3**，「結果不可信」

`exit 3` 是刻意的。「我看不到你的字典」跟「我看過了沒問題」不可以長成同一個綠勾。

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

baseline 的比對鍵**刻意不含行號** —— 否則一次格式化就讓整份失效。

要豁免單一個鍵（理由必填，而且被壓掉的項目會計入 summary）：

```xml
<!-- xamlkeys-ignore: 這個鍵由外掛在執行期取用 -->
<SolidColorBrush x:Key="PluginAccent" Color="#FF4081" />
```

## 已知盲區

每一條都是拿七個公開專案實跑找出來的 —— 完整歸因見
[docs/實查歸因-2026-11-14.md](docs/實查歸因-2026-11-14.md)。

| 盲區 | 後果 | 工具怎麼處理 |
|---|---|---|
| 執行期組出來的鍵名 | 可能看起來沒人用 | 計入動態查找點；`usedAtRuntime` 可壓制 |
| 第三方組件裡的字典 | 看不到內容 | 標 `external`、計數、拉低覆蓋率 |
| 程式用組出來的 URI 載入的字典 | 可能看起來是孤兒 | 認得 C# 裡的字面 `.xaml` 路徑，組出來的認不得 |
| 作用域是靜態近似 | `UserControl` 真正的父層不可知 | 鍵在別處存在時報 `out-of-scope`（warn），不報懸空（fail） |
| `ComponentResourceKey`、`{x:Static …Key}` | 不是字串鍵 | 計入 unresolved，不評 |
| 函式庫專案 | 它的鍵是公開 API | 不判 `unused` 與 `orphan-dictionary` |
| Avalonia `.axaml` | 掃得到，但**沒有在真實專案上驗證過** | 不宣稱支援 |

## 授權

MIT。內嵌的 WinUI 框架資源鍵清單由
[microsoft/microsoft-ui-xaml](https://github.com/microsoft/microsoft-ui-xaml)（MIT）產生 ——
見 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
