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
  - 合併路徑與 csproj Include 是被掃的 repo 寫的：**任何查檔案存不存在都要走 `SourceResolver.Exists`**
    （只認掃描根底下、或使用者 config 指名的目錄）。對 `\\主機\分享` 查一次 File.Exists，Windows 就會發 SMB 連線、
    送出 NTLM 認證。新增任何碰檔案系統的解析路徑之前，先確認它在這道檢查之後。
    這道檢查：只認掃描根底下、根之後沒有冒號、每一段都不是連結（讀不到屬性就**當成是**，fail closed）、
    路徑比較跟檔案系統走（Linux 分大小寫）。`externalDictionaries` 也只收根底下的目錄（config 是從被掃的 repo 撿的）。
    這四條是自動安全審查連續三輪指出的 —— 改這一段之前先想「被掃的 repo 能不能讓它碰到根外面」。
  - `HostileInputTests` 守這四條（範圍外路徑那條用 `Probe` 記錄器確認「從來沒被查過」，不是只看結果）。
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
- **「C# 讓它算有人用」只標定義，不加名字**（規則 31／35／43，`ctx.UsedByCode`）。`unused` 是以**名字**算的，
  把名字加進 `ReferencedNames` 等於全樹同名的定義一起赦免（maui-samples 一個範例的 `"Success"` 曾經藏掉另一個範例的）。
  範圍是 `ProjectIndex.Related`：同一個專案、或以 `<ProjectReference>` 連著的 —— 只認自己會漏掉
  Flow Launcher（主題在 App、讀它的 Theme.cs 在 Core）。用變數**寫入**資源的檔裡的字串是定義、不是使用。
- **「名字出現在 C#」只放寬 DynamicResource 與 C# 查找的懸空**，StaticResource 不碰（那是 gate 擋的那一級）。
  組法的樣式一定要有固定開頭（至少 3 個字）：只有結尾的 `$"{x}Background"` 會吃掉全樹每個 …Background。
- **找不到的鍵先看「合併我的上層」有沒有看不到的東西**（`ExternalAbove`），而且只看同一個專案的上層 ——
  別的專案（使用函式庫的 App）合併了什麼，不代表函式庫自己找得到。
- **`{x:Static …}`／`ComponentResourceKey` 的鍵換成身分字串**（`MarkupKeys`，例如 `{x:Static MyApp.Keys.A}`）。
  框架的鍵：引用算框架提供、定義不算沒人用也不算死副本。專案的鍵找不到時**不報懸空**（可能在 C# 或別的組件）。
  「型別在不在這棵樹」要找**宣告**（`class X`），只是用到不算。
- **方言逐檔判**（`Analyzer.LooksWinUi`、`Context.DialectOfRef`）：MAUI 專案裡的 WinUI 檔、混了 WPF 與 WinUI 的樹，
  用整棵樹的多數決都會判錯 —— 而且錯的時候會「誤打誤撞」蓋掉別的問題（PowerToys 的 FancyZonesEditor）。
- **「相關專案」是上下游，不含兄弟**（`ProjectIndex.Related`）。用無向連通塊的話，monorepo 裡大家都參考同一個
  共用函式庫，整棵樹都變成相關。
- **UserControl 的宿主**（`Analyzer.Context.HostsProvide`、`XamlScanner.UsesOfClass`）：只給 DynamicResource，
  而且要**每一個**放置處都有。StaticResource 在控制項被放進宿主之前就解析了，不能放寬。
  C# 也會建立那個控制項的話（`CSharpScanner.ClassesCreatedInCode`）宿主看不全，也不放寬。
- **「定義在哪」的指路一律先找同一個專案**（out-of-scope、死副本）。整棵樹的第一個常常是別的範例專案。
- **「有人載入它」不等於「知道它在哪個作用域」**：x:Class 字典在 C# 裡被 new 出來（規則 66）只豁免孤兒。
  第一版把它加進 Application 作用域，Avalonia 的 `new FluentTheme()`、SukiUI 的主題類別整本進了全專案的作用域，
  已回報上游的真 bug 被降級成 external —— 是舊語料逐筆比對抓到的。
