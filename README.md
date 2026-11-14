# <img src="https://raw.githubusercontent.com/HSU-YU-MING/cornhsu-xamlkeys/main/icon.png" width="28" alt=""/> XamlKeys

**Static scope audit for XAML resource keys. WPF, WinUI, Avalonia and .NET MAUI. No app launch, whole project, CI exit codes.**

[![NuGet](https://img.shields.io/nuget/v/Cornhsu.XamlKeys)](https://www.nuget.org/packages/Cornhsu.XamlKeys)
[![CI](https://github.com/HSU-YU-MING/cornhsu-xamlkeys/actions/workflows/ci.yml/badge.svg)](https://github.com/HSU-YU-MING/cornhsu-xamlkeys/actions)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

[繁體中文](README.zh-Hant.md)

A `{StaticResource}` whose key does not exist throws at runtime. The same `x:Key` twice in one
dictionary throws at load time too — and **the XAML compiler checks neither**. A key that merge
order quietly shadows renders the wrong colour forever. A key nobody uses sits there until someone
deletes the wrong one.

XamlKeys parses your XAML, works out **which definition each reference actually resolves to**, and
fails CI when something is broken.

```bash
dotnet tool install -g Cornhsu.XamlKeys
xamlkeys path/to/your/wpf/project
```

> Requires the **.NET 10 runtime**. The analysis is pure XML with no WPF dependency, so it runs on
> Linux CI too — this repository's own CI builds, tests and gates on `ubuntu-24.04`.

## What it finds

| Kind | Severity | What it means |
|---|---|---|
| `dangling-static` | **fail** | `{StaticResource K}` and nothing defines `K`. Throws at runtime — on load, or, inside a Style setter, the first time that property is used (a `FocusVisualStyle` setter waits for keyboard focus, so the window opens fine and crashes later). |
| `duplicate-key` | **fail** | The same `x:Key` twice in one dictionary. The parser throws at load time; the compiler does not check this. |
| `dangling-dynamic` | warn | `{DynamicResource K}` / `{ThemeResource K}` and nothing defines `K`. Falls back silently. |
| `out-of-scope` | warn | `K` exists in this project but not where this reference can see it. |
| `shadowed` | warn | Merge order makes this definition unreachable, and the winner has **different** content. |
| `shadowed-identical` | info | Same, but every copy is identical — redundant, not wrong. |
| `unused` | warn | Nothing in XAML or C# references this key — or a window's own copy of a key that nothing in that window can reach (the copy-pasted style nobody wired up). |
| `orphan-dictionary` | warn | A ResourceDictionary nothing merges and no code loads. |
| `external` | info | The key may come from a merged dictionary that could not be read. |

Only `fail` stops the build by default. `--fail-on warn` tightens the gate.

## Example

The repository ships a deliberately broken sample at `samples/demo`:

```
Themes/Base.xaml
      5  warn shadowed           'SharedBrush' is shadowed by a different definition at Themes/Override.xaml:5 (WPF resolves to the last merged dictionary); this definition never wins
      8  warn shadowed           'AppWins' is shadowed by a different definition at App.xaml:13 (WPF resolves to the last merged dictionary); this definition never wins
     11  warn unused             'DupKey' is defined but never referenced in XAML or code (2 definitions, e.g. also Themes/Base.xaml:12)
     12  fail duplicate-key      key 'DupKey' is defined more than once in the same dictionary; XAML requires keys to be unique within one dictionary and the parser throws at load time (first defined at Themes/Base.xaml:11)
     23  warn unused             'NobodyUsesThis' is defined but never referenced in XAML or code

Themes/Orphan.xaml
      3  warn orphan-dictionary  this ResourceDictionary (2 key(s)) is never merged by another dictionary and is not loaded from code -- nothing in it can be resolved
      6  warn unused             'StrandedBrush' is defined but never referenced in XAML or code
      7  warn unused             'StrandedBrush2' is defined but never referenced in XAML or code

Themes/Override.xaml
      8  warn shadowed           'AppWins' is shadowed by a different definition at App.xaml:13 (WPF resolves to the last merged dictionary); this definition never wins

Views/MainWindow.xaml
     27  fail dangling-static    'MissingBrush' is not defined anywhere in this tree; StaticResource throws at runtime -- on load, or for a Style setter the first time the property is used
     30  warn dangling-dynamic   'MissingDynamicBrush' is not defined anywhere in this tree; DynamicResource silently falls back to the default value
     42  warn out-of-scope       'OtherWindowLocal' is defined at Views/OtherWindow.xaml:6 but that is not in the scope of this reference

7 XAML file(s), 7 dictionar(ies), 19 key(s), 14 reference(s)
merged dictionaries resolved: 2/3 (66.7%)
dangling-static 1, dangling-dynamic 1, duplicate-key 1, out-of-scope 1, shadowed 3, shadowed-identical 1, unused 4, orphan-dictionary 1, external 1
note: the 4 unused finding(s) cover 5 definition(s) -- a key name defined in several dictionaries is reported once
note: 1 merged dictionar(ies) could not be read -- 'unused' results are incomplete for keys they may define
note: 1 runtime-composed resource lookup(s) in C# -- keys they reach cannot be seen statically (see 'usedAtRuntime' in the config)
note: 1 key(s) were counted as used because they match a key that C# composes at runtime (for example $"Icon{name}")
note: 1 finding(s) suppressed by ignore comments or config
```

## It tells you what the name was probably meant to be

Most dangling and unused keys are a typo or the leftover half of a rename, so the finding names the
closest key it can see:

```
'InverseBoolToVis' is defined but never referenced in XAML or code.
A similarly named key 'InverseBoolToVisibilityConverter' is referenced -- was this renamed?
```

The suggestion is deliberately quiet: it compares the parts of the two names that actually differ,
after stripping the common prefix and suffix. Without that, every key in a `Vector.*` namespace
looks like every other one, and a wrong suggestion is worse than none because it reads like an
answer.

## Why merge order is the whole problem

Finding every `x:Key` takes half an hour. The value is in the next step: **which definition wins.**

WPF resolves a resource through the element's own `Resources`, then every ancestor's, then
`Application.Resources`. Inside one dictionary, its **own** entries beat anything it merges, and
among merged dictionaries the **last one declared wins** — duplicate keys across merged
dictionaries are legal and never throw. WinUI reverses that order.

So none of the three questions can be answered by searching text:

- "defined twice" is only a problem if both definitions are in one lookup scope
- "nobody uses it" depends on who can see it
- "doesn't exist" depends on where you are standing

XamlKeys builds the scope graph and answers all three from it.

## It will not tell you to delete something it cannot see

The worst thing a tool like this can do is report "48 keys are unused", have you delete them, and
break the app. Every blind spot is therefore **counted and printed**, never swallowed:

- a merged dictionary it could not read → `external`, and the **unused** result is declared incomplete
- a key composed at runtime in C# (`FindResource($"Icon{name}")`) → counted as a dynamic site
- a key handed to such a lookup through a variable (`var k = ok ? "Green" : "Red"; ...SetResourceReference(p, k)`)
  → any string in that file that matches a key name counts as a use, and the number is printed
- keys in a **library** project → not checked at all (they are that library's public API)
- too many unreadable dictionaries → **exit 3**, "result is not trustworthy"

`exit 3` is deliberate. "I could not see your dictionaries" and "I looked and found nothing wrong"
must not produce the same green tick.

## CI

```yaml
- run: dotnet tool install -g Cornhsu.XamlKeys
- run: xamlkeys src --sarif xamlkeys.sarif
- uses: github/codeql-action/upload-sarif@v3
  with:
    sarif_file: xamlkeys.sarif
```

Exit codes:

| Code | Meaning |
|---|---|
| 0 | clean |
| 1 | findings at or above the gate |
| 2 | usage, config or input error |
| 3 | result is not trustworthy (nothing found, or too many dictionaries unreadable) |

## Adopting it on an existing project

Real projects start with debt. Record it once, then only new findings fail:

```bash
xamlkeys src --write-baseline xamlkeys-baseline.json   # once, commit the file
xamlkeys src --baseline xamlkeys-baseline.json         # from then on
```

When you pay some of it down, the run says so (`tip: 3 baseline entr(ies) no longer occur`).
Tighten the ratchet, so the entries you fixed cannot quietly come back:

```bash
xamlkeys src --baseline xamlkeys-baseline.json --stale                                       # what went away
xamlkeys src --baseline xamlkeys-baseline.json --write-baseline xamlkeys-baseline.json       # tighten
```

Baseline entries deliberately **do not contain line numbers** — one reformat would otherwise
invalidate the whole file.

To exempt a single key, with a reason (the reason is required, and suppressed items are counted):

```xml
<!-- xamlkeys-ignore: looked up by a plugin at runtime -->
<SolidColorBrush x:Key="PluginAccent" Color="#FF4081" />
```

## Configuration

`xamlkeys.config.json` next to the scanned folder, or `--config <path>`:

```json
{
  "dialect": "wpf",
  "failOn": "fail",
  "minCoverage": 80,
  "usedAtRuntime": ["Icon*", "Status.*"],
  "exclude": ["Generated"],
  "includeTests": false,
  "externalDictionaries": [
    { "assembly": "HandyControl", "path": "../packages/HandyControl/Themes" }
  ],
  "themeFiles": { "dark": ["Themes/Dark"], "light": ["Themes/Light"] },
  "severities": { "unused": "info", "out-of-scope": "fail" }
}
```

An unknown property is an **error**, not a shrug: a misspelled setting that silently does nothing
is worse than no setting at all.

## Options

```
--json <path>            write the JSON report
--sarif <path>           write a SARIF 2.1.0 report (fail/warn only)
--html <path>            write a self-contained HTML report (no scripts, no external files)
--msbuild                also print MSBuild diagnostics for the VS error list
--config <path>          config file (default: xamlkeys.config.json in <path>)
--baseline <path>        only report findings that are not in this baseline
--write-baseline <path>  write the current findings as a baseline
--stale                  with --baseline, list entries that no longer occur
--fail-on warn|fail      gate level (default: fail)
--dialect wpf|winui      merge-order dialect (default: detected from the project files)
--min-coverage <0-100>   minimum share of merged dictionaries that must resolve (default 80)
--exclude <fragment>     skip paths containing this fragment (repeatable)
--include-tests          also scan test projects and fixture folders
--show-info              also print info-level findings
```

## Known limitations

Every one of these was found by running the tool over seventeen public projects (WPF, WinUI,
Avalonia and .NET MAUI, about 5,600 XAML files) — see
[docs/實查歸因-2026-11-14.md](docs/實查歸因-2026-11-14.md).

| Limitation | Effect | What the tool does about it |
|---|---|---|
| Keys composed at runtime (`$"Icon{n}"`) | Could look unused | With a fixed part of three characters or more (`$"Icon{n}"`, `"Icon" + n`, `n + "Brush"`), keys in the same project that match count as used, and the summary says how many. Fully variable lookups are counted as dynamic sites; `usedAtRuntime` patterns suppress the keys they reach |
| Dictionaries inside third-party assemblies | Their contents are invisible | Marked `external`, counted, and they lower the coverage figure |
| Dictionaries merged by composed URIs in code | May look orphaned | Literal `.xaml` paths in C# are detected, and so are composed ones with a fixed part (`$"Themes/{name}.xaml"` loads every `Themes/*.xaml` in that project). `$"{name}.xaml"` and paths built with `+` are not |
| Scope is a static approximation | A `UserControl`'s real parent is unknown | A key that exists elsewhere is `out-of-scope` (warn), never `dangling` (fail) |
| `ComponentResourceKey`, `{x:Static …Key}` | Not string keys | Counted as unresolved sites, never graded |
| Library projects | Their keys are public API | `unused` and `orphan-dictionary` are not evaluated there |
| Symlinks and directory junctions | Their contents are skipped | Following them can loop forever when one points at an ancestor |
| Avalonia custom theme variants (`x:Key="{x:Static …}"`) | Treated as a neutral side | Mutual exclusion still applies, so they never shadow each other |

## License

MIT. The embedded list of WinUI framework resource keys is generated from
[microsoft/microsoft-ui-xaml](https://github.com/microsoft/microsoft-ui-xaml) (MIT) — see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
