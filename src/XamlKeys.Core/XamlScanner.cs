using System.Xml;
using System.Xml.Linq;

namespace XamlKeys.Core;

/// <summary>一個檔案解析失敗的紀錄。⚠ 解析失敗要進 summary 並影響覆蓋率 ——
/// 靜默跳過一個檔，裡面的鍵就會全部變成「沒人用」。</summary>
public sealed record ParseError(string File, string Message);

/// <summary>
/// 走訪 XAML，建出字典節點、鍵定義與引用（含作用域鏈）。
///
/// 設計要點：
///   - 作用域鏈由內而外。元素底下的 &lt;X.Resources&gt; 對它自己與整棵子樹有效。
///   - 設計時命名空間（mc:Ignorable 宣告的，通常是 d:）整個跳過 —— 那些不會進到執行期。
///   - 註解 &lt;!-- xamlkeys-ignore: 理由 --&gt; 壓下一個元素**與它的子樹**。
/// </summary>
public sealed partial class XamlScanner
{
    public const string IgnoreMarker = "xamlkeys-ignore";

    private static readonly XNamespace XamlNs = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace McNs = "http://schemas.openxmlformats.org/markup-compatibility/2006";

    private readonly string _root;
    private readonly SourceResolver _resolver;
    private readonly List<ResourceDictionaryNode> _dictionaries = [];
    private readonly List<KeyDefinition> _definitions = [];
    private readonly List<KeyReference> _references = [];
    private readonly List<ParseError> _errors = [];
    private readonly Dictionary<string, int> _standaloneByPath = new(StringComparer.OrdinalIgnoreCase);

    public XamlScanner(string root, SourceResolver resolver)
    {
        _root = Path.GetFullPath(root);
        _resolver = resolver;
    }

    public IReadOnlyList<ResourceDictionaryNode> Dictionaries => _dictionaries;
    public IReadOnlyList<KeyDefinition> Definitions => _definitions;
    public IReadOnlyList<KeyReference> References => _references;
    public IReadOnlyList<ParseError> Errors => _errors;

    /// <summary>獨立字典檔的路徑 → 字典 Id。合併解析完要用它接線。</summary>
    public IReadOnlyDictionary<string, int> StandaloneByPath => _standaloneByPath;

    public void ScanFile(string file)
    {
        XDocument doc;
        try
        {
            // ⚠ 這個工具會在 CI 上掃**別人的** repo。XDocument.Load 的預設設定會展開
            //   內部 DTD 實體（實測：`<!ENTITY k "X">` 定義的鍵真的被解出來），
            //   外部實體的解析也只靠「我們剛好不印資源的值」擋著 ——
            //   哪天有人把值加進報告，這就變成任意讀檔。
            //   XAML 從來不用 DTD，所以直接禁掉，並把 XmlResolver 關死。
            using var reader = XmlReader.Create(file, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreWhitespace = false,
            });
            doc = XDocument.Load(reader, LoadOptions.SetLineInfo);
        }
        catch (XmlException ex)
        {
            _errors.Add(new ParseError(Rel(file), ex.Message));
            return;
        }
        catch (IOException ex)
        {
            _errors.Add(new ParseError(Rel(file), ex.Message));
            return;
        }

        if (doc.Root is not { } root) return;

        var ignorable = IgnorablePrefixes(root);

