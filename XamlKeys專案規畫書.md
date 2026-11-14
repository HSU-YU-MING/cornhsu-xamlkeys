# XamlKeys 專案規劃書 v0.2

> 日期：2026-11-14（v0.2 同日修訂：M0～M4 完成後回填實查結果、修正被推翻的幾節）
> 狀態：**M0～M4 完成；repo 已公開、CI 全綠（M5a）；NuGet 首發未做（M5b）。**
> 最新的數字（測試數、語料、規則條數）以 `ROADMAP.md` 與 `docs/實查歸因-2026-11-14.md` 為準 ——
> 這份規劃書記錄的是「當初怎麼想、哪裡被實查推翻」，後續輪次不逐一回填。
> 一句話：**XAML 資源鍵的靜態作用域稽核 —— 懸空、被蓋掉、沒人用，三種鍵的毛病一次掃完。**

> **v0.2 的修訂摘要**（細節見 `docs/實查歸因-2026-11-14.md`）
>
> | 本文原本寫 | 實查之後 |
> |---|---|
> | C# 側用 Roslyn | **改用 regex**，理由見 4.4 |
> | 三類 finding | **九類**（多了 duplicate-key、shadowed-identical、orphan-dictionary、external、out-of-scope） |
> | 合併順序「後宣告的贏」 | 已查證；**但 WinUI 相反**，所以方言是查找的參數 |
> | 最大風險是第三方字典 | 成立，**但自家語料驗不到** —— 七個公開專案上第一版吐出約 36,000 筆假警報 |

---

## 0. 核心原則

**靜態分析 + 全量掃描 + CI 退出碼。** 跟 XamlContrast 同一條憲法，不另立。

另外加一條本專案自己的：**「工具沒看到」與「看到了沒問題」必須分得開。**
資源鍵稽核天生有一大塊靜態不可知的區域（執行期組出來的鍵名、第三方字典），
把不可知靜默當成「沒問題」，就會得出「這 48 個鍵沒人用，刪吧」然後刪爆使用者的 App。
**本工具最嚴重的失敗模式不是漏報，是叫人刪掉其實有用的東西。**

憲法第三條（繼承自 XamlContrast）：**每條規則來自實查。** 沒有在真實專案上見過的
情形，不寫進規則。

---

## 1. 這是什麼，跟現有工具的界線

| 工具 | 做什麼 | 缺什麼 |
|---|---|---|
| **ReSharper / Rider** | `Xaml.StaticResourceNotResolved`（懸空引用）、`Xaml.ElementNameReferenceNotResolved` | **沒有「定義了沒人用」** —— 那需要全專案的反向索引，不是單檔 inspection 做得到的。也沒有「被合併順序蓋掉」。要裝 IDE 引擎，CI 要跑 InspectCode 吃整個 solution |
| **XAML 編譯器** | 什麼都不擋 | 微軟官方文件明寫：鍵的唯一性**不由 XAML markup compiler 檢查**，重複或缺少的 `x:Key` 是**載入時由 parser 丟錯**。也就是上線才會爆 |
| **Rapid XAML** | XAML 靜態分析、可寫自訂規則、有 CI 用的 NuGet | 規則裡沒有資源鍵的作用域分析 |
| **microsoft/RESX-Unused-Finder** | 找沒用到的字串資源 | 只管 `.resx`，不碰 XAML 字典。**且已於 2021-01-24 被作者封存** |
| **Xamlizer** | 從資源字典產生強型別鍵類別（`Colors.xaml` → `ColorsKeys`） | 方向相反：它是產生引用，不是稽核引用 |
| **ReSource** | 從 ResourceDictionary 產生靜態參考 | 同上，且 `App.xaml` 裡直接定義的資源不解析 |
| **Snoop** | 執行期檢視實際生效的資源字典 | 執行期、手動、一次一個 |

**2026-11-14 查證結果：**

