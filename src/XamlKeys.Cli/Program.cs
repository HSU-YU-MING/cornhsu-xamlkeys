using System.Reflection;
using XamlKeys.Core;

// XamlKeys CLI —— XAML 資源鍵的靜態作用域稽核，退出碼給 CI 當守門員。
//
// 退出碼契約（1.0 凍結；判定順序見 DecideExit）：
//   0 通過
//   1 找到違規（達到 gate 的 finding）
//   2 用法、設定或輸入檔錯誤
//   3 結果不可信 —— 掃到 0 個鍵、或合併字典的解析率低於下限
//     （「工具沒看到」與「看到了有問題」在 CI 上的處置不同：前者要修設定，
//      後者要修程式碼。比照 Parity 與 XamlContrast 的 exit 3）

string? root = null;
string? jsonPath = null;
string? sarifPath = null;
string? htmlPath = null;
string? configPath = null;
string? baselinePath = null;
string? writeBaselinePath = null;
string? failOnFlag = null;
string? dialectFlag = null;
double? minCoverage = null;
var msbuild = false;
var showInfo = false;
var listStale = false;
var includeTests = false;
var excludes = new List<string>();

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--show-info": showInfo = true; break;
        case "--msbuild": msbuild = true; break;
        case "--stale": listStale = true; break;
        case "--include-tests": includeTests = true; break;

        case "--json":
            if (Value(args, ref i, "--json") is not { } jp) return 2;
            jsonPath = jp; break;

        case "--sarif":
            if (Value(args, ref i, "--sarif") is not { } sp) return 2;
            sarifPath = sp; break;

        case "--html":
            if (Value(args, ref i, "--html") is not { } hp) return 2;
            htmlPath = hp; break;

        case "--config":
            if (Value(args, ref i, "--config") is not { } cp) return 2;
            configPath = cp; break;

        case "--baseline":
            if (Value(args, ref i, "--baseline") is not { } bp) return 2;
            baselinePath = bp; break;

        case "--write-baseline":
            if (Value(args, ref i, "--write-baseline") is not { } wp) return 2;
            writeBaselinePath = wp; break;

        case "--exclude":
            if (Value(args, ref i, "--exclude") is not { } ex) return 2;
            excludes.Add(ex); break;

        case "--fail-on":
            if (i + 1 >= args.Length || args[i + 1] is not ("warn" or "fail"))
            { Console.Error.WriteLine("--fail-on takes 'warn' or 'fail'"); return 2; }
            failOnFlag = args[++i]; break;

        case "--dialect":
            if (i + 1 >= args.Length || args[i + 1] is not ("wpf" or "winui"))
            { Console.Error.WriteLine("--dialect takes 'wpf' or 'winui'"); return 2; }
            dialectFlag = args[++i]; break;

        case "--min-coverage":
            if (i + 1 >= args.Length ||
                !double.TryParse(args[i + 1], System.Globalization.NumberStyles.Float,
                                 System.Globalization.CultureInfo.InvariantCulture, out var mc) ||
                // ⚠ NaN 對任何比較都是 false：範圍檢查擋不住它，之後「覆蓋率 < NaN」
                //   也永遠不成立 → 下限靜默失效。IsFinite 一併擋掉 Infinity。
                !double.IsFinite(mc) || mc is < 0 or > 100)
            { Console.Error.WriteLine("--min-coverage takes a number between 0 and 100"); return 2; }
            minCoverage = mc; i++; break;

        // 旗標到此為止：之後的字一律是位置參數。
        // ⚠ 沒有這個出口的話，一個叫 --version 的目錄會被當成旗標（印版本、exit 0，什麼都沒稽核）。
        case "--":
            for (i++; i < args.Length; i++)
            {
                if (root is null) { root = args[i]; continue; }
                Console.Error.WriteLine($"unknown argument: {args[i]}");
                return 2;
            }
            break;

        case "-h" or "--help":
            PrintUsage(Console.Out);
            return 0;

        case "--version":
            Console.WriteLine(ToolVersion());
            return 0;

        default:
            if (args[i].StartsWith('-'))
            {
                Console.Error.WriteLine($"unknown option: {args[i]}");
                PrintUsage(Console.Error);
                return 2;
            }
            if (root is not null)
            {
                Console.Error.WriteLine($"unknown argument: {args[i]} (only one root path is accepted)");
                return 2;
            }
            root = args[i];
            break;
    }
}

