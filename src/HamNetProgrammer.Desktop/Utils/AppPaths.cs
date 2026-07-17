namespace HamNetProgrammer.Desktop.Utils;

public static class AppPaths
{
    // Matches the CLI's convention: bin/Debug/net9.0-windows.../ is 5 levels below the repo
    // root (src/HamNetProgrammer.Desktop/bin/Debug/<tfm>/), same depth as the CLI project.
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    public static string CodeplugDbPath => Path.Combine(RepoRoot, "data", "codeplug.db");
}
