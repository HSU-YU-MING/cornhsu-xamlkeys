// 從上游的主題資源檔產生「框架／函式庫主題提供的資源鍵」清單。
//
//   pack                    上游                                     產出
//   winui                   microsoft/microsoft-ui-xaml              WinUiFrameworkKeys.json
//   fluent                  dotnet/wpf（PresentationFramework.Fluent） WpfFluentKeys.json
//   avalonia-fluent         AvaloniaUI/Avalonia（Themes.Fluent）      AvaloniaFluentKeys.json
//   avalonia-fluent2        AvaloniaUI/Avalonia（Themes.Fluent2）     AvaloniaFluent2Keys.json
//   avalonia-simple         AvaloniaUI/Avalonia（Themes.Simple）      AvaloniaSimpleKeys.json
//   avalonia-colorpicker    AvaloniaUI/Avalonia（Controls.ColorPicker）AvaloniaColorPickerKeys.json
//   avalonia-datagrid       AvaloniaUI/Avalonia.Controls.DataGrid     AvaloniaDataGridKeys.json
//   avalonia-treedatagrid   AvaloniaUI/Avalonia.Controls.TreeDataGrid AvaloniaTreeDataGridKeys.json
//   toolkit                 CommunityToolkit/Windows                  CommunityToolkitKeys.json
//   semi-avalonia           irihitech/Semi.Avalonia                   SemiAvaloniaKeys.json
//   uno-material            unoplatform/Uno.Themes（Material）         UnoMaterialKeys.json
//   uno-cupertino           unoplatform/Uno.Themes（Cupertino）        UnoCupertinoKeys.json
//   uno-toolkit             unoplatform/uno.toolkit.ui                UnoToolkitKeys.json
//   modernwpf               Kinnara/ModernWpf                         ModernWpfKeys.json
//
// 為什麼需要這份資料：專案的 XAML 大量引用框架或主題套件內建的鍵
// （TextFillColorPrimaryBrush、ControlCornerRadius、SettingsCardWrapThreshold…），
// 定義不在使用者的 repo 裡 —— 沒有清單的話，WinUI Gallery 被報出 195 個不重複的
// 假懸空，工具第一天就被關掉。除了 winui 之外都是**選用**的：只在那棵樹真的合併了那個主題時才算。
//
// ⚠ 資料不是手抄的。重新產生：
//   1. sparse clone 上游（只要 *.xaml、*.axaml）
//   2. node scripts/regenerate-framework-keys.mjs <pack> <clone 目錄> <完整 SHA>
//   3. 看 git diff 再 commit，**並且把 THIRD-PARTY-NOTICES.md 的 commit 一起更新**
//
// 授權：Uno.Themes 是 Apache-2.0，其餘都是 MIT。產出的是鍵名清單（事實資料），聲明仍要附。

import { readFileSync, writeFileSync, readdirSync, statSync } from 'node:fs';
import { join, basename, sep } from 'node:path';

