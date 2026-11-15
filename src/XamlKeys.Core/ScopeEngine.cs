namespace XamlKeys.Core;

/// <summary>
/// 作用域查找。
///
/// WPF 的規則（微軟文件，2026-11-14 查證）：
///   - 字典**自己的**項目優先於合併進來的。
///   - 合併進來的字典之間，**最後宣告的贏**（the last dictionary found sequentially
///     in the MergedDictionaries collection）。實作上等於「反向搜尋，第一個命中的就是答案」。
///   - 跨合併字典的重複鍵是合法的，不會丟錯 —— 只有單一字典內部要求唯一。
///
/// ⚠ WinUI／UWP 是**相反**的（依宣告順序的反向檢查，等於先宣告的贏）。
///   同一份 XAML 搬到 Uno/WinUI 會換答案，所以 dialect 是查找的參數，不是細節。
/// </summary>
public sealed class ScopeEngine
{
    private readonly Dictionary<int, ResourceDictionaryNode> _byId;
    private readonly IReadOnlyList<ThemeSide> _sides;
    private readonly int[] _appScope;

    /// <summary>每本字典的「查找順序」快取：第一個命中的就是贏家。
    /// ⚠ 另外存一份依鍵名分組的索引：ScreenToGif 有 45,054 個鍵、5,509 個引用，
    /// 每次查找都線性掃過 order 是 2.5 億次比對（實測 8.5 秒）。
    /// ByKey 的每個 list 維持 order 的先後，所以語意完全一樣。</summary>
    private readonly Dictionary<int, Scope> _cache = [];

    private sealed record Scope(
        List<KeyDefinition> Order,
        Dictionary<string, List<KeyDefinition>> ByKey,
        List<string> Externals);

    /// <param name="dialect">非 null 時，整組字典一律用這個方言（明寫 --dialect、或測試）。
    /// null 時用**每本字典自己的** <see cref="ResourceDictionaryNode.Dialect"/> ——
    /// 一棵樹裡可以同時有 WPF 與 WinUI 專案。</param>
    public ScopeEngine(IReadOnlyList<ResourceDictionaryNode> dictionaries, XamlDialect? dialect = null,
                       IReadOnlyList<ThemeSide>? sides = null)
    {
        _byId = dictionaries.ToDictionary(d => d.Id);

        if (dialect is { } forced)
            foreach (var d in dictionaries) d.Dialect = forced;
        _sides = sides ?? [ThemeSide.Neutral];
        // Application.Resources，加上「程式在執行期合併進來的」字典 ——
        // 後者是主題切換的標準寫法，靜態看不到合併邊，但執行期確實在作用域裡。
        _appScope = dictionaries
            .Where(d => d.IsApplicationScope || d.IsRuntimeLoaded)
            .Select(d => d.Id)
            .ToArray();

        var mergeTargets = new HashSet<int>();
        foreach (var d in dictionaries)
            foreach (var m in d.Merged)
                if (m.ResolvedDictionaryId is { } id) mergeTargets.Add(id);

        RootDictionaries = dictionaries.Where(d => !mergeTargets.Contains(d.Id)).ToList();
    }

    /// <summary>沒有被任何人合併的字典 —— 重複鍵的偵測從這些點出發，避免同一組重複報很多次。</summary>
    public IReadOnlyList<ResourceDictionaryNode> RootDictionaries { get; }

    /// <param name="projectDir">引用所在檔案的專案目錄。
    /// ⚠ 不能從 chain[0] 推：畫面檔常常沒有自己的 &lt;X.Resources&gt;，鏈是空的 ——
    /// 而那正是最常見的情形。推不出來就只好接全樹的 Application 作用域，
    /// 於是別的專案合併的一本看不到的字典會污染這裡（實測 wpfui）。</param>
    public LookupResult Lookup(string key, IReadOnlyList<int> chain, ThemeSide side, string? projectDir = null)
    {
        string? firstExternal = null;

        foreach (var id in EffectiveChain(chain, projectDir))
        {
            var scope = OrderFor(id);

            if (scope.ByKey.TryGetValue(key, out var candidates))
                foreach (var def in candidates)          // 已經是查找順序
                    if (SideMatches(def, side))
                        return new LookupResult(LookupStatus.Found, def);

            if (firstExternal is null && scope.Externals.Count > 0) firstExternal = scope.Externals[0];
        }

        return firstExternal is not null
            ? new LookupResult(LookupStatus.Unknown, ExternalSource: firstExternal)
            : new LookupResult(LookupStatus.NotFound);
    }

