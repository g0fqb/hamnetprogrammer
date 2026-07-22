using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HamNetProgrammer.Desktop.Utils;

public sealed record RestorePoint(string FolderPath, DateTime Timestamp, string? Port, string? DeviceId);

/// <summary>Lists every past Write Codeplug session that captured a pre-write baseline (every
/// diagnostics folder under AppPaths.DiagnosticsDirectory with baseline_before.bin/.manifest.csv
/// and audit.jsonl - only OnWriteCodeplugClicked's flow ever produces that combination), not just
/// the current app session's most recent write. Restore Previous Codeplug used to only offer
/// "undo my last write, this session" - this is what makes "go back a week" possible, since the
/// backups were always being taken, just never exposed for selection.</summary>
public static class RestorePointPicker
{
    public static List<RestorePoint> Scan(string diagnosticsDirectory)
    {
        var points = new List<RestorePoint>();
        if (!Directory.Exists(diagnosticsDirectory)) return points;

        foreach (var folder in Directory.GetDirectories(diagnosticsDirectory))
        {
            var baselineBin = Path.Combine(folder, "baseline_before.bin");
            var baselineManifest = Path.Combine(folder, "baseline_before.manifest.csv");
            var auditPath = Path.Combine(folder, "audit.jsonl");
            if (!File.Exists(baselineBin) || !File.Exists(baselineManifest) || !File.Exists(auditPath))
                continue;

            string? operation = null, port = null, deviceId = null;
            var timestamp = Directory.GetCreationTimeUtc(folder);
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
                        port = root.TryGetProperty("port", out var p) ? p.GetString() : null;
                        deviceId = root.TryGetProperty("deviceId", out var d) ? d.GetString() : null;
                        if (root.TryGetProperty("utc", out var utc) && utc.TryGetDateTime(out var parsedUtc))
                            timestamp = parsedUtc;
                    }
                }
            }
            catch
            {
                // Malformed/partial log (e.g. the app crashed mid-session) - still offer it with a
                // filesystem-derived timestamp rather than hiding a potentially-needed backup.
            }

            // Only OnWriteCodeplugClicked's flow ever takes a baseline_before.bin this shape -
            // Sync Reference Data doesn't (see RadioPage.xaml.cs), so anything else here would be
            // a different kind of session this picker isn't meant to offer.
            if (operation != "write-codeplug") continue;
            points.Add(new RestorePoint(folder, timestamp, port, deviceId));
        }

        return points.OrderByDescending(p => p.Timestamp).ToList();
    }

    public static async Task<RestorePoint?> ShowAsync(XamlRoot xamlRoot, IReadOnlyList<RestorePoint> points)
    {
        var listView = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            MaxHeight = 360,
        };
        foreach (var point in points)
        {
            var label = $"{point.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm} - {point.DeviceId ?? "unknown device"} ({point.Port ?? "unknown port"})";
            listView.Items.Add(new ListViewItem { Content = label, Tag = point });
        }
        listView.SelectedIndex = 0;

        var dialog = new ContentDialog
        {
            Title = "Restore Codeplug From...",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap,
                        Text = "Pick which pre-write backup to restore. This writes that backup's " +
                               "data back to the radio currently connected, undoing everything that " +
                               "write changed.",
                    },
                    listView,
                },
            },
            PrimaryButtonText = "Restore",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = xamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary || listView.SelectedItem is not ListViewItem { Tag: RestorePoint picked })
            return null;
        return picked;
    }
}
