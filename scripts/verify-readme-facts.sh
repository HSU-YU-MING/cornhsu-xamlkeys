#!/usr/bin/env bash
# README 的 GitHub Action 範例釘的工具版本，要等於 CHANGELOG 最新的已發佈版本。
#
# 為什麼要擋：tag 是版本真相源、csproj 固定 0.0.0-dev，所以 repo 裡**唯一**會跟版本漂開的地方就是 README
# 的範例（XamlFit 0.3.0 發完才發現兩份 README 還釘著 0.1.0）。沒有人改範例時會想到它，所以讓 CI 自己比。
# 跟 XamlFit 的 verify-readme-facts.sh、Parity 的 verify-readme-facts.ps1 同一個形狀：
# 有 --update 讓人確認之後一鍵重貼，沒有 --update 的檢查會被關掉。
#
# 用法：bash scripts/verify-readme-facts.sh            # 檢查，不一致 exit 1
#       bash scripts/verify-readme-facts.sh --update   # 把兩份 README 的釘選改成 CHANGELOG 的最新版

set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

# CHANGELOG 最新的已發佈版本：第一個 `## [x.y.z]`（[Unreleased] 不算）
VER="$(grep -oE '^## \[[0-9]+\.[0-9]+\.[0-9]+\]' CHANGELOG.md | head -1 | tr -d '#[] ')"
if [ -z "$VER" ]; then
  echo "verify-readme-facts: no released version heading (## [x.y.z]) in CHANGELOG.md"
  exit 1
fi

FAIL=0
for f in README.md README.zh-Hant.md; do
  if [ "${1:-}" = "--update" ]; then
    sed -i -E "s/(cornhsu-xamlkeys@v)[0-9]+\.[0-9]+\.[0-9]+/\1$VER/g; s/^([[:space:]]+version: )[0-9]+\.[0-9]+\.[0-9]+/\1$VER/" "$f"
  fi
  USES="$(grep -oE 'cornhsu-xamlkeys@v[0-9]+\.[0-9]+\.[0-9]+' "$f" | sort -u | tr '\n' ' ')"
  PIN="$(grep -oE '^[[:space:]]+version: [0-9]+\.[0-9]+\.[0-9]+' "$f" | grep -oE '[0-9]+\.[0-9]+\.[0-9]+' | sort -u | tr '\n' ' ')"
  if [ "$USES" != "cornhsu-xamlkeys@v$VER " ] || [ "$PIN" != "$VER " ]; then
    echo "FAIL: $f pins [${USES% }] / version [${PIN% }], but the latest released version in CHANGELOG.md is $VER"
    echo "      (run: bash scripts/verify-readme-facts.sh --update)"
    FAIL=1
  else
    echo "ok: $f pins $VER (matches CHANGELOG.md)"
  fi
done

exit $FAIL