- 沒有任何專做「XAML 資源鍵使用圖譜」的工具。
- NuGet：`cornhsu.xamlkeys`、`xamlkeys`、`cornhsu.xamlscope`、`xamlscope`、
  `cornhsu.xamlsweep` 全部 404（未被使用）。
- GitHub：搜不到名為 `XamlKeys` / `xaml-keys` 的 repo。

**空缺是真的，而且比 XamlContrast 當初那個更乾淨** —— XamlContrast 至少還有
Accessibility Insights 在隔壁做執行期版本；這題連執行期版本都只有 Snoop 的人工檢視。

### 1.1 誰在什麼階段用

| 角色 | 階段 | 摩擦 |
|---|---|---|
| 開發者 | 刪了一個樣式、改了字典結構之後 | 一行指令，秒級 |
| CI | PR gate | 懸空的 StaticResource 就擋 |
| 維護者 | 接手舊專案、想清掉死資源 | 讀 JSON，拿 `unused` 清單當待辦 |
| 重構前 | 動主題檔之前先建 baseline | 之後只擋新增的債 |

**不取代 ReSharper。** 那個管單檔的即時提示；這個管全專案的反向索引與合併順序，
而且不需要 IDE。兩者互補。

---

## 2. 名詞對照

| 詞 | 意思 |
|---|---|
| **鍵（key）** | `x:Key` 定義的資源識別字。工具的基本單位 |
| **引用（reference）** | `{StaticResource K}`、`{DynamicResource K}`、`{ThemeResource K}`，以及 C# 側的 `FindResource("K")` 等 |
| **作用域（scope）** | 一個鍵在哪些檔案／哪些元素底下查得到。由合併鏈與樹的位置決定 |
| **合併鏈（merge chain）** | `ResourceDictionary.MergedDictionaries` 串起來的字典順序。**決定誰蓋掉誰** |
| **側（side）** | 深色／淺色。沿用 XamlContrast 的概念：只在 `Dark.xaml` 定義的鍵，在淺色側是懸空的 |
| **外部字典（external dictionary）** | 合併進來但來源解析不到的字典（第三方套件、跨組件 pack URI）。它裡面的鍵我們看不到 |
| **執行期鍵（runtime key）** | 鍵名是程式組出來的（`$"Icon{name}"`）。靜態不可知，**必須計數並大聲講** |

---

## 3. 範圍邊界

### 做

- 解析全專案的 `x:Key` 定義，建立「鍵 → 定義位置（檔案、側、合併鏈位置）」索引
- 解析全專案的資源引用，XAML 與 C# 兩側都掃
- 依合併順序判定**誰蓋掉誰**
- 三類 finding：**懸空（dangling）／被蓋掉（shadowed）／沒人用（unused）**
- 深淺兩側各判一次
- 外部字典標記與計數
- CI 退出碼 + JSON / SARIF / MSBuild 輸出
- baseline 與 ignore 註解

### 不做（v1 明確排除）

- **自動刪除未使用的資源** —— 跟 XamlContrast 的「只報不改」一致。刪哪個是人的決定，
  而且本工具的不可知區域正好落在「看起來沒人用」上，自動刪 = 自動炸
- **解析第三方套件 DLL 裡的 BAML** —— 外部字典一律標記為 external，不猜它裡面有什麼
- **執行期才組出來的鍵名** —— 不猜，計入 `unresolved` 並提供 allowlist
- **樣式／模板的語意正確性** —— 那是 Rapid XAML 與 ReSharper 的地盤
- **`.resx` 字串資源** —— 不同的問題，而且那塊已經有 ResxLint、LocalizationManager 等工具
- **隱含樣式（沒有 `x:Key` 的 `<Style TargetType="Button">`）** —— 它不是鍵，不在本工具的單位裡
- **效能分析（字典載入成本）** —— 不同的問題

---

## 4. 系統架構

### 4.1 真正的難題不是找 `x:Key`，是作用域

用 regex 把 `x:Key="X"` 全抓出來是半小時的事。這個工具的全部價值在後面那一步：
**這個鍵在哪裡看得到、哪一個定義會贏。**

