# Roadmap

規劃書（`XamlKeys專案規畫書.md`）是完整版；這份是對外的摘要。

## 0.1.0（首發）

- [x] M0 原型與實查：自家三個專案的鍵圖譜，每一類 finding 人工核對
      （`docs/實查歸因-2026-11-14.md` 第 2 節）
- [x] M1 作用域引擎：合併鏈、pack URI 六種寫法、主題分側、external 標記
      —— **驗收條件是假警報率**，拿七個公開專案實跑，約 36,000 筆假警報修到約 1,200 筆
- [x] M2 C# 側用法掃描：字面 vs 組出來的鍵名、`usedAtRuntime` allowlist
- [x] M3 輸出合約：JSON（summary + findings）、SARIF、`--msbuild`、退出碼四碼、全 ASCII 輸出
- [x] M4 導入模式：baseline／ratchet、ignore 註解（理由必填）、`suppressed` 計數
- [x] M5a repo 公開、CI 第一次實跑 —— 2026-11-14。
      ubuntu-24.04 與 windows-latest 兩個 job **第一次就全綠**，
      含示範專案的退出碼斷言、`jq` 計數斷言、SARIF 檢查與 README 範例檢查。
      「解析是純 XML、Linux 也跑得動」這句話到此才真的有證據
- [ ] M5b nuget.org 的 Trusted Publishing 政策（**只有維護者的帳號能建**）＋ 推 `v0.1.0`
      —— 2026-11-15 發版前檢查全跑過：測試、`verify-readme-sample.ps1`、`verify-baselines.ps1`、
      本機 `dotnet pack` ＋ `dotnet tool install` ＋ 示範專案（退出碼 3／1 照預期）、公開專案的 `corpus-compare.mjs`（第十七輪後 109 個）。
      推 tag 之前只剩：建政策、把 CHANGELOG `[0.1.0]` 的標題改成發版日期

## 0.x（介面凍結前）

- [ ] **至少一個非作者的專案實際導入並回報**。
      這不是禮貌性的社群指標 —— 它是唯一能發現「同源樣本」類缺陷的機制
      （XamlContrast 規劃書 8.3 的結論，本專案第一天就印證了一次）。
      **這一條沒有捷徑：要先把 0.x 發出去。**
