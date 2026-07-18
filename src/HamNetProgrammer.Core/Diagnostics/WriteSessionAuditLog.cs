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

    /// <summary>Reads back every "region" event logged by <see cref="LogRegion"/> in a session -
    /// the exact set of regions a write actually touched, used to scope a restore to undoing that
    /// one session rather than guessing from the current (possibly since-changed) database state.</summary>
    public static IReadOnlyList<(string Name, uint Address, int Length)> ReadWrittenRegions(string logPath)
    {
        var results = new List<(string, uint, int)>();
        foreach (var line in File.ReadLines(logPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("evt", out var evt) || evt.GetString() != "region") continue;
            if (!root.TryGetProperty("status", out var status) || status.GetString() != "written") continue;

            var name = root.GetProperty("name").GetString()!;
            var address = Convert.ToUInt32(root.GetProperty("address").GetString(), 16);
            var length = root.GetProperty("length").GetInt32();
            results.Add((name, address, length));
        }
        return results;
    }

    /// <summary>True if the session's log shows the write actually reached EndProgrammingSession -
    /// i.e. whether anything was committed to flash at all. Writes are buffered until then, so a
    /// session that failed before this point never touched the radio and has nothing to restore.</summary>
    public static bool WasCommitted(string logPath)
    {
        foreach (var line in File.ReadLines(logPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("evt", out var evt) && evt.GetString() == "note" &&
                root.TryGetProperty("message", out var msg) &&
                msg.GetString() == CommitConfirmedMessage)
                return true;
        }
        return false;
    }

    public const string CommitConfirmedMessage = "Write session ended (commit issued).";
}
