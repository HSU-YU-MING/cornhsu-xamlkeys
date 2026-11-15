// 把 scripts/corpus-run.sh 的輸出跟 docs/corpus.tsv 記的計數比。
//
//   node scripts/corpus-compare.mjs <輸出目錄> [舊輸出目錄]
//
// 只給一個目錄：逐專案比 XAML 檔數、finding 總數、gate（severity = fail）筆數。
// 再給一個舊輸出目錄：另外逐筆列出 finding 的增減（kind、key、檔、行）。
// 有任何差異就 exit 1 —— 每一筆差異都要能解釋（RELEASING.md「外部專案比對」）。
import { readFileSync, existsSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const [outDir, oldDir] = process.argv.slice(2);
if (!outDir) {
  console.error('usage: node scripts/corpus-compare.mjs <out-dir> [old-out-dir]');
  process.exit(2);
}
const tsv = join(dirname(fileURLToPath(import.meta.url)), '..', 'docs', 'corpus.tsv');
const rows = readFileSync(tsv, 'utf8').trim().split(/\r?\n/).slice(1).map(l => l.split('\t'));

const load = p => JSON.parse(readFileSync(p, 'utf8'));
const sig = f => `${f.kind}|${f.key}|${f.file}|${f.line}`;
let differ = 0, missing = 0;
const total = { xaml: [0, 0], findings: [0, 0], gated: [0, 0] };

for (const [name, , , xaml, findings, gated] of rows) {
  const p = join(outDir, `${name}.json`);
  if (!existsSync(p)) { console.log(`${name}: no output`); missing++; continue; }
  const r = load(p);
  const now = { xaml: r.summary.xamlFiles, findings: r.findings.length,
                gated: r.findings.filter(f => f.severity === 'fail').length };
  const was = { xaml: +xaml, findings: +findings, gated: +gated };
  for (const k of Object.keys(total)) { total[k][0] += was[k]; total[k][1] += now[k]; }
  const changed = Object.keys(was).filter(k => was[k] !== now[k]);
  if (changed.length === 0) continue;
  differ++;
  console.log(`${name}: ` + changed.map(k => `${k} ${was[k]} -> ${now[k]}`).join(', '));
  if (oldDir && existsSync(join(oldDir, `${name}.json`))) {
    const count = list => list.reduce((m, f) => m.set(sig(f), (m.get(sig(f)) ?? 0) + 1), new Map());
    const a = count(load(join(oldDir, `${name}.json`)).findings), b = count(r.findings);
    for (const [s, n] of b) for (let i = (a.get(s) ?? 0); i < n; i++) console.log(`   + ${s}`);
    for (const [s, n] of a) for (let i = (b.get(s) ?? 0); i < n; i++) console.log(`   - ${s}`);
  }
}
console.log(`total: xaml ${total.xaml[0]} -> ${total.xaml[1]}, findings ${total.findings[0]} -> ${total.findings[1]}, ` +
            `gated ${total.gated[0]} -> ${total.gated[1]}; ${differ} project(s) differ, ${missing} missing`);
process.exit(differ || missing ? 1 : 0);
