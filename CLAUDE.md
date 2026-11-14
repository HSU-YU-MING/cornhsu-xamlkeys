# XamlKeys — 工作規則

XAML 資源鍵的靜態作用域稽核。CLI + 函式庫，純 `System.Xml.Linq`，不依賴 WPF runtime。

## 三條憲法

1. **靜態分析 + 全量掃描 + CI 退出碼。** 三者缺一就退回成既有工具的樣子。
2. **「工具沒看到」與「看到了沒問題」必須分得開。**
   本工具最嚴重的失敗模式不是漏報，是**叫人刪掉其實有用的東西**。
   每一條會讓結果變寬鬆的路徑，都要在 summary 留下計數。
3. **每條規則來自實查，而且實查的樣本必須包含非作者的專案。**
   自家三個專案全是 WPF 應用程式 —— 函式庫、pack URI、ThemeDictionaries、WinUI
   那幾類缺陷在它們身上**一條都測不到**。第一版在七個公開專案上吐出約 36,000 筆假警報；
   後來擴到十七個公開專案（四輪），每一輪都還找得到新的 —— 動規則前先想「這在別人的專案上長怎樣」。

## 改動時最容易踩的坑

- **`ProjectIndex` 收絕對路徑，finding 帶相對路徑。** 相對路徑直接丟 `Path.GetFullPath`
  會對著行程的工作目錄解析 → 每個引用都比對不到專案 → 整批被當成「函式庫、跨專案」靜默跳過。
  用 `Context.Abs()`。這個 bug 曾經讓一整類檢查被關掉，而且**看起來像修好了**。
- **框架鍵的檢查要排在查找之後。** 排在前面會把本來解析得到的引用短路
  （wpfui 3,021 筆）。
- **外部字典解析不到 → `Unknown`，不是 `NotFound`。** 這是第一不變量。
- **方言（WPF／WinUI）由 csproj 判定，不是 XAML 寫法。** 判錯會把合併優先序整個倒過來，
  `shadowed` 的贏家與輸家對調。
- **輸出一律 ASCII。** Windows 主控台（cp950）會把 `——` 印成亂碼。
  `OutputContractTests.All_console_and_report_text_is_ascii` 守這條。
- **`x:` 有兩個命名空間。** WPF／WinUI／Avalonia 是 `winfx/2006/xaml`，
  **.NET MAUI 是 `winfx/2009/xaml`**。寫死 2006 的那一版，MAUI 專案是 0 個鍵、
  19,157 筆假懸空。要取 `x:Key`／`x:Name` 一律走 `XamlAttr()`／`IsXamlDirective()`。
- **跨專案的比對一律要先限定專案。** 已經踩過三次：作用域鏈（wpfui）、
  方言（ModernWpf）、程式載入的字典（maui-samples）。只比檔名或只看「這棵樹裡有沒有」，
  在多專案的 repo 上就是一次大赦。
- **掃到的東西都是別人的輸入。** 這個工具在 CI 上掃 PR，鍵名、檔名、baseline 的內容都不可信。
  - 純文字輸出（主控台、`--msbuild`、`--stale`）裡只要有掃描來的字串，一律過 `Report.OneLine()`；
    放在行首的檔名用 `Report.LineStart()`。`x:Key="A&#10;::error::..."` 是合法的 XAML，
    不消毒的話鍵名可以偽造診斷、下 GitHub／Azure 的 CI 指令。JSON／SARIF／baseline **不要**消毒。
  - `XamlScanner` 的走訪是遞迴的，靠 `MaxElementDepth`（串流階段就擋）保護。
    新增任何「對元素樹遞迴」的程式碼之前，確認它在那道檢查之後。
  - `action.yml` 的 input 一律走 `env`，不要在 `run:` 裡寫 `${{ inputs.x }}`。
  - `HostileInputTests` 守這三條。
