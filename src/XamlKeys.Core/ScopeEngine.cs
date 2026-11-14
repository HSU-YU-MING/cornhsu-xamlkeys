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
    private readonly XamlDialect _dialect;
    private readonly IReadOnlyList<ThemeSide> _sides;
    private readonly int[] _appScope;

    /// <summary>每本字典的「查找順序」快取：第一個命中的就是贏家。</summary>
    private readonly Dictionary<int, (List<KeyDefinition> Order, List<string> Externals)> _cache = [];

    public ScopeEngine(IReadOnlyList<ResourceDictionaryNode> dictionaries, XamlDialect dialect,
                       IReadOnlyList<ThemeSide>? sides = null)
    {
        _byId = dictionaries.ToDictionary(d => d.Id);
        _dialect = dialect;
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

    public LookupResult Lookup(string key, IReadOnlyList<int> chain, ThemeSide side)
    {
        string? firstExternal = null;

        foreach (var id in EffectiveChain(chain))
        {
            var (order, externals) = OrderFor(id);

            foreach (var def in order)
            {
                if (!string.Equals(def.Key, key, StringComparison.Ordinal)) continue;
                if (!SideMatches(def, side)) continue;
                return new LookupResult(LookupStatus.Found, def);
            }

            if (firstExternal is null && externals.Count > 0) firstExternal = externals[0];
        }

        return firstExternal is not null
            ? new LookupResult(LookupStatus.Unknown, ExternalSource: firstExternal)
            : new LookupResult(LookupStatus.NotFound);
    }

    /// <summary>真正的查找鏈：元素自己的作用域 → …→ 檔案根 → Application.Resources。
    /// ⚠ Application 那一層不在掃描器建的鏈裡（它在別的檔案），一定要補上，
    ///   否則全域色票會被整批報成懸空。</summary>
    private IEnumerable<int> EffectiveChain(IReadOnlyList<int> chain)
    {
        foreach (var id in chain) yield return id;
        foreach (var id in _appScope)
            if (!chain.Contains(id)) yield return id;
    }

    private bool SideMatches(KeyDefinition def, ThemeSide side)
    {
        if (side == ThemeSide.Neutral) return true;
        var defSide = _byId.TryGetValue(def.DictionaryId, out var d) ? d.Side : ThemeSide.Neutral;
        return defSide == ThemeSide.Neutral || defSide == side;
    }

    private (List<KeyDefinition> Order, List<string> Externals) OrderFor(int dictId)
    {
        if (_cache.TryGetValue(dictId, out var cached)) return cached;

        var order = new List<KeyDefinition>();
        var externals = new List<string>();
        Walk(dictId, [], order, externals);

        var entry = (order, externals);
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

        var merged = _dialect == XamlDialect.Wpf
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
        var (order, _) = OrderFor(dictId);
        if (order.Count < 2) yield break;

        foreach (var side in _sides)
        {
            var visible = order.Where(d => SideMatches(d, side)).ToList();

            foreach (var group in visible.GroupBy(d => d.Key, StringComparer.Ordinal))
            {
                var list = group.ToList();
                if (list.Count < 2) continue;

                // order 已經是查找順序：第一個就是贏家
                yield return (group.Key, list[0], list.Skip(1).ToList());
            }
        }
    }
}
