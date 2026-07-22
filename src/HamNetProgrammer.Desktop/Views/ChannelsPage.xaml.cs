using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using HamNetProgrammer.Core.Data;
using HamNetProgrammer.Desktop.Utils;
using Microsoft.Data.Sqlite;

namespace HamNetProgrammer.Desktop.Views;

/// <summary>
/// Direct channel CRUD, independent of any zone - closes a real gap: Zones only ever offered
/// "New Channel from Talk Group" (creates + adds to that zone in one step) or "Add Existing
/// Channel" (adds to that zone's membership), and "Remove" there only ever deleted ZONE
/// MEMBERSHIP, never the channel itself - there was no way to actually delete a channel, or find
/// one that isn't in any zone at all (orphaned - e.g. left behind after every zone stopped using
/// it), anywhere in the app.
/// </summary>
public sealed partial class ChannelsPage : Page
{
    private static readonly SolidColorBrush ZonesBrush = new(Microsoft.UI.ColorHelper.FromArgb(255, 0x88, 0x96, 0xa0));
    private static readonly SolidColorBrush OrphanedBrush = new(Microsoft.UI.ColorHelper.FromArgb(255, 0xff, 0xb7, 0x4d));

    public sealed record ChannelRow(long ChannelId, int ChannelNumber, string Name, string FrequencyMHz,
        string TalkGroup, string ColorCodeSlot, string Zones, SolidColorBrush ZonesColor);

    public ChannelsPage()
    {
        this.InitializeComponent();
        this.NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Enabled;
        LoadChannels();
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) => LoadChannels();
    private void OnOrphanedFilterChanged(object sender, RoutedEventArgs e) => LoadChannels();

    private void LoadChannels()
    {
        try
        {
            using var db = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);
            using var cmd = db.CreateCommand();

            var query = SearchBox.Text.Trim();
            var conditions = new List<string>();
            if (query.Length > 0) conditions.Add("(c.Name LIKE $pattern OR CAST(c.ChannelNumber AS TEXT) LIKE $pattern)");
            var where = conditions.Count > 0 ? $"WHERE {string.Join(" AND ", conditions)}" : "";

            cmd.CommandText = $"""
                SELECT c.Id, c.ChannelNumber, c.Name, c.RxFrequencyHz, ct.Name, c.ColorCode, c.TimeSlot,
                       (SELECT GROUP_CONCAT(z.Name, ', ') FROM ZoneChannels zc JOIN Zones z ON z.Id = zc.ZoneId WHERE zc.ChannelId = c.Id)
                FROM Channels c
                LEFT JOIN Contacts ct ON ct.Id = c.ContactId
                {where}
                ORDER BY c.ChannelNumber;
                """;
            if (query.Length > 0) cmd.Parameters.Add(new SqliteParameter("$pattern", $"%{query}%"));

            using var reader = cmd.ExecuteReader();
            var rows = new List<ChannelRow>();
            var orphanCount = 0;
            while (reader.Read())
            {
                var zones = reader.IsDBNull(7) ? null : reader.GetString(7);
                var isOrphan = zones is null;
                if (isOrphan) orphanCount++;
                if (OrphanedOnlyCheckBox.IsChecked == true && !isOrphan) continue;

                var talkGroup = reader.IsDBNull(4) ? "-" : reader.GetString(4);
                var colorCode = reader.IsDBNull(5) ? "-" : reader.GetInt32(5).ToString();
                var timeSlot = reader.IsDBNull(6) ? "-" : reader.GetInt32(6).ToString();

                rows.Add(new ChannelRow(
                    reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2),
                    (reader.GetInt64(3) / 1_000_000.0).ToString("F5"),
                    talkGroup, $"{colorCode}/{timeSlot}",
                    zones ?? "(not in any zone)", isOrphan ? OrphanedBrush : ZonesBrush));
            }

            ChannelListView.ItemsSource = rows;
            SummaryText.Text = $"{rows.Count} shown, {orphanCount} not in any zone ({AppPaths.CodeplugDbPath})";
        }
        catch (Exception ex)
        {
            SummaryText.Text = $"Could not load channels: {ex.Message}";
        }
    }

    /// <summary>Creates a blank placeholder channel, then immediately opens it in ChannelEditDialog
    /// for real values - the "Create" ChannelsPage was missing (only Edit/Delete existed at first),
    /// since every existing creation path (Zones' New Channel from Talk Group) needs a zone to
    /// inherit frequency/mode/etc. from, which doesn't fit here where a channel isn't tied to any
    /// zone yet. Deletes the placeholder again if the dialog is cancelled, rather than leaving an
    /// empty "New Channel" behind.</summary>
    private async void OnNewClicked(object sender, RoutedEventArgs e)
    {
        long channelId;
        try
        {
            using var db = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO Channels (ChannelNumber, Name, Mode, RxFrequencyHz, TxFrequencyHz, ColorCode, TimeSlot, Power)
                VALUES ((SELECT COALESCE(MAX(ChannelNumber), 0) + 1 FROM Channels), 'New Channel', 'Digital', 433450000, 433450000, 1, 1, 'Low');
                SELECT last_insert_rowid();
                """;
            channelId = (long)cmd.ExecuteScalar()!;
        }
        catch (Exception ex)
        {
            SummaryText.Text = $"Could not create channel: {ex.Message}";
            return;
        }

        var saved = await ChannelEditDialog.ShowAsync(this.XamlRoot, channelId);
        if (!saved)
        {
            using var db = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);
            using var cmd = db.CreateCommand();
            cmd.CommandText = "DELETE FROM Channels WHERE Id = $id;";
            cmd.Parameters.Add(new SqliteParameter("$id", channelId));
            cmd.ExecuteNonQuery();
        }
        LoadChannels();
    }

    private async void OnEditClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not long channelId) return;
        var saved = await ChannelEditDialog.ShowAsync(this.XamlRoot, channelId);
        if (saved) LoadChannels();
    }

    private async void OnDeleteClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ChannelRow row) return;

        var inAnyList = row.Zones != "(not in any zone)";
        var dialog = new ContentDialog
        {
            Title = "Delete Channel",
            Content = $"Delete channel {row.ChannelNumber} '{row.Name}' entirely? This removes it from the Channels table " +
                      (inAnyList
                          ? $"AND every zone/scan list/roaming zone currently using it ({row.Zones}) - not just this list. This cannot be undone."
                          : "(it isn't in any zone right now). This cannot be undone."),
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            using var db = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);
            using var cmd = db.CreateCommand();
            // ZoneChannels/ScanListChannels/RoamingZoneChannels all have ON DELETE CASCADE (and
            // PRAGMA foreign_keys = ON is set in CodeplugDatabase.OpenOrCreate) - deleting the
            // channel itself cleans up every membership row automatically, no separate DELETEs
            // needed. RadioSettings.AprsReportChannelId has no CASCADE (deliberately - silently
            // clearing a configured APRS report channel would be a worse surprise than a blocked
            // delete), so that specific case throws and is reported below instead.
            cmd.CommandText = "DELETE FROM Channels WHERE Id = $id;";
            cmd.Parameters.Add(new SqliteParameter("$id", row.ChannelId));
            cmd.ExecuteNonQuery();

            SummaryText.Text = $"Deleted channel {row.ChannelNumber} '{row.Name}'.";
            LoadChannels();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // SQLITE_CONSTRAINT
        {
            SummaryText.Text = $"Could not delete '{row.Name}' - it's set as the APRS report channel in Radio Settings. Change that first.";
        }
        catch (Exception ex)
        {
            SummaryText.Text = $"Could not delete '{row.Name}': {ex.Message}";
        }
    }
}
