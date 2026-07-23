using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HamNetProgrammer.Desktop.Utils;

public sealed record MemoryDumpEntry(string BinPath, string ManifestPath, DateTime Timestamp, string Label, string? DeviceId, string? Port);

/// <summary>Lists every full memory dump (.bin + matching .manifest.csv) this app has ever
/// produced - Backup Radio Memory's own dumps/ folder, plus every dump any diagnostics-folder
/// session left behind (Write/Read/Sync/Sample/Restore Previous Codeplug/Restore Radio Memory,
/// before- and after- where applicable) - so Restore Radio Memory can offer a friendly "pick from
/// what you already have" list instead of a bare file-system browser with no hint where to look.</summary>
public static class MemoryDumpPicker
{
    // Friendly suffix for known filenames within a diagnostics session folder - anything else
    // still gets picked up, just labeled with its raw filename instead.
    private static readonly Dictionary<string, string> KnownFileLabels = new()
    {
        ["baseline_before.bin"] = "pre-write backup",
        ["baseline_after.bin"] = "post-write backup",
        ["baseline_after_restore.bin"] = "post-restore backup",
        ["verify_now.bin"] = "verification read",
        ["read.bin"] = "codeplug read",
        ["sample.bin"] = "memory sample",
        ["backup.bin"] = "diagnostic report backup",
        ["pre_restore_backup.bin"] = "pre-restore safety backup",
        ["post_restore.bin"] = "post-restore verification",
    };

    private static readonly Dictionary<string, string> OperationLabels = new()
    {
        ["write-codeplug"] = "Write Codeplug",
        ["read-codeplug"] = "Read Codeplug",
        ["restore-codeplug"] = "Restore Previous Codeplug",
        ["restore-radio-memory"] = "Restore Radio Memory",
        ["sync-reference-data"] = "Sync Reference Data",
        ["manual-report"] = "Diagnostic Report",
    };

    public static List<MemoryDumpEntry> Scan(string dumpsDirectory, string diagnosticsDirectory)
    {
        var entries = new List<MemoryDumpEntry>();

        if (Directory.Exists(dumpsDirectory))
        {
            foreach (var binPath in Directory.GetFiles(dumpsDirectory, "*.bin"))
            {
                var manifestPath = Path.Combine(Path.GetDirectoryName(binPath)!, Path.GetFileNameWithoutExtension(binPath) + ".manifest.csv");
                if (!File.Exists(manifestPath)) continue;
                entries.Add(new MemoryDumpEntry(binPath, manifestPath, File.GetCreationTimeUtc(binPath), "Backup Radio Memory", null, null));
            }
        }

        if (Directory.Exists(diagnosticsDirectory))
        {
            foreach (var folder in Directory.GetDirectories(diagnosticsDirectory))
            {
                string? operation = null, deviceId = null, port = null;
                var auditPath = Path.Combine(folder, "audit.jsonl");
                if (File.Exists(auditPath))
                {
                    try
                    {
                        var firstLine = File.ReadLines(auditPath).FirstOrDefault();
                        if (firstLine is not null)
                        {
                            using var doc = JsonDocument.Parse(firstLine);
                            var root = doc.RootElement;
                            if (root.TryGetProperty("evt", out var evt) && evt.GetString() == "session_start")
                            {
                                operation = root.TryGetProperty("operation", out var op) ? op.GetString() : null;
                                deviceId = root.TryGetProperty("deviceId", out var d) ? d.GetString() : null;
                                port = root.TryGetProperty("port", out var p) ? p.GetString() : null;
                            }
                        }
                    }
                    catch
                    {
                        // Malformed/partial log (e.g. the app crashed mid-session) - still offer
                        // any dumps the folder has, just without the friendlier operation label.
                    }
                }

                var opLabel = operation is not null && OperationLabels.TryGetValue(operation, out var known) ? known : (operation ?? "Session");

                foreach (var binPath in Directory.GetFiles(folder, "*.bin"))
                {
                    var manifestPath = Path.Combine(folder, Path.GetFileNameWithoutExtension(binPath) + ".manifest.csv");
                    if (!File.Exists(manifestPath)) continue;

                    var fileName = Path.GetFileName(binPath);
                    var kind = KnownFileLabels.TryGetValue(fileName, out var kindLabel) ? kindLabel : fileName;
                    entries.Add(new MemoryDumpEntry(binPath, manifestPath, File.GetCreationTimeUtc(binPath), $"{opLabel} ({kind})", deviceId, port));
                }
            }
        }

        return entries.OrderByDescending(e => e.Timestamp).ToList();
    }

    public static async Task<MemoryDumpEntry?> ShowAsync(XamlRoot xamlRoot, IReadOnlyList<MemoryDumpEntry> entries)
    {
        var listView = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            MaxHeight = 360,
        };
        foreach (var entry in entries)
        {
            var deviceBit = entry.DeviceId is not null ? $" - {entry.DeviceId}" : "";
            var portBit = entry.Port is not null ? $" ({entry.Port})" : "";
            var label = $"{entry.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm} - {entry.Label}{deviceBit}{portBit}";
            listView.Items.Add(new ListViewItem { Content = label, Tag = entry });
        }
        listView.SelectedIndex = 0;

        var dialog = new ContentDialog
        {
            Title = "Restore Radio Memory From...",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap,
                        Text = "Pick which memory dump to restore. This writes its ENTIRE contents back to the radio currently connected.",
                    },
                    listView,
                },
            },
            PrimaryButtonText = "Continue",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = xamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary || listView.SelectedItem is not ListViewItem { Tag: MemoryDumpEntry picked })
            return null;
        return picked;
    }
}
