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
