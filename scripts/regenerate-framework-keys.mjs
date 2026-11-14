// 從 microsoft/microsoft-ui-xaml 的主題資源檔產生「WinUI 框架提供的資源鍵」清單。
//
// 為什麼需要這份資料：WinUI 專案的 XAML 大量引用框架內建的鍵
// （TextFillColorPrimaryBrush、ControlCornerRadius、BodyTextBlockStyle…）。
// 那些鍵的定義不在使用者的 repo 裡 —— 沒有這份清單，WinUI Gallery 這種專案
// 會被報出 195 個不重複的假懸空，工具第一天就被關掉。
//
// ⚠ 資料不是手抄的。重新產生：
//   1. sparse clone microsoft/microsoft-ui-xaml（只要 *.xaml）
//   2. node scripts/regenerate-framework-keys.mjs <clone 目錄> <完整 SHA>
//   3. 看 git diff 再 commit
//
// 授權：microsoft-ui-xaml 是 MIT。產出的是鍵名清單（事實資料），
// THIRD-PARTY-NOTICES.md 仍要附上出處與授權。

import { readFileSync, writeFileSync, readdirSync, statSync } from 'node:fs';
import { join, basename, sep } from 'node:path';

const [, , root, commit] = process.argv;
if (!root || !commit) {
  console.error('usage: node regenerate-framework-keys.mjs <muxc-clone-dir> <full-sha>');
  process.exit(2);
}

const SKIP = ['TestUI', 'Tests', 'RegressionProjects', 'InteractionTests', 'ApiTests', 'DevTests'];

function* walk(dir) {
  for (const entry of readdirSync(dir)) {
    const p = join(dir, entry);
    let st;
    try { st = statSync(p); } catch { continue; }
    if (st.isDirectory()) {
      if (SKIP.includes(entry)) continue;
      yield* walk(p);
    } else if (entry.toLowerCase().endsWith('.xaml')) {
      yield p;
    }
  }
}

// 只收「會被合併進應用程式主題字典」的檔：
//   - 任何 *themeresources*.xaml
//   - generic.xaml
//   - controls/dev 底下的控制項樣式檔（它們被 generic.xaml 合併）
function isThemeFile(path) {
  const name = basename(path).toLowerCase();
  if (name.includes('themeresources')) return true;
  if (name === 'generic.xaml') return true;
  return path.split(sep).join('/').includes('/controls/dev/');
}

const KEY_RE = /\bx:Key\s*=\s*"([^"]+)"/g;

const keys = new Set();
let files = 0;

for (const file of walk(root)) {
  if (!isThemeFile(file)) continue;
  files++;

  const text = readFileSync(file, 'utf8');
  for (const m of text.matchAll(KEY_RE)) {
    const k = m[1].trim();
    // {x:Type …} 之類的型別鍵不是字串鍵；Dark/Light/HighContrast 是 ThemeDictionaries 的分側鍵
    if (k.startsWith('{')) continue;
    if (k === 'Default' || k === 'Dark' || k === 'Light' || k === 'HighContrast') continue;
    keys.add(k);
  }
}

const out = {
  source: `microsoft/microsoft-ui-xaml@${commit}`,
  license: 'MIT',
  generatedBy: 'scripts/regenerate-framework-keys.mjs',
  files,
  keys: [...keys].sort(),
};

const target = 'src/XamlKeys.Core/WinUiFrameworkKeys.json';
writeFileSync(target, JSON.stringify(out, null, 1) + '\n');
console.log(`${out.keys.length} keys from ${files} files -> ${target}`);
