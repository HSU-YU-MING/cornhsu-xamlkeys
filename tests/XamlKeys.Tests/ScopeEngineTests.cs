using XamlKeys.Core;

namespace XamlKeys.Tests;

/// <summary>
/// 合併順序。這是整個工具的核心,而且 **WPF 與 WinUI 是相反的** ——
/// 搞錯方向的話,shadowed 會把贏家與輸家對調,使用者照著改就是改錯那一個。
/// </summary>
public class ScopeEngineTests
{
    private static ResourceDictionaryNode Dict(int id, params string[] keys)
    {
        var d = new ResourceDictionaryNode
        {
            Id = id,
            File = $"d{id}.xaml",
            Location = new SourceLocation($"d{id}.xaml", 1),
            IsStandaloneFile = true,
        };

        var line = 1;
        foreach (var k in keys)
            d.OwnKeys.Add(new KeyDefinition
            {
                Key = k,
                Location = new SourceLocation($"d{id}.xaml", line++),
                DictionaryId = id,
            });

        return d;
    }

    private static void Merge(ResourceDictionaryNode parent, params ResourceDictionaryNode[] children)
    {
        foreach (var c in children)
            parent.Merged.Add(new MergedReference
            {
                RawSource = c.File,
                Location = new SourceLocation(parent.File, 1),
                ResolvedDictionaryId = c.Id,
            });
    }

    [Fact]
    public void Wpf_resolves_to_the_last_merged_dictionary()
    {
        // 微軟文件:the resource returned will come from the last dictionary
        // found sequentially in the MergedDictionaries collection
        var first = Dict(1, "K");
        var last = Dict(2, "K");
        var root = Dict(0);
        Merge(root, first, last);

        var engine = new ScopeEngine([root, first, last], XamlDialect.Wpf);
        var hit = engine.Lookup("K", [0], ThemeSide.Neutral);

        Assert.Equal(LookupStatus.Found, hit.Status);
        Assert.Equal("d2.xaml", hit.Definition!.Location.File);
    }

    [Fact]
    public void WinUi_resolves_in_the_inverse_order()
    {
        var first = Dict(1, "K");
        var last = Dict(2, "K");
        var root = Dict(0);
        Merge(root, first, last);

        var engine = new ScopeEngine([root, first, last], XamlDialect.WinUi);
        var hit = engine.Lookup("K", [0], ThemeSide.Neutral);

        Assert.Equal("d1.xaml", hit.Definition!.Location.File);
    }

    [Fact]
    public void The_dictionary_itself_beats_everything_it_merges()
    {
        var merged = Dict(1, "K");
        var root = Dict(0, "K");
        Merge(root, merged);

        var engine = new ScopeEngine([root, merged], XamlDialect.Wpf);
        Assert.Equal("d0.xaml", engine.Lookup("K", [0], ThemeSide.Neutral).Definition!.Location.File);
    }

    [Fact]
    public void An_unreadable_merged_dictionary_makes_the_answer_Unknown_not_NotFound()
    {
        // ⚠ 這是整個工具最重要的一條不變量。把 external 當成 NotFound,
        //   每一個用第三方主題庫的專案都會在第一天看到滿螢幕的假懸空。
        var root = Dict(0, "Present");
        root.Merged.Add(new MergedReference
        {
            RawSource = "pack://application:,,,/ThirdParty;component/Themes/Generic.xaml",
            Location = new SourceLocation("d0.xaml", 1),
            ResolvedDictionaryId = null,
            ExternalReason = "not in the scanned tree",
        });

        var engine = new ScopeEngine([root], XamlDialect.Wpf);
        var miss = engine.Lookup("Absent", [0], ThemeSide.Neutral);

        Assert.Equal(LookupStatus.Unknown, miss.Status);
        Assert.Contains("ThirdParty", miss.ExternalSource);
    }

    [Fact]
    public void Without_any_external_dictionary_a_miss_is_NotFound()
    {
        var root = Dict(0, "Present");
        var engine = new ScopeEngine([root], XamlDialect.Wpf);
        Assert.Equal(LookupStatus.NotFound, engine.Lookup("Absent", [0], ThemeSide.Neutral).Status);
    }