if (root is null)
{
    Console.Error.WriteLine("a path to scan is required");
    PrintUsage(Console.Error);
    return 2;
}

if (!Directory.Exists(root))
{
    Console.Error.WriteLine($"not a directory: {root}");
    return 2;
}

// ⚠ 旗標組合不成立時要出聲。靜靜什麼都不做,使用者會以為自己檢查過了。
if (listStale && baselinePath is null)
{
    Console.Error.WriteLine("--stale lists baseline entries that no longer occur, so it needs --baseline <path>");
    return 2;
}

Config config;
var autoDiscoveredConfig = (string?)null;
try
{
    if (configPath is null && DefaultConfigIn(root) is { } found) autoDiscoveredConfig = found;

    var effective = configPath ?? autoDiscoveredConfig;
    config = effective is not null ? Config.Load(effective) : new Config();

    if (configPath is not null && !File.Exists(configPath))
    {
        Console.Error.WriteLine($"config not found: {configPath}");
        return 2;
    }
}
catch (ConfigException ex)
{
    Console.Error.WriteLine(Report.OneLine(ex.Message));
    return 2;
}

// 旗標覆寫 config
if (failOnFlag is not null) config.FailOn = failOnFlag == "warn" ? Severity.Warn : Severity.Fail;
if (minCoverage is { } m) config.MinCoverage = m;
if (dialectFlag is not null)
{
    config.Dialect = dialectFlag == "winui" ? XamlDialect.WinUi : XamlDialect.Wpf;
    config.DialectAutoDetected = false;
}
config.Exclude.AddRange(excludes);
if (includeTests) config.IncludeTests = true;

// ⚠ 自動撿到的設定檔**來自被掃描的那棵樹**。在 CI 上掃一個 PR 的時候，
//   那個檔案就是 PR 的內容 —— 它可以把 severities 全部降成 info、
//   或用 usedAtRuntime 把整批鍵壓掉，把守門員靜靜關掉。
//   不禁止（本地使用者就是靠它設定），但**一定要講出來**，而且只在它真的放寬了結果時講。
if (autoDiscoveredConfig is not null && config.HasOverrides)
    Console.Error.WriteLine(
        $"note: using '{autoDiscoveredConfig}' found in the scanned tree; it changes severities, " +
        $"suppressions or thresholds. Pass --config explicitly to control which file is used.");

var result = Analyzer.Run(root, config);
var version = ToolVersion();

// ⚠ --write-baseline 要記的是「現在全部的 finding」，不是「扣掉舊 baseline 之後剩下的」。
//   `--baseline b.json --write-baseline b.json`（更新基準線最自然的打法）
//   在舊版會把那個檔清成 0 條 —— 而它是進版控的檔案。所以先把完整清單留一份。
var allFindings = result.Findings.ToList();

// baseline 在輸出之前套用：報告裡不該出現已知債
Baseline? baseline = null;
if (baselinePath is not null)
{
    if (!File.Exists(baselinePath))
    {
        Console.Error.WriteLine($"baseline not found: {baselinePath} (create it with --write-baseline)");
        return 2;
    }

    try
    {
        baseline = Baseline.Load(baselinePath);
        baseline.Apply(result);
    }
    catch (ConfigException ex)
    {
        Console.Error.WriteLine(Report.OneLine(ex.Message));
        return 2;
    }
}

if (writeBaselinePath is not null)
{
    try
    {
        File.WriteAllText(writeBaselinePath, Baseline.FromFindings(allFindings).ToJson(version));
        Console.WriteLine($"wrote baseline with {allFindings.Count} entr(ies) to {writeBaselinePath}");
    }
    catch (IOException ex)
    {
        Console.Error.WriteLine($"cannot write baseline: {ex.Message}");
        return 2;
    }
}

