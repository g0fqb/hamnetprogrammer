using System.IO.Compression;

namespace HamNetProgrammer.Core.Diagnostics;

/// <summary>Zips a diagnostic session folder (audit log + memory dumps) into a single attachable file.</summary>
public static class DiagnosticPackager
{
    public static string CreateZip(string sessionFolder)
    {
        var zipPath = sessionFolder.TrimEnd('\\', '/') + ".zip";
        if (File.Exists(zipPath)) File.Delete(zipPath);
        ZipFile.CreateFromDirectory(sessionFolder, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
        return zipPath;
    }
}
