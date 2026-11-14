# Security

## What this tool does, and what that means

XamlKeys **reads** `.xaml`, `.axaml`, `.cs` and `.csproj` files as text and writes a report. It does
not load assemblies, instantiate XAML, execute anything from the project it scans, or read
credentials. There is no server and no daemon.

**It makes no network requests at all** — not for statistics, not for updates, not for anything. The
only files it writes are the ones you name with `--json`, `--sarif`, `--html` and `--write-baseline`.

That narrows the realistic threat surface to one thing: **it parses input it does not trust.**
People run it in CI, on pull requests, on branches pushed by strangers. A crafted `.xaml` file is
attacker-controlled input reaching an XML parser that runs with the permissions of your CI job.

### XML external entities and entity expansion

**Blocked.** XAML is parsed through `XmlReader.Create` with `DtdProcessing = Prohibit` and
`XmlResolver = null`. A file that declares a DTD is refused outright and counted as a parse error:

```xml
<!DOCTYPE foo [ <!ENTITY xxe SYSTEM "file:///etc/passwd"> ]>
```

This is set explicitly rather than relied upon. An earlier build used `XDocument.Load(path)`, whose
defaults **did** expand internal entities — a probe confirmed that a key declared through
`<!ENTITY>` was resolved. Nothing leaked, because resource *values* are never printed, but that is
a property of today's report format, not a security boundary. The parser is now locked down so the
boundary does not depend on it.

Because DTDs are refused, entity-expansion attacks ("billion laughs") cannot start.
`CorpusLessonTests.A_DTD_is_refused_rather_than_expanded` covers this.

### Symlinks and directory junctions

**Skipped.** Recursive enumeration does not follow reparse points. A junction that points at one of
its own ancestors would otherwise make a scan run forever. The cost — source reached only through a
link is not scanned — is listed in the README's limitations.

### A config file inside the scanned tree

`xamlkeys.config.json` is picked up automatically from the folder being scanned. **On CI, that file
is part of the pull request.** It can lower every severity to `info`, add `usedAtRuntime` patterns
that suppress whole families of keys, or drop `minCoverage` — in other words, it can turn the gate
off from inside the change being reviewed.

This is not forbidden, because it is also how people configure the tool locally. Instead:

- the run **prints a note on stderr** whenever an auto-discovered config actually loosens the result;
- `summary.configOverrides` is `true` in the JSON report whenever any non-default setting is in play.

**In CI, pass `--config <path>` explicitly** — pointing at a file outside the pull request's reach —
if you want the gate to be beyond the author's control.

### Text a scanned file could inject into a CI log

**Escaped.** An attribute value can contain a real line break (`x:Key="A&#10;::error::..."` is valid
XAML), so a key name could otherwise start its own line in the console or `--msbuild` output — a
forged diagnostic, a GitHub Actions workflow command, or an Azure Pipelines `##vso[...]` command.
Everything scanned that reaches plain-text output (keys, file names, `--stale` entries, config and
baseline error messages) is printed on one line with control characters escaped, `##vso[` and `##[`
defused, and no line allowed to start with `::`. JSON, SARIF and baseline files keep the original
value; their serialisers escape it. `HostileInputTests` covers this.

### Pathologically nested files

**Skipped, not followed.** Nesting is measured while streaming, before the file is loaded. A file
nested more than 512 elements deep is counted as a parse error (so it still feeds the "is this
result trustworthy" decision). Without the limit, about 8,000 levels — a 100 KB file — overflowed
the stack and took the whole run down with an exit code outside the documented 0-3.

### The GitHub Action

`action.yml` passes every input to the shell through `env`, never as `${{ inputs.x }}` inside a
script, so an input wired to something a pull request author controls stays a string.
`HostileInputTests.The_action_never_interpolates_an_input_into_a_shell_script` keeps it that way.

### Paths in the output

The HTML report embeds the absolute path of the folder that was scanned, so that a reader knows what
the report covers. The JSON and SARIF reports contain only paths relative to the scan root. Resource
*key names* appear in all of them; resource *values* appear in none.

## Reporting a vulnerability

Open a [security advisory](https://github.com/HSU-YU-MING/cornhsu-xamlkeys/security/advisories/new)
rather than a public issue. A crafted input that makes the tool hang, crash, read a file outside the
scan root, or write outside the paths given on the command line is in scope.

Findings that amount to "the tool reported something inaccurate" are bugs, not vulnerabilities —
please open a normal issue, and see `CONTRIBUTING.md`, which explains why a false positive is taken
more seriously here than a missed finding.