WPF 的資源查找順序是：元素自己的 `Resources` → 沿樹往上每一層的 `Resources`
→ `Application.Resources` → 主題／系統資源。而**單一字典內部**的規則更刁：
字典自己的項目優先於合併進來的；合併進來的字典之間，**後合併的贏**。

> **2026-11-14 查證（微軟文件）**：*the resource returned will come from the last dictionary
> found sequentially in the MergedDictionaries collection*；而且
> *a key can exist multiple times in a set of merged dictionaries* —— 跨合併字典的重複鍵
> **合法、不丟錯**，只有單一字典內部要求唯一（違反時載入期 parser 丟錯，編譯器不檢查）。
>
> ⚠ **WinUI／UWP 的順序相反**（依宣告順序的反向檢查）。同一份 XAML 搬到 Uno／WinUI
> 會換答案，所以方言是查找的**參數**，不是細節 —— 而且方言要由 **csproj** 判定，
> 不能看 XAML 寫法（wpfui 是 WPF 函式庫卻刻意沿用 WinUI 的 `{ThemeResource}` 與鍵名）。

這代表三件事：

1. 「重複定義」不能只看「同一個鍵出現兩次」，要看**它們在不在同一條合併鏈上**。
   兩個互不相干的字典各自定義 `PrimaryBrush`，而且從來沒有被合併到同一個作用域，
   那不是重複，是兩個不同的東西。報它就是假警報。
2. 「沒人用」不能只看「全專案沒有 `{StaticResource K}`」，要看**鍵的作用域裡有沒有引用**。
3. 「懸空」不能只看「全專案沒有這個 `x:Key`」，要看**引用的那個位置看不看得到那個定義**。

**所以核心資料結構是作用域圖，不是鍵的集合。**

> 這一節同時解釋了為什麼本工具不能併進 XamlContrast：XamlContrast 全庫沒有
> `MergedDictionaries` 這個字，它刻意用的是**全專案扁平索引**（對算顏色來說是對的，
> 因為它只需要知道「這個鍵最後是什麼色」）。把合併順序加進去會改變它的色盤偵測結果，
> 而它的 CI 用 `jq` 寫死了 finding 計數斷言。詳見第 8 節。

### 4.2 合併字典與 pack URI 解析

`<ResourceDictionary.MergedDictionaries>` 裡的 `Source` 有這些形狀，全部要解：

| 寫法 | 解法 |
|---|---|
| `Themes/Dark.xaml` | 相對於目前檔案 |
| `/Themes/Dark.xaml` | 相對於專案根 |
| `pack://application:,,,/Themes/Dark.xaml` | 同組件，去掉 pack 前綴 |
| `pack://application:,,,/MyLib;component/Themes/Generic.xaml` | **跨組件** —— 若 `MyLib` 是本方案內的專案就解得到；是 NuGet 套件就解不到 → **external** |
| `/MyLib;component/Themes/Generic.xaml` | 同上的簡寫 |
| `{DynamicResource ...}` 或程式碼動態 Add | 解不到 → external，且整個作用域標記為不完整 |

**解不到就標 external，絕不當成「這個字典是空的」。** 把 HandyControl 的字典當空的，
會讓專案裡每一個引用 HandyControl 鍵的地方都變成「懸空」—— 工具第一天就被關掉。
這是本專案最大的假警報來源，M1 的驗收條件就是它。

config 要能補：

```json
{
  "externalDictionaries": [
    { "assembly": "HandyControl", "path": "../packages/HandyControl/Themes" }
  ]
}
```

指得到就解析進來，精度升級；指不到就維持 external，**而且要在 summary 講出來**。

### 4.3 finding 的種類與分級

（v0.1 只規劃了三類。`duplicate-key`、`out-of-scope`、`shadowed-identical`、
`orphan-dictionary` 是實作與實查期間長出來的，每一類的來源見 `docs/實查歸因-2026-11-14.md`。）