- **C++/WinRT**：`.vcxproj` 一律 WinUI；只讀有 XAML 的 vcxproj 底下的 `.cpp`／`.h`（Windows Terminal 有上千個）。
- **連結進專案的檔屬於連它的專案**（`SourceResolver.LinkedInto`、`Analyzer.Context.ProjectOfFile`）：根目錄路徑、App.xaml 作用域、
  方言、應用程式或函式庫、code-behind 位置**全部**要跟著改 —— 只改一半，Windows-universal-samples 的 391 筆 external 會變成 314 筆假懸空。
- **外部專案比對用 `docs/corpus.tsv` ＋ `scripts/corpus-run.sh` ＋ `scripts/corpus-compare.mjs`**，不要再手抓。
  改了要讀的副檔名，`corpus-run.sh` 的 PATTERNS 要加，tsv 的計數要整批重算（第十四輪踩過：早期 clone 少抓檔）。
- **看不到的來源會讓程式查找不再報懸空**（`result.ExternalSources` 是整棵樹的）：任何新增 external 來源的規則都要跑全語料，
  確認沒有把別的專案的真懸空遮掉（第十五輪規則 92 第一版就遮掉了 egmaui 的 ColorToBrushConverter）。
- **shadowed 要問「有沒有引用解析到輸家」**（`ctx.ReachedByStatic`）：StaticResource 載入時拿同檔那份；DynamicResource、ThemeResource 不算。
- **C# 加進 Application.Resources 的字典**（`CSharpScanner.DictionariesAddedToApplication`）只認宣告 `: Application`
  的那個檔，而且要同專案 —— 一般地方 `new` 出來的字典不知道放進哪裡（規則 66），不要放寬成「看到 new 就算」。
- **專案設定可能在 Directory.Build.props**（`ProjectIndex.IsWinUiProject`）：只看**最近的**那一個（MSBuild 也是），
  專案檔自己寫明 UseWPF 的優先。AssemblyName 是 `$(…)` 時退回檔名，不要照字面記。
- **條件式 XAML**（命名空間「?IsApiContractPresent(...)」）：同一個鍵寫兩次、條件互補是合法的二選一（Microsoft Calculator）。
  判互補要「同一個 API、同一個版本、一正一反」—— 只看有沒有問號會把真的重複鍵也放過。
- **UWP 應用程式的 OutputType 是 AppContainerExe**。漏認它，UWP App 全部被當成函式庫（懸空降級、unused 不判）——
  mutation 測試拿掉守門還是綠，就是被這個蓋掉的。
- **作用域的放寬要看「會不會藏 bug」**：不屬於任何專案的 App.xaml 以前會接進每個專案（`ScopeEngine.EffectiveChain`），
  它合併的一本看不到的字典讓整個 repo 的真懸空都變成 external。「保守」是對**推不出專案的引用**保守，不是對別人的 App.xaml。
- **主題套件的鍵名清單是選用的**（`FrameworkKeys.Packs`）：只有樹裡真的合併了那個主題才算。
  清單可以限定方言（`ThemePack.Dialect`）：不同生態系的主題元素會撞名（Uno／Avalonia 的 `SimpleTheme`）。
  新增清單照 `scripts/regenerate-framework-keys.mjs` 的表加一列、`.csproj` 加 EmbeddedResource、
  **THIRD-PARTY-NOTICES 加一節（授權全文從上游 `git show` 拿，不要手抄）**。內容已知的主題在覆蓋率裡算解析得到，
  但查找時仍是看不到的合併 —— 清單外的鍵照舊 external，別改成懸空（清單只收 XAML 裡寫出來的鍵，C# 產生的不在裡面）。
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
