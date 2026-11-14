using System.Net;
using System.Text;

namespace XamlKeys.Core;

/// <summary>
/// 自包含的 HTML 報告。
///
/// 為什麼需要：主控台在舊專案上就是一面牆（實測 ScreenToGif 1,100 筆）。
/// 要分類、要篩選、要先看最嚴重的那幾筆，主控台做不到。
///
/// 三個刻意的限制（照 XamlContrast 的形狀）：
///   - **單一檔案**：內嵌 CSS，沒有外部資源 —— CI 的 artifact 直接點開就能看。
///   - **沒有 script**：篩選用純 CSS（checkbox ＋ :has()）。
///     CI artifact 常常在嚴格的 CSP 下開啟，有 script 的報告會變成一片空白。
///   - **沒有時間戳**：同樣的輸入要產生同樣的檔案，不然沒辦法 diff。
/// </summary>
public static class ReportHtml
{
    public static string ToHtml(AnalysisResult r, string toolVersion)
    {
        var sb = new StringBuilder();

        sb.Append("""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>XamlKeys report</title>
            <style>
            :root {
              --bg: #ffffff; --fg: #1a1a1a; --muted: #5c6370; --line: #e2e5e9;
              --card: #f7f8fa; --fail: #b3261e; --warn: #8a5a00; --info: #3b6ea5;
              --failbg: #fdecea; --warnbg: #fdf4e3; --infobg: #eaf2fb;
            }
            @media (prefers-color-scheme: dark) {
              :root {
                --bg: #16181c; --fg: #e6e8eb; --muted: #9aa3ad; --line: #2b2f36;
                --card: #1d2026; --fail: #ff8a80; --warn: #ffcc80; --info: #90caf9;
                --failbg: #2b1b1a; --warnbg: #2a2318; --infobg: #19242f;
              }
            }
            * { box-sizing: border-box; }
            body { margin: 0; padding: 24px 16px 64px; background: var(--bg); color: var(--fg);
                   font: 14px/1.5 ui-sans-serif, -apple-system, "Segoe UI", system-ui, sans-serif; }
            main { max-width: 1100px; margin: 0 auto; }
            h1 { font-size: 20px; margin: 0 0 4px; }
            h2 { font-size: 15px; margin: 32px 0 8px; }
            .sub { color: var(--muted); margin: 0 0 24px; font-size: 13px; word-break: break-all; }
            .cards { display: flex; flex-wrap: wrap; gap: 8px; margin-bottom: 8px; }
            .card { background: var(--card); border: 1px solid var(--line); border-radius: 8px;
                    padding: 10px 14px; min-width: 104px; }
            .card b { display: block; font-size: 19px; font-weight: 650; }
            .card span { color: var(--muted); font-size: 12px; }
            .blind { background: var(--card); border: 1px solid var(--line); border-left: 3px solid var(--info);
                     border-radius: 6px; padding: 12px 14px; margin: 16px 0; }
            .blind p { margin: 0 0 6px; font-weight: 600; }
            .blind ul { margin: 0; padding-left: 20px; color: var(--muted); }
            .filters { display: flex; flex-wrap: wrap; gap: 6px; margin: 8px 0 16px; }
            .filters label { border: 1px solid currentColor; border-radius: 999px; padding: 4px 12px;
                             cursor: pointer; user-select: none; font-size: 13px; font-weight: 600;
                             background: var(--card); }
            label.f-fail { color: var(--fail); } label.f-warn { color: var(--warn); } label.f-info { color: var(--info); }
            /* 控制用的 checkbox 放在 main 之前（兄弟選擇器才找得到後面的內容）。
               ⚠ 不用 hidden 屬性：用位移到畫面外，<label for> 的點擊行為最可靠。 */
            .toggle { position: fixed; left: -9999px; width: 1px; height: 1px; opacity: 0; }
            table { width: 100%; border-collapse: collapse; }
            th { text-align: left; font-size: 12px; color: var(--muted); font-weight: 600;
                 border-bottom: 1px solid var(--line); padding: 6px 8px; }
            td { border-bottom: 1px solid var(--line); padding: 7px 8px; vertical-align: top; }
            td.ln { text-align: right; color: var(--muted); font-variant-numeric: tabular-nums; width: 1%; }
            .sev { display: inline-block; border-radius: 4px; padding: 1px 7px; font-size: 11px;
                   font-weight: 700; text-transform: uppercase; letter-spacing: .03em; }
            .sev.fail { color: var(--fail); background: var(--failbg); }
            .sev.warn { color: var(--warn); background: var(--warnbg); }
            .sev.info { color: var(--info); background: var(--infobg); }
            code { font: 12.5px/1.4 ui-monospace, "Cascadia Mono", Consolas, monospace;
                   background: var(--card); border-radius: 4px; padding: 1px 5px; }
            .kind { color: var(--muted); font-size: 12px; white-space: nowrap; }
            .file { font: 13px ui-monospace, Consolas, monospace; margin: 24px 0 2px; word-break: break-all; }
            .rel { color: var(--muted); font-size: 12px; }
            .none { color: var(--muted); padding: 24px 0; }
            /* ── 純 CSS 篩選 ──────────────────────────────────────────────
               沒勾的那一級：收起它的列，按鈕本身也要看得出來被關掉。 */
            #f-fail:not(:checked) ~ main tr.fail,
            #f-warn:not(:checked) ~ main tr.warn,
            #f-info:not(:checked) ~ main tr.info { display: none; }

            #f-fail:not(:checked) ~ main label.f-fail,
            #f-warn:not(:checked) ~ main label.f-warn,
            #f-info:not(:checked) ~ main label.f-info {
              opacity: .45; text-decoration: line-through; font-weight: 400;
            }

            /* 一個區塊的列全被篩掉時，連標題一起收起來 ——
               不然篩成「只看 fail」會剩一頁空的檔名，看起來像壞了。
               三個開關共 7 種關閉組合，每一種一條規則（checkbox 的文件順序是 fail→warn→info，
               所以 ~ 串接得起來）。 */
            #f-fail:not(:checked) ~ main section:not(:has(tr.warn)):not(:has(tr.info)),
            #f-warn:not(:checked) ~ main section:not(:has(tr.fail)):not(:has(tr.info)),
            #f-info:not(:checked) ~ main section:not(:has(tr.fail)):not(:has(tr.warn)),
            #f-fail:not(:checked) ~ #f-warn:not(:checked) ~ main section:not(:has(tr.info)),
            #f-fail:not(:checked) ~ #f-info:not(:checked) ~ main section:not(:has(tr.warn)),
            #f-warn:not(:checked) ~ #f-info:not(:checked) ~ main section:not(:has(tr.fail)),
            #f-fail:not(:checked) ~ #f-warn:not(:checked) ~ #f-info:not(:checked) ~ main section {
              display: none;
            }
            </style>
            </head>
            <body>
            """);

        // checkbox 必須在 main 之前，純 CSS 的兄弟選擇器才找得到後面的列
        sb.Append($"""
            <input class="toggle" type="checkbox" id="f-fail" checked>
            <input class="toggle" type="checkbox" id="f-warn" checked>
            <input class="toggle" type="checkbox" id="f-info" checked>
            <main>
            <h1>XamlKeys report</h1>
            <p class="sub">{E(r.Root)} &middot; xamlkeys {E(toolVersion)}</p>
            """);

        // ── 數字 ───────────────────────────────────────────────────────────
        sb.Append("<div class=\"cards\">");
        Card(sb, r.XamlFiles, "XAML files");
        Card(sb, r.Dictionaries, "dictionaries");
        Card(sb, r.KeysDefined, "keys");
        Card(sb, r.References, "references");
        Card(sb, $"{r.Coverage:F0}%", "merges resolved");
        sb.Append("</div><div class=\"cards\">");
        Card(sb, r.CountSeverity(Severity.Fail), "fail");
        Card(sb, r.CountSeverity(Severity.Warn), "warn");
        Card(sb, r.CountSeverity(Severity.Info), "info");
        sb.Append("</div>");

        // ── 工具沒看到的東西：放在最前面，不是埋在最後 ──────────────────────
        var blind = new List<string>();
        if (r.ExternalSources.Count > 0)
            blind.Add($"{r.ExternalSources.Count} merged dictionar(ies) could not be read, so "
                    + "<strong>'unused' is incomplete</strong> for keys they may define");
        if (r.DynamicKeySites.Count > 0)
            blind.Add($"{r.DynamicKeySites.Count} resource lookup(s) in C# build the key at runtime");
        if (r.KeysNamedNearDynamicLookups > 0)
            blind.Add($"{r.KeysNamedNearDynamicLookups} key(s) counted as used because their name appears as a string "
                    + "in a C# file that looks resources up by variable");
        if (r.LibraryKeysNotChecked > 0)
            blind.Add($"{r.LibraryKeysNotChecked} key(s) in library projects were not checked for usage "
                    + "(a library's keys are its public API)");
        if (r.LibraryUnresolvedReferences > 0)
            blind.Add($"{r.LibraryUnresolvedReferences} reference(s) in library projects resolve to nothing "
                    + "in their own project and are not gated");
        if (r.CrossProjectReferences > 0)
            blind.Add($"{r.CrossProjectReferences} reference(s) point at another project in this tree");
        if (r.RuntimeSelectedFamilyFiles > 0)
            blind.Add($"{r.RuntimeSelectedFamilyFiles} unmerged dictionar(ies) look like a runtime-selected family");
        if (r.VariantDictionaryGroups > 0)
            blind.Add($"{r.VariantDictionaryGroups} dictionary pair(s) look like locale/theme variants");
        if (r.FrameworkProvided > 0)
            blind.Add($"{r.FrameworkProvided} reference(s) resolve to WinUI framework keys");
        if (r.ParseErrors.Count > 0)
            blind.Add($"{r.ParseErrors.Count} file(s) failed to parse and were skipped");
        if (r.Suppressed > 0)
            blind.Add($"{r.Suppressed} finding(s) suppressed by ignore comments, config or a baseline");
        if (r.TestFilesExcluded > 0)
            blind.Add($"{r.TestFilesExcluded} XAML file(s) under test paths were skipped");

        if (blind.Count > 0)
        {
            sb.Append("<div class=\"blind\"><p>What this run could not see</p><ul>");
            foreach (var b in blind) sb.Append($"<li>{b}</li>");
            sb.Append("</ul></div>");
        }

        if (r.Untrustworthy(out var reason))
            sb.Append($"<div class=\"blind\" style=\"border-left-color: var(--fail)\">"
                    + $"<p>Result is not trustworthy</p><ul><li>{E(reason)}</li></ul></div>");

        // ── 篩選 ───────────────────────────────────────────────────────────
        sb.Append($"""
            <div class="filters">
              <label class="f-fail" for="f-fail">fail {r.CountSeverity(Severity.Fail)}</label>
              <label class="f-warn" for="f-warn">warn {r.CountSeverity(Severity.Warn)}</label>
              <label class="f-info" for="f-info">info {r.CountSeverity(Severity.Info)}</label>
            </div>
            """);

        // ── finding，依檔案分組 ─────────────────────────────────────────────
        if (r.Findings.Count == 0)
        {
            sb.Append("<p class=\"none\">No findings.</p>");
        }
        else
        {
            foreach (var group in r.Findings.GroupBy(f => f.Location.File)
                                            .OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                sb.Append($"<section><h2 class=\"file\">{E(group.Key)}</h2><table><tr>"
                        + "<th>line</th><th>severity</th><th>kind</th><th>key</th><th>what</th></tr>");

                foreach (var f in group.OrderBy(f => f.Location.Line))
                {
                    var sev = Finding.SeveritySlug(f.Severity);
                    var side = f.Side == ThemeSide.Neutral ? "" : $" <span class=\"rel\">[{Finding.SideSlug(f.Side)}]</span>";
                    var rel = f.Related is { } rl ? $"<div class=\"rel\">see {E(rl.ToString())}</div>" : "";

                    sb.Append($"<tr class=\"{sev}\">"
                            + $"<td class=\"ln\">{f.Location.Line}</td>"
                            + $"<td><span class=\"sev {sev}\">{sev}</span></td>"
                            + $"<td class=\"kind\">{Finding.KindSlug(f.Kind)}</td>"
                            + $"<td><code>{E(f.Key)}</code></td>"
                            + $"<td>{E(f.Message)}{side}{rel}</td></tr>");
                }

                sb.Append("</table></section>");
            }
        }

        sb.Append("</main></body></html>\n");
        return sb.ToString();
    }

    private static void Card(StringBuilder sb, object value, string label) =>
        sb.Append($"<div class=\"card\"><b>{E(value.ToString() ?? "")}</b><span>{E(label)}</span></div>");

    /// <summary>⚠ 鍵名與訊息裡有使用者的資料，一定要轉義。
    /// 資源鍵可以含有 &lt; 與 &amp;，不轉義的話報告會壞掉。</summary>
    private static string E(string s) => WebUtility.HtmlEncode(s);
}
