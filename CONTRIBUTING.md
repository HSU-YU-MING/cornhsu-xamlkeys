# Contributing

## 這個專案的規矩只有一條半

**一條**：每一條判定規則都要來自實查，而且實查的樣本要包含**不是作者寫的**專案。

本專案第一個能跑的版本，在第一批七個公開專案（1,593 個 XAML 檔）上吐出約 36,000 筆假警報；
之後又擴了三輪到十七個公開專案，每一輪都再找出新的缺陷（第四輪在 .NET MAUI 上是 0 個鍵、19,395 筆假懸空）——
而那些缺陷在作者自己的三個專案上**一條都複製不出來**。原因很單純：
那三個專案都是 WPF 應用程式，沒有函式庫、沒有 pack URI、沒有 ThemeDictionaries、沒有 WinUI。

完整紀錄在 `docs/實查歸因-2026-11-14.md`。

**半條**：新規則的 PR 請照這個形狀：

1. 先說明是哪個真實專案的哪一個檔案讓你發現這件事
2. 在 `tests/XamlKeys.Tests/CorpusLessonTests.cs` 加一條測試，
   註解寫明「來源」與「不做會怎樣」
3. 再改規則

沒有第 1 步的規則，就是沒有驗證過的猜測。

## 回報假警報

假警報對本工具來說**比漏報嚴重**：這個工具的 finding 會讓人去刪檔案。

開 issue 時請附上：

- `xamlkeys <path> --json report.json` 的 `summary` 區塊
- 那一筆 finding
- 那個鍵實際上為什麼是有用的

## 本機驗證

```bash
dotnet test XamlKeys.slnx
./scripts/verify-readme-sample.ps1
./scripts/verify-baselines.ps1
```
