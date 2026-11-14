# Changelog

All notable changes to this project are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
versions follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

**This project is on 0.x: pin an exact version.** Interfaces are not frozen until 1.0 —
see [ROADMAP.md](ROADMAP.md) for the freeze checklist.

## [Unreleased]

## [0.1.0] - unreleased

First release.

### What it does

Parses every XAML file under a root, builds the resource **scope graph** (merge order included),
and reports nine kinds of finding: `dangling-static`, `duplicate-key`, `dangling-dynamic`,
`out-of-scope`, `shadowed`, `shadowed-identical`, `unused`, `orphan-dictionary`, `external`.
Outputs a console report, JSON (`--json`), SARIF 2.1.0 (`--sarif`) and MSBuild diagnostics
(`--msbuild`). Baseline/ratchet adoption (`--baseline` / `--write-baseline`) and
`<!-- xamlkeys-ignore: reason -->` comments.

Exit codes: `0` clean, `1` findings at or above the gate, `2` usage/config/input error,
`3` result is not trustworthy.

### Validated against

Three maintainer WPF applications and **seven public projects** — ScreenToGif, HandyControl,
MahApps.Metro, wpfui, MaterialDesignInXamlToolkit, WinUI Gallery and ILSpy, 1,593 XAML files in
total. The first working version produced roughly **36,000 false positives** on that corpus; none
of them could be reproduced on the maintainer's own projects. Every rule below came from that run
and has a regression test in `tests/XamlKeys.Tests/CorpusLessonTests.cs`. The full attribution is
in [docs/實查歸因-2026-11-14.md](docs/實查歸因-2026-11-14.md).

- **An unreadable merged dictionary makes a lookup `Unknown`, never "missing".** Treating it as
  missing reports every third-party theme key as dangling (HandyControl, MahApps).
- **`x:Name` is a resource key** when `x:Key` is absent (WinUI Gallery, wpfui).
- **A markup extension is only parsed at the start of a value** — `{ThemeResource Key}` inside a
  sentence is prose (WinUI Gallery).
- **WinUI framework keys are not dangling**: an embedded list of 3,573 keys generated from
  microsoft-ui-xaml. Without it, WinUI Gallery reported 975 false danglings.
- **Library projects are not checked for `unused` or `orphan-dictionary`** — a library's keys are
  its public API (MaterialDesignInXamlToolkit: 1,767 false positives).
- **Cross-project references are not scope-checked** (wpfui: 421 false positives).
- **Dictionaries with near-identical key sets are variants**, not shadowing — locale and theme
  files define the same keys on purpose (ScreenToGif: 28,704 false positives).
- **`unused` is reported once per key name**, not once per definition (ScreenToGif: 14,468 lines
  for 1,094 real keys).
- **`Themes/Generic.xaml` is loaded implicitly by WPF** and is never an orphan (ScreenToGif).
- **`HighContrast` is a third, mutually exclusive theme side**, and the meaning of `Default`
  depends on which siblings exist in the same `ThemeDictionaries` group (WinUI Gallery).
- **Test fixture paths are skipped by default** — they contain deliberately broken XAML (ILSpy).
- **Dictionaries loaded from C# join the application scope** (wpfui, QuillNest).
- **A definition shadowed by an identical copy is `info`**, not a warning (wpfui,
  MaterialDesignInXamlToolkit).
- **The dialect comes from the project files, not the XAML** — wpfui is a WPF library that borrows
  WinUI's key names and `{ThemeResource}` syntax; guessing from XAML flips the merge order and
  swaps the winner and loser of every `shadowed` finding.

### Known blind spots

Listed in the README with what the tool does about each. The short version: anything it cannot see
is **counted and printed**, and too much of it produces `exit 3` rather than a green tick.

### Hardening found by the post-build review

