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
- [ ] M5 發佈：GitHub repo 公開、CI、`action.yml`、nuget.org Trusted Publishing 政策、`v0.1.0`

## 0.x（介面凍結前）

- [ ] **至少一個非作者的專案實際導入並回報**。
      這不是禮貌性的社群指標 —— 它是唯一能發現「同源樣本」類缺陷的機制
      （XamlContrast 規劃書 8.3 的結論，本專案第一天就印證了一次）
- [ ] 重新辯論 `unused` 的預設分級。目前是 warn 但不在預設 gate 內；
      看實際導入的人怎麼用再決定要不要改
- [x] `--html` 自包含報告 —— 2026-11-14：內嵌 CSS、無 script（純 CSS 篩選）、無外部資源、無時間戳
- [ ] 效能：ScreenToGif（121 檔、45,054 個鍵）目前跑得完，但沒有量過大型方案

## 1.x+

- **Avalonia（`.axaml`）**。目前只是把副檔名收進掃描範圍，**沒有任何實查**。
  Avalonia 的 `ResourceInclude`／`StyleInclude` 與 WPF 的合併語意不同。
  **前置條件：要有真實專案當驗證白老鼠** —— 「每條規則來自實查」是本專案的憲法，
  沒有白老鼠的平台支援只是沒驗證過的猜測。
- `ComponentResourceKey` 與 `{x:Static …Key}`（目前計入 unresolved，不評）
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
