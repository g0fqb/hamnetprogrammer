using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Data.Sqlite;
using HamNetProgrammer.Core.Data;
using HamNetProgrammer.Core.Radios.AnyTone.Codecs;

namespace HamNetProgrammer.Desktop.Utils;

/// <summary>
/// Editor for a scan list's own behavior settings (as opposed to its member channels, which
/// ScanListsPage handles the same way ZonesPage does). These are real AT-D878UV scan-list fields,
/// not decorative - see ScanListRecordCodec for the confirmed byte layout this maps onto.
/// </summary>
public static class ScanListSettingsDialog
{
    private sealed record RevertOption(ScanListRevertMode Mode, string Display);

    private static readonly RevertOption[] RevertOptions =
    [
        new(ScanListRevertMode.Selected, "Selected channel"),
        new(ScanListRevertMode.SelectedTalkback, "Selected channel + Talkback"),
        new(ScanListRevertMode.PriorityChannel1, "Priority Channel 1"),
        new(ScanListRevertMode.PriorityChannel2, "Priority Channel 2"),
        new(ScanListRevertMode.LastCalled, "Last Called"),
        new(ScanListRevertMode.LastUsed, "Last Used"),
        new(ScanListRevertMode.PriorityChannel1Talkback, "Priority Channel 1 + Talkback"),
        new(ScanListRevertMode.PriorityChannel2Talkback, "Priority Channel 2 + Talkback"),
    ];

    public static async Task<bool> ShowAsync(XamlRoot xamlRoot, long scanListId)
    {
        using var db = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);

        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT PriorityChannel1Id, PriorityChannel2Id, LookBackTimeA, LookBackTimeB,
                   DropoutDelayTime, DwellTime, RevertMode
            FROM ScanLists WHERE Id = $id;
            """;
        cmd.Parameters.Add(new SqliteParameter("$id", scanListId));
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return false;

        long? priority1Id = reader.IsDBNull(0) ? null : reader.GetInt64(0);
        long? priority2Id = reader.IsDBNull(1) ? null : reader.GetInt64(1);
        var lookBackA = reader.IsDBNull(2) ? 0.5 : reader.GetDouble(2);
        var lookBackB = reader.IsDBNull(3) ? 0.5 : reader.GetDouble(3);
        var dropout = reader.IsDBNull(4) ? 0.1 : reader.GetDouble(4);
        var dwell = reader.IsDBNull(5) ? 0.1 : reader.GetDouble(5);
        var revertMode = reader.IsDBNull(6) || !Enum.TryParse<ScanListRevertMode>(reader.GetString(6), out var parsed)
            ? ScanListRevertMode.Selected
            : parsed;
        reader.Close();

        var allChannels = ChannelQueries.GetAllChannels(db);
        var priority1Picker = ChannelButtonPicker.Build(xamlRoot, allChannels, priority1Id, "Priority Channel 1");
        var priority2Picker = ChannelButtonPicker.Build(xamlRoot, allChannels, priority2Id, "Priority Channel 2");

        // NumberBox, not free text - all four are hardware-confirmed 0.1-5.0s (see
        // ScanListRecordCodec's remarks); SmallChange=0.1 matches the radio's own step, and typing
        // out of range gets visibly clamped as you leave the field instead of silently rewritten
        // to something else at save time with no feedback.
        NumberBox TimingBox(double value) => new()
        {
            Value = value, Minimum = 0.1, Maximum = 5.0, SmallChange = 0.1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Width = 120,
        };
        var lookBackABox = TimingBox(lookBackA);
        var lookBackBBox = TimingBox(lookBackB);
        var dropoutBox = TimingBox(dropout);
        var dwellBox = TimingBox(dwell);

        var revertCombo = new ComboBox
        {
            ItemsSource = RevertOptions,
            DisplayMemberPath = "Display",
            SelectedItem = RevertOptions.First(o => o.Mode == revertMode),
        };

        var form = new StackPanel { Spacing = 10 };
        form.Children.Add(FormField.Row("Priority Channel 1", priority1Picker.Container,
            "A channel scanned more frequently than the rest of the list. Off if unset."));
        form.Children.Add(FormField.Row("Priority Channel 2", priority2Picker.Container,
            "A second channel scanned more frequently than the rest of the list. Off if unset."));
        form.Children.Add(FormField.Row("Look Back Time A (s)", lookBackABox,
            "How long the radio pauses on Priority Channel 1 after activity stops before resuming the scan. Range 0.1-5.0s."));
        form.Children.Add(FormField.Row("Look Back Time B (s)", lookBackBBox,
            "Same as Look Back Time A, for Priority Channel 2. Range 0.1-5.0s."));
        form.Children.Add(FormField.Row("Dropout Delay Time (s)", dropoutBox,
            "How long the radio waits after a signal drops before resuming the scan. Range 0.1-5.0s."));
        form.Children.Add(FormField.Row("Dwell Time (s)", dwellBox,
            "How long the radio stays on a channel with ongoing activity before moving on. Range 0.1-5.0s."));
        form.Children.Add(FormField.Row("Revert Channel", revertCombo,
            "Which channel the radio returns to when you press PTT while scanning."));

        var scrollViewer = new ScrollViewer
        {
            Content = form,
            Height = 420,
            Width = 520,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        var dialog = new ContentDialog
        {
            Title = "Scan List Settings",
            Content = scrollViewer,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return false;

        using var updateCmd = db.CreateCommand();
        updateCmd.CommandText = """
            UPDATE ScanLists SET
                PriorityChannel1Id = $p1, PriorityChannel2Id = $p2,
                LookBackTimeA = $lba, LookBackTimeB = $lbb, DropoutDelayTime = $dropout, DwellTime = $dwell,
                RevertMode = $revert
            WHERE Id = $id;
            """;
        updateCmd.Parameters.Add(new SqliteParameter("$p1", (object?)priority1Picker.GetSelectedId() ?? DBNull.Value));
        updateCmd.Parameters.Add(new SqliteParameter("$p2", (object?)priority2Picker.GetSelectedId() ?? DBNull.Value));
        updateCmd.Parameters.Add(new SqliteParameter("$lba", NumberBoxValueOrDefault(lookBackABox, 0.5)));
        updateCmd.Parameters.Add(new SqliteParameter("$lbb", NumberBoxValueOrDefault(lookBackBBox, 0.5)));
        updateCmd.Parameters.Add(new SqliteParameter("$dropout", NumberBoxValueOrDefault(dropoutBox, 0.1)));
        updateCmd.Parameters.Add(new SqliteParameter("$dwell", NumberBoxValueOrDefault(dwellBox, 0.1)));
        updateCmd.Parameters.Add(new SqliteParameter("$revert", ((RevertOption)revertCombo.SelectedItem).Mode.ToString()));
        updateCmd.Parameters.Add(new SqliteParameter("$id", scanListId));
        updateCmd.ExecuteNonQuery();

        return true;
    }

    private static double NumberBoxValueOrDefault(NumberBox box, double fallback) =>
        double.IsNaN(box.Value) ? fallback : box.Value;
}
