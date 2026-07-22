namespace HamNetProgrammer.Desktop.Utils;

public static class AppPaths
{
    // Dev/debug runs live 5 levels below the repo root (src/HamNetProgrammer.Desktop/bin/Debug/
    // <tfm>/) - if that structure is actually present (the .sln sits there), use the repo directly
    // so an existing dev database isn't orphaned. The installed app has no such nesting at all -
    // installer/HamNetProgrammer.iss publishes flat, with the exe directly in the install folder -
    // so walking "up 5 levels" from there lands in an arbitrary, likely-unwritable parent
    // directory (confirmed: resolves outside the install tree entirely). Fall back to a real
    // per-user data directory in that case instead.
    private static readonly string DataRoot = ResolveDataRoot();

    private static string ResolveDataRoot()
    {
        var devRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        if (File.Exists(Path.Combine(devRoot, "HamNetProgrammer.sln")))
            return devRoot;

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HamNetProgrammer");
    }

    public static string CodeplugDbPath => EnsureParent(Path.Combine(DataRoot, "data", "codeplug.db"));
    public static string DumpsDirectory => EnsureDirectory(Path.Combine(DataRoot, "dumps"));
    public static string DiagnosticsDirectory => EnsureDirectory(Path.Combine(DataRoot, "diagnostics"));

    // Matches the CLI's lookup-dmrid convention (7-day cache of radioid.net's ~17MB user CSV).
    public static string RadioIdCachePath => EnsureParent(Path.Combine(DataRoot, "data", "radioid_users.csv"));

    // The dev tree already has these folders (git-ignored but present); an installed copy starts
    // with none of them, and SQLite/File APIs don't create missing parent directories themselves -
    // ensuring here, on every access, is cheap (Directory.CreateDirectory is a no-op if it already
    // exists) and means every call site gets a guaranteed-usable path with no separate setup step.
    private static string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }

    private static string EnsureParent(string filePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        return filePath;
    }
}