| 種類 | 意思 | 預設分級 | 理由 |
|---|---|---|---|
| `dangling-static` | `{StaticResource K}` 在該側的作用域裡找不到 K | **fail** | 執行期**丟例外**，App 直接掛 |
| `duplicate-key` | 同一本字典裡同一個 `x:Key` 兩次 | **fail** | 非法 XAML，載入期 parser 丟錯，**編譯器不擋** |
| `dangling-dynamic` | `{DynamicResource K}` / `{ThemeResource K}` 找不到 K | **warn** | 退回預設值，不爆但畫面會怪 |
| `out-of-scope` | 鍵在**同一個專案**裡存在，但不在這條鏈上 | **warn** | 作用域是靜態近似，可能是我們看漏，不是使用者寫錯 |
| `shadowed` | 合併順序讓這份定義輪不到，而且贏家**內容不同** | **warn** | 畫面真的會跟某一份定義寫的不一樣 |
| `shadowed-identical` | 同上，但每一份**一模一樣** | **info** | 結果正確，只是多餘（實查：wpfui、MaterialDesign 在每個樣式檔各宣告一次同樣的轉換器） |
| `unused` | 全專案（XAML＋C#）沒有任何地方引用這個鍵名 | **warn**，預設不擋 | 不可知區域正好落在這裡，預設擋會害人 |
| `orphan-dictionary` | 沒有人合併、程式也沒載入的獨立字典 | **warn** | 整本進不了作用域 |
| `external` | 引用的鍵來自解析不到的外部字典 | **info**，只計數 | 不是錯，是看不到 |
| `unresolved` | 鍵名執行期才組出來、或引用寫法看不懂 | 不評，計入 summary | 「工具沒看到」 |

`unused` 的定義最後採用**最保守的那一種**：全專案沒有任何地方引用過這個鍵名，
**不看作用域**。理由是作用域是靜態近似，而這一類 finding 的後果是使用者去刪檔案。
代價是少報，但少報只是沒幫上忙，多報會害人。

**預設 gate 只擋 `dangling-static`。** 這跟 XamlContrast 相反（它預設擋 warn），
理由是兩者的債務形狀不同：對比度的債是「顏色不夠深」，改了就好；資源鍵的
`unused` 債在大型專案動輒上百筆，而且有一部分是假的。第一天全紅的工具會被關掉。

**1.0 之前要重新辯論這個預設值**（照 XamlContrast 的前例，它在 1.0 把預設從
放行改成 `--fail-on warn`）。

### 4.4 C# 側的用法掃描

不掃 C# 就會把這些鍵全部誤報成 `unused`：

```csharp
FindResource("K");  TryFindResource("K");  Resources["K"];
Application.Current.Resources["K"];  SetResourceReference(prop, "K");
```

**做法：regex，不是 Roslyn。**（v0.1 原本寫 Roslyn，實作時反過來，理由如下。）

1. Roslyn 會讓這個 tool 套件大上一個數量級。本工具對 ReSharper 的賣點就是「輕」，
   為了四個方法名付 10MB+ 的相依不划算。
2. Roslyn 要能解析到型別才有附加價值，那等於要求專案**建置得起來** ——
   靜態工具不該有這個前提（使用者的專案可能缺 NuGet、缺 SDK）。
3. 我們只需要區分「字面字串」與「組出來的字串」，那是**語法層**的事，不需要語意層。

取捨：註解與字串裡出現 `FindResource("X")` 會被當成引用。**這個誤差的方向是安全的** ——
它只會讓「沒人用」少報，不會讓人誤刪。即使如此仍先剝掉註解（成本很低）。

要區分的就是這兩行：

```csharp
var brush = (Brush)FindResource("DangerBrush");      // 字面 → 記成引用
var icon  = (Geometry)FindResource($"Icon{name}");   // 組出來 → unresolved，不是 unused
```

第二行是本工具最危險的輸入。**遇到非字面的鍵名，必須把整個「可能符合的鍵集合」
標成不可知**，否則 `Icon*` 底下那幾十個鍵全部會被報成沒人用。

