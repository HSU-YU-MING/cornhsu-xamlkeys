#!/usr/bin/env bash
# 外部專案比對：把 docs/corpus.tsv 列的公開專案，照釘死的 commit 做 sparse clone，逐一跑 xamlkeys --json。
#
#   scripts/corpus-run.sh <xamlkeys 執行檔> <clone 目錄> <輸出目錄> [名稱…]
#
# 已經 clone 過的不會重抓（同一個 commit 就直接跑）。只給名稱時只跑那幾個。
# 跑完用 scripts/corpus-compare.mjs 跟 docs/corpus.tsv 的計數比。
#
# ⚠ 只抓 XamlKeys 會讀的檔（下面的 PATTERNS）。改了要讀的副檔名，這裡要跟著加，
#   而且 docs/corpus.tsv 的計數要整批重算 —— 少抓一種檔，計數就不是同一回事
#   （2026-11-15 實際發生過：早期幾輪沒抓 .props／.vb／.cpp）。
set -euo pipefail

if [ $# -lt 3 ]; then
  echo "usage: $0 <xamlkeys> <clone-dir> <out-dir> [name...]" >&2
  exit 2
fi
XK=$1; CLONES=$2; OUT=$3; shift 3
HERE=$(cd "$(dirname "$0")" && pwd)
TSV="$HERE/../docs/corpus.tsv"
PATTERNS=('/**/*.xaml' '/**/*.axaml' '/**/*.cs' '/**/*.vb' '/**/*.cpp' '/**/*.h'
          '/**/*.csproj' '/**/*.vbproj' '/**/*.vcxproj' '/**/*.props' '/**/*.projitems'
          # GetDirectoryNameOfFileAbove 的錨點檔（Windows-universal-samples 找 LICENSE、WoWDatabaseEditor 找 .sln）
          '/**/LICENSE' '/**/*.sln')
mkdir -p "$CLONES" "$OUT"

tail -n +2 "$TSV" | tr -d '\r' | while IFS=$'\t' read -r name repo commit _; do
  if [ $# -gt 0 ] && ! printf '%s\n' "$@" | grep -qx "$name"; then continue; fi
  dir="$CLONES/$name"
  if [ "$(git -C "$dir" rev-parse HEAD 2>/dev/null || true)" != "$commit" ]; then
    rm -rf "$dir"
    git init -q "$dir"
    git -C "$dir" config core.longpaths true   # Windows：有些 repo 的路徑超過 260 字元（Uno.Samples、SteamTools、maui-samples）
    git -C "$dir" remote add origin "https://github.com/$repo.git"
    git -C "$dir" sparse-checkout set --no-cone "${PATTERNS[@]}"
    if ! git -C "$dir" fetch -q --depth 1 --filter=blob:none origin "$commit"; then
      echo "$name: fetch failed (repository gone or commit unreachable)" >&2
      continue
    fi
    if ! git -C "$dir" -c advice.detachedHead=false checkout -q FETCH_HEAD; then
      echo "$name: checkout failed" >&2
      continue
    fi
  fi
  # 退出碼 1／2 是「有 finding」，不是失敗 —— 照存，比對交給 corpus-compare
  "$XK" "$dir" --json "$OUT/$name.json" > "$OUT/$name.txt" 2>&1 || true
  echo "$name done"
done
