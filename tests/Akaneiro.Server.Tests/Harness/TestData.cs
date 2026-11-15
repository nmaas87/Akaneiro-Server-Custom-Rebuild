using System.Runtime.CompilerServices;
using Akaneiro.Server.Game;

namespace Akaneiro.Server.Tests.Harness;

/// <summary>The game data tables, loaded once for all tests (they are only read).</summary>
public static class TestData
{
    public static string ModernAssetsDir => FindClientAssets("client-source");
    public static string ModernDataDir => Path.Combine(AppContext.BaseDirectory, "GameData");

    private static readonly Lazy<GameData> _data = new(() => new GameData(ModernAssetsDir, ModernDataDir));

    public static GameData Data => _data.Value;

    /// <summary>Finds the repository's client-source folder (the client's data tables), wherever the tests were built to.</summary>
    public static string FindClientAssets(string folder, [CallerFilePath] string sourceFile = "")
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(sourceFile) })
        {
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, folder);
                if (Directory.Exists(Path.Combine(candidate, "Resources")))
                    return candidate;
            }
        }
        throw new DirectoryNotFoundException(folder + " not found above " + AppContext.BaseDirectory);
    }
}

/// <summary>The April 2013 client's own content, exactly as Program wires it: the 2013 bundle's
/// tables, the modern content standing in for everything that bundle never shipped.</summary>
public static class TestData2013
{
    private static readonly Lazy<GameData> _data = new(() => new GameData(
        TestData.FindClientAssets("client-source-2013"),
        Path.Combine(AppContext.BaseDirectory, "GameData2013"),
        Path.Combine(TestData.ModernAssetsDir, "Resources", "Localization", "EN"),
        TestData.ModernDataDir));

    public static GameData Data => _data.Value;
}