config 的 allowlist 是正式出口：

```json
{ "usedAtRuntime": ["Icon*", "Status.*"] }
```

被 allowlist 蓋到的鍵列入 `summary.suppressed` 並可列出 —— 否則它就是新開的一條
靜默退化通道。

### 4.5 輸出合約（JSON、退出碼、語言）

```json
{
  "schemaVersion": 1,
  "toolVersion": "0.1.0",
  "summary": {
    "files": 128,
    "dictionaries": 22,
    "dictionariesResolved": 20,
    "dictionariesExternal": 2,
    "keysDefined": 614,
    "references": 2317,
    "unresolved": 11,
    "suppressed": 0,
    "counts": {
      "danglingStatic": 2, "danglingDynamic": 5,
      "shadowed": 3, "unused": 48, "external": 96
    }
  },
  "findings": [ ... ]
}
```

`summary` 與 `findings` 分兩層的理由跟 XamlContrast 一樣：CI 只消費 findings 的話，
「有兩個字典沒解析到」這件事等於不存在。

**退出碼契約：**

| 情況 | 碼 | 可設定 |
|---|---|---|
| 通過 | 0 | — |
| 找到違規（達到 gate 的 finding） | 1 | `--fail-on`：`fail`（預設）/ `warn` |
| 用法、設定或輸入檔錯誤 | 2 | — |
| **結果不可信** | 3 | 見下 |

exit 3 的觸發條件（**這是本工具的核心防線**）：

- 掃到 0 個鍵定義 —— 一次重構讓路徑全失效，綠燈放行就是謊報健康
- 合併字典的解析成功率低於 `--min-coverage`（預設 80%）——
  有五分之一的字典看不到內容時，`unused` 的結論不可信

**語言：CLI 輸出與 JSON 欄位值一律英文**，中文只留在 `README.zh-Hant.md`。

### 4.6 導入既有專案：baseline 與 ignore

照 XamlContrast 的形狀，不發明新的。

**baseline / ratchet**：第一次跑產生已知債清單進 repo，之後只擋新增的。
鍵的寫法不含行號（防漂移）。鍵的寫法改了就遞增 `keyVersion`。

**ignore 註解**：

```xml
<!-- xamlkeys-ignore: 這個鍵由外掛在執行期取用 -->
<SolidColorBrush x:Key="PluginAccent" Color="#FF4081" />
```

- 管**下一個元素**，不管整個檔
- **理由必填**，沒理由視為無效並警告
- 計入 `summary.suppressed` 並可列出

---

## 5. Repo 結構（照 XamlContrast）

```
cornhsu-xamlkeys/
├── src/
│   ├── XamlKeys.Core/            作用域引擎（合併鏈、pack URI、索引、判定）
│   └── XamlKeys.Cli/             CLI 外殼
├── tests/
│   └── XamlKeys.Tests/
├── samples/
│   └── demo/                     刻意做壞的示範專案（三類 finding 各有）
├── baselines/                    四個自家專案的基準線
├── docs/
├── scripts/                      verify-baselines.ps1 / verify-readme-sample.ps1
├── .github/workflows/            ci.yml + release.yml
├── action.yml                    GitHub Action
├── README.md / README.zh-Hant.md
├── CHANGELOG.md / ROADMAP.md / RELEASING.md
├── CLAUDE.md
├── Directory.Build.props         ⚠ Version 固定 0.0.0-dev，tag 才是版本真相源
├── THIRD-PARTY-NOTICES.md
└── LICENSE                       MIT
```

**不做 npm 包裝。** 理由照抄 XamlContrast 的 ROADMAP 決定：使用者是 XAML/.NET 開發者，
人人都有 dotnet，`dotnet tool install` 不構成採用門檻。受眾論證不成立就不付
發佈管線與平台二進位的維護成本。出現真實需求再開。

---

## 6. 里程碑

