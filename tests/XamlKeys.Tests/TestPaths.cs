using System.Reflection;

namespace XamlKeys.Tests;

internal static class TestPaths
{
    /// <summary>從測試組件往上找到 repo 根（有 samples/demo 的那一層）。</summary>
    public static string RepoRoot { get; } = FindRoot();

    public static string Demo => Path.Combine(RepoRoot, "samples", "demo");

    /// <summary>真正的 xamlkeys.dll。路徑由 csproj 的 target 寫進組件中繼資料 ——
    /// CLI 契約（旗標解析、退出碼順序）只存在於 Program.cs，Core 的單元測試碰不到。</summary>
    public static string CliDll { get; } =
        typeof(TestPaths).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "XamlKeysCliDll")?.Value
        ?? throw new InvalidOperationException("XamlKeysCliDll metadata is missing");

    private static string FindRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, "samples", "demo"))) return dir;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        throw new InvalidOperationException("cannot locate the repo root from the test assembly");
    }
}
