using XamlKeys.Core;

namespace XamlKeys.Tests;

/// <summary>
/// 2026-11-15：選用主題套件的鍵清單（Avalonia Fluent／Simple／ColorPicker／DataGrid／TreeDataGrid、
/// CommunityToolkit），以及 csproj 用 Link 從別處連結進來的 XAML。
/// 來源：第五輪語料剩下的 external（WalletWasabi 131、SukiUI 116）與 PowerToys 的 exit 3。
/// </summary>
public class ThemePackAndLinkTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("xamlkeys-packs");
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

    private const string AppProj = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup></Project>";
    private const string WinUiProj = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType><UseWinUI>true</UseWinUI></PropertyGroup></Project>";
    private const string Ava = "xmlns=\"https://github.com/avaloniaui\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";
    private const string Wpf = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";

    private static string AvaApp(string styles, string resources = "") => $"""
        <Application {Ava}>
          <Application.Resources>{resources}</Application.Resources>
          <Application.Styles>{styles}</Application.Styles>
        </Application>
        """;

    private static List<string> Keys(AnalysisResult r, FindingKind k) =>
        r.Findings.Where(f => f.Kind == k).Select(f => f.Key).Distinct().Order(StringComparer.Ordinal).ToList();

    private static string Win(string ns, string body) => $"<Window {ns}>{body}</Window>";

    // ── 選用主題套件 ───────────────────────────────────────────────────

    [Fact]
    public void A_key_of_a_merged_Avalonia_theme_is_framework_provided_and_the_theme_counts_as_resolved()
    {
        // WalletWasabi：ButtonForeground、ScrollBarButtonArrowIconFontSize 是 FluentTheme 的鍵
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["App.axaml"] = AvaApp("<FluentTheme />"),
            ["Main.axaml"] = Win(Ava, """<Button Foreground="{DynamicResource ButtonForeground}" Tag="{DynamicResource NotOnAnyList}" />"""),
        });

        Assert.DoesNotContain("ButtonForeground", Keys(r, FindingKind.External));
        Assert.Contains("NotOnAnyList", Keys(r, FindingKind.External));   // 清單外的照舊是「看不到」
        Assert.Contains("avalonia-fluent", r.ThemePacksInUse);
        Assert.Contains("<FluentTheme/>", r.KnownThemeSources);
        Assert.Empty(r.ExternalSources);
        Assert.Equal(100, r.Coverage);
    }

    [Fact]
    public void A_theme_list_only_applies_when_that_theme_is_merged()
    {
        // 用 SimpleTheme 的樹，Fluent 才有的鍵不算框架提供
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["App.axaml"] = AvaApp("<SimpleTheme />"),
            ["Main.axaml"] = Win(Ava, """<Button Foreground="{DynamicResource ScrollBarButtonArrowIconFontSize}" />"""),
        });

        Assert.Contains("ScrollBarButtonArrowIconFontSize", Keys(r, FindingKind.External));
        Assert.DoesNotContain("avalonia-fluent", r.ThemePacksInUse);
    }

    [Fact]
    public void Without_any_theme_an_Avalonia_key_name_in_a_WPF_app_is_dangling()
    {
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["App.xaml"] = $"<Application {Wpf}><Application.Resources><SolidColorBrush x:Key=\"Own\" Color=\"Red\" /></Application.Resources></Application>",
            ["Main.xaml"] = Win(Wpf, """<Border Background="{StaticResource Own}" BorderBrush="{StaticResource ButtonForeground}" />"""),
        });

        Assert.Equal(["ButtonForeground"], Keys(r, FindingKind.DanglingStatic));
        Assert.Empty(r.ThemePacksInUse);
    }

    [Fact]
    public void Redefining_a_key_of_a_merged_theme_is_not_unused()
    {
        // 輕量樣式覆寫：Avalonia 的樣板在執行期讀它
        var r = Analyze(new()
        {
            ["App.csproj"] = AppProj,
            ["App.axaml"] = AvaApp("<FluentTheme />", """<SolidColorBrush x:Key="ButtonForeground" Color="Red" />"""),
        });

        Assert.Empty(Keys(r, FindingKind.Unused));
    }

    [Fact]
    public void A_CommunityToolkit_lightweight_styling_key_is_not_unused_and_the_toolkit_counts_as_resolved()
    {
        // PowerToys：SettingsCardWrapThreshold 是 CommunityToolkit 的 SettingsCard 讀的鍵
        var r = Analyze(new()
        {
            ["App.csproj"] = WinUiProj,
            ["App.xaml"] = $"""
                <Application {Wpf}>
                  <Application.Resources><ResourceDictionary>
                    <ResourceDictionary.MergedDictionaries>
                      <XamlControlsResources />
                      <ResourceDictionary Source="ms-appx:///CommunityToolkit.WinUI.Controls.SettingsControls/SettingsCard/SettingsCard.xaml" />
                    </ResourceDictionary.MergedDictionaries>
                    <x:Double x:Key="SettingsCardWrapThreshold">286</x:Double>
                  </ResourceDictionary></Application.Resources>
                </Application>
                """,
        });

        Assert.Empty(Keys(r, FindingKind.Unused));
        Assert.Contains("community-toolkit", r.ThemePacksInUse);
        Assert.Equal(100, r.Coverage);   // XamlControlsResources 與 CommunityToolkit 都是內容已知的主題
    }

    [Fact]
    public void A_CSharp_lookup_of_a_framework_key_is_not_dangling()
    {
        // 已知主題不再算「讀不到」之後，C# 查找要自己認框架的鍵
        var r = Analyze(new()
        {
            ["App.csproj"] = WinUiProj,
            ["App.xaml"] = $"""
                <Application {Wpf}><Application.Resources><ResourceDictionary>
                  <ResourceDictionary.MergedDictionaries><XamlControlsResources /></ResourceDictionary.MergedDictionaries>
                </ResourceDictionary></Application.Resources></Application>
                """,
            ["Main.cs"] = """class M { object A() => Application.Current.Resources["TextFillColorPrimaryBrush"]; }""",
        });

        Assert.Empty(Keys(r, FindingKind.DanglingDynamic));
        Assert.True(r.FrameworkProvided >= 1);
    }

    // ── csproj 的 Link ─────────────────────────────────────────────────

    [Theory]
    [InlineData("""<Page Include="..\Shared\Styles\Button.xaml"><Link>Resources\Styles\Button.xaml</Link></Page>""")]
    [InlineData("""<Page Include="..\Shared\Styles\Button.xaml" Link="Resources\Styles\Button.xaml" />""")]
    public void A_dictionary_linked_in_from_another_project_resolves(string item)
    {
        // PowerToys QuickAccess 借用設定頁的樣式：Source="/Resources/Styles/Button.xaml" 只存在於建置後的套件
        var r = Analyze(new()
        {
            ["App/App.csproj"] = $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup><ItemGroup>{item}</ItemGroup></Project>",
            ["App/App.xaml"] = $"""
                <Application {Wpf}><Application.Resources><ResourceDictionary>
                  <ResourceDictionary.MergedDictionaries><ResourceDictionary Source="/Resources/Styles/Button.xaml" /></ResourceDictionary.MergedDictionaries>
                </ResourceDictionary></Application.Resources></Application>
                """,
            ["App/Main.xaml"] = Win(Wpf, """<Button Style="{StaticResource SharedButton}" />"""),
            ["Shared/Shared.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Library</OutputType></PropertyGroup></Project>",
            ["Shared/Styles/Button.xaml"] = $"<ResourceDictionary {Wpf}><Style x:Key=\"SharedButton\" TargetType=\"Button\" /></ResourceDictionary>",
        });

        Assert.Empty(r.ExternalSources);
        Assert.DoesNotContain(r.Findings, f => f.Kind is FindingKind.DanglingStatic or FindingKind.External or FindingKind.OutOfScope);
    }

    [Fact]
    public void A_relative_Source_to_a_linked_file_resolves_too()
    {
        var r = Analyze(new()
        {
            ["App/App.csproj"] = """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup><ItemGroup><Page Include="..\Shared\Colors.xaml" Link="Themes\Colors.xaml" /></ItemGroup></Project>""",
            ["App/App.xaml"] = $"""
                <Application {Wpf}><Application.Resources><ResourceDictionary>
                  <ResourceDictionary.MergedDictionaries><ResourceDictionary Source="Themes/Colors.xaml" /></ResourceDictionary.MergedDictionaries>
                </ResourceDictionary></Application.Resources></Application>
                """,
            ["App/Main.xaml"] = Win(Wpf, """<Border Background="{StaticResource SharedBrush}" />"""),
            ["Shared/Shared.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Library</OutputType></PropertyGroup></Project>",
            ["Shared/Colors.xaml"] = $"<ResourceDictionary {Wpf}><SolidColorBrush x:Key=\"SharedBrush\" Color=\"Red\" /></ResourceDictionary>",
        });

        Assert.Empty(r.ExternalSources);
        Assert.Empty(r.Findings);
    }

    [Fact]
    public void A_wildcard_or_property_Include_is_not_guessed()
    {
        var r = Analyze(new()
        {
            ["App/App.csproj"] = """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup><ItemGroup><Page Include="..\Shared\*.xaml" Link="Themes\Colors.xaml" /><Page Include="$(Root)\Shared\Colors.xaml" Link="Themes\Colors.xaml" /></ItemGroup></Project>""",
            ["App/App.xaml"] = $"""
                <Application {Wpf}><Application.Resources><ResourceDictionary>
                  <ResourceDictionary.MergedDictionaries><ResourceDictionary Source="Themes/Colors.xaml" /></ResourceDictionary.MergedDictionaries>
                </ResourceDictionary></Application.Resources></Application>
                """,
            ["Shared/Colors.xaml"] = $"<ResourceDictionary {Wpf}><SolidColorBrush x:Key=\"SharedBrush\" Color=\"Red\" /></ResourceDictionary>",
        });

        Assert.Contains("Themes/Colors.xaml", r.ExternalSources);
    }
}