### M0 — 原型與實查 ✅ 2026-11-14 完成
在自家專案上跑出鍵圖譜，**每一類 finding 人工核對**。
⚠ 本文原本寫「四個自家專案」，但 **ZeroCel.WPF 不在 `D:\應用程式` 底下**，
實際語料是 Cornea / Kindling / QuillNest 三個。
產出：三份基準線（`baselines/`）＋歸因文件（`docs/實查歸因-2026-11-14.md`）。
核對結果：自家語料的 12 筆 `unused` **全是真陽性**，其中 `InverseBoolToVis`
是一次改名留下的孤兒（定義用短名、所有使用處用長名）。

### M1 — 作用域引擎 ✅ 2026-11-14 完成
合併鏈、pack URI 六種寫法、主題分側、external 標記。

**驗收條件是假警報率，而自家語料驗不到它。** 自家三個專案沒有函式庫、沒有 pack URI、
沒有第三方字典、沒有 ThemeDictionaries、沒有 WinUI —— 本文宣稱的「最大假警報來源」
在它們身上**一條都複製不出來**。
改用七個公開專案（ScreenToGif、HandyControl、MahApps.Metro、wpfui、
MaterialDesignInXamlToolkit、WinUI Gallery、ILSpy，合計 1,593 個 XAML 檔）：
第一版吐出**約 36,000 筆假警報**，修正後約 1,200 筆（其中 1,094 筆是 ScreenToGif
真正沒人用的在地化字串鍵）。十五條規則與各自的來源見歸因文件第 3 節。

### M2 — C# 側用法掃描 ✅ 2026-11-14 完成
regex（不是 Roslyn，理由見 4.4）、字面 vs 組合鍵名的區分、`usedAtRuntime` allowlist、
以及「程式在執行期載入的字典算進 Application 作用域」。

### M3 — 輸出合約 ✅ 2026-11-14 完成
JSON（summary + findings + 診斷區）、SARIF 2.1.0、`--msbuild`、退出碼四碼、
**全 ASCII 輸出**（原本只要求「英文」，實作時踩到 cp950 把破折號印成亂碼，收緊成 ASCII
並加了回歸測試）。

### M4 — 導入模式 ✅ 2026-11-14 完成
baseline / ratchet（比對鍵不含行號、`keyVersion`）、ignore 註解（理由必填）、
`suppressed` 計數。

### M5 — 發佈（未做）
GitHub repo 公開、CI（含示範專案的退出碼與 `jq` 計數斷言）、`action.yml`、
nuget.org Trusted Publishing 政策、`v0.1.0`。
⚠ repo 骨架、CI、action.yml、發版文件都已經寫好並在本機驗證過；
**建立公開 repo 與推 tag 發版是對外動作，留給維護者決定。**

---

## 7. 已知盲區（誠實清單，要寫進 README）

| 盲區 | 後果 | 處置 |
|---|---|---|
| 執行期組出來的鍵名 | 可能誤報 unused | 計入 `unresolved`，`usedAtRuntime` allowlist |
| 第三方套件 DLL 內的 BAML | 看不到外部字典的內容 | 標 external、計數、納入 `--min-coverage` |
| 程式碼動態 `Resources.Add()` | 該鍵看起來沒定義 | 標 unresolved，不報懸空 |
| `ComponentResourceKey` / `SystemColors` | 不是字串鍵 | v1 不處理，文件揭露 |
| 跨 `UserControl` 實例的樹位置 | 作用域是靜態近似，不是真實視覺樹 | 以檔案與合併鏈近似，誤差寫進文件 |
| 條件式 XAML（`mc:Ignorable`、設計時命名空間） | 可能多算引用 | 設計時命名空間預設排除 |

---

## 7.1 憲法的取樣偏誤（2026-11-14，七個公開專案實跑後補記）

本文第 0 節把「每條規則來自實查」當憲法。它有一個內建的盲點，而且這個盲點
**XamlContrast 的規劃書 §8.3 已經寫下來過**：實查的樣本是同一位作者的專案。

