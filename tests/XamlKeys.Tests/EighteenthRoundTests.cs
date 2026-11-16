using XamlKeys.Core;

namespace XamlKeys.Tests;

/// <summary>
/// 第十八輪（2026-11-16）：程式寫內容的字典（Unigram 的 &lt;common:Theme/&gt;）。
/// 歸因在 docs/實查歸因-2026-11-14.md 的「第十八輪」。
/// </summary>
public class EighteenthRoundTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("xamlkeys-round18");
    public void Dispose() => _dir.Delete(true);

    private AnalysisResult Analyze(Dictionary<string, string> files)
    {
        foreach (var (rel, content) in files)
        {
            var path = Path.Combine(_dir.FullName, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
        return Analyzer.Run(_dir.FullName, new Config());
    }

    private const string Xaml = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";
    private const string WinUiExe = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType><UseWinUI>true</UseWinUI></PropertyGroup></Project>";

    private static List<string> Keys(AnalysisResult r, FindingKind k) =>
        r.Findings.Where(f => f.Kind == k).Select(f => f.Key).Distinct().Order(StringComparer.Ordinal).ToList();

    // Unigram：App.xaml 合併 <common:Theme/>，Theme 是樹裡的 C# 類別，建構子用 this["…"] = 寫鍵，
    // 也用 prefix + "ButtonForeground" 組鍵；InstantPage 的 ChannelItemTemplate 定義被註解掉了，樣式還在用。
    private Dictionary<string, string> UnigramLike(string page, string theme = "") => new()
    {
        ["Telegram/Telegram.csproj"] = WinUiExe,
        ["Telegram/App.xaml"] = $"<Application {Xaml} xmlns:common=\"using:Telegram.Common\"><Application.Resources><ResourceDictionary>" +
                                "<ResourceDictionary.MergedDictionaries><common:Theme/></ResourceDictionary.MergedDictionaries>" +
                                "</ResourceDictionary></Application.Resources></Application>",
        ["Telegram/Common/Theme.cs"] = "namespace Telegram.Common { public partial class Theme : ResourceDictionary { public Theme() { " +
                                       "this[\"EmojiThemeFontFamily\"] = new FontFamily(\"Segoe UI\"); " +
                                       "AddPalette(\"Danger\"); } " +
                                       "void AddPalette(string prefix) { AddOrUpdate(prefix + \"ButtonForeground\"); } " + theme + " } }",
        ["Telegram/Views/InstantPage.xaml"] = page,
    };

    [Fact]
    public void A_key_no_code_in_the_tree_names_is_not_hidden_behind_a_code_built_dictionary()
    {
        var r = Analyze(UnigramLike($"<Page {Xaml}><Page.Resources><Style x:Key=\"ChannelBlockStyle\" TargetType=\"Button\">" +
                                    "<Setter Property=\"ContentTemplate\" Value=\"{StaticResource ChannelItemTemplate}\"/></Style></Page.Resources>" +
                                    "<Button Style=\"{StaticResource ChannelBlockStyle}\"/></Page>"));

        Assert.Equal(["ChannelItemTemplate"], Keys(r, FindingKind.DanglingStatic));
        Assert.Empty(Keys(r, FindingKind.External));
        Assert.Equal(1, r.KeysNoCodeBuiltDictionaryNames);
    }

    [Fact]
    public void Keys_the_code_built_dictionary_can_write_stay_external()
    {
        // 字串寫的（EmojiThemeFontFamily）、組出來的（Danger + ButtonForeground）
        var r = Analyze(UnigramLike($"<Page {Xaml}><TextBlock FontFamily=\"{{ThemeResource EmojiThemeFontFamily}}\" " +
                                    "Foreground=\"{ThemeResource DangerButtonForeground}\"/></Page>"));

        Assert.Equal(["DangerButtonForeground", "EmojiThemeFontFamily"], Keys(r, FindingKind.External));
        Assert.Empty(Keys(r, FindingKind.DanglingStatic));
        Assert.Equal(0, r.KeysNoCodeBuiltDictionaryNames);
    }

    [Fact]
    public void A_dictionary_merged_next_to_the_code_built_one_is_not_shielded_by_it_either()
    {
        // 「合併我的那本也合併了看不到的字典」那條路：Styles.xaml 跟 <common:Theme/> 一起被 App 合併
        var files = UnigramLike($"<Page {Xaml}><Button Style=\"{{StaticResource ChannelBlockStyle}}\"/></Page>");
        files["Telegram/App.xaml"] = $"<Application {Xaml} xmlns:common=\"using:Telegram.Common\"><Application.Resources><ResourceDictionary>" +
                                     "<ResourceDictionary.MergedDictionaries><common:Theme/><ResourceDictionary Source=\"/Themes/Styles.xaml\"/></ResourceDictionary.MergedDictionaries>" +
                                     "</ResourceDictionary></Application.Resources></Application>";
        files["Telegram/Themes/Styles.xaml"] = $"<ResourceDictionary {Xaml}><Style x:Key=\"ChannelBlockStyle\" TargetType=\"Button\">" +
                                               "<Setter Property=\"ContentTemplate\" Value=\"{StaticResource ChannelItemTemplate}\"/></Style></ResourceDictionary>";
        var r = Analyze(files);

        Assert.Equal(["ChannelItemTemplate"], Keys(r, FindingKind.DanglingStatic));
    }

    [Fact]
    public void A_key_defined_in_some_xaml_the_code_built_dictionary_may_load_stays_external()
    {
        // Unigram 的 Theme 在程式裡合併樹裡的字典（ScrollBar_themeresources.xaml）。樹裡某本字典定義了這個鍵、
        // 只是靜態接不上 —— 程式字典可能把它帶進來，不能說「沒有」
        var files = UnigramLike($"<Page {Xaml}><ScrollBar Background=\"{{ThemeResource TelegramScrollThumbFill}}\"/></Page>");
        files["Telegram/Themes/Scrolls.xaml"] = $"<ResourceDictionary {Xaml}><SolidColorBrush x:Key=\"TelegramScrollThumbFill\" Color=\"Gray\"/></ResourceDictionary>";
        var r = Analyze(files);

        Assert.Equal(["TelegramScrollThumbFill"], Keys(r, FindingKind.External));
    }

    [Fact]
    public void A_code_built_dictionary_that_loads_xaml_from_another_assembly_still_shields_every_key()
    {
        // ModernWpf 的 FluentControlsResources：程式裡合併 /PresentationFramework.Fluent;component/Themes/Fluent.xaml（樹外的組件），
        // Gallery 用的 KeyboardFocusBorderColorBrush 就在那本裡
        var r = Analyze(new()
        {
            ["App/App.csproj"] = WinUiExe,
            ["App/App.xaml"] = $"<Application {Xaml} xmlns:ui=\"using:ModernWpf.Controls\"><Application.Resources><ResourceDictionary>" +
                               "<ResourceDictionary.MergedDictionaries><ui:FluentControlsResources/></ResourceDictionary.MergedDictionaries>" +
                               "</ResourceDictionary></Application.Resources></Application>",
            ["App/Controls/FluentControlsResources.cs"] = "namespace ModernWpf.Controls { public class FluentControlsResources : ResourceDictionary { " +
                "public FluentControlsResources() { MergedDictionaries.Add(new ResourceDictionary { Source = new System.Uri(" +
                "\"pack://application:,,,/PresentationFramework.Fluent;component/Themes/Fluent.xaml\") }); } } }",
            ["App/IconsPage.xaml"] = $"<Page {Xaml}><Rectangle Stroke=\"{{ThemeResource KeyboardFocusBorderColorBrush}}\"/></Page>",
        });

        Assert.Equal(["KeyboardFocusBorderColorBrush"], Keys(r, FindingKind.External));
        Assert.Equal(0, r.KeysNoCodeBuiltDictionaryNames);
    }

    [Fact]
    public void A_library_dictionary_next_to_the_code_built_one_still_shields_every_key()
    {
        // 同一個作用域還合併了函式庫的字典：鍵可能在那裡，不能因為程式字典寫不出來就說「沒有」
        var files = UnigramLike($"<Page {Xaml}><Button ContentTemplate=\"{{StaticResource ChannelItemTemplate}}\"/></Page>");
        files["Telegram/App.xaml"] = $"<Application {Xaml} xmlns:common=\"using:Telegram.Common\" xmlns:lib=\"using:Some.Library\"><Application.Resources><ResourceDictionary>" +
                                     "<ResourceDictionary.MergedDictionaries><common:Theme/><lib:LibraryTheme/></ResourceDictionary.MergedDictionaries>" +
                                     "</ResourceDictionary></Application.Resources></Application>";
        var r = Analyze(files);

        Assert.Equal(["ChannelItemTemplate"], Keys(r, FindingKind.External));
    }

    [Fact]
    public void A_dictionary_class_whose_base_is_a_library_dictionary_still_shields_every_key()
    {
        // 基底是函式庫的字典類別（不是 ResourceDictionary 本身）：基底自己可能帶鍵，跟規則 92 同理
        var files = UnigramLike($"<Page {Xaml}><Button ContentTemplate=\"{{StaticResource ChannelItemTemplate}}\"/></Page>");
        files["Telegram/Common/Theme.cs"] = "namespace Telegram.Common { public partial class Theme : Some.Library.ThemeDictionary { public Theme() { " +
                                            "this[\"EmojiThemeFontFamily\"] = new FontFamily(\"Segoe UI\"); } } }";
        var r = Analyze(files);

        Assert.Equal(["ChannelItemTemplate"], Keys(r, FindingKind.External));
    }

    [Fact]
    public void A_dictionary_class_that_is_not_in_the_tree_still_shields_every_key()
    {
        // 函式庫的 <controls:XamlControlsResources/> 之類 —— 類別不在樹裡，照舊算看不到
        var r = Analyze(new()
        {
            ["App/App.csproj"] = WinUiExe,
            ["App/App.xaml"] = $"<Application {Xaml} xmlns:lib=\"using:Some.Library\"><Application.Resources><ResourceDictionary>" +
                               "<ResourceDictionary.MergedDictionaries><lib:LibraryTheme/></ResourceDictionary.MergedDictionaries>" +
                               "</ResourceDictionary></Application.Resources></Application>",
            ["App/MainPage.xaml"] = $"<Page {Xaml}><Button ContentTemplate=\"{{StaticResource ChannelItemTemplate}}\"/></Page>",
        });

        Assert.Equal(["ChannelItemTemplate"], Keys(r, FindingKind.External));
    }
}
