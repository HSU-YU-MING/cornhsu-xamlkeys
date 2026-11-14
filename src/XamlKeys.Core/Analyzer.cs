namespace XamlKeys.Core;

public sealed class AnalysisResult
{
    public required string Root { get; init; }
    public required Config Config { get; init; }
    public List<Finding> Findings { get; } = [];

    public int XamlFiles { get; set; }
    public int CSharpFiles { get; set; }
    public int Dictionaries { get; set; }
    public int MergeEdges { get; set; }
    public int MergeEdgesResolved { get; set; }
    public int KeysDefined { get; set; }
    public int References { get; set; }
    public int Suppressed { get; set; }
    public List<ParseError> ParseErrors { get; } = [];
    public List<SourceLocation> UnresolvedSites { get; } = [];
    public List<DynamicKeySite> DynamicKeySites { get; } = [];
    public List<string> ExternalSources { get; } = [];
    public bool HasApplication { get; set; }
    public List<ThemeSide> Sides { get; } = [];

    public int Projects { get; set; }
    public int ApplicationProjects { get; set; }

    /// <summary>因為定義在函式庫專案而沒有判斷「沒人用」的鍵數。
    /// ⚠ 一定要出現在 summary —— 否則使用者會以為工具看過那些鍵了。</summary>
    public int LibraryKeysNotChecked { get; set; }

    /// <summary>被認成「同一組的替代字典」（語系／主題變體）而沒有報覆蓋的組數。</summary>
    public int VariantDictionaryGroups { get; set; }

    /// <summary>解析不到、但確認是框架提供的引用數。</summary>
    public int FrameworkProvided { get; set; }

    /// <summary>這棵樹引用了 .NET 內建的 Fluent 主題（PresentationFramework.Fluent）。</summary>
    public bool WpfFluentThemeInUse { get; set; }

    /// <summary>程式在執行期合併進來的字典數（主題切換）。</summary>
    public int RuntimeLoadedDictionaries { get; set; }

    /// <summary>框架隱含載入的主題字典數（Themes/Generic.xaml 之類）。</summary>
    public int ImplicitThemeDictionaries { get; set; }

    /// <summary>被認成「執行期才挑一本」的家族（語系檔、主題檔）而沒有報孤兒的檔案數。</summary>
    public int RuntimeSelectedFamilyFiles { get; set; }

    /// <summary>函式庫專案裡解析不到、因此沒有納入 gate 的引用數。</summary>
    public int LibraryUnresolvedReferences { get; set; }

    /// <summary>unused 的 finding 涵蓋的定義總數（同名鍵在多本變體字典裡只報一筆）。</summary>
    public int UnusedDefinitions { get; set; }

    /// <summary>鍵定義在別的專案裡而沒有判斷作用域的引用數。</summary>
    public int CrossProjectReferences { get; set; }

    /// <summary>被當成測試素材而跳過的檔案數。</summary>
    public int TestFilesExcluded { get; set; }

    public double Coverage => MergeEdges == 0 ? 100.0 : 100.0 * MergeEdgesResolved / MergeEdges;

    public bool Untrustworthy(out string reason)
    {
        if (XamlFiles == 0)
        {
            reason = TestFilesExcluded > 0
                ? $"no XAML files were found under the scan root -- {TestFilesExcluded} file(s) were skipped " +
                  $"because they sit under test paths (set includeTests in the config to scan them)"
                : "no XAML files were found under the scan root";
            return true;
        }
        if (KeysDefined == 0) { reason = "no resource keys were found -- the scan root may be wrong"; return true; }
        if (Coverage < Config.MinCoverage)
        {
            reason = $"only {Coverage:F1}% of merged dictionaries could be resolved " +
                     $"(minimum {Config.MinCoverage:F0}%) -- 'unused' cannot be trusted";
            return true;
        }

        // ⚠ 讀不進來的檔跟讀不到的字典一樣危險：它裡面的引用我們沒看到,
        //   於是它用到的鍵會被報成「沒人用」。之前這只是結尾的一行 note,
        //   退出碼照樣 0 —— 那就是另一條「謊報健康」的通道。
        var parsed = XamlFiles - ParseErrors.Count;
        if (XamlFiles > 0 && 100.0 * parsed / XamlFiles < Config.MinCoverage)
        {
            reason = $"{ParseErrors.Count} of {XamlFiles} XAML file(s) could not be parsed " +
                     $"(minimum {Config.MinCoverage:F0}% must parse) -- references inside them were not seen";
            return true;
        }

        reason = "";
        return false;
    }

    public int Count(FindingKind k) => Findings.Count(f => f.Kind == k);
    public int CountSeverity(Severity s) => Findings.Count(f => f.Severity == s);
}

/// <summary>
/// 作用域查找與 finding 判定。
///
/// 核心不變量（違反任何一條就會害使用者刪掉有用的東西）：
///   1. 路上遇到解析不到的外部字典 -> Unknown，**絕不是** NotFound。
///   2. 「沒人用」只看鍵名有沒有被任何地方引用過，不看作用域 —— 刻意少報。
///   3. 鍵在專案裡找得到、只是不在這條鏈上 -> OutOfScope（warn），不是懸空（fail）。
///   4. 函式庫專案不報 unused／orphan，作用域放寬到整個專案 —— 它的字典是給別人合併的。
///   5. 框架自己提供的鍵不是懸空。
/// </summary>
public sealed class Analyzer
{
    private static readonly string[] DefaultExcludes =
        ["bin", "obj", "node_modules", "packages", "artifacts", "TestResults"];

    /// <summary>兩本字典的鍵有這個比例以上重疊、而且都夠大時，當成「同一組的替代字典」
    /// （語系檔、深淺主題檔），不報覆蓋。</summary>
    private const double VariantOverlap = 0.6;
    private const int VariantMinKeys = 5;