const PACKS = {
  winui: {
    repo: 'microsoft/microsoft-ui-xaml', out: 'WinUiFrameworkKeys.json',
    // 任何 *themeresources*.xaml、generic.xaml、以及 controls/dev 底下的控制項樣式檔（它們被 generic.xaml 合併）
    take: (unix, name) => name.includes('themeresources') || name === 'generic.xaml' || unix.includes('/controls/dev/'),
  },
  fluent: {
    repo: 'dotnet/wpf', out: 'WpfFluentKeys.json',
    take: unix => unix.includes('/Themes/PresentationFramework.Fluent/'),
  },
  'avalonia-fluent': {
    repo: 'AvaloniaUI/Avalonia', out: 'AvaloniaFluentKeys.json',
    take: unix => unix.includes('/src/Avalonia.Themes.Fluent/'),
  },
  'avalonia-fluent2': {
    repo: 'AvaloniaUI/Avalonia', out: 'AvaloniaFluent2Keys.json',
    take: unix => unix.includes('/src/Avalonia.Themes.Fluent2/'),
  },
  'avalonia-simple': {
    repo: 'AvaloniaUI/Avalonia', out: 'AvaloniaSimpleKeys.json',
    take: unix => unix.includes('/src/Avalonia.Themes.Simple/'),
  },
  'avalonia-colorpicker': {
    repo: 'AvaloniaUI/Avalonia', out: 'AvaloniaColorPickerKeys.json',
    take: unix => unix.includes('/src/Avalonia.Controls.ColorPicker/Themes/'),
  },
  'avalonia-datagrid': {
    repo: 'AvaloniaUI/Avalonia.Controls.DataGrid', out: 'AvaloniaDataGridKeys.json',
    take: unix => unix.includes('/Themes/'),
  },
  'avalonia-treedatagrid': {
    repo: 'AvaloniaUI/Avalonia.Controls.TreeDataGrid', out: 'AvaloniaTreeDataGridKeys.json',
    take: unix => unix.includes('/Themes/'),
  },
  'semi-avalonia': {
    repo: 'irihitech/Semi.Avalonia', out: 'SemiAvaloniaKeys.json',
    take: unix => unix.includes('/src/Semi.Avalonia/'),
  },
  'uno-material': {
    repo: 'unoplatform/Uno.Themes', out: 'UnoMaterialKeys.json',
    // Material 主題本身＋它建在上面的共用層（Uno.Themes）
    take: unix => unix.includes('/src/library/Uno.Material/') || unix.includes('/src/library/Uno.Themes/'),
  },
  'uno-cupertino': {
    repo: 'unoplatform/Uno.Themes', out: 'UnoCupertinoKeys.json',
    take: unix => unix.includes('/src/library/Uno.Cupertino/') || unix.includes('/src/library/Uno.Themes/'),
  },
  'uno-toolkit': {
    repo: 'unoplatform/uno.toolkit.ui', out: 'UnoToolkitKeys.json',
    take: unix => unix.includes('/src/Uno.Toolkit.UI/') || unix.includes('/src/library/'),
  },
  modernwpf: {
    repo: 'Kinnara/ModernWpf', out: 'ModernWpfKeys.json',
    // WPF 上仿 WinUI 外觀的函式庫：主題資源與控制項樣式（Gallery、samples、test、tools 不算）
    take: unix => unix.includes('/ModernWpf/') || unix.includes('/ModernWpf.Controls/'),
  },
  toolkit: {
    repo: 'CommunityToolkit/Windows', out: 'CommunityToolkitKeys.json',
    // 每個元件的 src/（控制項樣式與 lightweight styling 鍵）；samples、tests 不算
    take: unix => unix.includes('/components/') && unix.includes('/src/'),
  },
};

const [, , which, root, commit] = process.argv;
const pack = PACKS[which];
if (!pack || !root || !commit) {
  console.error(`usage: node regenerate-framework-keys.mjs <${Object.keys(PACKS).join('|')}> <clone-dir> <full-sha>`);
  process.exit(2);
}

const SKIP = ['TestUI', 'Tests', 'tests', 'RegressionProjects', 'InteractionTests', 'ApiTests', 'DevTests',
              'samples', 'Samples', '.git'];

function* walk(dir) {
  for (const entry of readdirSync(dir)) {
    const p = join(dir, entry);
    let st;
    try { st = statSync(p); } catch { continue; }
    if (st.isDirectory()) {
      if (SKIP.includes(entry)) continue;
      yield* walk(p);
    } else if (/\.a?xaml$/i.test(entry)) {
      yield p;
    }
  }
}

const KEY_RE = /\bx:Key\s*=\s*"([^"]+)"/g;

const keys = new Set();
let files = 0;

for (const file of walk(root)) {
  const unix = file.split(sep).join('/');
  if (!pack.take(unix, basename(file).toLowerCase())) continue;
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

const target = `src/XamlKeys.Core/${pack.out}`;
const out = {
  source: `${pack.repo}@${commit}`,
  license: 'MIT',
  generatedBy: 'scripts/regenerate-framework-keys.mjs',
  files,
  keys: [...keys].sort(),
};

writeFileSync(target, JSON.stringify(out, null, 1) + '\n');
console.log(`${out.keys.length} keys from ${files} files -> ${target}`);
