# `xamlkeys.config.json`

放在被掃描的資料夾裡會被自動撿到，或用 `--config <path>` 指定。

**不認得的屬性是錯誤，不是忽略。** 打錯字的設定靜默失效，使用者會以為自己設了、
實際上沒有 —— 那比沒有這個設定更糟。

**自動撿到的設定檔會被印出來**（只在它真的放寬了結果時）。理由：在 CI 上掃一個 PR 的時候，
那個檔案就是 PR 的內容，它可以把 `severities` 全部降成 `info` 把守門員靜靜關掉。
不禁止，但一定要留下痕跡。用 `--config` 明確指定可以避免這件事。

---

## 完整範例

```json
{
  "dialect": "wpf",
  "failOn": "fail",
  "minCoverage": 80,
  "includeTests": false,
  "usedAtRuntime": ["Icon*", "Status.*"],
  "exclude": ["Generated", "ThirdParty"],
  "externalDictionaries": [
    { "assembly": "HandyControl", "path": "vendor/HandyControl/Themes" }
  ],
  "themeFiles": {
    "dark": ["Themes/Dark"],
    "light": ["Themes/Light"]
  },
  "severities": {
    "unused": "info",
    "out-of-scope": "fail"
  }
}
```

## 欄位

| 欄位 | 型別 | 預設 | 說明 |
|---|---|---|---|
| `dialect` | `"wpf"` \| `"winui"` | 偵測 | 合併字典的優先序。**兩者相反**：WPF 與 Avalonia 是「後宣告的贏」，WinUI 是「先宣告的贏」。不寫的話逐專案由 csproj 判定（`UseWinUI`／`WindowsAppSDK`／`UAP`）。明寫會套用到整棵樹 |
| `failOn` | `"fail"` \| `"warn"` | `"fail"` | gate 的門檻。`warn` 會把 warn 級也算成失敗 |
| `minCoverage` | 0–100 | `80` | 合併字典的解析成功率下限，以及 XAML 檔的解析成功率下限。低於它 → **exit 3**，因為 `unused` 的結論不可信 |
| `includeTests` | bool | `false` | 連測試專案與 fixture 資料夾一起掃。預設關：測試素材常常是**刻意寫壞的** XAML |
| `usedAtRuntime` | string[] | `[]` | 視為「執行期會用到」的鍵樣式（結尾 `*` 萬用）。用來處理 `FindResource($"Icon{n}")` 這種組出來的鍵名。被蓋到的計入 `summary.suppressed` |
| `exclude` | string[] | `[]` | 路徑**片段**比對（不是 glob）。內建的排除（`bin`、`obj`、`node_modules`、`packages`、`artifacts`、`TestResults`、點開頭的目錄）不需要寫 |
| `externalDictionaries` | `{assembly, path}[]` | `[]` | 把第三方組件的字典指到**掃描範圍內**的目錄（vendored 的副本）；範圍外的路徑（含 `\\主機\分享`、絕對路徑、`../`）一律忽略 —— 設定檔是從被掃的 repo 撿的，不能讓它碰到範圍外。指得到就解析進來，覆蓋率與 `unused` 的可信度跟著升級；指不到維持 external（不是錯誤） |
| `themeFiles.dark` / `.light` | string[] | `[]` | 把符合這些路徑片段的字典標成深／淺側。沒有 `ThemeDictionaries` 但用檔案分主題時用得上 |
| `severities` | `{kind: level}` | 見下 | 逐類覆寫分級。kind 用 kebab-case，level 是 `fail`／`warn`／`info` |

## 預設分級

| kind | 預設 | 為什麼 |
|---|---|---|
| `dangling-static` | **fail** | 執行期丟例外，App 直接掛 |
| `duplicate-key` | **fail** | 非法 XAML，載入期 parser 丟錯，編譯器不擋 |
| `dangling-dynamic` | warn | 退回預設值，不爆但畫面會怪 |
| `out-of-scope` | warn | 作用域是靜態近似，可能是工具看漏 |
| `shadowed` | warn | 合併順序讓這份定義輪不到，而且贏家內容不同 |
| `shadowed-identical` | info | 每一份一模一樣：結果正確，只是多餘 |
| `unused` | warn | **不在預設 gate 內** —— 不可知區域正好落在這裡 |
| `orphan-dictionary` | warn | 整本進不了作用域 |
| `external` | info | 不是錯，是看不到 |

在**函式庫專案**裡，解析不到的引用會再降一級（`dangling-static` → warn、`dangling-dynamic` → info）：
函式庫的字典是給別人合併的，而那個「別人」會先合併什麼無從得知。

## 會讓結果變寬鬆的設定，都會留下痕跡

| 設定 | 痕跡 |
|---|---|
| 任何非預設值 | `summary.configOverrides: true` |
| `usedAtRuntime` 蓋到的鍵 | `summary.suppressed` |
| `externalDictionaries` 解析成功 | `summary.coverage` 上升、`summary.externalSources` 變短 |
| `includeTests` | `summary.testFilesExcluded` 變 0 |
| 自動撿到的設定檔 | stderr 的一行 note |

這不是為了防誰，是因為這個工具的 finding 會讓人去刪檔案 ——
**「工具沒看到」與「看到了沒問題」必須分得開。**
