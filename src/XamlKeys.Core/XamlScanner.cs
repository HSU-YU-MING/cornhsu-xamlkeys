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

    /// <summary>
    /// XAML 語言命名空間（x:Key、x:Name、x:Type…）。
    ///
    /// ⚠ **有兩個**。WPF／WinUI／Avalonia 用 2006，**.NET MAUI 用 2009**
    /// （實測 dotnet/maui-samples 的 1,238 個檔裡有 1,122 個是 2009）。
    /// 只認 2006 的話，MAUI 專案的 x:Key 一個都看不到 —— 實測是 0 個鍵、19,157 筆假懸空。
    /// </summary>
    private static readonly XNamespace[] XamlNamespaces =
    [
        "http://schemas.microsoft.com/winfx/2006/xaml",
        "http://schemas.microsoft.com/winfx/2009/xaml",
    ];

    /// <summary>取 x: 指令屬性（Key／Name），兩個 XAML 命名空間都認。</summary>
    private static XAttribute? XamlAttr(XElement el, string local)
    {
        foreach (var ns in XamlNamespaces)
            if (el.Attribute(ns + local) is { } a) return a;
        return null;
    }

    private static bool IsXamlDirective(XName name, string local)
    {
        foreach (var ns in XamlNamespaces)
            if (name == ns + local) return true;
        return false;
    }
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

    /// <summary>元素巢狀的上限。超過的檔不載入也不走訪（見 ScanFile）。</summary>
    internal const int MaxElementDepth = 512;

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
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreWhitespace = false,
            };

            // ⚠ 巢狀深度要在**載入之前**量，而且一超過就停。實測過兩種死法：
            //   - 8,000 層（約 100 KB）：後面的遞迴走訪 stack overflow，行程直接死掉，
            //     退出碼不在契約的 0–3 裡，其他檔的結果全部跟著消失
            //   - 80,000 層：XDocument.Load 本身就要 18 秒，而且是平方成長的
            //   真實的 XAML 不會超過幾十層，所以超過上限的檔當成「讀不了」：
            //   跳過、計入 parseErrors，照樣算進「結果可不可信」。
            using (var probe = XmlReader.Create(file, settings))
            {
                while (probe.Read())
                {
                    if (probe.NodeType != XmlNodeType.Element || probe.Depth < MaxElementDepth) continue;

                    _errors.Add(new ParseError(Rel(file),
                        $"elements are nested more than {MaxElementDepth} levels deep; file skipped"));
                    return;
                }
            }

            using var reader = XmlReader.Create(file, settings);
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
        if (XamlAttr(root, "Class")?.Value?.Trim() is { Length: > 0 } rootClass)
            _classOfFile[Path.GetFullPath(file)] = rootClass;
        if (root.Name.NamespaceName == "http://xamarin.com/schemas/2014/forms")
            _xamarinFormsFiles.Add(Path.GetFullPath(file));

        if (root.Name.LocalName == "ResourceDictionary")
        {
            var dict = NewDictionary(file, owner: null, LineOf(root), standalone: true, appScope: false);
            _standaloneByPath[Path.GetFullPath(file)] = dict.Id;
            RegisterClass(root, dict);
            ReadDictionaryElement(root, dict, [dict.Id], ignorable, null);
        }
        else if (root.Name.LocalName is "Styles" or "Style")
        {
            // Avalonia 的樣式檔：根元素是 <Styles>，資源在 <Styles.Resources>，
            // 直接子層的 <StyleInclude>／主題物件（<SimpleTheme/>）也會帶資源進來。
            // ⚠ 2026-11-15 之前只認 <X.Styles> 屬性元素，根是 <Styles> 的檔整份不算字典 ——
            //   實測 WalletWasabi：App 用 <StyleInclude Source="avares://…/Styles.axaml"/> 合併它，
            //   被判成「不是字典、讀不到」，131 筆鍵降級成 external；
            //   SukiUI 的 SukiTheme.axaml 在根層放 <SimpleTheme/>，它帶的鍵被報成懸空。
            // 根是單一個 <Style> 的檔也能被 StyleInclude（SukiUI 的 ColorTheme/ColorsStyles.xaml），資源在 <Style.Resources>。
            var dict = NewDictionary(file, owner: null, LineOf(root), standalone: true, appScope: false);
            dict.IsStylesFile = true;
            _standaloneByPath[Path.GetFullPath(file)] = dict.Id;
            RegisterClass(root, dict);
            // <Style> 的子層是 Setter 之類，不是主題物件 —— 只有 <Styles> 才收 StyleInclude／主題物件
            if (root.Name.LocalName == "Styles") AddStyleSources(root, dict, ignorable);

            IReadOnlyList<int> chain = [dict.Id];
            foreach (var child in root.Elements())
            {
                if (IsIgnorable(child, ignorable)) continue;
                if (child.Name.LocalName == root.Name.LocalName + ".Resources")
                {
                    var wrapper = child.Elements().FirstOrDefault(e => e.Name.LocalName == "ResourceDictionary"
                                                                    && !IsIgnorable(e, ignorable));
                    if (wrapper is not null) ReadDictionaryElement(wrapper, dict, chain, ignorable, null);
                    else ReadDictionaryBody(child, dict, chain, ignorable, null);
                }
                else if (child.Name.LocalName is not ("StyleInclude" or "StyleIncludes"))
                {
                    WalkElement(child, file, chain, ignorable, null);
                }
            }
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
            if (el.Parent is null) _rootResourcesOfFile[Path.GetFullPath(file)] = dict.Id;

            if (wrapper is not null)
                ReadDictionaryElement(wrapper, dict, chainHere, ignorable, suppressed);
            else
                ReadDictionaryBody(resProp, dict, chainHere, ignorable, suppressed);

            // 同一個元素同時有 .Resources 與 .Styles 時，Styles 帶進來的資源也算這個作用域的。
            // ⚠ 但優先序**低於** .Resources：Avalonia 先查 Resources（含它合併的），查不到才查 Styles。
            //   所以放在合併清單的最前面（WPF 式「後宣告的贏」裡，最前面＝最後才輪到）。
            //   2026-11-15 實測 FluentAvalonia 的範例 App：Resources 裡刻意覆寫 NavigationViewContentMargin，
            //   放在後面的話被判成「被函式庫的主題蓋掉」—— 方向剛好相反。
            if (stylesProp is not null && !IsIgnorable(stylesProp, ignorable))
            {
                var before = dict.Merged.Count;
                AddStyleSources(stylesProp, dict, ignorable);
                var added = dict.Merged.GetRange(before, dict.Merged.Count - before);
                dict.Merged.RemoveRange(before, added.Count);
                dict.Merged.InsertRange(0, added);
            }
        }

        // 自訂型別的元素（<views:SearchView/>）：記下它被放在哪裡、當時的作用域 ——
        // 那是 SearchView.xaml 的 DynamicResource 在執行期往上找得到的地方（見 Analyzer.HostsProvide）。
        var custom = IsCustomTyped(el);
        if (custom)
        {
            _elementUses.Add((el.Name.NamespaceName, el.Name.LocalName,
                              new SourceLocation(Rel(file), LineOf(el)), chainHere));
            _typeStack.Add((el.Name.NamespaceName, el.Name.LocalName));
        }

        try
        {
            CollectRefs(el, file, chainHere, ignorable, suppressed);

            string? pendingIgnore = null;
            foreach (var node in el.Nodes())
            {
                if (node is XComment c)
                {
                    pendingIgnore = ParseIgnore(c, file) ?? pendingIgnore;
                    continue;
                }

                if (node is not XElement child) continue;
                if (ReferenceEquals(child, FindResourcesProperty(el))) { pendingIgnore = null; continue; }

                WalkElement(child, file, chainHere, ignorable, suppressed ?? pendingIgnore);
                pendingIgnore = null;
            }
        }
        finally
        {
            if (custom) _typeStack.RemoveAt(_typeStack.Count - 1);
        }
    }

    /// <summary>框架自己的命名空間。其他命名空間的元素（clr-namespace:、using:、函式庫的 URL）可能是樹裡的類別。</summary>
    private static readonly HashSet<string> FrameworkNamespaces = new(StringComparer.Ordinal)
    {
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation",
        "http://schemas.microsoft.com/winfx/2006/xaml",
        "http://schemas.microsoft.com/winfx/2009/xaml",
        "https://github.com/avaloniaui",
        "http://schemas.microsoft.com/dotnet/2021/maui",
        "http://xamarin.com/schemas/2014/forms",
        "http://schemas.openxmlformats.org/markup-compatibility/2006",
        "http://schemas.microsoft.com/expression/blend/2008",
    };

    private static bool IsCustomTyped(XElement el) =>
        el.Name.NamespaceName.Length > 0
        && !el.Name.LocalName.Contains('.')
        && !FrameworkNamespaces.Contains(el.Name.NamespaceName);

    /// <summary>讀一個 &lt;ResourceDictionary&gt; 元素：Source、MergedDictionaries、ThemeDictionaries、以及自己的鍵。</summary>
    private void ReadDictionaryElement(XElement rdEl, ResourceDictionaryNode dict,
                                       IReadOnlyList<int> chain, HashSet<string> ignorable, string? suppressed)
    {
        // <ResourceDictionary Source="…"/> 當成「把那本合併進來」
        if (rdEl.Attribute("Source")?.Value is { Length: > 0 } ownSource)
            AddMerged(dict, ownSource, dict.File, LineOf(rdEl));

        List<XElement>? keyed = null;
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
                        {
                            // Uno 的主題物件用屬性帶進覆寫字典：
                            //   <MaterialToolkitTheme ColorOverrideSource="ms-appx:///Styles/ColorPaletteOverride.xaml"
                            //                         FontOverrideSource="ms-appx:///Styles/MaterialFontsOverride.xaml"/>
                            // 覆寫的值蓋過主題，所以排在主題前面（WinUI 先宣告的贏）。
                            // ⚠ 2026-11-15 第七輪 Uno Chefs：舊版把這兩本報成孤兒，裡面的 ChefsLogoWithIcon 等鍵變成 external。
                            AddAttributeSources(m, dict);
                            // 行內合併：<ResourceDictionary> 直接寫在 MergedDictionaries 裡
                            InlineMerged(m, dict, chain, ignorable, suppressed);
                        }
                    }
                    break;

                case "ResourceDictionary.ThemeDictionaries":
                {
                    var group = child.Elements().Where(t => !IsIgnorable(t, ignorable)).ToList();
                    var groupId = _nextThemeGroupId++;

                    foreach (var t in group)
                    {
                        var themeKey = XamlAttr(t, "Key")?.Value;
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
                    if (child.Name.LocalName == "ResourceDictionary")
                    {
                        if (XamlAttr(child, "Key") is { } k && !k.Value.TrimStart().StartsWith('{'))
                        {
                            keyed ??= [];
                            keyed.Add(child);
                            break;
                        }

                        // ⚠ .NET MAUI：沒有 x:Key 的 <ResourceDictionary Source="…"/> 直接放在字典裡，
                        //   等於合併（Xamarin.Forms 3.0 起的簡寫）。WPF 不允許這種寫法，所以照合併讀不會誤判。
                        //   2026-11-15 實測 dotnet-podcasts：整份主題這樣載入，舊版把它當成一個沒有鍵的項目 ——
                        //   83 筆 out-of-scope、8 筆假 unused、主題檔本身被報成孤兒。
                        if (XamlAttr(child, "Name") is null)
                        {
                            InlineMerged(child, dict, chain, ignorable, suppressed);
                            break;
                        }
                    }
                    ReadResourceEntry(child, dict, chain, ignorable, suppressed);
                    break;
            }
        }

        // 有 x:Key 的子字典（<ResourceDictionary x:Key="Compact">…）：程式在執行期挑一本合併，
        // 跟 ThemeDictionaries 同樣是互斥的一組。
        // ⚠ 2026-11-15 實測 SukiUI：Normal／Compact／SuperCompact／Big 四本各定義一份
        //   SmallCornerRadius、MediumCornerRadius，舊版只把外層的鍵（"Compact"）當成一個資源，
        //   裡面的鍵全部看不到 —— 41 筆假懸空。
        if (keyed is not null)
        {
            var groupId = _nextThemeGroupId++;
            foreach (var t in keyed)
            {
                var name = XamlAttr(t, "Key")!.Value.Trim();
                var sub = NewDictionary(dict.File, $"ResourceDictionary[{name}]", LineOf(t),
                                        standalone: false, appScope: false);
                sub.ThemeGroupId = groupId;
                ReadDictionaryElement(t, sub, Prepend(sub.Id, chain), ignorable, suppressed);
                dict.Merged.Add(new MergedReference
                {
                    RawSource = $"ResourceDictionary[{name}] (selected at runtime)",
                    Location = new SourceLocation(Rel(dict.File), LineOf(t)),
                    ResolvedDictionaryId = sub.Id,
                });
                KeyedDictionaries++;
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
            LinkByClassLater(m, parent.Merged[^1]);
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
        var keyAttr = XamlAttr(el, "Key") ?? XamlAttr(el, "Name");
        if (keyAttr is not null)
        {
            var raw = keyAttr.Value.Trim();

            // x:Key="{x:Static local:Keys.A}"／"{ComponentResourceKey …}"：換成可比對的身分（見 MarkupKeys）。
            // x:Key="{x:Type Button}" 之類（隱含樣式的顯式寫法）換不出來，照舊不是我們的單位。
            MarkupKeys.Canonical? markup = null;
            if (raw.StartsWith('{'))
            {
                markup = MarkupKeys.From(raw, p => NamespaceOf(el, p));
                if (markup is { } m) raw = m.Key;
            }

            if (!raw.StartsWith('{') || markup is not null)
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
                    IsMarkupKey = markup is not null,
                    IsFrameworkKey = markup?.Framework ?? false,
                    MemberName = markup?.Member,
                    MarkupType = markup?.TypeName,
                    MarkupTypeNamespace = markup?.TypeNamespace,
                    Location = new SourceLocation(Rel(dict.File), LineOf(el)),
                    DictionaryId = dict.Id,
                    ElementName = el.Name.LocalName,
                    AliasTarget = alias,
                    FieldName = XamlAttr(el, "Name")?.Value?.Trim() is { Length: > 0 } fieldName ? fieldName : null,
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
                        Owner = def,
                    });

                // 這個資源整棵子樹裡的引用，都屬於它（巢狀的有鍵資源會再往內換）
                var outer = _owner;
                _owner = def;
                try { WalkElement(el, dict.File, chain, ignorable, suppressed); }
                finally { _owner = outer; }
                return;
            }
        }

        WalkElement(el, dict.File, chain, ignorable, suppressed);
    }

    /// <summary>目前走訪位置所在的最內層「有鍵的資源」。見 KeyReference.Owner。</summary>
    private KeyDefinition? _owner;

    // ── 引用蒐集 ───────────────────────────────────────────────────────────

    private void CollectRefs(XElement el, string file, IReadOnlyList<int> chain,
                             HashSet<string> ignorable, string? suppressed)
    {
        // 屬性元素形式：<StaticResource ResourceKey="K"/>（沒有 x:Key 的那種，有的話已在上面當別名處理）
        if (el.Name.LocalName is "StaticResource" or "DynamicResource" or "ThemeResource"
            && XamlAttr(el, "Key") is null && XamlAttr(el, "Name") is null
            && el.Attribute("ResourceKey")?.Value is { Length: > 0 } rk)
        {
            // ⚠ ResourceKey 的值也可以是 markup extension：
            //    <StaticResource ResourceKey="{x:Static SystemColors.HighlightColorKey}"/>
            //    舊版把整串當成鍵名,於是報出 10 筆叫「{x:Static SystemColors.…}」的假懸空
            //    （實測 ModernWpf 的 HighContrast.xaml）。
            MarkupKeys.Canonical? markup = null;
            if (rk.TrimStart().StartsWith('{'))
            {
                markup = MarkupKeys.From(rk, p => NamespaceOf(el, p));
                if (markup is null)
                {
                    _unresolvedSites.Add(new SourceLocation(Rel(file), LineOf(el)));
                    return;
                }
            }

            _references.Add(new KeyReference
            {
                IsMarkupKey = markup is not null,
                IsFrameworkKey = markup?.Framework ?? false,
                Key = markup?.Key ?? rk.Trim(),
                Location = new SourceLocation(Rel(file), LineOf(el)),
                Kind = el.Name.LocalName switch
                {
                    "DynamicResource" => RefKind.Dynamic,
                    "ThemeResource" => RefKind.Theme,
                    _ => RefKind.Static,
                },
                ScopeChain = chain,
                SuppressedReason = suppressed,
                Owner = _owner,
            });
            RememberEnclosingTypes(_references[^1]);
        }

        foreach (var attr in el.Attributes())
        {
            if (attr.IsNamespaceDeclaration) continue;
            if (IsIgnorableName(attr.Name, ignorable)) continue;
            // x:Key 是定義不是引用；Source 由合併解析處理
            if (IsXamlDirective(attr.Name, "Key")) continue;

            foreach (var r in MarkupExtensions.Scan(attr.Value))
                AddExtensionRef(r, el, file, LineOf(attr), chain, suppressed);
        }

        // 文字節點也可能整段是 markup extension（<Setter.Value>{StaticResource K}</Setter.Value>）
        foreach (var text in el.Nodes().OfType<XText>())
        {
            var v = text.Value;
            if (v.Length == 0 || !v.Contains('{')) continue;
            foreach (var r in MarkupExtensions.Scan(v))
                AddExtensionRef(r, el, file, LineOf(el), chain, suppressed);
        }
    }

    private void AddExtensionRef(ExtensionRef r, XElement el, string file, int line,
                                 IReadOnlyList<int> chain, string? suppressed)
    {
        MarkupKeys.Canonical? markup = null;
        var key = r.Key;
        if (key is null && r.Nested is { } nested && MarkupKeys.From(nested, p => NamespaceOf(el, p)) is { } m)
        {
            markup = m;
            key = m.Key;
        }

        if (key is null)
        {
            // {StaticResource {x:Type Button}} 之類 —— 鍵不是字串，也換不出身分。
            // 不報任何東西，但要讓 summary 知道這裡有個看不懂的引用。
            _unresolvedSites.Add(new SourceLocation(Rel(file), line));
            return;
        }

        _references.Add(new KeyReference
        {
            IsMarkupKey = markup is not null,
            IsFrameworkKey = markup?.Framework ?? false,
            Key = key,
            Location = new SourceLocation(Rel(file), line),
            Kind = r.Kind,
            ScopeChain = chain,
            SuppressedReason = suppressed,
            Owner = _owner,
        });
        RememberEnclosingTypes(_references[^1]);
    }

    private void RememberEnclosingTypes(KeyReference r)
    {
        if (_typeStack.Count == 0) return;
        var arr = _typeStack.ToArray();
        Array.Reverse(arr);                       // 由內而外
        _enclosingOfRef[r] = arr;
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

        foreach (var (mref, ns, name) in _pendingClassLinks)
        {
            foreach (var clr in ClrNamespacesOf(ns))
            {
                if (clr.Length == 0 || !_dictionaryByClass.TryGetValue($"{clr}.{name}", out var id)) continue;
                mref.ResolvedDictionaryId = id;
                mref.ExternalReason = null;
                ClassBackedDictionariesLinked++;
                break;
            }
        }
    }

    // ── 自訂控制項：它被放在哪裡、它自己帶的資源 ────────────────────────────

    private readonly Dictionary<string, string> _classOfFile = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _rootResourcesOfFile = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Ns, string Name, SourceLocation Location, IReadOnlyList<int> Chain)> _elementUses = [];
    private readonly List<(string Ns, string Name)> _typeStack = [];
    private readonly Dictionary<KeyReference, (string Ns, string Name)[]> _enclosingOfRef =
        new(ReferenceEqualityComparer.Instance);

    private readonly HashSet<string> _xamarinFormsFiles = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>根元素是 Xamarin.Forms 命名空間的檔（絕對路徑）。</summary>
    public IReadOnlySet<string> XamarinFormsFiles => _xamarinFormsFiles;

    /// <summary>有 x:Class 的字典檔：類別全名 → 字典 Id。</summary>
    public IReadOnlyDictionary<string, int> DictionaryByClass => _dictionaryByClass;

    /// <summary>根元素的 x:Class：檔案（絕對路徑） → 類別全名。</summary>
    public IReadOnlyDictionary<string, string> ClassOfFile => _classOfFile;

    /// <summary>類別全名 → 它在 XAML 裡被當成元素放置的每一處（含當時的作用域鏈）。<see cref="ResolveElementTypes"/> 之後才有。</summary>
    public Dictionary<string, List<ElementUse>> UsesOfClass { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// 把元素名（命名空間＋名字）對回樹裡的 x:Class。要在 <see cref="XmlnsMap"/> 填好之後呼叫。
    /// 同時替每個寫在自訂元素裡面的引用，記下那些元素的類別自己的 &lt;X.Resources&gt;（由內而外）。
    /// </summary>
    public void ResolveElementTypes()
    {
        var filesOfClass = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (file, cls) in _classOfFile)
        {
            if (!filesOfClass.TryGetValue(cls, out var list)) filesOfClass[cls] = list = [];
            list.Add(file);
        }

        string? ClassOf(string ns, string name)
        {
            foreach (var clr in ClrNamespacesOf(ns))
            {
                var full = clr.Length == 0 ? name : $"{clr}.{name}";
                if (filesOfClass.ContainsKey(full)) return full;
            }
            return null;
        }

        foreach (var (ns, name, loc, chain) in _elementUses)
            if (ClassOf(ns, name) is { } cls)
            {
                if (!UsesOfClass.TryGetValue(cls, out var list)) UsesOfClass[cls] = list = [];
                list.Add(new ElementUse(cls, loc, chain));
            }

        foreach (var (r, stack) in _enclosingOfRef)
        {
            List<int>? ids = null;
            foreach (var (ns, name) in stack)
                if (ClassOf(ns, name) is { } cls)
                    foreach (var f in filesOfClass[cls])
                        if (_rootResourcesOfFile.TryGetValue(f, out var id)) (ids ??= []).Add(id);
            if (ids is not null) r.EnclosingClassScopes = ids;
        }
    }

    /// <summary>以元素寫法（&lt;suki:SukiTheme/&gt;）載入、而類別就在這棵樹裡的字典數。</summary>
    public int ClassBackedDictionariesLinked { get; private set; }

    /// <summary>字典裡有 x:Key 的 &lt;ResourceDictionary&gt;（執行期由程式挑一本合併）的個數。</summary>
    public int KeyedDictionaries { get; private set; }

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
            // 根是 <Styles> 時，<Styles.Resources> 這類屬性元素也是直接子層 —— 不是主題物件
            if (e.Name.LocalName.Contains('.')) continue;

            if (e.Name.LocalName is "StyleInclude" or "StyleIncludes")
            {
                if (e.Attribute("Source")?.Value is { Length: > 0 } src)
                    AddMerged(dict, src, dict.File, LineOf(e));
                continue;
            }

            if (e.Name.LocalName is "Style" or "Styles") continue;

            // 自訂的 include 元素（StyleInclude／ResourceInclude 的子類別，內容全部來自 *Source 屬性）：讀完就好，
            // 不再另外記成一本看不到的字典 —— 否則它會一直拉低覆蓋率，external 訊息也會指錯對象。
            if (AddAttributeSources(e, dict) > 0
                && (e.Name.LocalName.Contains("StyleInclude", StringComparison.Ordinal)
                    || e.Name.LocalName.Contains("ResourceInclude", StringComparison.Ordinal)))
                continue;

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
            LinkByClassLater(e, dict.Merged[^1]);
        }
    }

    /// <summary>
    /// 主題物件、自訂 include 元素用屬性帶進來的字典：屬性名以 Source 結尾（不含 Source 本身，那由呼叫端處理）。
    /// </summary>
    /// <remarks>
    /// - Uno：<c>&lt;MaterialToolkitTheme ColorOverrideSource="…" FontOverrideSource="…"/&gt;</c>（規則 63）。覆寫蓋過主題，排在主題前面。
    /// - Avalonia 自訂的 <c>&lt;StyleIncludeColorAware DarkSource="…" LightSource="…"/&gt;</c>（2026-11-15 第八輪 WoWDatabaseEditor，46 筆 external）：
    ///   前綴是 Dark／Light／HighContrast 的是**執行期二選一**，像 ThemeDictionaries 一樣做成同一群組的兩側，
    ///   兩本的同名鍵不會被判成互相覆蓋。
    /// 只收非命名空間的屬性、值不是 markup extension 的。
    /// </remarks>
    private int AddAttributeSources(XElement e, ResourceDictionaryNode dict)
    {
        int? groupId = null;
        var added = 0;
        foreach (var a in e.Attributes())
        {
            if (a.Name.Namespace != XNamespace.None) continue;
            var name = a.Name.LocalName;
            if (name == "Source" || !name.EndsWith("Source", StringComparison.Ordinal)) continue;
            if (a.Value.Trim() is not { Length: > 0 } src || src.StartsWith('{')) continue;

            var sideName = name[..^"Source".Length];
            var side = SideOf(sideName);
            if (sideName is "Dark" or "Light" or "HighContrast")
            {
                groupId ??= _nextThemeGroupId++;
                var sideDict = NewDictionary(dict.File, $"{e.Name.LocalName}.{name}", LineOf(e), standalone: false, appScope: false);
                sideDict.Side = side;
                sideDict.ThemeGroupId = groupId;
                AddMerged(sideDict, src, dict.File, LineOf(e));
                dict.Merged.Add(new MergedReference
                {
                    RawSource = $"{e.Name.LocalName}.{name}",
                    Location = new SourceLocation(Rel(dict.File), LineOf(e)),
                    ResolvedDictionaryId = sideDict.Id,
                });
                added++;
            }
            else if (name.EndsWith("OverrideSource", StringComparison.Ordinal))
            {
                AddMerged(dict, src, dict.File, LineOf(e));
                added++;
            }
        }
        return added;
    }

    /// <summary>根元素有 x:Class 的字典／樣式檔：記下類別全名，讓 &lt;local:SukiTheme/&gt; 這種元素接得回來。</summary>
    private void RegisterClass(XElement root, ResourceDictionaryNode dict)
    {
        if (XamlAttr(root, "Class")?.Value?.Trim() is { Length: > 0 } cls)
            _dictionaryByClass[cls] = dict.Id;
    }

    /// <summary>
    /// 主題物件或 ResourceDictionary 子類別寫成元素（&lt;suki:SukiTheme/&gt;、&lt;local:DefaultTheme/&gt;）時，
    /// 它的類別如果就是掃描範圍裡某個檔的 x:Class，那本字典其實看得到 —— 不是 external。
    /// ⚠ 2026-11-15 實測 SukiUI：示範 App 用 &lt;suki:SukiTheme/&gt; 載入主題，主題檔 SukiTheme.axaml
    ///   就在同一棵樹裡（x:Class="SukiUI.SukiTheme"），舊版一律當成函式庫的、看不到。
    /// 只接得到 clr-namespace:／using: 的命名空間；框架預設命名空間的（&lt;FluentTheme/&gt;）維持 external。
    /// </summary>
    private void LinkByClassLater(XElement e, MergedReference mref)
    {
        _pendingClassLinks.Add((mref, e.Name.NamespaceName, e.Name.LocalName));
    }

    /// <summary>XmlnsDefinition 對照：XML 命名空間 URL → CLR 命名空間（可多個）。由 Analyzer 從 C# 讀來。
    /// ⚠ 2026-11-15 實測 SukiUI：示範 App 寫 xmlns:suki="https://github.com/kikipoulet/SukiUI"，
    ///   靠 [assembly: XmlnsDefinition(…)] 對到 SukiUI 命名空間 —— 不認這個，&lt;suki:SukiTheme/&gt; 接不回主題檔。</summary>
    public Dictionary<string, List<string>> XmlnsMap { get; } = new(StringComparer.Ordinal);

    private IEnumerable<string> ClrNamespacesOf(string ns)
    {
        if (ns.StartsWith("clr-namespace:", StringComparison.Ordinal))
            yield return ns["clr-namespace:".Length..].Split(';')[0].Trim();
        else if (ns.StartsWith("using:", StringComparison.Ordinal))
            yield return ns["using:".Length..].Trim();
        else if (XmlnsMap.TryGetValue(ns, out var list))
            foreach (var n in list) yield return n;
    }

    private readonly Dictionary<string, int> _dictionaryByClass = new(StringComparer.Ordinal);
    private readonly List<(MergedReference Ref, string Ns, string Name)> _pendingClassLinks = [];

    /// <summary>元素看得到的 xmlns 前綴 → 命名空間 URI。空字串是預設命名空間。</summary>
    private static string? NamespaceOf(XElement el, string prefix)
    {
        if (prefix.Length == 0) return el.GetDefaultNamespace().NamespaceName is { Length: > 0 } d ? d : null;
        return el.GetNamespaceOfPrefix(prefix)?.NamespaceName;
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

    /// <summary>
    /// 解析 <c>&lt;!-- xamlkeys-ignore: 理由 --&gt;</c>。
    ///
    /// ⚠ **理由必填**。沒寫理由的 ignore 視為無效（不壓制任何東西），並記進
    /// <see cref="InvalidIgnores"/> 讓報告講出來 ——
    /// 否則 <c>&lt;!-- xamlkeys-ignore --&gt;</c> 就是一條沒有任何交代的靜默壓制通道，
    /// 而這個工具的第二條憲法正是「讓結果變寬鬆的每一條路徑都要留下痕跡」。
    /// </summary>
    private string? ParseIgnore(XComment c, string file)
    {
        var t = c.Value.Trim();
        if (!t.StartsWith(IgnoreMarker, StringComparison.OrdinalIgnoreCase)) return null;

        var rest = t[IgnoreMarker.Length..].TrimStart();
        if (rest.StartsWith(':')) rest = rest[1..].Trim();

        if (rest.Length == 0)
        {
            _invalidIgnores.Add(new SourceLocation(Rel(file), LineOf(c)));
            return null;
        }

        return rest;
    }

    private readonly List<SourceLocation> _invalidIgnores = [];

    /// <summary>沒有寫理由、因此被當成無效的 ignore 註解。</summary>
    public IReadOnlyList<SourceLocation> InvalidIgnores => _invalidIgnores;

    /// <summary>把直接子層的 ignore 註解套到緊接著的資源項目上。</summary>
    private void ApplyIgnoreComments(XElement container, ResourceDictionaryNode dict)
    {
        string? pending = null;
        foreach (var node in container.Nodes())
        {
            if (node is XComment c)
            {
                pending = ParseIgnore(c, dict.File) ?? pending;
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
                                     && !IsXamlDirective(a.Name, "Key") && !IsXamlDirective(a.Name, "Name"))
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