        if (root.Name.LocalName == "ResourceDictionary")
        {
            var dict = NewDictionary(file, owner: null, LineOf(root), standalone: true, appScope: false);
            _standaloneByPath[Path.GetFullPath(file)] = dict.Id;
            ReadDictionaryElement(root, dict, [dict.Id], ignorable, null);
        }
        else
        {
            // 畫面檔（Window／UserControl／Page／Application）。Application.Resources 是最外層作用域。
            WalkElement(root, file, [], ignorable, null);
        }
    }

    // ── 走訪 ───────────────────────────────────────────────────────────────

    private void WalkElement(XElement el, string file, IReadOnlyList<int> chain,
                             HashSet<string> ignorable, string? suppressed)
    {
        if (IsIgnorable(el, ignorable)) return;

        var chainHere = chain;
        var resProp0 = FindResourcesProperty(el);
        var stylesProp = FindStylesProperty(el);

        // Avalonia：<X.Styles> 底下的 <StyleInclude Source="…"/> 與函式庫主題元素
        // （<semi:SemiTheme/>、<FluentAvaloniaTheme/>…）都會把資源帶進這個作用域。
        // 它們不在 <X.Resources> 裡,不認的話那些資源等於不存在。
        if (stylesProp is not null && !IsIgnorable(stylesProp, ignorable) && resProp0 is null
            && stylesProp.Elements().Any(e => BringsResources(e)))
        {
            var sd = NewDictionary(file, $"{el.Name.LocalName}.Styles", LineOf(stylesProp),
                                   standalone: false,
                                   appScope: el.Name.LocalName.EndsWith("Application", StringComparison.Ordinal));
            AddStyleSources(stylesProp, sd, ignorable);
            chainHere = Prepend(sd.Id, chain);
        }

        // 這個元素自己的 <X.Resources>
        if (resProp0 is { } resProp && !IsIgnorable(resProp, ignorable))
        {
            var isApp = el.Name.LocalName.EndsWith("Application", StringComparison.Ordinal);
            var dict = NewDictionary(file, $"{el.Name.LocalName}.Resources", LineOf(resProp),
                                     standalone: false, appScope: isApp);

            // <X.Resources> 底下可能直接放資源，也可能包一層 <ResourceDictionary>
            var wrapper = resProp.Elements()
                                 .FirstOrDefault(e => e.Name.LocalName == "ResourceDictionary"
                                                   && !IsIgnorable(e, ignorable));

            chainHere = Prepend(dict.Id, chainHere);

            if (wrapper is not null)
                ReadDictionaryElement(wrapper, dict, chainHere, ignorable, suppressed);
            else
                ReadDictionaryBody(resProp, dict, chainHere, ignorable, suppressed);

            // 同一個元素同時有 .Resources 與 .Styles 時，Styles 帶進來的資源也算這個作用域的
            if (stylesProp is not null && !IsIgnorable(stylesProp, ignorable))
                AddStyleSources(stylesProp, dict, ignorable);
        }

        CollectRefs(el, file, chainHere, ignorable, suppressed);

        string? pendingIgnore = null;
        foreach (var node in el.Nodes())
        {
            if (node is XComment c)
            {
                pendingIgnore = ParseIgnore(c) ?? pendingIgnore;
                continue;
            }

            if (node is not XElement child) continue;
            if (ReferenceEquals(child, FindResourcesProperty(el))) { pendingIgnore = null; continue; }

            WalkElement(child, file, chainHere, ignorable, suppressed ?? pendingIgnore);
            pendingIgnore = null;
        }
    }

    /// <summary>讀一個 &lt;ResourceDictionary&gt; 元素：Source、MergedDictionaries、ThemeDictionaries、以及自己的鍵。</summary>
    private void ReadDictionaryElement(XElement rdEl, ResourceDictionaryNode dict,
                                       IReadOnlyList<int> chain, HashSet<string> ignorable, string? suppressed)
    {
        // <ResourceDictionary Source="…"/> 當成「把那本合併進來」
        if (rdEl.Attribute("Source")?.Value is { Length: > 0 } ownSource)
            AddMerged(dict, ownSource, dict.File, LineOf(rdEl));

        foreach (var child in rdEl.Elements())
        {
            if (IsIgnorable(child, ignorable)) continue;

            switch (child.Name.LocalName)
            {
                case "ResourceDictionary.MergedDictionaries":
                    foreach (var m in child.Elements())
                    {
                        if (IsIgnorable(m, ignorable)) continue;

                        // ⚠ Avalonia 用 <ResourceInclude Source="…"/>（舊版是 MergeResourceInclude），
                        //    不是 <ResourceDictionary Source="…"/>。兩者語意一樣：
                        //    把那本合併進來,而且 Avalonia 的查找也是反向走合併清單（後宣告的贏）——
                        //    這點與 WPF 相同,查證自 Avalonia.Base/Controls/ResourceDictionary.cs。
                        if (m.Attribute("Source")?.Value is { Length: > 0 } s)
                            AddMerged(dict, s, dict.File, LineOf(m));
                        else
                            // 行內合併：<ResourceDictionary> 直接寫在 MergedDictionaries 裡
                            InlineMerged(m, dict, chain, ignorable, suppressed);
                    }
                    break;

                case "ResourceDictionary.ThemeDictionaries":
                {
                    var group = child.Elements().Where(t => !IsIgnorable(t, ignorable)).ToList();
                    var groupId = _nextThemeGroupId++;

                    foreach (var t in group)
                    {
                        var themeKey = t.Attribute(XamlNs + "Key")?.Value;
                        var themeDict = NewDictionary(dict.File, $"ThemeDictionaries[{themeKey}]",
                                                      LineOf(t), standalone: false, appScope: false);
                        themeDict.Side = SideOf(themeKey);
                        themeDict.ThemeGroupId = groupId;

                        if (t.Attribute("Source")?.Value is { Length: > 0 } ts)
                            AddMerged(themeDict, ts, dict.File, LineOf(t));

                        ReadDictionaryElement(t, themeDict, Prepend(themeDict.Id, chain), ignorable, suppressed);
                        dict.Merged.Add(new MergedReference
                        {
                            RawSource = $"ThemeDictionaries[{themeKey}]",
                            Location = new SourceLocation(Rel(dict.File), LineOf(t)),
                            ResolvedDictionaryId = themeDict.Id,
                        });
                    }
                    break;
                }

                default:
                    ReadResourceEntry(child, dict, chain, ignorable, suppressed);
                    break;
            }
        }

        // 註解壓制：直接子層的 <!-- xamlkeys-ignore --> 要對應到下一個資源項目
        ApplyIgnoreComments(rdEl, dict);
    }

    private void InlineMerged(XElement m, ResourceDictionaryNode parent, IReadOnlyList<int> chain,
                              HashSet<string> ignorable, string? suppressed)
    {
        // ⚠ MergedDictionaries 裡放的不一定是 <ResourceDictionary>：
        //    函式庫常提供 ResourceDictionary 的**子類別**，用元素寫法合併進來
        //    （Wpf.Ui 的 <ui:ThemesDictionary />、<ui:ControlsDictionary />）。
        //    它們的內容在組件裡，靜態看不到。
        //    舊版直接 return —— 等於把一本看不到的字典當成「不存在」，
        //    正好違反第一不變量。實測：QuillLook 因此多出 117 筆假 dangling-dynamic。
        // Avalonia 的 <ResourceInclude>／<MergeResourceInclude> 沒有 Source 時是空的,不是外部字典
        if (m.Name.LocalName is "ResourceInclude" or "MergeResourceInclude") return;

        if (m.Name.LocalName != "ResourceDictionary")
        {
            parent.Merged.Add(new MergedReference
            {
                RawSource = $"<{m.Name.LocalName}/>",
                Location = new SourceLocation(Rel(parent.File), LineOf(m)),
                ResolvedDictionaryId = null,
                ExternalReason = $"'{m.Name.LocalName}' is a ResourceDictionary subclass supplied by a library; "
                               + "its contents are not visible in the source tree",
            });
            return;
        }

        var inline = NewDictionary(parent.File, "MergedDictionaries[inline]", LineOf(m),
                                   standalone: false, appScope: false);
        ReadDictionaryElement(m, inline, Prepend(inline.Id, chain), ignorable, suppressed);
        parent.Merged.Add(new MergedReference
        {
            RawSource = "(inline)",
            Location = new SourceLocation(Rel(parent.File), LineOf(m)),
            ResolvedDictionaryId = inline.Id,
        });
    }

    /// <summary>&lt;X.Resources&gt; 底下沒有包 ResourceDictionary 的情形：直接子元素就是資源項目。</summary>
    private void ReadDictionaryBody(XElement container, ResourceDictionaryNode dict,
                                   IReadOnlyList<int> chain, HashSet<string> ignorable, string? suppressed)
    {
        foreach (var child in container.Elements())
        {
            if (IsIgnorable(child, ignorable)) continue;
            ReadResourceEntry(child, dict, chain, ignorable, suppressed);
        }

        ApplyIgnoreComments(container, dict);
    }

    /// <summary>一個資源項目：登記 x:Key 定義，然後把它整棵子樹當一般元素走（裡面會有引用）。</summary>
    private void ReadResourceEntry(XElement el, ResourceDictionaryNode dict,
                                   IReadOnlyList<int> chain, HashSet<string> ignorable, string? suppressed)
    {
        // ⚠ 資源字典項目沒有 x:Key 時，x:Name 就是它的鍵（XAML 規格）。
        //   實查（WinUI Gallery、wpfui）：ImplicitAnimationSet、轉換器、Layout 常這樣寫 ——
        //   漏掉這條會把它們的引用全部報成懸空，而且定義端也會被算成「沒有鍵」。
        var keyAttr = el.Attribute(XamlNs + "Key") ?? el.Attribute(XamlNs + "Name");
        if (keyAttr is not null)
        {
            var raw = keyAttr.Value.Trim();

            // x:Key="{x:Type Button}" 之類：鍵不是字串（隱含樣式的顯式寫法）。不是我們的單位。
            if (!raw.StartsWith('{'))
            {
                // 資源別名：<StaticResource x:Key="A" ResourceKey="B"/>（Dynamic／Theme 同）
                string? alias = null;
                if (el.Name.LocalName is "StaticResource" or "DynamicResource" or "ThemeResource")
                    alias = el.Attribute("ResourceKey")?.Value?.Trim();

                // 別名指向的也可能是 markup extension（{x:Static …Key}）—— 那不是字串鍵
                if (alias is not null && alias.StartsWith('{')) alias = null;

                var def = new KeyDefinition
                {
                    Key = raw,
                    Location = new SourceLocation(Rel(dict.File), LineOf(el)),
                    DictionaryId = dict.Id,
                    ElementName = el.Name.LocalName,
                    AliasTarget = alias,
                    SuppressedReason = suppressed,
                    ContentHash = ContentOf(el),
                };

                dict.OwnKeys.Add(def);
                _definitions.Add(def);

                if (alias is { Length: > 0 })
                    _references.Add(new KeyReference
                    {
                        Key = alias,
                        Location = def.Location,
                        Kind = el.Name.LocalName switch
                        {
                            "DynamicResource" => RefKind.Dynamic,
                            "ThemeResource" => RefKind.Theme,
                            _ => RefKind.Static,
                        },
                        ScopeChain = chain,
                        SuppressedReason = suppressed,
                    });
            }
        }

        WalkElement(el, dict.File, chain, ignorable, suppressed);
    }

    // ── 引用蒐集 ───────────────────────────────────────────────────────────

    private void CollectRefs(XElement el, string file, IReadOnlyList<int> chain,
                             HashSet<string> ignorable, string? suppressed)
    {
        // 屬性元素形式：<StaticResource ResourceKey="K"/>（沒有 x:Key 的那種，有的話已在上面當別名處理）
        if (el.Name.LocalName is "StaticResource" or "DynamicResource" or "ThemeResource"
            && el.Attribute(XamlNs + "Key") is null && el.Attribute(XamlNs + "Name") is null
            && el.Attribute("ResourceKey")?.Value is { Length: > 0 } rk)
        {
            // ⚠ ResourceKey 的值也可以是 markup extension：
            //    <StaticResource ResourceKey="{x:Static SystemColors.HighlightColorKey}"/>
            //    舊版把整串當成鍵名,於是報出 10 筆叫「{x:Static SystemColors.…}」的假懸空
            //    （實測 ModernWpf 的 HighContrast.xaml）。
            if (rk.TrimStart().StartsWith('{'))
            {
                _unresolvedSites.Add(new SourceLocation(Rel(file), LineOf(el)));
                return;
            }

            _references.Add(new KeyReference
            {
                Key = rk.Trim(),
                Location = new SourceLocation(Rel(file), LineOf(el)),
                Kind = el.Name.LocalName switch
                {
                    "DynamicResource" => RefKind.Dynamic,
                    "ThemeResource" => RefKind.Theme,
                    _ => RefKind.Static,
                },
                ScopeChain = chain,
                SuppressedReason = suppressed,
            });
        }

        foreach (var attr in el.Attributes())
        {
            if (attr.IsNamespaceDeclaration) continue;
            if (IsIgnorableName(attr.Name, ignorable)) continue;
            // x:Key 是定義不是引用；Source 由合併解析處理
            if (attr.Name == XamlNs + "Key") continue;

            foreach (var r in MarkupExtensions.Scan(attr.Value))
                AddExtensionRef(r, file, LineOf(attr), chain, suppressed);
        }

        // 文字節點也可能整段是 markup extension（<Setter.Value>{StaticResource K}</Setter.Value>）
        foreach (var text in el.Nodes().OfType<XText>())
        {
            var v = text.Value;
            if (v.Length == 0 || !v.Contains('{')) continue;
            foreach (var r in MarkupExtensions.Scan(v))
                AddExtensionRef(r, file, LineOf(el), chain, suppressed);
        }
    }

    private void AddExtensionRef(ExtensionRef r, string file, int line,
                                 IReadOnlyList<int> chain, string? suppressed)
    {
        if (r.Key is null)
        {
            // {StaticResource {x:Type Button}} / {x:Static …Key} —— 鍵不是字串。
            // 不報任何東西，但要讓 summary 知道這裡有個看不懂的引用。
            _unresolvedSites.Add(new SourceLocation(Rel(file), line));
            return;
        }

        _references.Add(new KeyReference
        {
            Key = r.Key,
            Location = new SourceLocation(Rel(file), line),
            Kind = r.Kind,
            ScopeChain = chain,
            SuppressedReason = suppressed,
        });
    }

    private readonly List<SourceLocation> _unresolvedSites = [];
    public IReadOnlyList<SourceLocation> UnresolvedSites => _unresolvedSites;

    // ── 工具 ───────────────────────────────────────────────────────────────

    private void AddMerged(ResourceDictionaryNode dict, string rawSource, string containingFile, int line)
    {
        var res = _resolver.Resolve(rawSource, containingFile);
        dict.Merged.Add(new MergedReference
        {
            RawSource = rawSource,
            Location = new SourceLocation(Rel(dict.File), line),
            Assembly = res.Assembly,
            ExternalReason = res.ExternalReason,
            // 真正的 Id 要等所有檔都掃完才接得起來（被合併的檔可能還沒掃到）
            ResolvedDictionaryId = null,
        });

        if (res.Path is not null)
            _pendingLinks.Add((dict.Merged[^1], res.Path));
    }

    private readonly List<(MergedReference Ref, string Path)> _pendingLinks = [];
    private int _nextThemeGroupId;

    /// <summary>所有檔掃完之後把合併參考接到實際的字典節點上。</summary>
    public void LinkMergedDictionaries()
    {
        foreach (var (mref, path) in _pendingLinks)
        {
            if (_standaloneByPath.TryGetValue(Path.GetFullPath(path), out var id))
                mref.ResolvedDictionaryId = id;
            else
                mref.ExternalReason ??= $"'{Rel(path)}' resolved to a file that is not a ResourceDictionary in scope";
        }
    }

    private ResourceDictionaryNode NewDictionary(string file, string? owner, int line, bool standalone, bool appScope)
    {
        var d = new ResourceDictionaryNode
        {
            Id = _dictionaries.Count,
            File = file,
            Owner = owner,
            Location = new SourceLocation(Rel(file), line),
            IsStandaloneFile = standalone,
            IsApplicationScope = appScope,
        };
        _dictionaries.Add(d);
        return d;
    }

    private static XElement? FindResourcesProperty(XElement el) =>
        el.Elements().FirstOrDefault(e =>
            e.Name.LocalName.EndsWith(".Resources", StringComparison.Ordinal));

    /// <summary>Avalonia 的 &lt;X.Styles&gt; 屬性元素。</summary>
    private static XElement? FindStylesProperty(XElement el) =>
        el.Elements().FirstOrDefault(e =>
            e.Name.LocalName.EndsWith(".Styles", StringComparison.Ordinal));

    /// <summary>這個放在 Styles 裡的元素會不會帶資源進來？
    /// &lt;Style&gt; 自己的資源由一般走訪處理；會帶「看不到的」資源進來的是
    /// &lt;StyleInclude Source="…"/&gt; 與函式庫的主題元素。</summary>
    private static bool BringsResources(XElement e) =>
        e.Name.LocalName is "StyleInclude" or "StyleIncludes"
        || (e.Name.LocalName is not ("Style" or "Styles") && !e.HasElements && e.Attribute("Source") is null);

    /// <summary>把 &lt;X.Styles&gt; 裡的 StyleInclude 與函式庫主題元素記成這個作用域的合併邊。</summary>
    private void AddStyleSources(XElement stylesProp, ResourceDictionaryNode dict, HashSet<string> ignorable)
    {
        foreach (var e in stylesProp.Elements())
        {
            if (IsIgnorable(e, ignorable)) continue;

            if (e.Name.LocalName is "StyleInclude" or "StyleIncludes")
            {
                if (e.Attribute("Source")?.Value is { Length: > 0 } src)
                    AddMerged(dict, src, dict.File, LineOf(e));
                continue;
            }

            if (e.Name.LocalName is "Style" or "Styles") continue;

            // <semi:SemiTheme/>、<FluentAvaloniaTheme/> 之類：函式庫提供的主題物件，
            // 它帶的資源在組件裡,靜態看不到 -> external（絕不能當成不存在）
            dict.Merged.Add(new MergedReference
            {
                RawSource = $"<{e.Name.LocalName}/>",
                Location = new SourceLocation(Rel(dict.File), LineOf(e)),
                ResolvedDictionaryId = null,
                ExternalReason = $"'{e.Name.LocalName}' is a theme object supplied by a library; "
                               + "the resources it brings are not visible in the source tree",
            });
        }
    }

    private static IReadOnlyList<int> Prepend(int id, IReadOnlyList<int> chain)
    {
        var list = new List<int>(chain.Count + 1) { id };
        list.AddRange(chain);
        return list;
    }

    /// <summary>
    /// ThemeDictionaries 的分側。
    ///
    /// ⚠ "Default" 不是固定的某一側 —— 它是「沒有對應主題時用這本」，
    /// 所以它代表哪一側要看同一組裡還有誰：
    ///   Default ＋ Dark（沒有 Light）→ Default 就是**淺色**那本
    ///   Default ＋ Light（沒有 Dark）→ Default 就是深色那本
    ///   只有 Default                  → 兩側共用
    ///   Default ＋ Light ＋ Dark      → Default 在執行期永遠輪不到（Fallback）
    ///
    /// 實查（WinUI Gallery，XamlResourcesPage）：一組 Default＋Dark，Default 裡寫的是
    /// 淺色值。把 Default 一律當深色的話，兩本會被判成互相覆蓋 —— 四筆假 shadowed。
    /// </summary>
    private static ThemeSide SideOf(string? themeKey) => themeKey switch
    {
        "Dark" => ThemeSide.Dark,
        "Light" => ThemeSide.Light,
        "HighContrast" => ThemeSide.HighContrast,

        // ⚠ "Default" 一律當**兩側共用**。
        //   它在兩個方言裡都是「要求的主題沒有對應字典時用這本」的退路 ——
        //   Avalonia 的原始碼寫得最清楚（ResourceDictionary.TryGetResource：
        //   先查要求的 variant、再沿 InheritVariant 往上、最後才查 ThemeVariant.Default）。
        //   早期版本依同組還有誰去推它代表哪一側，那會在「鍵只定義在 Default」時
        //   誤判成另一側缺漏。往少報的方向錯,不往害人的方向錯。
        //   同組字典之間不會互相覆蓋這件事，由 ThemeGroupId 負責，不靠這裡。
        "Default" => ThemeSide.Neutral,
        _ => ThemeSide.Neutral,
    };

    private static string? ParseIgnore(XComment c)
    {
        var t = c.Value.Trim();
        if (!t.StartsWith(IgnoreMarker, StringComparison.OrdinalIgnoreCase)) return null;

        var rest = t[IgnoreMarker.Length..].TrimStart();
        if (rest.StartsWith(':')) rest = rest[1..].Trim();

        // ⚠ 理由必填。沒理由的 ignore 視為無效 —— 回傳空字串讓上層報警告。
        return rest;
    }

    /// <summary>把直接子層的 ignore 註解套到緊接著的資源項目上。</summary>
    private static void ApplyIgnoreComments(XElement container, ResourceDictionaryNode dict)
    {
        string? pending = null;
        foreach (var node in container.Nodes())
        {
            if (node is XComment c)
            {
                pending = ParseIgnore(c) ?? pending;
                continue;
            }

            if (node is not XElement el) continue;
            if (pending is not null)
            {
                var line = LineOf(el);
                foreach (var d in dict.OwnKeys.Where(d => d.Location.Line == line))
                    d.SuppressedReason = pending;
                pending = null;
            }
        }
    }

    private static HashSet<string> IgnorablePrefixes(XElement root)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var attr = root.Attribute(McNs + "Ignorable")?.Value;
        if (attr is null) return set;

        foreach (var prefix in attr.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var ns = root.GetNamespaceOfPrefix(prefix);
            if (ns is not null) set.Add(ns.NamespaceName);
        }

        return set;
    }

    private static bool IsIgnorable(XElement el, HashSet<string> ignorable) =>
        ignorable.Count > 0 && ignorable.Contains(el.Name.NamespaceName);

    private static bool IsIgnorableName(XName name, HashSet<string> ignorable) =>
        ignorable.Count > 0 && ignorable.Contains(name.NamespaceName);

    // ⚠ HasLineInfo 是方法不是屬性，不能寫成屬性模式
    private static int LineOf(XObject o) => o is IXmlLineInfo li && li.HasLineInfo() ? li.LineNumber : 0;

    /// <summary>資源內容的正規化字串：拿掉 x:Key／x:Name，屬性依名稱排序，空白壓成一格。
    /// 只用來比對「兩個定義是不是同一個東西」，不求密碼學強度。</summary>
    private static string ContentOf(XElement el)
    {
        var clone = new XElement(el.Name);

        foreach (var a in el.Attributes()
                            .Where(a => !a.IsNamespaceDeclaration
                                     && a.Name != XamlNs + "Key" && a.Name != XamlNs + "Name")
                            .OrderBy(a => a.Name.ToString(), StringComparer.Ordinal))
            clone.SetAttributeValue(a.Name, a.Value);

        foreach (var n in el.Nodes())
            clone.Add(n is XText t ? new XText(t.Value.Trim()) : n);

        return WhitespaceRegex().Replace(clone.ToString(SaveOptions.DisableFormatting), " ");
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\s+")]
    private static partial System.Text.RegularExpressions.Regex WhitespaceRegex();

    private string Rel(string file)
    {
        var full = Path.GetFullPath(file);
        return full.StartsWith(_root, StringComparison.OrdinalIgnoreCase)
            ? full[_root.Length..].TrimStart(Path.DirectorySeparatorChar, '/').Replace('\\', '/')
            : full.Replace('\\', '/');
    }
}
