# Changelog

All notable changes to this project are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
versions follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

**This project is on 0.x: pin an exact version.** Interfaces are not frozen until 1.0 —
see [ROADMAP.md](ROADMAP.md) for the freeze checklist.

## [Unreleased]

## [0.1.0] - 2026-11-14

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

### Avalonia, with a guinea pig

The roadmap gated Avalonia on having a real project to validate against. Three arrived:
**AvaloniaUI/Avalonia itself, FluentAvalonia and Semi.Avalonia** — 1,172 `.axaml` files.

The merge order was not guessed. `Avalonia.Base/Controls/ResourceDictionary.cs` resolves merged
dictionaries with `for (var i = _mergedDictionaries.Count - 1; i >= 0; --i)` — reverse order, last
declared wins, **the same as WPF**. No third dialect is needed. The same method also settles
`ThemeVariant.Default`: it is the fallback consulted when the requested variant has no dictionary,
so `Default` is now treated as neutral in every dialect rather than being guessed from its
siblings — erring towards under-reporting instead of towards false positives.

- `<ResourceInclude Source="…"/>` (and the older `MergeResourceInclude`) is a real merge, not an
  unknown element.
- `avares://Assembly/Path.axaml` resolves against projects in the tree.
- `<X.Styles>` carries resources: a `<StyleInclude Source="…"/>` is a resolvable merge, and a
  library's theme object (`<semi:SemiTheme/>`) is an external one.
- **Platform-supplied keys (`SystemAccentColor…`, `SystemColor…`) count in every dialect**, not
  only WinUI. This alone removed 162 false danglings in Avalonia and 48 in ModernWpf.
- **A theme entry's side reaches the dictionaries it includes.** Themes are often split by
  directory, with the `Dark` entry holding a single `ResourceInclude`. And mutual exclusion is
  "two dictionaries reached from *different entries of the same group*", not "the same group id" —
  Semi.Avalonia declares the same `ThemeDictionaries` twice, in `SemiTheme.axaml` and
  `Index.axaml`, so every key was reached from two groups. 66 false `shadowed` findings.

All three Avalonia projects now report zero `fail`-level findings, and re-running the twelve
WPF/WinUI projects shows no finding lost or changed.

### .NET MAUI, and the namespace that made every key invisible

A probe of a fourth ecosystem — `dotnet/maui-samples` (1,238 XAML files across 231 separate sample
projects) and `dotnet/eShop` — found **zero keys** and **19,395 false danglings**. One cause:
MAUI's `x:` namespace is `http://schemas.microsoft.com/winfx/2009/xaml`, while WPF, WinUI and
Avalonia use `.../2006/xaml`. The 2006 URI was hard-coded, so every `x:Key` was invisible.

- **Both XAML language namespaces are recognised** for `x:Key` and `x:Name`.
- **"Loaded from code" is matched per project.** With 231 samples in one repository, matching a
  dictionary by filename alone meant that one sample mentioning `Colors.xaml` in C# exempted the
  other 230 from the orphan check.
- **Symlinks and directory junctions are skipped**, because following one that points at an
  ancestor never terminates. Noted in the README's limitations.

After the fix: 4,361 keys in maui-samples and 40 `fail`-level findings — **all of them real**;
`ThemePrimary`, `TextPrimaryColor` and the rest are defined nowhere in that repository. eShop goes
from 239 false danglings to zero. None of the fifteen existing projects changed by a single finding.

### The built-in WPF Fluent theme, and naming hints

- **`PresentationFramework.Fluent` keys are recognised** — 955 names generated from `dotnet/wpf`,
  the same way the WinUI list is generated. It is **opt-in**: the list is only consulted when the
  tree actually merges that theme, because WPF's Fluent theme is optional while WinUI's framework
  resources are always present. Excusing those names unconditionally would hide real danglings in
  every WPF project that does not use Fluent. `microsoft/WPF-Samples` goes from 448 `external`
  findings to 16, and no other project in the corpus changes by a single finding.
- **Dangling and unused findings name the closest key they can see.** Most of them are a typo or
  the leftover half of a rename — QuillNest's `InverseBoolToVis` sat next to an
  `InverseBoolToVisibilityConverter` that is referenced twelve times, and `microsoft/maui-samples`
  references `CardBackgroundColor` where it defines `CardBackground`.
  The comparison runs on the parts of the two names that actually differ, after the common prefix
  and suffix are stripped: without that, `Vector.Back` suggests `Vector.Clock` and
  `TextPrimaryColor` suggests `LightPrimaryColor`. A wrong suggestion is worse than none, so the
  tests for this feature are mostly about **staying quiet**.
- **`out-of-scope` now names where the key is defined** instead of saying "elsewhere in the tree".

### Getting the interfaces ready to freeze

1.0 means the interfaces stop changing, so this round was a last pass over them while they can
still be changed — plus tests that lock them.

Three traps found and fixed:

- **`--baseline b.json --write-baseline b.json` wiped the baseline to zero entries.** That is the
  natural way to re-record a ratchet, and the file is committed to the repository. `--write-baseline`
  now always records the full current set, not what is left after the old baseline is subtracted.
- **`--stale` without `--baseline` silently did nothing** — the exact sin this project complains
  about elsewhere. It is now a usage error.
- **An ignore comment with no reason suppressed findings anyway.** The design has always said the
  reason is required and a reasonless ignore is invalid; that was never implemented, so
  `<!-- xamlkeys-ignore -->` was an unaccountable silent-suppression channel. It now suppresses
  nothing, and the run prints a warning naming the lines.

Also: `--include-tests` (the option existed only in the config file), and a nudge when a baseline
has entries that no longer occur — a ratchet nobody tightens eventually hides new debt.

**The default gate is settled: it stays at `fail` only.** Across the twenty projects in the corpus,
gating on `fail` turns two of them red with 42 findings, every one verified by hand; gating on
`warn` would turn sixteen of them red with 2,536. A gate is worth having only if you believe it
when it fires, so the default stops at "your app will throw at load" and everything else is advice.

**Nine frozen surfaces, 37 tests.** CLI flags and positional arguments, the four exit codes and
their order, the JSON shape and `schemaVersion`, SARIF rule ids, the config schema and its default
severities, the baseline `keyVersion` and entry format, the action inputs, the ignore syntax, and
the default audit scope. Touching any of them turns CI red, so a change has to start by admitting
it is a change. Two more tests cross-check `docs/report.schema.json` and `docs/config-schema.md`
against what the tool actually writes and accepts — a spec nobody validates is decoration.
