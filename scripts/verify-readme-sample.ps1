<#
.SYNOPSIS
  確認 README 裡貼的示範輸出，跟工具現在真的跑出來的一樣。

.DESCRIPTION
  文件裡的示範輸出會隨著工具改版而過期，而且人工比對一定會漏 ——
  改輸出格式的時候沒有人會想到 README。解法是讓 CI 自己跑一次再比對。

  不一致時列出差異並 exit 1。確認新輸出才是對的，就用 -Update 一鍵重貼。
  （沒有 -Update 的檢查會讓人想把它關掉。）

.PARAMETER Update
  用實際輸出覆蓋 README 裡的那一段。
#>
param([switch]$Update)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

$cli  = Join-Path $repo 'src/XamlKeys.Cli/XamlKeys.Cli.csproj'
$demo = Join-Path $repo 'samples/demo'

Write-Host 'building the CLI...'
dotnet build $cli -c Release -v q --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'build failed' }

$dll = Get-ChildItem -Path (Join-Path $repo 'src/XamlKeys.Cli/bin/Release') -Recurse -Filter 'xamlkeys.dll' |
       Select-Object -First 1
if (-not $dll) { throw 'cannot find the built xamlkeys.dll' }

# ⚠ --min-coverage 0：示範專案刻意含一本讀不到的外部字典，預設會是 exit 3，
#    那會蓋掉 finding 的輸出。README 要展示的是 finding。
$actual = (& dotnet $dll.FullName $demo --min-coverage 0 2>&1 | Out-String).Replace("`r`n", "`n").TrimEnd()

$readmePath = Join-Path $repo 'README.md'
$readme = Get-Content $readmePath -Raw -Encoding UTF8

# README 裡的那一段是 "## Example" 之後第一個沒有語言標籤的 ``` 區塊
# ⚠ \r?\n：README 可能是 CRLF（被別的工具改寫過）。只認 \n 的 regex 會整個對不上，
#    而錯誤訊息是「找不到示範區塊」，看起來像 README 被改壞了,其實只是換行字元。
$pattern = '(?s)(## Example.*?\r?\n```\r?\n)(.*?)(\r?\n```)'
$m = [regex]::Match($readme, $pattern)
if (-not $m.Success) { throw 'cannot locate the sample block in README.md' }

$expected = $m.Groups[2].Value.Replace("`r`n", "`n").TrimEnd()

if ($expected -eq $actual) {
    Write-Host 'README sample matches the tool output.' -ForegroundColor Green
    exit 0
}

if ($Update) {
    $updated = $readme.Remove($m.Groups[2].Index, $m.Groups[2].Length).Insert($m.Groups[2].Index, $actual)
    [System.IO.File]::WriteAllText($readmePath, $updated, [System.Text.UTF8Encoding]::new($false))
    Write-Host 'README sample updated.' -ForegroundColor Yellow
    exit 0
}

Write-Host 'README sample is out of date.' -ForegroundColor Red
Write-Host '--- expected (README) ---'
Write-Host $expected
Write-Host '--- actual (tool) ---'
Write-Host $actual
Write-Host ''
Write-Host 'If the new output is correct, run: scripts/verify-readme-sample.ps1 -Update'
exit 1