    [Fact]
    public void The_application_scope_is_searched_even_when_it_is_not_in_the_chain()
    {
        // 掃描器建的鏈只含「這個檔裡的」字典。Application.Resources 在另一個檔,
        // 不補上去的話全域色票會被整批報成懸空。
        var app = new ResourceDictionaryNode
        {
            Id = 9,
            File = "App.xaml",
            Owner = "Application.Resources",
            Location = new SourceLocation("App.xaml", 1),
            IsApplicationScope = true,
        };
        app.OwnKeys.Add(new KeyDefinition { Key = "Global", Location = new SourceLocation("App.xaml", 2), DictionaryId = 9 });

        var local = Dict(0);
        var engine = new ScopeEngine([local, app], XamlDialect.Wpf);

        Assert.Equal(LookupStatus.Found, engine.Lookup("Global", [0], ThemeSide.Neutral).Status);
    }

    [Fact]
    public void A_cycle_between_dictionaries_terminates()
    {
        var a = Dict(0, "A");
        var b = Dict(1, "B");
        Merge(a, b);
        Merge(b, a);

        var engine = new ScopeEngine([a, b], XamlDialect.Wpf);
        Assert.Equal(LookupStatus.Found, engine.Lookup("B", [0], ThemeSide.Neutral).Status);
        Assert.Equal(LookupStatus.NotFound, engine.Lookup("Nope", [0], ThemeSide.Neutral).Status);
    }

    [Fact]
    public void Merging_the_same_dictionary_twice_is_not_reported_as_shadowing_itself()
    {
        var shared = Dict(1, "K");
        var root = Dict(0);
        Merge(root, shared, shared);

        var engine = new ScopeEngine([root, shared], XamlDialect.Wpf);
        Assert.Empty(engine.DuplicatesIn(0));
    }

    [Fact]
    public void Duplicates_name_the_winner_first()
    {
        var loser = Dict(1, "K");
        var winner = Dict(2, "K");
        var root = Dict(0);
        Merge(root, loser, winner);

        var engine = new ScopeEngine([root, loser, winner], XamlDialect.Wpf);
        var dup = Assert.Single(engine.DuplicatesIn(0));

        Assert.Equal("K", dup.Key);
        Assert.Equal("d2.xaml", dup.Winner.Location.File);
        Assert.Equal("d1.xaml", Assert.Single(dup.Losers).Location.File);
    }

    [Fact]
    public void Dark_and_light_definitions_of_one_key_are_not_shadowing()
    {
        // 雙主題的正常寫法:同一個鍵在兩側各定義一次。報成覆蓋就是整組假警報。
        var dark = Dict(1, "Fg");
        dark.Side = ThemeSide.Dark;
        var light = Dict(2, "Fg");
        light.Side = ThemeSide.Light;
        var root = Dict(0);
        Merge(root, dark, light);

        var engine = new ScopeEngine([root, dark, light], XamlDialect.Wpf,
                                     [ThemeSide.Dark, ThemeSide.Light]);

        Assert.Empty(engine.DuplicatesIn(0));
        Assert.Equal("d1.xaml", engine.Lookup("Fg", [0], ThemeSide.Dark).Definition!.Location.File);
        Assert.Equal("d2.xaml", engine.Lookup("Fg", [0], ThemeSide.Light).Definition!.Location.File);
    }

    [Fact]
    public void A_key_defined_only_on_the_dark_side_is_missing_on_the_light_side()
    {
        var dark = Dict(1, "DarkOnly");
        dark.Side = ThemeSide.Dark;
        var root = Dict(0);
        Merge(root, dark);

        var engine = new ScopeEngine([root, dark], XamlDialect.Wpf, [ThemeSide.Dark, ThemeSide.Light]);

        Assert.Equal(LookupStatus.Found, engine.Lookup("DarkOnly", [0], ThemeSide.Dark).Status);
        Assert.Equal(LookupStatus.NotFound, engine.Lookup("DarkOnly", [0], ThemeSide.Light).Status);
    }
}