XamlKeys 從一開始就知道這條，所以 M1 的驗收直接拿公開專案跑。結果完全印證：

| | 自家三個專案 | 七個公開專案 |
|---|---|---|
| 假警報 | **0** | **約 36,000** |
| 函式庫專案 | 0 個 | 4 個 |
| pack URI | 0 處 | 數千處 |
| 第三方資源字典 | 0 本 | 到處都是 |
| ThemeDictionaries | 0 | WinUI Gallery 全靠它 |
| 建置時產生的主題檔 | 0 | MahApps 全部都是 |
| 用 XAML 做在地化 | 0 | ScreenToGif 四十個語系 |

也就是說，**本文第 9 節風險表把「第三方字典造成大量假懸空」列為頭號風險，
而那個風險在自家語料上的量測值是零。** 寫風險表的時候沒有辦法知道它有多大，
因為手上的樣本量不出來。

**結論寫進憲法**：「每條規則來自實查」要加一句 ——
**實查的樣本必須包含非作者的專案**，而且**在第一版就要跑**，不是等到 1.0 前。

## 7.2 開發期間自己出過的一個錯：假的進步

修假警報修到一半時，`ProjectIndex` 收的是絕對路徑，但 finding 帶的是相對路徑
（baseline 要跨 OS 可攜）。`Path.GetFullPath` 於是對著行程的工作目錄解析，
結果**每一個引用都比對不到專案**，整批被當成「函式庫、跨專案」而靜默跳過。

表面上 wpfui 的 289 筆 `out-of-scope` 歸零了，看起來像修好了；實際上是整類檢查被關掉。

抓到它的是**示範專案的計數斷言**（`out-of-scope` 從 1 變 0），
而那個斷言之所以存在，就是為了抓「擋的數量莫名其妙變動」。

這跟 XamlContrast §8.1「工具自己出過四次錯，每一次都是**謊報健康**」是同一種病。
它也解釋了為什麼第 0 節的第二條原則要寫得那麼硬：
**讓結果變寬鬆的每一條路徑，都要在 summary 留下計數。**

---

## 8. 為什麼不併進 XamlContrast（決策紀錄，2026-11-14）

曾評估把它做成 `xamlcontrast` 的子指令或 `--resources` 模式旗標。**結論是獨立套件。**
三個理由，依重要性排序：

1. **XamlContrast 全庫沒有 `MergedDictionaries`。** 它用的是全專案扁平的色鍵索引，
   刻意繞開合併語意 —— 而合併順序正是本工具的核心。把它加進共用程式碼會改動
   色盤偵測結果，而 XamlContrast 的 CI 用 `jq` 寫死了 `fail == 3` 等計數斷言，
   那是拿八個公開 WPF 專案加 WinUI Gallery 對照出來的。風險不對稱。
2. **CLI 沒有子指令，而且已凍結。** `xamlcontrast [旗標] <root>` 只收一個位置參數。
   `xamlcontrast resources <path>` 會跟**一個叫 `Resources` 的資料夾**撞名 ——
   那正是這幾個 WPF 專案的標準資料夾，Windows 又不分大小寫。這是破壞性變更，
   等於要為這個功能發 XamlContrast 2.0。
3. **共用程式碼比想像的少。** Core 共 6,870 行，其中色彩語意（`PaletteDetector` 1373、
   `Auditor` 1577、`ColorResolver` 218…）佔絕大多數。真正可用的只有
   `{StaticResource}` 鍵名抽取、`x:Key` 的 regex 眉角、資源別名、ThemeDictionaries，
   **不到一成**。而且那一成有價值的是**知識而非架構**（HandyControl 把 `x:Key`
   寫在第二個屬性、MahApps 的 `<Color x:Key>` 與色值分屬不同行、註解掉的舊定義會被誤收）
   —— 知識抄得走，不需要共用套件。

