// 從上游的主題資源檔產生「框架提供的資源鍵」清單。
//
// 支援兩套：
//   winui  —— microsoft/microsoft-ui-xaml      -> src/XamlKeys.Core/WinUiFrameworkKeys.json
//   fluent —— dotnet/wpf 的 PresentationFramework.Fluent -> src/XamlKeys.Core/WpfFluentKeys.json
//
// 為什麼需要這份資料：那些專案的 XAML 大量引用框架內建的鍵
// （TextFillColorPrimaryBrush、ControlCornerRadius、BodyTextBlockStyle…），
// 定義不在使用者的 repo 裡 —— 沒有清單的話，WinUI Gallery 被報出 195 個不重複的
// 假懸空，工具第一天就被關掉。
//
// ⚠ 資料不是手抄的。重新產生：
//   1. sparse clone 上游（只要 *.xaml）
//   2. node scripts/regenerate-framework-keys.mjs <winui|fluent> <clone 目錄> <完整 SHA>
//   3. 看 git diff 再 commit，**並且把 THIRD-PARTY-NOTICES.md 的 commit 一起更新**
//
// 授權：兩個上游都是 MIT。產出的是鍵名清單（事實資料），聲明仍要附。

import { readFileSync, writeFileSync, readdirSync, statSync } from 'node:fs';
import { join, basename, sep } from 'node:path';

const [, , which, root, commit] = process.argv;
if (!['winui', 'fluent'].includes(which) || !root || !commit) {
  console.error('usage: node regenerate-framework-keys.mjs <winui|fluent> <clone-dir> <full-sha>');
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

// 只收「會被合併進應用程式主題字典」的檔。
function isThemeFile(path) {
  const unix = path.split(sep).join('/');

  if (which === 'fluent') {
    // dotnet/wpf：整個 PresentationFramework.Fluent 主題（Resources/ 與 Styles/）
    return unix.includes('/Themes/PresentationFramework.Fluent/');
  }

  // microsoft-ui-xaml：任何 *themeresources*.xaml、generic.xaml、
  // 以及 controls/dev 底下的控制項樣式檔（它們被 generic.xaml 合併）
  const name = basename(path).toLowerCase();
  if (name.includes('themeresources')) return true;
  if (name === 'generic.xaml') return true;
  return unix.includes('/controls/dev/');
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

const repo = which === 'fluent' ? 'dotnet/wpf' : 'microsoft/microsoft-ui-xaml';
const target = which === 'fluent'
  ? 'src/XamlKeys.Core/WpfFluentKeys.json'
  : 'src/XamlKeys.Core/WinUiFrameworkKeys.json';

const out = {
  source: `${repo}@${commit}`,
  license: 'MIT',
  generatedBy: 'scripts/regenerate-framework-keys.mjs',
  files,
  keys: [...keys].sort(),
};

writeFileSync(target, JSON.stringify(out, null, 1) + '\n');
console.log(`${out.keys.length} keys from ${files} files -> ${target}`);