- [ ] **假如一直等不到外部回饋**（2026-11-14 定）：0.1.0 在 nuget.org 上架滿 **三個月**，
      還沒有任何非作者的導入回報，就改用下面三條**替代條件**判斷能不能凍結成 1.0，三條都要成立：
      1. **新一輪語料，每一筆都有歸因。** 至少 5 個這輪沒跑過的公開專案（至少兩種框架），
         新舊版逐筆比對；每一筆 finding 都寫明「真的／假的、為什麼」進 `docs/`。
         假的要嘛修掉、要嘛寫進 README 的已知盲區 —— 不能有「沒人解釋」的 finding。
      2. **至少一筆上游回報被對方維護者確認或修掉。** 這是「它叫的時候是真的」唯一不靠作者自己判斷的證據。
         **✅ 2026-11-15 達成**：[dnSpyEx/dnSpy#536](https://github.com/dnSpyEx/dnSpy/issues/536)
         （`DialogBorderStyle` 引用不存在的 `TabItemSelectedBackground`）送出當天，維護者 ElektroKill 以
         [5d63b7f](https://github.com/dnSpyEx/dnSpy/commit/5d63b7fb38f220b4f52d8d23b57a2bf7333fe499)
         「Fix reference to non existent XAML resource (fixes #536)」修掉，改法與回報建議的一字不差。
         **第二則，2026-11-15**：[MaterialDesignInXamlToolkit#4124](https://github.com/MaterialDesignInXAML/MaterialDesignInXamlToolkit/issues/4124)
         （DataGrid 下拉清單鍵盤焦點那一項引用不存在的 `…TextBox.ComboBoxHover`），貢獻者 corvinsz 開
         [#4125](https://github.com/MaterialDesignInXAML/MaterialDesignInXamlToolkit/pull/4125)「fix(DataGrid): use defined brush for keyboard-focused ComboBoxItem」，
         維護者 Keboo 合併。
         **第三則，2026-11-16 修掉**：[HandBrake#8266](https://github.com/HandBrake/HandBrake/issues/8266)
         （可編輯 ComboBox 樣板引用不存在的 `ComboBoxEditableTextBox`，潛伏），維護者 sr55 以
         [005b91a](https://github.com/HandBrake/HandBrake/commit/005b91a697ceefec13877f7b020e293fb8d7a8c5)
         「WinGui: Fix a Xaml Warning #8266」在深色、淺色兩份主題補上該樣式，排進 1.12.0。
         **第四則，2026-11-17 修掉**：[Avalonia#22833](https://github.com/AvaloniaUI/Avalonia/issues/22833)
         （Simple 主題 TabbedPage 引用色盤沒有的 `ThemeForegroundMidColor`、`ThemeDisabledColor`），維護者 maxkatz6 開
         [#22857](https://github.com/AvaloniaUI/Avalonia/pull/22857)「SimpleTheme: fix missing resource on TabbedPage, DatePicker and TimePicker with unified theme tests」，
         MrJul 合併；順手修了 DatePicker／TimePicker 的同類缺鍵，並把「SolidColorBrush 的 DynamicResource 指到不存在的鍵」擴成全主題、全控制項的單元測試。
         **維護者確認、尚未修**：[dnGrep#1554](https://github.com/dnGrep/dnGrep/issues/1554)
         （主題化 RadioButton 焦點框的 `ControlTextBrush`，2026-11-15 doug24 回說下一版修）、
         [PicView#453](https://github.com/Ruben2776/PicView/issues/453)
         （主題改名後留下的舊鍵名，2026-11-16 作者 Ruben2776 確認是殘留，更新 Linux 版時會改）。
         其他送出的：[lepoco/wpfui#1780](https://github.com/lepoco/wpfui/issues/1780)
         （`TreeViewItemFocusVisual`）、[microsoft/WPF-Samples#819 的留言](https://github.com/microsoft/WPF-Samples/pull/819#issuecomment-6877668604)
         （`CustomNavWindowStyle`／`NavButtonFocusVisualStyle`；2026-11-18 PR 作者 ade0003 照建議以 238354e 刪掉該 setter，
         但他是外部貢獻者、PR 尚未被微軟合併，所以不算維護者確認）、
         [MaterialDesignInXamlToolkit#4124](https://github.com/MaterialDesignInXAML/MaterialDesignInXamlToolkit/issues/4124)、
         [SukiUI#674](https://github.com/kikipoulet/SukiUI/issues/674)、
         [WeatherTwentyOne#72](https://github.com/davidortinau/WeatherTwentyOne/issues/72)。
         追蹤表在 repo 外（維護者本機的上游回報資料夾）。
      3. **凍結面撐過至少三個 0.x 版本。** 從 0.1.0 起連續三版沒有改動 `FrozenContractTests` 的任何預期值。

      為什麼是三個月：小眾的 CLI 工具，沒有回報不代表沒有人用，也可能只是沒人用 ——
      等太短分不出來，等太久則是讓一個已經驗證過的工具一直掛著「介面隨時會變」。
      **替代條件證明的是「準」，證明不了「好用」**（導入的痛點、設定檔夠不夠、訊息看不看得懂）。
      所以走這條路凍結的話，1.0 的 CHANGELOG 要明寫「沒有外部導入回報，依替代條件凍結」；
      之後第一份外部回報若需要破壞凍結面，照規矩升 2.0，不走後門。
      到期日：0.1.0 上架當天寫在這裡（尚未發佈）。
- [x] 重新辯論預設 gate —— 2026-11-14 定案：**維持現狀（只擋 fail 級）**。
      二十個專案的實測數據：只擋 fail 時 2 個專案紅燈、共 42 筆，而且**每一筆都人工驗證過是真的**；
      改成擋 warn 會變成 16 個專案紅燈、共 2,536 筆。
      守門員的價值在於「它叫的時候你會相信」—— 預設只擋「你的 App 會在載入時掛掉」那兩類
      （`dangling-static`、`duplicate-key`），其餘一律是建議。要更嚴格的人用 `--fail-on warn`。
- [x] 九個凍結面全部鎖成測試（`FrozenContractTests.cs`，37 條）—— 2026-11-14。
      任何人動到其中一條，CI 立刻紅，必須先回來改那個檔，也就是必須先承認自己在破壞契約
- [x] `docs/config-schema.md` 與 `docs/report.schema.json` —— 2026-11-14，
      並且有測試交叉比對「規格寫的」與「程式真的輸出的」
- [x] `--html` 自包含報告 —— 2026-11-14：內嵌 CSS、無 script（純 CSS 篩選）、無外部資源、無時間戳
- [x] WPF 內建 Fluent 主題（`PresentationFramework.Fluent`）的鍵清單 —— 2026-11-14，
      955 個鍵，**選用式**（只在真的有合併時才查）。wpfsamples 的 external 448 -> 16
- [x] 近似鍵名建議（打錯字／改名的另一半）—— 2026-11-14
- [x] 組出來、但有固定部分的鍵名與字典路徑（`$"Icon{n}"`、`"Icon" + n`、`$"Themes/{name}.xaml"`）
      —— 2026-11-14，原本是 README 已知盲區的前兩條。同專案限定、固定部分至少 3 個字、一定計數
- [x] 效能 —— 2026-11-15 量過（Release 建置、三次取最快、含 .NET 啟動，Windows 筆電）：

      | 專案 | XAML | C# | 鍵 | 引用 | 時間 | 記憶體峰值 |
      |---|---|---|---|---|---|---|
      | PowerToys | 313 | 4,530 | 1,173 | 4,235 | 2.6 秒 | 75 MB |
      | maui-samples | 1,238 | 2,778 | 4,361 | 19,589 | 2.0 秒 | 85 MB |
      | ScreenToGif | 121 | 644 | 45,054 | 5,577 | 1.7 秒 | 103 MB |
      | dnSpy | 124 | 4,206 | 248 | 3,135 | 1.6 秒 | 62 MB |

      最大的成本是讀 C#（每個檔都要剝註解、找字串）；鍵多的 ScreenToGif 記憶體最高。CI 上跑沒有問題。

## Avalonia（2026-11-14 完成）

白老鼠：**AvaloniaUI/Avalonia 本身、FluentAvalonia、Semi.Avalonia**（合計 1,172 個 axaml）。

- 合併順序**與 WPF 相同**（後宣告的贏）。這不是推測：
  `Avalonia.Base/Controls/ResourceDictionary.cs` 的 `TryGetResource` 寫著
  `for (var i = _mergedDictionaries.Count - 1; i >= 0; --i)`。所以不需要第三種方言。
- `<ResourceInclude Source="…"/>`（與舊版的 `MergeResourceInclude`）等同於
  `<ResourceDictionary Source="…"/>`
- `avares://組件名/路徑` URI
- `<X.Styles>` 裡的 `<StyleInclude Source="…"/>` 會帶資源進這個作用域；
  函式庫的主題物件（`<semi:SemiTheme/>`）帶的資源看不到 -> external
- 主題用**目錄**分時（ThemeDictionaries 的一本只放一行 ResourceInclude），
  側別要往下傳給被包含的字典

實測結果：三個專案的 `fail` 級 finding 都是 0。

## .NET MAUI（2026-11-14 完成）

白老鼠：**dotnet/maui-samples（1,238 個 xaml、231 個獨立範例專案）與 dotnet/eShop**。

- MAUI 的 `x:` 命名空間是 **`winfx/2009/xaml`**，不是 WPF／WinUI／Avalonia 的 2006。
  只認 2006 的話，MAUI 專案的 `x:Key` **一個都看不到** —— 實測是 0 個鍵、19,157 筆假懸空。
- 合併語意與 WPF 相同（`<ResourceDictionary Source="…"/>` ＋ MergedDictionaries）。
- `{AppThemeBinding Light={StaticResource …}, Dark={StaticResource …}}` 裡的巢狀引用
  由既有的 markup extension 掃描器處理，不需要特別支援。

實測之後，`maui-samples` 的 40 筆 `fail` 都是**真的**：`ThemePrimary`、`TextPrimaryColor`
之類的鍵在整個 repo 都沒有定義。

## 1.x+
- ~~`ComponentResourceKey` 與 `{x:Static …Key}`~~ —— 2026-11-15 提前做完（規則 47）：框架的鍵算框架提供，
  專案的鍵照字串鍵的規則判；找不到的照舊 unresolved
- 自動修（刪除未使用的資源）。**不做** —— 見規劃書第 3 節，
  本工具的不可知區域正好落在「看起來沒人用」上，自動刪 = 自動炸

## 與 XamlContrast 的關係

兩者刻意**不共用程式碼**，理由寫在規劃書第 8 節（決策紀錄）：

- XamlContrast 全庫沒有 `MergedDictionaries` —— 它用全專案扁平索引，刻意繞開合併語意，
  而合併順序正是本工具的核心。把它加進共用程式碼會改動色盤偵測結果，
  而 XamlContrast 的 CI 用 `jq` 寫死了 finding 計數斷言。
- XamlContrast 的 CLI 只收一個位置參數且已凍結，加子指令會跟叫 `Resources` 的資料夾撞名。
- 真正可共用的只有解析層的幾百行，而且有價值的是**知識而非架構**（`x:Key` 不一定在第一個屬性、
  註解掉的舊定義會被誤收）—— 知識抄得走。

兩邊的 README 互相點名分工：XamlContrast 管「看不看得見」，XamlKeys 管「找不找得到」。