- **DTDs are refused, not expanded.** `XDocument.Load`'s default settings expand internal DTD
  entities — a probe confirmed a key declared through `<!ENTITY>` was resolved. External entities
  were only being blocked by the fact that resource *values* are never printed. This tool scans
  other people's repositories in CI, so `DtdProcessing` is now `Prohibit` and `XmlResolver` is
  null; a DOCTYPE becomes a visible parse error.
- **Too many unparsable files now produce `exit 3`.** A file that could not be read hides the
  references inside it, which turns the keys it uses into false `unused` findings. Previously this
  was a note at the end of the run and the exit code stayed 0 — another silent-degradation channel.
- **`THIRD-PARTY-NOTICES.md` is packed into the NuGet package.** It was not, and the package
  embeds data derived from microsoft-ui-xaml (MIT), whose licence requires the notice to travel
  with every copy.
- **Scope lookups are indexed and variant comparisons are cached.** ScreenToGif (121 files,
  45,054 keys) went from 8.5s to 1.1s with identical output.

### A second corpus round: six more public projects, seven more rules

Added Files (WinUI 3), Flow.Launcher (WPF app plus plugin projects, 289 locale files), ModernWpf
(WPF library with a WinUI helper tool in the same repo), Fluent.Ribbon, microsoft/WPF-Samples
(dozens of separate projects) and QuickLook (plugin architecture) — bringing the corpus to twelve
public projects plus three maintainer applications, around 3,600 XAML files. They produced roughly
970 more false positives. Each fix has a test in `CorpusLessonTests.cs`.

- **A `ResourceDictionary` subclass inside `MergedDictionaries` is an external merge edge.**
  QuickLook and Flow.Launcher merge `<ui:ThemesDictionary />`, whose contents live in an assembly.
  They were being ignored entirely — an unreadable dictionary treated as if it did not exist, which
  is the exact inverse of the tool's first invariant. 717 false danglings.
- **The dialect is decided per project, not per tree.** ModernWpf is a WPF library, but a helper
  tool under `tools/` declares `<UseWinUI>true</UseWinUI>` — one project flipped the merge order for
  the whole scan, swapping the winner and loser of every `shadowed` finding.
- **Two dictionaries in one `ThemeDictionaries` group can never shadow each other.** They are
  mutually exclusive at runtime. This replaces guessing from the key names, which got Files'
  `Default` + `HighContrast` pair wrong.
- **`Application.Resources` belongs to its own project.** Appending every application scope in the
  tree let a sample app's unreadable dictionary poison the scope of every file in the library —
  and silently downgraded an already-verified real bug from `dangling-static` to `external`.
- **A family of unmerged, near-identical dictionaries in one directory is runtime-selected.**
  Flow.Launcher's `Languages/` holds 289 files of exactly 606 keys each, picked at runtime through a
  fully composed path. 289 false orphans.
- **A `ResourceKey` attribute can hold a markup extension.** `ResourceKey="{x:Static
  SystemColors.HighlightColorKey}"` was being read as a literal key name.
- **An unresolved reference in a library project is reported but not gated.** A library's
  dictionaries are merged by someone else, and what that someone merges first is unknowable.
  `{DynamicResource}` drops to `info` (it is the documented way to let a consumer override a value);
  `{StaticResource}` stays at `warn`, because it throws at load — wpfui's real bug is exactly that
  shape.

After this round, across all fifteen projects the gate fires on **two** findings, both in
`microsoft/WPF-Samples` and both verified by hand: `CustomNavWindowStyle` and
`NavButtonFocusVisualStyle` are referenced but defined nowhere in that repository.

### `--html`: a self-contained report

The console is a wall on a legacy project — 1,100 findings for ScreenToGif. `--html` writes a
single file with inline CSS, **no scripts**, no external resources and no timestamp, so it can be
opened straight from a CI artifact (a report with scripts renders blank under a strict CSP) and
diffed between runs. Severity filtering is pure CSS; a file section collapses when all of its rows
are filtered out. "What this run could not see" sits at the top of the page, not at the bottom,
because it is the precondition for trusting `unused`.
