using System.Text.Json;

namespace HamNetProgrammer.Core.Diagnostics;

/// <summary>
/// Appends one JSON object per line to a log file as a write session progresses, rather than
/// building an in-memory record and saving it at the end. If the radio never comes back after a
/// write, the in-memory approach loses everything; this one leaves a file showing exactly how far
/// the session got, which is the actual diagnostic question after a bad write.
/// </summary>
public sealed class WriteSessionAuditLog : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly object _lock = new();

    public string LogPath { get; }

    private WriteSessionAuditLog(string path)
    {
        LogPath = path;
        _writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true,
        };
    }

    public static WriteSessionAuditLog Start(string path, string operation, string port, string? deviceId, string toolVersion)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var log = new WriteSessionAuditLog(path);
        log.WriteEvent(new
        {
            evt = "session_start",
            operation,
            port,
            deviceId,
            toolVersion,
            utc = DateTime.UtcNow,
        });
        return log;
    }

    public void LogRegion(string name, uint address, int length, string status, string? error = null) =>
        WriteEvent(new
        {
            evt = "region",
            name,
            address = $"0x{address:x8}",
            length,
            status,
            error,
            utc = DateTime.UtcNow,
        });

    public void LogNote(string message) =>
        WriteEvent(new { evt = "note", message, utc = DateTime.UtcNow });

    public void End(string status, string? error = null) =>
        WriteEvent(new { evt = "session_end", status, error, utc = DateTime.UtcNow });

    private void WriteEvent(object payload)
    {
        var line = JsonSerializer.Serialize(payload);
        lock (_lock) _writer.WriteLine(line);
    }

    public void Dispose() => _writer.Dispose();
}