Report.WriteConsole(Console.Out, result, showInfo);

if (msbuild) Report.WriteMsBuild(Console.Out, result);

if (jsonPath is not null && !TryWrite(jsonPath, Report.ToJson(result, version))) return 2;
if (sarifPath is not null && !TryWrite(sarifPath, ReportSarif.ToSarif(result, version))) return 2;
if (htmlPath is not null && !TryWrite(htmlPath, ReportHtml.ToHtml(result, version))) return 2;

// 棘輪只有在會「收緊」的時候才有意義。債還掉了卻沒人去縮 baseline,
// 那份清單就會一直把將來的新債一起蓋住 —— 所以即使沒下 --stale 也要提一句。
if (baseline is not null)
{
    var stale = baseline.Stale(allFindings).ToList();

    if (listStale)
    {
        if (stale.Count > 0)
        {
            Console.WriteLine($"{stale.Count} baseline entr(ies) no longer occur -- regenerate with --write-baseline:");
            // ⚠ baseline 檔是 PR 的內容，項目可以是任何字串。GitHub Actions 會先去掉行首空白
            //   再判斷 `::` 指令，所以只縮排不夠 —— 前面要有一個固定的非空白字元。
            foreach (var s in stale) Console.WriteLine($"  - {Report.OneLine(s)}");
        }
        else Console.WriteLine("every baseline entry still occurs; nothing to tighten");
    }
    else if (stale.Count > 0)
    {
        Console.WriteLine($"tip: {stale.Count} baseline entr(ies) no longer occur. " +
                          $"Run with --stale to list them, then --write-baseline to tighten the ratchet.");
    }
}

return DecideExit(result, writeBaselinePath is not null);

// ── 判定 ───────────────────────────────────────────────────────────────────

static int DecideExit(AnalysisResult r, bool wroteBaseline)
{
    // ⚠ 順序有意義：「不可信」排在「有違規」前面。
    //   一次重構讓所有路徑失效時，findings 會是空的 —— 綠燈放行就是謊報健康。
    if (r.Untrustworthy(out var reason))
    {
        Console.Error.WriteLine($"result is not trustworthy: {reason}");
        return 3;
    }

    // 剛寫完 baseline 的那一次不擋：使用者是在建立清單，不是在驗收
    if (wroteBaseline) return 0;

    var gate = r.Config.FailOn;
    var offending = r.Findings.Count(f => f.Severity >= gate && f.Severity != Severity.Info);
    return offending > 0 ? 1 : 0;
}

static bool TryWrite(string path, string content)
{
    try
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir is { Length: > 0 }) Directory.CreateDirectory(dir);
        File.WriteAllText(path, content);
        return true;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"cannot write '{path}': {ex.Message}");
        return false;
    }
}

static string? DefaultConfigIn(string root)
{
    var p = Path.Combine(root, Config.DefaultFileName);
    return File.Exists(p) ? p : null;
}

/// <summary>旗標的值：必填，且不得以 - 開頭 —— 否則 `--json --msbuild` 會把下一個旗標吃成路徑。</summary>
static string? Value(string[] args, ref int i, string flag)
{
    if (i + 1 >= args.Length || args[i + 1].StartsWith('-'))
    {
        Console.Error.WriteLine($"{flag} takes a path");
        return null;
    }
    return args[++i];
}

static string ToolVersion() =>
    Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?.Split('+')[0] ?? "unknown";

static void PrintUsage(TextWriter o)
{
    o.WriteLine("""
        XamlKeys — static scope audit for XAML resource keys.

        Usage:
          xamlkeys [options] <path>

        Finds, across the whole project without launching the app:
          - {StaticResource} references whose key is defined nowhere   (throws at load time)
          - the same x:Key defined twice in one dictionary             (throws at load time)
          - definitions that merge order makes unreachable
          - keys that nothing references
          - ResourceDictionaries that nothing merges

        Options:
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
          --version                print the tool version
          -h, --help               this text

        Exit codes:
          0  clean
          1  findings at or above the gate
          2  usage, config or input error
          3  result is not trustworthy (nothing found, or too many dictionaries unreadable)
        """);
}
