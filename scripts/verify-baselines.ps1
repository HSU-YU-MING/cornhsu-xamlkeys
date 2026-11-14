<#
.SYNOPSIS
  拿維護者自己的專案當回歸基準：工具在它們上面的輸出不該無聲無息地變動。

.DESCRIPTION
  baselines/*.json 是每個專案的 JSON 報告快照。規則改動讓計數變了，這裡會紅。
  確認新結果才是對的，就用 -Update 重拍。

  ⚠ 這個腳本只守維護者的專案 —— 它們全部是 WPF 應用程式，
  測不到函式庫、pack URI、ThemeDictionaries、WinUI 那幾類缺陷
  （docs/實查歸因-2026-11-14.md 第 1 節）。動到解析或判定規則時，
  那一輪外部專案比對值得再跑一次。

.PARAMETER Update
  用目前的輸出重拍所有基準線。

.PARAMETER Root
  掃描對象的根目錄（預設 D:\應用程式）。
#>
param(
    [switch]$Update,
    [string]$Root = 'D:\應用程式'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

$projects = @(
    @{ Name = 'quillnest'; Path = Join-Path $Root 'QuillNest' }
    @{ Name = 'kindling';  Path = Join-Path $Root 'Kindling'  }
    @{ Name = 'cornea';    Path = Join-Path $Root 'Cornea'    }
)

dotnet build (Join-Path $repo 'src/XamlKeys.Cli/XamlKeys.Cli.csproj') -c Release -v q --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'build failed' }

$dll = Get-ChildItem -Path (Join-Path $repo 'src/XamlKeys.Cli/bin/Release') -Recurse -Filter 'xamlkeys.dll' |
       Select-Object -First 1
if (-not $dll) { throw 'cannot find the built xamlkeys.dll' }

$baselineDir = Join-Path $repo 'baselines'
New-Item -ItemType Directory -Force -Path $baselineDir | Out-Null

$failed = @()

foreach ($p in $projects) {
    if (-not (Test-Path $p.Path)) {
        Write-Host ("skip {0}: {1} not found" -f $p.Name, $p.Path) -ForegroundColor DarkGray
        continue
    }

    $out = Join-Path ([System.IO.Path]::GetTempPath()) ("xamlkeys-{0}.json" -f $p.Name)
    & dotnet $dll.FullName $p.Path --min-coverage 0 --json $out | Out-Null

    # toolVersion 每次建置都可能不同，比對前拿掉
    $actual = (Get-Content $out -Raw -Encoding UTF8) -replace '"toolVersion": "[^"]*"', '"toolVersion": "<redacted>"'
    $file = Join-Path $baselineDir ("{0}.json" -f $p.Name)

    if ($Update -or -not (Test-Path $file)) {
        [System.IO.File]::WriteAllText($file, $actual, [System.Text.UTF8Encoding]::new($false))
        Write-Host ("wrote {0}" -f $p.Name) -ForegroundColor Yellow
        continue
    }

    $expected = Get-Content $file -Raw -Encoding UTF8
    if ($expected.Replace("`r`n", "`n").TrimEnd() -eq $actual.Replace("`r`n", "`n").TrimEnd()) {
        Write-Host ("ok   {0}" -f $p.Name) -ForegroundColor Green
    } else {
        Write-Host ("DIFF {0}" -f $p.Name) -ForegroundColor Red
        $failed += $p.Name
    }
}

if ($failed.Count -gt 0) {
    Write-Host ''
    Write-Host ("{0} baseline(s) differ: {1}" -f $failed.Count, ($failed -join ', ')) -ForegroundColor Red
    Write-Host 'If the new results are correct, run: scripts/verify-baselines.ps1 -Update'
    exit 1
}

exit 0
