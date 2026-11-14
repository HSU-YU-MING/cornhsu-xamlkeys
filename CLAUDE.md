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
- **`baseline` 的比對鍵不含行號。** 含了的話一次格式化就讓整份失效。
- **ignore 註解按行比對定義** —— 同一行有兩個 `x:Key` 時會一起壓掉（已知盲區）。
- **別名鏈刻意不做遞移**：死掉的別名 `A -> B` 仍然算成 `B` 的一次引用。
  做遞移會讓 `unused` 多報，而這個工具的第一失敗模式就是叫人刪掉有用的東西。
  要改的話，先想清楚怎麼在訊息裡把整條鏈講出來，讓使用者有得覆核。

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