    public static AnalysisResult Run(string root, Config config)
    {
        root = Path.GetFullPath(root);
        var result = new AnalysisResult { Root = root, Config = config };

        var testSkipped = 0;
        var xamlFiles = EnumerateFiles(root, "*.xaml", config, () => testSkipped++)
                        .Concat(EnumerateFiles(root, "*.axaml", config, () => testSkipped++)).ToList();
        var csFiles = EnumerateFiles(root, "*.cs", config).ToList();
        var projFiles = EnumerateFiles(root, "*.csproj", config).ToList();
        result.TestFilesExcluded = testSkipped;

        result.XamlFiles = xamlFiles.Count;
        result.CSharpFiles = csFiles.Count;

        var projects = new ProjectIndex(root, projFiles, xamlFiles);
        result.Projects = projects.ProjectCount;
        result.ApplicationProjects = projects.ApplicationCount;

        var resolver = new SourceResolver(root, projFiles, config.ExternalDictionaries);
        var scanner = new XamlScanner(root, resolver);
        foreach (var f in xamlFiles) scanner.ScanFile(f);
        scanner.LinkMergedDictionaries();

        var cs = new CSharpScanner(root) { ProjectOf = projects.ProjectDirOf };
        foreach (var f in csFiles) cs.ScanFile(f);

        result.ParseErrors.AddRange(scanner.Errors);
        result.UnresolvedSites.AddRange(scanner.UnresolvedSites);
        result.DynamicKeySites.AddRange(cs.DynamicSites);

        var dicts = scanner.Dictionaries;
        result.Dictionaries = dicts.Count;
        result.KeysDefined = scanner.Definitions.Count;

        foreach (var d in dicts)
            foreach (var m in d.Merged)
            {
                result.MergeEdges++;
                if (m.IsExternal)
                {
                    if (!result.ExternalSources.Contains(m.RawSource)) result.ExternalSources.Add(m.RawSource);
                }
                else result.MergeEdgesResolved++;
            }

        // 每本字典記住自己屬於哪個專案 —— Application.Resources 的作用域是該專案的,
        // 不是整棵樹的（見 ScopeEngine.EffectiveChain）。
        foreach (var d in dicts) d.ProjectDir = projects.ProjectDirOf(d.File);

        // .NET 9 起 WPF 內建的 Fluent 主題是**選用**的 —— 只有真的被合併進來時,
        // 它的 955 個鍵才算「框架提供」。沒用它的專案不該被這份清單遮掉真懸空。
        result.WpfFluentThemeInUse = result.ExternalSources.Any(FrameworkKeys.IsWpfFluentSource);

        ApplyThemeSides(dicts, config);
        var themeEntries = PropagateThemeSides(dicts);
        MarkRuntimeLoaded(dicts, cs.LoadedDictionaryPaths, result);
        result.HasApplication = projects.HasAnyApplication;

        var sides = DetermineSides(dicts);
        result.Sides.AddRange(sides);

        var allRefs = scanner.References.Concat(cs.References).ToList();
        result.References = allRefs.Count;

        // 方言。
        // ⚠ 這會把合併字典的優先序整個倒過來（WPF 後宣告的贏、WinUI 相反），
        //   判錯的話 shadowed 的贏家與輸家會對調，使用者會改錯那一個。
        //
        //   兩條教訓：
        //   (a) **依據是 csproj，不是 XAML 寫法** —— wpfui 是 WPF 函式庫但刻意沿用
        //       WinUI 的 {ThemeResource} 與鍵名，只看 XAML 會判錯。
        //   (b) **逐專案判，不是整棵樹判一次** —— ModernWpf 是 WPF 函式庫，
        //       但 repo 裡一個 tools/ 底下的輔助工具寫了 <UseWinUI>true</UseWinUI>，
        //       整次掃描的優先序因此被翻過來。
        //   明寫 --dialect／config 時一律照使用者說的，整棵樹同一個值。
        AssignDialects(dicts, projects, config, allRefs);

        // dialect 傳 null：每本字典用自己專案的方言（AssignDialects 已經設好）
        var engine = new ScopeEngine(dicts, dialect: null, SidesPresent(dicts));
        var ctx = new Context(root, dicts, projects, scanner.Definitions, allRefs, engine, config, themeEntries);

        EmitDuplicateKeys(dicts, result, config);
        EmitReferenceFindings(ctx, sides, result);
        EmitShadowed(ctx, result);
        EmitUnused(ctx, result);
        EmitOrphanDictionaries(ctx, result);

        result.Findings.Sort(CompareFindings);
        return result;
    }

    /// <summary>判定期間用得到的所有索引。</summary>
    private sealed class Context
    {
        private readonly string _root;

        public Context(string root, IReadOnlyList<ResourceDictionaryNode> dicts, ProjectIndex projects,
                       IReadOnlyList<KeyDefinition> defs, List<KeyReference> refs,
                       ScopeEngine engine, Config config,
                       Dictionary<int, HashSet<int>> themeEntries)
        {
            _root = root;
            ThemeEntriesOf = themeEntries;
            Dicts = dicts;
            Projects = projects;
            Definitions = defs;
            References = refs;
            Engine = engine;
            Config = config;

            ById = dicts.ToDictionary(d => d.Id);
            DefinedNames = new HashSet<string>(defs.Select(d => d.Key), StringComparer.Ordinal);
            ReferencedNames = new HashSet<string>(refs.Select(r => r.Key), StringComparer.Ordinal);

            // 函式庫專案的作用域：整個專案的鍵（使用者會合併哪幾本，我們不可能知道）
            DefinedByProject = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in defs)
            {
                var dict = ById[d.DictionaryId];
                var proj = projects.ProjectDirOf(dict.File) ?? "";
                if (!DefinedByProject.TryGetValue(proj, out var set))
                    DefinedByProject[proj] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(d.Key);
            }
        }

        public IReadOnlyList<ResourceDictionaryNode> Dicts { get; }
        public ProjectIndex Projects { get; }
        public IReadOnlyList<KeyDefinition> Definitions { get; }
        public List<KeyReference> References { get; }
        public ScopeEngine Engine { get; }
        public Config Config { get; }
        public Dictionary<int, ResourceDictionaryNode> ById { get; }
        public HashSet<string> DefinedNames { get; }
        public HashSet<string> ReferencedNames { get; }
        public Dictionary<string, HashSet<string>> DefinedByProject { get; }