- **C# 的查找要同時認「字面」與「變數」兩種寫法。** 只加了字面版本、沒加變數版本的話，
  那類查找連動態查找點都算不到，summary 不會警告。`CSharpScannerTests` 兩邊各有一組。
  `Resources[k] = v` 是寫入，不能算成查找（否則主題服務的色盤會讓整排鍵都「有人用」）。
  ⚠ 「不是字面」指**整個參數**不是單一字串字面，不是「不以引號開頭」：`FindResource("Pic01" + n)`
  曾經兩邊都不算，完全隱形（`DynamicLookupRegex` 的註解有經過）。
- **組出來但有固定開頭／結尾的鍵名與字典路徑會比對**（`$"Icon{n}"`、`"Icon" + n`、
  `$"Themes/{name}.xaml"`）。這是放寬，三道保險缺一不可：固定部分至少 3 個字
  （`DynamicKeySite.MinPatternLength`）、只比同一個專案、計數（`keysMatchingComposedLookups`、
  `runtimeLoadedDictionaries`）。`ComposedLookupTests` 的每一條都以變異測試確認過。
  **測整條流程，不要只測解析函式**：第一版的單元測試全綠，語料一跑才發現字串開頭的串接根本沒進到解析。
- **`unused` 有兩條路**：`EmitUnused` 以名字算一次（為了在地化），`EmitDeadLocalCopies` 抓畫面裡
  死掉的同名副本。後者的每一個 `continue` 都是一種「執行期找得到」，拿掉任何一個，
  `DeadLocalCopyTests` 都會紅 —— 新增例外時也要照這個標準補測試。
- **`baseline` 的比對鍵不含行號。** 含了的話一次格式化就讓整份失效。
- **ignore 註解按行比對定義** —— 同一行有兩個 `x:Key` 時會一起壓掉（已知盲區）。
- **連帶的「沒人用」只從已經報出來的定義往下推**（`EmitUnusedOnlyViaUnused`）。
  每個引用都記著它寫在哪個有鍵的資源裡（`KeyReference.Owner`）；一個鍵的**每一個**引用都在
  已判死的資源裡才算。它會讓 `unused` 多報，所以：起點必須已經通過全部例外、訊息一定要講出
  是被誰拖下水的。這原本是 README 的已知盲區（死掉的別名把目標養著），2026-11-14 補上。
- **重新定義框架內建鍵不是沒人用**（WinUI 輕量樣式覆寫）。框架的樣板在執行期讀它，
  引用不在原始碼樹裡。計數在 `frameworkKeyOverrides`。

## 判定規則的來源

每一條規則在 `tests/XamlKeys.Tests/CorpusLessonTests.cs` 都有對應的測試，
測試註解寫明「來源專案」與「不做會怎樣」。完整歸因在
`docs/實查歸因-2026-11-14.md`。**新增規則時照這個形狀做**：先有實查，再有測試，再有規則。

## 凍結面（1.0 之後不能破）

CLI 旗標與位置參數、退出碼四碼的語意、JSON `schemaVersion`、SARIF 規則 ID（`XK00xx`）、
`xamlkeys.config.json` schema、baseline 檔格式（`keyVersion`）、`action.yml` 的 inputs、
ignore 註解語法、稽核範圍的硬規則。

**目前在 0.x，還沒凍結。** 0.x 期間 README 要明寫「請 pin 確切版本」。

## 驗證

```bash
dotnet test XamlKeys.slnx                    # 單元 + CLI 契約 + 語料教訓
./scripts/verify-readme-sample.ps1           # README 的示範輸出沒走鐘
./scripts/verify-baselines.ps1               # 自家三個專案的結果沒有無聲變動
```

`verify-baselines.ps1` **只守自家專案**。動到解析或判定規則時，
`RELEASING.md` 的「外部專案比對」那一節值得再跑一次。

## 不做

- 自動刪除未使用的資源（見規劃書第 3 節）
- 解析第三方套件 DLL 裡的 BAML
- 猜執行期組出來的鍵名
- `.resx`（那是別的工具的地盤）
