# Releasing

**推 tag 就是發版動作。** 其餘全部由 `.github/workflows/release.yml` 完成：
從 tag 導出版本 → build → test → `dotnet pack` → NuGet Trusted Publishing（OIDC 換臨時金鑰，
沒有長效 API key、沒有 repo secret）。

## 版本號的唯一真相源是 tag

`Directory.Build.props` / csproj 裡那格固定寫 **`0.0.0-dev`**。

- **寫死一個真版本號** → 一定會跟 tag 漂開。發到 NuGet 的沒事（CI 會覆寫），但
  **從原始碼 build 出來的執行檔會自稱舊版**，連它蓋在 JSON 報告上的 `toolVersion` 都是錯的。
  這個 bug 在 XamlContrast 真的發生過。
- **整格留空** → 退回 MSBuild 預設的 `1.0.0`，更糟。本專案刻意留在 0.x 直到介面凍結，
  自稱 1.0.0 等於宣告介面已經凍結了。

## 每次發布

1. `CHANGELOG.md` 的版本段落寫完整（**tag 推出去就發佈了，沒有反悔的機會**）
2. `dotnet test XamlKeys.slnx` 全綠
3. `scripts/verify-readme-sample.ps1` 綠 —— 動過 console 輸出就會紅，確認新輸出正確後跑 `-Update`
4. `scripts/verify-baselines.ps1` 綠 —— 自家三個專案的結果沒有無聲變動
5. **動到解析或判定規則時，外部專案比對值得再跑一次**（下一節）
6. 相依升版或增減時，`THIRD-PARTY-NOTICES.md` 要跟著改
7. 推 tag：

```bash
git tag v0.1.0
git push origin v0.1.0
```

## 外部專案比對（規則變動時）

自家三個專案**全部是 WPF 應用程式**，測不到函式庫、pack URI、ThemeDictionaries、WinUI
那幾類缺陷。`verify-baselines.ps1` 守不到它們。

做法（照 XamlContrast 的流程）：sparse clone 只拿 `*.xaml`、`*.cs`、`*.csproj`，
新舊兩版各跑 `--json`、逐筆比 findings：

```bash
for repo in NickeManarin/ScreenToGif HandyOrg/HandyControl MahApps/MahApps.Metro \
            lepoco/wpfui MaterialDesignInXAML/MaterialDesignInXamlToolkit \
            microsoft/WinUI-Gallery icsharpcode/ILSpy; do
  name=$(basename "$repo")
  git clone --filter=blob:none --no-checkout --depth 1 "https://github.com/$repo.git" "$name"
  (cd "$name" && git sparse-checkout init --cone \
     && git sparse-checkout set --no-cone '/**/*.xaml' '/**/*.cs' '/**/*.csproj' \
     && git checkout)
  xamlkeys "$name" --min-coverage 0 --json "$name.json"
done
```

2026-11-14 的基準數字（第一版）記在 `docs/實查歸因-2026-11-14.md` 第 3 節。
**任何一類的計數大幅變動都要能解釋**。

## 框架鍵清單（WinUI 與 WPF Fluent）

`WinUiFrameworkKeys.json`（3,573 個鍵）與 `WpfFluentKeys.json`（955 個鍵）都不是手抄的：

```bash
# WinUI（microsoft/microsoft-ui-xaml）
git clone --filter=blob:none --no-checkout --depth 1 \
  https://github.com/microsoft/microsoft-ui-xaml.git muxc
(cd muxc && git sparse-checkout init --cone \
   && git sparse-checkout set --no-cone '/**/*.xaml' && git checkout)
node scripts/regenerate-framework-keys.mjs winui muxc "$(cd muxc && git rev-parse HEAD)"

# WPF 內建的 Fluent 主題（dotnet/wpf，.NET 9 起）
git clone --filter=blob:none --no-checkout --depth 1 https://github.com/dotnet/wpf.git dotnet-wpf
(cd dotnet-wpf && git sparse-checkout init --cone \
   && git sparse-checkout set --no-cone '/**/*.xaml' && git checkout)
node scripts/regenerate-framework-keys.mjs fluent dotnet-wpf "$(cd dotnet-wpf && git rev-parse HEAD)"
```

看 `git diff` 再 commit，**並且把 `THIRD-PARTY-NOTICES.md` 裡對應的 commit 一起更新**。

⚠ 兩份清單的適用條件不同：WinUI 的框架資源**一定在**，所以 WinUI 方言一律查；
WPF 的 Fluent 主題是**選用**的，所以只有在真的有合併 `PresentationFramework.Fluent` 時才查
（`FrameworkKeys.IsWpfFluentSource`）。不分條件地豁免，會把沒用 Fluent 的 WPF 專案裡的
真懸空一起遮掉。

## 首次發佈（一次性設定，做過就不用再做）

1. repo 推上 GitHub（`Directory.Build.props` 的 `RepositoryUrl` 與 `release.yml` 指到同一個路徑）
2. nuget.org → 帳號 → Trusted Publishing 建一條政策。政策綁的是 **repo + workflow**：
   - Package Owner：`Cornhsu`
   - Repository Owner：`HSU-YU-MING`
   - Repository：`cornhsu-xamlkeys`
   - Workflow File：`release.yml`
   - Environment：留空
   - Scope 要涵蓋得到 `Cornhsu.XamlKeys`
3. 確認 `release.yml` 裡 `NuGet/login` 的 `user:` 是 nuget.org 使用者名稱（`Cornhsu`）
4. GitHub repo 的 Actions 需要 `id-token: write`（workflow 已宣告）

## 常見的漏

- 發版前去手動改 csproj 的版本 —— **不用改，改了反而製造漂移**
- 改了 console 輸出但沒更新 README 的示範段落（`verify-readme-sample.ps1` 會擋）
- `CHANGELOG.md` 沒寫就推 tag
- 改了判定規則但沒跑外部專案比對
- 重產 WinUI 鍵清單卻忘了更新 `THIRD-PARTY-NOTICES.md` 的 commit
- 把 Actions 改回移動 tag（`@v7`）
- 還沒凍結介面就跳 1.0.0