        /// <summary>字典 Id -> 把它合併進來的那些「主題入口」（見 MutuallyExclusive）。</summary>
        public Dictionary<int, HashSet<int>> ThemeEntriesOf { get; }

        /// <summary>兩兩的變體判定快取（見 AreVariants 的檔頭）。</summary>
        public Dictionary<(int, int), bool> VariantVerdict { get; } = [];

        private readonly Dictionary<int, HashSet<string>> _keySets = [];

        /// <summary>一本字典自己的鍵名集合，建一次就好。</summary>
        public HashSet<string> KeySetOf(int dictId)
        {
            if (_keySets.TryGetValue(dictId, out var set)) return set;

            set = ById.TryGetValue(dictId, out var d)
                ? d.OwnKeys.Select(k => k.Key).ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);

            _keySets[dictId] = set;
            return set;
        }

        /// <summary>這個鍵名在樹裡的第一個定義（給 out-of-scope 指路用）。</summary>
        public KeyDefinition? FirstDefinitionOf(string key) =>
            Definitions.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.Ordinal));

        public ResourceDictionaryNode? DictOfRef(KeyReference r) =>
            r.ScopeChain.Count > 0 && ById.TryGetValue(r.ScopeChain[0], out var d) ? d : null;

        /// <summary>⚠ finding 與引用帶的是**相對於掃描根**的路徑（baseline 要跨 OS 可攜），
        /// 但 ProjectIndex 收的是絕對路徑。直接把相對路徑丟給 Path.GetFullPath 會對著
        /// 行程的工作目錄解析 —— 結果是每一個引用都比對不到專案，整批被當成「函式庫、跨專案」
        /// 而靜默跳過。這個 bug 曾經讓 wpfui 的 289 筆 out-of-scope 看起來像被修好了。</summary>
        public string Abs(string file) =>
            Path.IsPathRooted(file) ? file : Path.GetFullPath(Path.Combine(_root, file));

        /// <summary>這個引用所在的檔，是在函式庫專案裡嗎？</summary>
        public bool InLibrary(string file) => !Projects.IsInApplication(Abs(file));

        public bool DefinedSomewhereInProjectOf(string file, string key)
        {
            var proj = Projects.ProjectDirOf(Abs(file)) ?? "";
            return DefinedByProject.TryGetValue(proj, out var set) && set.Contains(key);
        }
    }

    // ── 各類 finding ───────────────────────────────────────────────────────

    /// <summary>同一本字典裡同一個鍵兩次 —— 非法 XAML，載入時 parser 會丟錯。</summary>
    private static void EmitDuplicateKeys(IReadOnlyList<ResourceDictionaryNode> dicts, AnalysisResult result, Config cfg)
    {
        foreach (var d in dicts)
        {
            foreach (var group in d.OwnKeys.GroupBy(k => k.Key, StringComparer.Ordinal))
            {
                if (group.Count() < 2) continue;

                var ordered = group.OrderBy(k => k.Location.Line).ToList();
                foreach (var dup in ordered.Skip(1))
                {
                    if (dup.SuppressedReason is not null) { result.Suppressed++; continue; }

                    result.Findings.Add(new Finding
                    {
                        Kind = FindingKind.DuplicateKey,
                        Severity = cfg.SeverityFor(FindingKind.DuplicateKey),
                        Key = dup.Key,
                        Location = dup.Location,
                        Related = ordered[0].Location,
                        Message = $"key '{dup.Key}' is defined more than once in the same dictionary; " +
                                  $"XAML requires keys to be unique within one dictionary and the parser throws at load time " +
                                  $"(first defined at {ordered[0].Location})",
                    });
                }
            }
        }
    }

    private static void EmitReferenceFindings(Context ctx, List<ThemeSide> sides, AnalysisResult result)
    {
        var cfg = ctx.Config;

        foreach (var r in ctx.References)
        {
            if (r.SuppressedReason is not null) { result.Suppressed++; continue; }

            // C# 的查找沒有靜態作用域（執行期沿真實樹走）：只能確認鍵名存不存在。
            if (r.Kind == RefKind.CSharp)
            {
                if (!ctx.DefinedNames.Contains(r.Key) && result.ExternalSources.Count == 0
                    && !ctx.InLibrary(r.Location.File))
                    result.Findings.Add(Make(FindingKind.DanglingDynamic, r, ThemeSide.Neutral, cfg,
                        $"'{r.Key}' is looked up from code but no XAML in this tree defines it"));
                continue;
            }

            // ⚠ 引用如果本身就在某一側的字典裡（ThemeDictionaries 的 Dark／Light／HighContrast），
            //   只用**那一側**判定。拿深色字典裡的引用去問淺色側，答案沒有意義。
            var ownSide = ctx.DictOfRef(r)?.Side ?? ThemeSide.Neutral;
            var evaluate = ownSide == ThemeSide.Neutral ? sides : [ownSide];

            // ⚠ 專案由**引用所在的檔案**決定,不是鏈的第一個字典 ——
            //   畫面檔常常沒有自己的 <X.Resources>,鏈是空的。
            var refProject = ctx.Projects.ProjectDirOf(ctx.Abs(r.Location.File));

            var failures = new List<(ThemeSide Side, LookupResult Res)>();
            foreach (var side in evaluate)
            {
                var res = ctx.Engine.Lookup(r.Key, r.ScopeChain, side, refProject);
                if (res.Status != LookupStatus.Found) failures.Add((side, res));
            }

            if (failures.Count == 0) continue;
            var allSidesFailed = failures.Count == evaluate.Count;

            // ⚠ 框架提供的鍵（WinUI 的 TextFillColorPrimaryBrush 之類）不是懸空。
            //    這一條必須排在查找**之後**：排在前面會把本來解析得到的引用也短路掉 ——
            //    實測 wpfui（WPF 專案，自己定義了同名的鍵）有 3021 筆被誤判成「框架提供」。
            if (FrameworkKeys.IsProvidedByFramework(r.Key, cfg.Dialect, result.WpfFluentThemeInUse))
            { result.FrameworkProvided++; continue; }

            // ⚠ 函式庫專案：它的字典是給**使用者**合併的，我們看不到使用者的作用域。
            //   只要鍵在同一個專案裡找得到，就當作它解析得到 —— 否則 wpfui 會冒出 1339 筆假 out-of-scope。
            if (ctx.InLibrary(r.Location.File)
                && ctx.DefinedSomewhereInProjectOf(r.Location.File, r.Key)) continue;

            var unknown = failures.FirstOrDefault(f => f.Res.Status == LookupStatus.Unknown);

            if (unknown.Res is { Status: LookupStatus.Unknown })
            {
                result.Findings.Add(new Finding
                {
                    Kind = FindingKind.External,
                    Severity = cfg.SeverityFor(FindingKind.External),
                    Key = r.Key,
                    Location = r.Location,
                    Side = allSidesFailed ? ThemeSide.Neutral : unknown.Side,
                    ExternalSource = unknown.Res.ExternalSource,
                    Message = $"'{r.Key}' is not defined in this tree, but the scope merges " +
                              $"'{unknown.Res.ExternalSource}' which could not be read -- not reported as dangling",
                });
                continue;
            }

            var side0 = allSidesFailed ? ThemeSide.Neutral : failures[0].Side;

            if (ctx.DefinedNames.Contains(r.Key))
            {
                // ⚠ 鍵定義在**別的專案**裡：那是「應用程式引用它相依的函式庫」的正常形狀，
                //   而使用者的 App 用什麼方式把那本字典合併進來（pack URI、generic.xaml、
                //   或在程式裡組路徑載入）我們追不完。實測 wpfui 的範例 App 因此冒出 289 筆假 out-of-scope。
                //   out-of-scope 的價值在「同一個專案裡用錯了別的畫面的區域資源」,跨專案不要猜。
                if (!ctx.DefinedSomewhereInProjectOf(r.Location.File, r.Key))
                {
                    result.CrossProjectReferences++;
                    continue;
                }

                // 鍵存在，只是不在這條鏈上。本工具的作用域是靜態近似 -- 可能是我們看漏。
                // ⚠ 一定要講出它定義在哪：只說「在別處」等於要使用者自己去全專案搜尋。
                var where = ctx.FirstDefinitionOf(r.Key);
                result.Findings.Add(new Finding
                {
                    Kind = FindingKind.OutOfScope,
                    Severity = cfg.SeverityFor(FindingKind.OutOfScope),
                    Key = r.Key,
                    Location = r.Location,
                    Side = side0,
                    Related = where?.Location,
                    Message = $"'{r.Key}' is defined at {where?.Location.ToString() ?? "another place in the tree"} " +
                              $"but that is not in the scope of this reference" +
                              (allSidesFailed ? "" : $" on the {Finding.SideSlug(side0)} side"),
                });
                continue;
            }

            var kind = r.Kind == RefKind.Static ? FindingKind.DanglingStatic : FindingKind.DanglingDynamic;
            var consequence = kind == FindingKind.DanglingStatic
                ? "StaticResource throws at load time"
                : "DynamicResource silently falls back to the default value";

            // 懸空的鍵絕大多數是打錯字或改名留下的 —— 講出最接近的那個名字,
            // 使用者才看得懂發生了什麼事（實測 QuillNest 的 InverseBoolToVis）。
            var hint = NearMiss.Suggest(r.Key, ctx.DefinedNames) is { } near
                ? $". Did you mean '{near}'?" : "";

            var finding = Make(kind, r, side0, cfg,
                $"'{r.Key}' is not defined anywhere in this tree; {consequence}{hint}");

            // ⚠ 函式庫／外掛專案：它的字典是給**別人**合併的,而那個「別人」會先合併什麼
            //   我們不可能知道。ModernWpf、MaterialDesignThemes.MahApps、Wpf.Ui、
            //   Flow Launcher 的外掛都靠這個模式（實測這一類佔了 190 筆）。
            //
            //   **不隱藏,只降級**：還是報出來、還是進 JSON,但不進 gate，
            //   因為在函式庫裡這件事本來就證不出來。
            //   DynamicResource 降到 info（那是「讓使用者覆寫」的標準寫法）、
            //   StaticResource 只降到 warn（它會在載入期丟例外,風險實在不同 ——
            //   實測 wpfui 的 TreeViewItemFocusVisual 就是這樣的真 bug）。
            if (ctx.InLibrary(r.Location.File))
            {
                finding = new Finding
                {
                    Kind = kind,
                    Severity = kind == FindingKind.DanglingStatic ? Severity.Warn : Severity.Info,
                    Key = r.Key,
                    Location = r.Location,
                    Side = side0,
                    Message = $"'{r.Key}' is not defined anywhere in this library project; {consequence}. " +
                              $"A library's consumer may supply it, so this is not gated{hint}",
                };
                result.LibraryUnresolvedReferences++;
            }

            result.Findings.Add(finding);
        }
    }

    private static Finding Make(FindingKind kind, KeyReference r, ThemeSide side, Config cfg, string message) => new()
    {
        Kind = kind,
        Severity = cfg.SeverityFor(kind),
        Key = r.Key,
        Location = r.Location,
        Side = side,
        Message = message,
    };

    /// <summary>合併順序造成的覆蓋。</summary>
    private static void EmitShadowed(Context ctx, AnalysisResult result)
    {
        var cfg = ctx.Config;
        var reported = new HashSet<string>(StringComparer.Ordinal);
        var variantPairs = new HashSet<(int, int)>();

        foreach (var rootDict in ctx.Engine.RootDictionaries)
        {
            foreach (var (key, winner, losers) in ctx.Engine.DuplicatesIn(rootDict.Id))
            {
                // 沒人引用的鍵被蓋掉不重要 —— 那種情形由 Unused 覆蓋，兩邊都報只是噪音。
                if (!ctx.ReferencedNames.Contains(key)) continue;

                foreach (var loser in losers)
                {
                    if (loser.SuppressedReason is not null) { result.Suppressed++; continue; }

                    // ⚠ 同一個 ThemeDictionaries 群組裡的兩本字典本質上互斥（執行期只選中一本），
                    //   所以它們之間永遠不是覆蓋。這條比用鍵名去推穩健 ——
                    //   實測 Files 的 GridSplitter.xaml 是 Default ＋ HighContrast,靠鍵名會判錯。
                    if (MutuallyExclusive(ctx, winner.DictionaryId, loser.DictionaryId)) continue;

                    // ⚠ 語系檔／主題檔**刻意**定義同一組鍵，執行期才換。
                    //   實測：ScreenToGif 用 XAML 做在地化（四十種語言、各約 1200 個鍵），
                    //   不認這條會報出 28,704 筆 shadowed —— 整份報告直接變成垃圾。
                    if (AreVariants(ctx, winner.DictionaryId, loser.DictionaryId))
                    {
                        variantPairs.Add((Math.Min(winner.DictionaryId, loser.DictionaryId),
                                          Math.Max(winner.DictionaryId, loser.DictionaryId)));
                        continue;
                    }

                    var sig = $"{loser.Location}|{key}";
                    if (!reported.Add(sig)) continue;

                    // 被一個**一模一樣**的定義蓋掉：結果是對的，只是多餘。
                    var identical = loser.ContentHash is not null
                                 && string.Equals(loser.ContentHash, winner.ContentHash, StringComparison.Ordinal);

                    var kind = identical ? FindingKind.ShadowedIdentical : FindingKind.Shadowed;
                    var how = cfg.Dialect == XamlDialect.Wpf
                        ? "WPF resolves to the last merged dictionary"
                        : "WinUI resolves in the inverse of declaration order";

                    result.Findings.Add(new Finding
                    {
                        Kind = kind,
                        Severity = cfg.SeverityFor(kind),
                        Key = key,
                        Location = loser.Location,
                        Related = winner.Location,
                        Message = identical
                            ? $"'{key}' is also defined at {winner.Location} with identical content; " +
                              $"this declaration is redundant ({how})"
                            : $"'{key}' is shadowed by a different definition at {winner.Location} ({how}); " +
                              $"this definition never wins",
                    });
                }
            }
        }

        result.VariantDictionaryGroups = variantPairs.Count;
    }

    /// <summary>
    /// 「執行期才挑一本」的字典家族 —— 同一個目錄底下、沒有人合併、而且鍵集合幾乎一樣的那一批。
    ///
    /// ⚠ 實測（Flow Launcher）：`Languages/` 底下 289 個語系檔，**每一個都剛好 606 個鍵**，
    /// 全部沒有被任何 MergedDictionaries 合併 —— 程式用完全組出來的路徑
    /// （`Path.Combine(directory, name + ".xaml")`）在執行期挑一本，而那行程式還在
    /// 預設被排除的測試專案裡。靜態追不到，但 289 個鍵集合一模一樣的檔案擺在同一個目錄，
    /// 沒有別的合理解釋。
    ///
    /// 門檻刻意訂得保守（同目錄、至少三本、重疊度與變體判定同一條線），
    /// 而且家族的檔案數要進 summary —— 這是一條會讓結果變寬鬆的路徑。
    /// </summary>
    private static HashSet<int> RuntimeSelectedFamilies(Context ctx, HashSet<int> merged)
    {
        const int MinFamilySize = 3;
        var result = new HashSet<int>();

        var candidates = ctx.Dicts
            .Where(d => d.IsStandaloneFile && !merged.Contains(d.Id)
                     && !d.IsRuntimeLoaded && d.OwnKeys.Count >= VariantMinKeys)
            .GroupBy(d => Path.GetDirectoryName(d.File) ?? "", StringComparer.OrdinalIgnoreCase);

        foreach (var dir in candidates)
        {
            var list = dir.ToList();
            if (list.Count < MinFamilySize) continue;

            // 以第一本為基準，數有幾本跟它是變體
            var family = list.Where(d => d.Id == list[0].Id || AreVariants(ctx, list[0].Id, d.Id)).ToList();
            if (family.Count < MinFamilySize) continue;

            foreach (var d in family) result.Add(d.Id);
        }

        return result;
    }

    /// <summary>
    /// 這兩本字典在執行期互斥嗎？
    ///
    /// 判準：存在某一個 ThemeDictionaries 群組，兩者**分別**從它的不同入口被合併進來。
    /// 同一組裡執行期只會選中一個入口，所以兩邊的鍵不可能同時在場 —— 那不是覆蓋。
    ///
    /// ⚠ 不能只比「同一個群組 id」。實測（Semi.Avalonia）：同一套 ThemeDictionaries
    /// 在 `SemiTheme.axaml` 與 `Index.axaml` 各宣告了一次，於是 `Themes/Dark/_index.axaml`
    /// 是從**兩個**群組被合併到的 —— 用單一群組 id 判會整批漏掉，66 筆假 shadowed。
    /// </summary>
    private static bool MutuallyExclusive(Context ctx, int a, int b)
    {
        if (!ctx.ThemeEntriesOf.TryGetValue(a, out var ea)) return false;
        if (!ctx.ThemeEntriesOf.TryGetValue(b, out var eb)) return false;

        foreach (var ta in ea)
            foreach (var tb in eb)
            {
                if (ta == tb) continue;
                if (ctx.ById.TryGetValue(ta, out var na) && ctx.ById.TryGetValue(tb, out var nb)
                    && na.ThemeGroupId is { } ga && nb.ThemeGroupId is { } gb && ga == gb)
                    return true;
            }

        return false;
    }

    /// <summary>兩本字典是不是「同一組的替代品」（語系檔、深淺主題檔）？
    /// 判準是鍵集合的重疊度：刻意的變體會幾乎一模一樣，意外的覆蓋只會撞到幾個。
    ///
    /// ⚠ 鍵集合與兩兩的判定結果都要快取。ScreenToGif 有 40 本語系字典、各約 1,200 個鍵，
    /// 不快取的話每一個重複鍵都會重建兩份 HashSet —— 實測整個掃描 8.5 秒，這裡佔絕大部分。
    /// </summary>
    private static bool AreVariants(Context ctx, int a, int b)
    {
        if (a == b) return false;

        var pair = a < b ? (a, b) : (b, a);
        if (ctx.VariantVerdict.TryGetValue(pair, out var cached)) return cached;

        var verdict = Compute();
        ctx.VariantVerdict[pair] = verdict;
        return verdict;

        bool Compute()
        {
            var sa = ctx.KeySetOf(a);
            var sb = ctx.KeySetOf(b);
            if (sa.Count < VariantMinKeys || sb.Count < VariantMinKeys) return false;

            var (small, large) = sa.Count <= sb.Count ? (sa, sb) : (sb, sa);
            var shared = 0;
            foreach (var k in small) if (large.Contains(k)) shared++;

            return (double)shared / small.Count >= VariantOverlap;
        }
    }

    /// <summary>
    /// 沒人用。
    ///
    /// ⚠ 刻意採用最保守的定義：**全專案（XAML＋C#）沒有任何地方引用過這個鍵名**。
    /// 不看作用域。理由：作用域是靜態近似，而這一類 finding 的後果是使用者去刪檔案。
    /// 代價是少報，但少報只是沒幫上忙，多報會害人。
    /// </summary>
    private static void EmitUnused(Context ctx, AnalysisResult result)
    {
        var cfg = ctx.Config;
        var candidates = new List<KeyDefinition>();

        foreach (var d in ctx.Definitions)
        {
            if (ctx.ReferencedNames.Contains(d.Key)) continue;

            var dict = ctx.ById[d.DictionaryId];

            // ⚠ 函式庫的鍵是它的公開 API：使用者的專案才會引用。
            //   不認這條，MaterialDesignInXamlToolkit 的 3372 個鍵會被報掉 1767 個。
            if (ctx.InLibrary(dict.File)) { result.LibraryKeysNotChecked++; continue; }

            if (d.SuppressedReason is not null) { result.Suppressed++; continue; }

            if (cfg.UsedAtRuntime.Any(p => Config.MatchesGlob(d.Key, p))) { result.Suppressed++; continue; }

            candidates.Add(d);
        }

        // ⚠ 一個鍵名只報一筆，不管它被定義了幾次。
        //   實測：ScreenToGif 用 XAML 做在地化,同一個字串鍵在四十個語系檔各定義一次,
        //   逐一報的話是 14,468 筆 —— 背後其實只有 362 個真正沒人用的鍵名。
        //   一萬四千行的報告沒有人會讀,等於工具沒做事。
        foreach (var group in candidates.GroupBy(d => d.Key, StringComparer.Ordinal))
        {
            var all = group.OrderBy(d => d.Location.File, StringComparer.Ordinal)
                           .ThenBy(d => d.Location.Line).ToList();
            result.UnusedDefinitions += all.Count;

            var where = all.Count == 1
                ? ""
                : $" ({all.Count} definitions, e.g. also {all[1].Location})";

            // 改名留下的孤兒：另一半通常就在旁邊,而且**有人在用**。
            // 只在「被引用過的鍵名」裡找,才不會拿另一個同樣沒人用的鍵去誤導。
            var renamed = NearMiss.Suggest(group.Key, ctx.ReferencedNames) is { } other
                ? $". A similarly named key '{other}' is referenced -- was this renamed?" : "";

            result.Findings.Add(new Finding
            {
                Kind = FindingKind.Unused,
                Severity = cfg.SeverityFor(FindingKind.Unused),
                Key = group.Key,
                Location = all[0].Location,
                // ⚠ 執行期組出來的鍵名那條提醒刻意**不**放在每一筆 finding 上：
                //   它會在每一行重複一次，使用者只會學會忽略它。位置在 summary 與 console 結尾。
                Message = $"'{group.Key}' is defined but never referenced in XAML or code{where}{renamed}",
            });
        }
    }

    /// <summary>沒有被任何人合併、也沒有被程式載入的獨立字典檔。</summary>
    private static void EmitOrphanDictionaries(Context ctx, AnalysisResult result)
    {
        var cfg = ctx.Config;

        var merged = new HashSet<int>();
        foreach (var d in ctx.Dicts)
            foreach (var m in d.Merged)
                if (m.ResolvedDictionaryId is { } id) merged.Add(id);

        var families = RuntimeSelectedFamilies(ctx, merged);
        result.RuntimeSelectedFamilyFiles = families.Count;

        foreach (var d in ctx.Dicts)
        {
            if (!d.IsStandaloneFile) continue;
            if (merged.Contains(d.Id)) continue;
            if (d.OwnKeys.Count == 0) continue;
            // 程式在執行期載入的、以及框架隱含載入的主題字典，都不是孤兒（MarkRuntimeLoaded 標過）
            if (d.IsRuntimeLoaded) continue;
            // 執行期才挑一本的家族（語系檔、主題檔）：見 RuntimeSelectedFamilies
            if (families.Contains(d.Id)) continue;

            // ⚠ 函式庫的字典本來就靠**使用者**合併。不認這條，HandyControl 會報 140 筆、
            //   MaterialDesign 103 筆，全部是假的。
            if (ctx.InLibrary(d.File)) continue;

            result.Findings.Add(new Finding
            {
                Kind = FindingKind.OrphanDictionary,
                Severity = cfg.SeverityFor(FindingKind.OrphanDictionary),
                Key = Path.GetFileName(d.File),
                Location = d.Location,
                Message = $"this ResourceDictionary ({d.OwnKeys.Count} key(s)) is never merged by another dictionary " +
                          $"and is not loaded from code -- nothing in it can be resolved",
            });
        }
    }

    // ── 輔助 ───────────────────────────────────────────────────────────────

    /// <summary>
    /// WPF 會**自動**載入的主題字典 —— 它們不會出現在任何 MergedDictionaries 裡。
    ///
    /// <c>Themes/Generic.xaml</c> 是自訂控制項的預設樣式來源（由組件的 ThemeInfo 屬性指定），
    /// 其餘幾個是作業系統佈景主題對應的檔名。
    /// ⚠ 實查（ScreenToGif）：不認這條會把 <c>Themes/Generic.xaml</c> 報成孤兒 ——
    /// 而那是整個專案最重要的一本字典。
    /// </summary>
    private static bool IsImplicitThemeDictionary(string file)
    {
        var name = Path.GetFileName(file);
        var dir = Path.GetFileName(Path.GetDirectoryName(file) ?? "");

        if (!dir.Equals("Themes", StringComparison.OrdinalIgnoreCase)) return false;

        if (name.Equals("Generic.xaml", StringComparison.OrdinalIgnoreCase)) return true;

        // 經典 OS 佈景主題檔名：Aero2.NormalColor.xaml、Luna.NormalColor.xaml、Classic.xaml…
        foreach (var prefix in (ReadOnlySpan<string>)["Aero", "Luna", "Royale", "Classic", "Zune"])
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    /// <summary>程式碼裡出現過的 .xaml 路徑 -> 那本字典在執行期會被合併進 Application.Resources。
    /// 比對用檔名（程式裡寫的是 pack URI 或相對路徑，形式不一定對得起來）。</summary>
    private static void MarkRuntimeLoaded(IReadOnlyList<ResourceDictionaryNode> dicts,
                                          IReadOnlyList<(string Path, string? Project)> loadedPaths,
                                          AnalysisResult result)
    {
        // ⚠ 檔名比對要**限定在同一個專案裡**。
        //   實測（dotnet/maui-samples，231 個範例專案）：只比檔名的話，
        //   任何一個範例的 C# 提到 "Colors.xaml"，其餘 230 份 Colors.xaml 都會被
        //   當成「程式會載入」而豁免孤兒檢查 —— 一次跨專案的大赦。
        var byProject = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, project) in loadedPaths)
        {
            var name = Path.GetFileName(path.Replace('\\', '/'));
            if (name.Length == 0) continue;

            var key = project ?? "";
            if (!byProject.TryGetValue(key, out var set))
                byProject[key] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            set.Add(name);
        }

        foreach (var d in dicts)
        {
            if (!d.IsStandaloneFile) continue;

            var loaded = byProject.TryGetValue(d.ProjectDir ?? "", out var names)
                      && names.Contains(Path.GetFileName(d.File));
            var implicitTheme = IsImplicitThemeDictionary(d.File);
            if (!loaded && !implicitTheme) continue;

            d.IsRuntimeLoaded = true;
            if (implicitTheme) result.ImplicitThemeDictionaries++;
            else result.RuntimeLoadedDictionaries++;
        }
    }

    /// <summary>
    /// 把主題字典的「側」與「群組」往下傳給它合併進來的字典。
    ///
    /// ⚠ 實測（Semi.Avalonia）：主題常常是用**目錄**分的 ——
    /// <c>Themes/_index.axaml</c> 的 ThemeDictionaries 裡，`Dark` 這一本只放一行
    /// <c>&lt;ResourceInclude Source="Dark/_index.axaml"/&gt;</c>，真正的鍵在那個檔裡。
    /// 側別不往下傳的話，Dark 與 HighContrast 兩整套鍵會被判成互相覆蓋 —— 66 筆假 shadowed。
    ///
    /// 被**多個**不同側合併到的字典（兩側共用的那些）維持中性：
    /// 它本來就同時屬於兩邊，不能硬塞一個側別給它。
    /// </summary>
    private static Dictionary<int, HashSet<int>> PropagateThemeSides(IReadOnlyList<ResourceDictionaryNode> dicts)
    {
        var byId = dicts.ToDictionary(d => d.Id);

        // 字典 Id -> 它是從哪幾個「主題入口」被合併到的（入口 = ThemeDictionaries 裡的一本）
        var reachedFrom = new Dictionary<int, HashSet<int>>();

        foreach (var theme in dicts.Where(d => d.ThemeGroupId is not null))
        {
            var stack = new Stack<int>();
            var seen = new HashSet<int> { theme.Id };

            foreach (var m in theme.Merged)
                if (m.ResolvedDictionaryId is { } id && seen.Add(id)) stack.Push(id);

            while (stack.Count > 0)
            {
                var id = stack.Pop();
                if (!reachedFrom.TryGetValue(id, out var set)) reachedFrom[id] = set = [];
                set.Add(theme.Id);

                if (!byId.TryGetValue(id, out var node)) continue;
                foreach (var m in node.Merged)
                    if (m.ResolvedDictionaryId is { } child && seen.Add(child)) stack.Push(child);
            }
        }

        // 側別：所有入口都是同一側時才指派（兩側共用的檔案維持中性）
        foreach (var (id, entries) in reachedFrom)
        {
            if (!byId.TryGetValue(id, out var node)) continue;
            if (node.Side != ThemeSide.Neutral) continue;

            var sides = entries.Select(e => byId[e].Side).Distinct().ToList();
            if (sides.Count == 1 && sides[0] != ThemeSide.Neutral) node.Side = sides[0];
        }

        // 入口自己也要在表裡，互斥判定才問得到
        foreach (var theme in dicts.Where(d => d.ThemeGroupId is not null))
        {
            if (!reachedFrom.TryGetValue(theme.Id, out var set)) reachedFrom[theme.Id] = set = [];
            set.Add(theme.Id);
        }

        return reachedFrom;
    }

    /// <summary>逐本字典決定方言（見 Run 裡的註解）。順便把「整棵樹的代表值」寫回 config，
    /// 因為報告與訊息還是需要一個可以印的值。</summary>
    private static void AssignDialects(IReadOnlyList<ResourceDictionaryNode> dicts, ProjectIndex projects,
                                       Config cfg, List<KeyReference> allRefs)
    {
        if (!cfg.DialectAutoDetected)
        {
            foreach (var d in dicts) d.Dialect = cfg.Dialect;
            return;
        }

        // 連一個 csproj 都沒有（資源資料夾、sparse clone）→ 只好看 XAML 的寫法
        if (projects.ProjectCount == 0)
        {
            var guess = allRefs.Any(x => x.Kind == RefKind.Theme) ? XamlDialect.WinUi : XamlDialect.Wpf;
            foreach (var d in dicts) d.Dialect = guess;
            cfg.Dialect = guess;
            return;
        }

        foreach (var d in dicts)
            d.Dialect = projects.IsInWinUi(d.File) ? XamlDialect.WinUi : XamlDialect.Wpf;

        // 報告要印一個值：以「字典數比較多的那一邊」為代表。
        // summary 另外帶 dialectMixed，讓混合的情形看得出來。
        var winui = dicts.Count(d => d.Dialect == XamlDialect.WinUi);
        cfg.Dialect = winui * 2 > dicts.Count ? XamlDialect.WinUi : XamlDialect.Wpf;
        cfg.DialectMixed = winui > 0 && winui < dicts.Count;
    }

    private static void ApplyThemeSides(IReadOnlyList<ResourceDictionaryNode> dicts, Config cfg)
    {
        if (cfg.DarkFiles.Count == 0 && cfg.LightFiles.Count == 0) return;

        foreach (var d in dicts)
        {
            if (d.Side != ThemeSide.Neutral) continue;   // ThemeDictionaries 已經分過側
            var rel = d.Location.File;
            if (cfg.DarkFiles.Any(p => rel.Contains(p, StringComparison.OrdinalIgnoreCase))) d.Side = ThemeSide.Dark;
            else if (cfg.LightFiles.Any(p => rel.Contains(p, StringComparison.OrdinalIgnoreCase))) d.Side = ThemeSide.Light;
        }
    }

    /// <summary>要逐一判定的側。HighContrast 不在預設清單裡：大多數 App 只在那本字典裡
    /// 覆寫少數幾個鍵，把它當成一個完整主題去評，會把其餘每一個鍵都報成缺漏。
    /// 它自己字典裡的引用仍然會用 HighContrast 側判定（見 EmitReferenceFindings）。</summary>
    private static List<ThemeSide> DetermineSides(IReadOnlyList<ResourceDictionaryNode> dicts) =>
        dicts.Any(d => d.Side is ThemeSide.Dark or ThemeSide.Light)
            ? [ThemeSide.Dark, ThemeSide.Light]
            : [ThemeSide.Neutral];

    /// <summary>重複鍵的偵測要涵蓋**所有**出現過的側，HighContrast 也要 ——
    /// 否則它自己那本字典裡的重複鍵會沒人檢查。</summary>
    private static List<ThemeSide> SidesPresent(IReadOnlyList<ResourceDictionaryNode> dicts)
    {
        var present = dicts.Select(d => d.Side).Distinct().Where(s => s != ThemeSide.Neutral).ToList();
        return present.Count == 0 ? [ThemeSide.Neutral] : present;
    }

    internal static IEnumerable<string> EnumerateFiles(string root, string pattern, Config cfg,
                                                       Action? onTestFileSkipped = null)
    {
        if (!Directory.Exists(root)) yield break;

        foreach (var f in Directory.EnumerateFiles(root, pattern, new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive,
            // ⚠ 跳過重解析點（符號連結、目錄接合點）。.NET 的遞迴列舉預設會跟著走，
            //   指回祖先的接合點會讓掃描永遠跑不完 —— 掃到別人的 repo 時這不是假設性風險。
            //   代價是連結進來的原始碼會被略過，這一條寫進 README 的已知盲區。
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
        }))
        {
            var verdict = Classify(f, root, cfg);
            if (verdict == Exclusion.Test) { onTestFileSkipped?.Invoke(); continue; }
            if (verdict != Exclusion.Keep) continue;
            yield return f;
        }
    }

    private enum Exclusion { Keep, Hard, Test }

    private static Exclusion Classify(string file, string root, Config cfg)
    {
        var rel = Path.GetFullPath(file);
        if (rel.StartsWith(root, StringComparison.OrdinalIgnoreCase)) rel = rel[root.Length..];
        rel = rel.Replace('\\', '/').TrimStart('/');

        var isTest = false;

        foreach (var seg in rel.Split('/'))
        {
            // ⚠ 點開頭的目錄一律排除。實查教訓：.claude/worktrees/ 底下有整份專案副本，
            //    不排除的話每個鍵都會被數三次，重複定義也會整批假警報。
            if (seg.StartsWith('.') && seg.Length > 1) return Exclusion.Hard;

            foreach (var d in DefaultExcludes)
                if (seg.Equals(d, StringComparison.OrdinalIgnoreCase)) return Exclusion.Hard;

            if (!cfg.IncludeTests && IsTestSegment(seg)) isTest = true;
        }

        foreach (var ex in cfg.Exclude)
            if (rel.Contains(ex, StringComparison.OrdinalIgnoreCase)) return Exclusion.Hard;

        return isTest ? Exclusion.Test : Exclusion.Keep;
    }

    /// <summary>測試素材的目錄名。
    /// ⚠ 實查（ILSpy）：BamlDecompiler 的測試案例資料夾裡放著**刻意寫壞的** XAML
    ///   （同一本字典裡重複的 x:Key、不被任何人合併的字典）。那是測試資料，不是產品程式碼，
    ///   報出來只會讓使用者學會忽略這個工具。用 includeTests 可以關掉這個排除。</summary>
    private static bool IsTestSegment(string seg)
    {
        if (seg.Equals("TestData", StringComparison.OrdinalIgnoreCase)
            || seg.Equals("Fixtures", StringComparison.OrdinalIgnoreCase)
            || seg.Equals("TestCases", StringComparison.OrdinalIgnoreCase)) return true;

        // Tests / Test / Foo.Tests / Foo.Tests.Windows
        foreach (var part in seg.Split('.'))
            if (part.Equals("Test", StringComparison.OrdinalIgnoreCase)
                || part.Equals("Tests", StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    private static int CompareFindings(Finding a, Finding b)
    {
        var s = b.Severity.CompareTo(a.Severity);           // Fail 先
        if (s != 0) return s;
        var k = a.Kind.CompareTo(b.Kind);
        if (k != 0) return k;
        var f = string.CompareOrdinal(a.Location.File, b.Location.File);
        if (f != 0) return f;
        var l = a.Location.Line.CompareTo(b.Location.Line);
        return l != 0 ? l : string.CompareOrdinal(a.Key, b.Key);
    }
}