**連帶決定：不為此抽出 `XamlContrast.Core` 共用套件。** XamlContrast 的 ROADMAP
寫明「觸發條件才動工 —— 沒有消費者的公開 API 只是提前凍結自己的重構自由」。
本工具只吃最薄的解析層，不吃色彩引擎，構不成那個觸發條件。
**做法：複製那幾百行並在檔頭註明出處。**

---

## 9. 風險表

| 風險 | 影響 | 對策 |
|---|---|---|
| **第三方字典造成大量假懸空** | 工具第一天被關掉 | external 標記（4.2）；M1 的驗收條件就是這個 |
| **誤報 unused，使用者照刪炸掉** | 比漏報嚴重得多 | unused 預設不擋；`unresolved` 大聲計數；exit 3 的覆蓋率防線 |
| **作用域是靜態近似** | 邊緣情形判錯 | 近似範圍寫進 README 盲區表；每條規則來自實查 |
| **跟 XamlContrast 看起來重疊** | 使用者困惑「該裝哪個」 | 兩邊 README 互相點名分工；第 8 節的理由對外講清楚 |
| **市場** | — | 比 XamlContrast 大：不限於在意無障礙的專案，**每個 WPF/WinUI 專案都有資源字典** |
| **ReSharper 補上 unused 檢查** | 部分被取代 | 它要全專案反向索引才做得到，那不是單檔 inspection 的形狀；真被補上也證明方向對 |

---

## 10. 版本策略（照 XamlContrast）

**留在 0.x 直到介面凍結。** 0.x 期間 README 明寫「請 pin 確切版本」。
**起始版本 0.1.0。** csproj / `Directory.Build.props` 的版本固定 `0.0.0-dev`，
**tag 才是版本真相源**。

1.0 的凍結面（照 XamlContrast 的九面）：

- [ ] CLI 旗標與位置參數
- [ ] 退出碼四碼的語意
- [ ] JSON 報告（`schemaVersion`）
- [ ] SARIF 規則 ID
- [ ] `xamlkeys.config.json` 的 schema
- [ ] baseline 檔格式（`keyVersion`）
- [ ] `action.yml` 的 inputs
- [ ] ignore 註解語法
- [ ] 稽核範圍的硬規則（預設行為）

1.0 的條件：

- [ ] 四個自家專案零設定可用，假懸空為零
- [ ] 至少一個非自己的專案用過並回報
- [ ] 重新辯論預設 gate（4.3）
- [ ] baseline 與 ignore 在既有專案實際用過

---

## 11. 命名（定案）

```
產品名 / repo    XamlKeys
NuGet            Cornhsu.XamlKeys
CLI 指令         xamlkeys
GitHub           HSU-YU-MING/cornhsu-xamlkeys
```

2026-11-14 查證：NuGet 與 GitHub 上精確名稱皆未被使用。

**選它的理由**：跟 `XamlContrast` 同一個命名公式 ——「Xaml + 這個工具處理的那個東西」。
資源鍵就是本工具的基本單位，而三類 finding（懸空／被蓋／沒人用）全都是**鍵**的毛病。
讀起來是同一個家族。

**落選的兩個：**

- `XamlScope` —— 「作用域」精準描述了機制，但使用者搜的是症狀不是機制；
  而且 scope 容易被誤讀成「限制範圍」。
- `XamlSweep` —— 只講到「清掉沒用的」那一半，涵蓋不到懸空與被蓋掉。

**已知的輕微撞名**：`Xamlizer` 產生的類別叫 `<檔名>Keys`（`Colors.xaml` → `ColorsKeys`）。
概念不同、受眾極小，不構成問題，但發佈時 README 要能跟它區分。

---

## 下一步

1. **M0 原型** —— 在四個自家專案上跑出鍵圖譜並人工歸因。
   不先做這一步，後面所有規則都是猜的（這正是 XamlContrast 憲法的由來）
2. 開 GitHub repo，搬 XamlContrast 的 CI / RELEASING / Directory.Build.props 骨架
3. nuget.org 建 Trusted Publishing 政策（新 repo 要新政策，政策綁 repo + workflow）
4. M1 作用域引擎