    /// <summary>真正的查找鏈：元素自己的作用域 → …→ 檔案根 → **同一個專案的** Application.Resources。
    ///
    /// ⚠ Application 那一層不在掃描器建的鏈裡（它在別的檔案），一定要補上，
    ///   否則全域色票會被整批報成懸空。
    /// ⚠ 但只能補**同一個專案的**。實測（wpfui）：補全樹的話，
    ///   `samples/Wpf.Ui.Demo.Mvvm/App.xaml` 合併的一本看不到的字典，
    ///   會讓函式庫 `src/Wpf.Ui/` 底下每一個檔案的查找都變成「不可知」，
    ///   一筆真的懸空引用因此被降級成 external。</summary>
    private IEnumerable<int> EffectiveChain(IReadOnlyList<int> chain, string? projectDir)
    {
        foreach (var id in chain) yield return id;

        var project = projectDir
                   ?? (chain.Count > 0 && _byId.TryGetValue(chain[0], out var first) ? first.ProjectDir : null);

        foreach (var id in _appScope)
        {
            if (chain.Contains(id)) continue;

            // 專案推不出來時（檔案不在任何 csproj 底下）保守一點：全部都接
            // ⚠ 反過來不行：引用在某個專案裡時，**不屬於任何專案**的 App.xaml 不接。
            //   2026-11-15 第九輪 dotnet/maui：src/ProfiledAot/src/maui/App.xaml 沒有 csproj，它合併一本不存在的 Styles.xaml
            //   —— 舊版把它接進整個 repo 每一個專案的作用域，真的懸空全部被降級成「也許在那本看不到的裡面」。
            if (project is not null && _byId.TryGetValue(id, out var app)
                && !string.Equals(app.ProjectDir, project, StringComparison.OrdinalIgnoreCase))
                continue;

            yield return id;
        }
    }

    private bool SideMatches(KeyDefinition def, ThemeSide side)
    {
        if (side == ThemeSide.Neutral) return true;
        var defSide = _byId.TryGetValue(def.DictionaryId, out var d) ? d.Side : ThemeSide.Neutral;
        return defSide == ThemeSide.Neutral || defSide == side;
    }

    private Scope OrderFor(int dictId)
    {
        if (_cache.TryGetValue(dictId, out var cached)) return cached;

        var order = new List<KeyDefinition>();
        var externals = new List<string>();
        Walk(dictId, [], order, externals);

        var byKey = new Dictionary<string, List<KeyDefinition>>(StringComparer.Ordinal);
        foreach (var d in order)
        {
            if (!byKey.TryGetValue(d.Key, out var list)) byKey[d.Key] = list = [];
            list.Add(d);
        }

        var entry = new Scope(order, byKey, externals);
        _cache[dictId] = entry;
        return entry;
    }

    private void Walk(int dictId, HashSet<int> visited, List<KeyDefinition> order, List<string> externals)
    {
        // ⚠ 字典互相合併會成環（A 合併 B、B 合併 A）。visited 同時也避免同一本被合併兩次時
        //    它的鍵出現兩份，被誤判成自己蓋自己。
        if (!visited.Add(dictId)) return;
        if (!_byId.TryGetValue(dictId, out var d)) return;

        order.AddRange(d.OwnKeys);

        // ⚠ 方言是**每本字典自己的**（跟著它所屬的專案），不是整次掃描一個值。
        //    一棵樹裡可以同時有 WPF 與 WinUI 專案，而兩者的合併優先序相反。
        var merged = d.Dialect == XamlDialect.Wpf
            ? Enumerable.Reverse(d.Merged)      // WPF：後宣告的贏 → 反向先查
            : d.Merged;                          // WinUI：宣告順序先查

        foreach (var m in merged)
        {
            if (m.ResolvedDictionaryId is { } id) Walk(id, visited, order, externals);
            else externals.Add(m.RawSource);
        }
    }

    /// <summary>某個作用域裡被重複定義、而且「後面那個永遠贏不了」的鍵。依側分開算 ——
    /// Dark 與 Light 各定義一次不是覆蓋，那是正常的雙主題寫法。</summary>
    public IEnumerable<(string Key, KeyDefinition Winner, List<KeyDefinition> Losers)> DuplicatesIn(int dictId)
    {
        var scope = OrderFor(dictId);
        if (scope.Order.Count < 2) yield break;

        foreach (var side in _sides)
        {
            // 只有「同一個鍵名出現過兩次以上」的才需要看 —— 其餘直接跳過
            foreach (var (key, defs) in scope.ByKey)
            {
                if (defs.Count < 2) continue;

                var visible = defs.Where(d => SideMatches(d, side)).ToList();
                if (visible.Count < 2) continue;

                // ByKey 的每個 list 維持 order 的先後：第一個就是贏家
                yield return (key, visible[0], visible.Skip(1).ToList());
            }
        }
    }
}
