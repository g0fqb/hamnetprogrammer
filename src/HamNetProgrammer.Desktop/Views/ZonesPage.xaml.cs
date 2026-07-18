using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using HamNetProgrammer.Core.Data;
using HamNetProgrammer.Desktop.Utils;
using Microsoft.Data.Sqlite;

namespace HamNetProgrammer.Desktop.Views;

public sealed partial class ZonesPage : Page
{
    public sealed record ChannelRow(long ChannelId, int Position, string Name, string FrequencyMHz, string TalkGroup, string ColorCodeSlot);
    public sealed record ZoneRow(string Name, bool IsActive);

    public ZonesPage()
    {
        this.InitializeComponent();
        LoadZones();
    }

    private string? SelectedZoneName => (ZoneListView.SelectedItem as ZoneRow)?.Name;

    private void LoadZones(string? selectZoneName = null)
    {
        try
        {
            using var db = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT Name, IsActive FROM Zones ORDER BY Name;";
            using var reader = cmd.ExecuteReader();

            var rows = new List<ZoneRow>();
            while (reader.Read())
                rows.Add(new ZoneRow(reader.GetString(0), reader.GetInt64(1) != 0));

            ZoneListView.ItemsSource = rows;

            using var countCmd = db.CreateCommand();
            countCmd.CommandText = "SELECT COUNT(*) FROM Channels;";
            var totalChannels = Convert.ToInt32(countCmd.ExecuteScalar());
            var activeCount = rows.Count(r => r.IsActive);
            SummaryText.Text = $"{rows.Count} zones ({activeCount} active), {totalChannels} channels ({AppPaths.CodeplugDbPath})";

            if (rows.Count > 0)
            {
                var indexToSelect = selectZoneName is not null ? rows.FindIndex(r => r.Name == selectZoneName) : 0;
                ZoneListView.SelectedIndex = indexToSelect >= 0 ? indexToSelect : 0;
            }
            else
            {
                ChannelListView.ItemsSource = null;
            }
        }
        catch (Exception ex)
        {
            SummaryText.Text = $"Could not open codeplug database: {ex.Message}";
        }
    }

    private void OnZoneActiveToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: string zoneName } checkBox) return;

        try
        {
            using var db = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);
            using var cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE Zones SET IsActive = $active WHERE Name = $name;";
            cmd.Parameters.Add(new SqliteParameter("$active", checkBox.IsChecked == true ? 1 : 0));
            cmd.Parameters.Add(new SqliteParameter("$name", zoneName));
            cmd.ExecuteNonQuery();

            LoadZones(zoneName);
        }
        catch (Exception ex)
        {
            SummaryText.Text = $"Could not update zone: {ex.Message}";
        }
    }

    private void OnZoneSelectionChanged(object sender, SelectionChangedEventArgs e) => LoadChannels();

    private void LoadChannels()
    {
        if (SelectedZoneName is not { } zoneName)
        {
            ChannelListView.ItemsSource = null;
            return;
        }

        try
        {
            using var db = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT zc.Position, c.Id, c.Name, c.RxFrequencyHz, ct.Name AS TalkGroup, c.ColorCode, c.TimeSlot
                FROM Zones z
                JOIN ZoneChannels zc ON zc.ZoneId = z.Id
                JOIN Channels c ON c.Id = zc.ChannelId
                LEFT JOIN Contacts ct ON ct.Id = c.ContactId
                WHERE z.Name = $name
                ORDER BY zc.Position;
                """;
            cmd.Parameters.Add(new SqliteParameter("$name", zoneName));
            using var reader = cmd.ExecuteReader();

            var rows = new List<ChannelRow>();
            while (reader.Read())
            {
                var position = reader.GetInt32(0);
                var channelId = reader.GetInt64(1);
                var name = reader.GetString(2);
                var rxHz = reader.GetInt64(3);
                var talkGroup = reader.IsDBNull(4) ? "-" : reader.GetString(4);
                var colorCode = reader.IsDBNull(5) ? "-" : reader.GetInt32(5).ToString();
                var timeSlot = reader.IsDBNull(6) ? "-" : reader.GetInt32(6).ToString();

                rows.Add(new ChannelRow(channelId, position, name, (rxHz / 1_000_000.0).ToString("F5"), talkGroup, $"{colorCode}/{timeSlot}"));
            }

            ChannelListView.ItemsSource = rows;
        }
        catch (Exception ex)
        {
            SummaryText.Text = $"Could not load zone '{zoneName}': {ex.Message}";
        }
    }

    private static long GetZoneId(SqliteConnection db, string zoneName)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT Id FROM Zones WHERE Name = $name;";
        cmd.Parameters.Add(new SqliteParameter("$name", zoneName));
        return (long)cmd.ExecuteScalar()!;
    }

    private async void OnNewZoneClicked(object sender, RoutedEventArgs e)
    {
        var name = await TextInputDialog.ShowAsync(this.XamlRoot, "New Zone", "Zone name", "");
        if (string.IsNullOrWhiteSpace(name)) return;

        try
        {
            using var db = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);
            using var cmd = db.CreateCommand();
            cmd.CommandText = "INSERT INTO Zones (Name) VALUES ($name);";
            cmd.Parameters.Add(new SqliteParameter("$name", name.Trim()));
            cmd.ExecuteNonQuery();

            LoadZones(name.Trim());
        }
        catch (Exception ex)
        {
            SummaryText.Text = $"Could not create zone: {ex.Message}";
        }
    }

    private async void OnRenameZoneClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedZoneName is not { } zoneName) return;

        var newName = await TextInputDialog.ShowAsync(this.XamlRoot, "Rename Zone", "Zone name", zoneName);
        if (string.IsNullOrWhiteSpace(newName) || newName.Trim() == zoneName) return;

        try
        {
            using var db = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);
            using var cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE Zones SET Name = $newName WHERE Name = $oldName;";
            cmd.Parameters.Add(new SqliteParameter("$newName", newName.Trim()));
            cmd.Parameters.Add(new SqliteParameter("$oldName", zoneName));
            cmd.ExecuteNonQuery();

            LoadZones(newName.Trim());
        }
        catch (Exception ex)
        {
            SummaryText.Text = $"Could not rename zone: {ex.Message}";
        }
    }

    private async void OnDeleteZoneClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedZoneName is not { } zoneName) return;

        var dialog = new ContentDialog
        {
            Title = "Delete Zone",
            Content = $"Delete zone '{zoneName}' and its channel membership? Channels themselves are not deleted.",
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
            cmd.CommandText = "DELETE FROM Zones WHERE Name = $name;";
            cmd.Parameters.Add(new SqliteParameter("$name", zoneName));
            cmd.ExecuteNonQuery();

            LoadZones();
        }
        catch (Exception ex)
        {
            SummaryText.Text = $"Could not delete zone: {ex.Message}";
        }
    }

    private async void OnNewChannelClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedZoneName is not { } zoneName) return;

        using var db = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);
        var zoneId = GetZoneId(db, zoneName);

        var created = await NewChannelDialog.ShowAsync(this.XamlRoot, db, zoneId, zoneName);
        if (created)
        {
            LoadChannels();
            LoadZones(zoneName);
        }
    }

    private async void OnAddChannelClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedZoneName is not { } zoneName) return;

        using var db = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);
        var zoneId = GetZoneId(db, zoneName);

        var candidates = ChannelQueries.GetChannelsNotIn(db, "ZoneChannels", "ZoneId", zoneId);
        var picked = await ChannelPickerDialog.ShowAsync(this.XamlRoot, candidates);
        if (picked is null) return;

        try
        {
            using var insertCmd = db.CreateCommand();
            insertCmd.CommandText = """
                INSERT INTO ZoneChannels (ZoneId, ChannelId, Position)
                VALUES ($zoneId, $channelId, (SELECT COALESCE(MAX(Position) + 1, 0) FROM ZoneChannels WHERE ZoneId = $zoneId));
                """;
            insertCmd.Parameters.Add(new SqliteParameter("$zoneId", zoneId));
            insertCmd.Parameters.Add(new SqliteParameter("$channelId", picked.ChannelId));
            insertCmd.ExecuteNonQuery();

            LoadChannels();
        }
        catch (Exception ex)
        {
            SummaryText.Text = $"Could not add channel: {ex.Message}";
        }
    }

    private async void OnEditChannelClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not long channelId) return;

        var saved = await ChannelEditDialog.ShowAsync(this.XamlRoot, channelId);
        if (saved) LoadChannels();
    }

    private void OnRemoveChannelClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedZoneName is not { } zoneName) return;
        if ((sender as Button)?.Tag is not int position) return;

        try
        {
            using var db = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);
            using var tx = db.BeginTransaction();
            var zoneId = GetZoneId(db, zoneName);

            using (var deleteCmd = db.CreateCommand())
            {
                deleteCmd.Transaction = tx;
                deleteCmd.CommandText = "DELETE FROM ZoneChannels WHERE ZoneId = $zoneId AND Position = $position;";
                deleteCmd.Parameters.Add(new SqliteParameter("$zoneId", zoneId));
                deleteCmd.Parameters.Add(new SqliteParameter("$position", position));
                deleteCmd.ExecuteNonQuery();
            }

            using (var shiftCmd = db.CreateCommand())
            {
                shiftCmd.Transaction = tx;
                shiftCmd.CommandText = "UPDATE ZoneChannels SET Position = Position - 1 WHERE ZoneId = $zoneId AND Position > $position;";
                shiftCmd.Parameters.Add(new SqliteParameter("$zoneId", zoneId));
                shiftCmd.Parameters.Add(new SqliteParameter("$position", position));
                shiftCmd.ExecuteNonQuery();
            }

            tx.Commit();
            LoadChannels();
        }
        catch (Exception ex)
        {
            SummaryText.Text = $"Could not remove channel: {ex.Message}";
        }
    }

    private void OnMoveUpClicked(object sender, RoutedEventArgs e) => MoveChannel(sender, -1);
    private void OnMoveDownClicked(object sender, RoutedEventArgs e) => MoveChannel(sender, 1);

    private void MoveChannel(object sender, int direction)
    {
        if (SelectedZoneName is not { } zoneName) return;
        if ((sender as Button)?.Tag is not int position) return;
        var otherPosition = position + direction;
        if (otherPosition < 0) return;

        try
        {
            using var db = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);
            var zoneId = GetZoneId(db, zoneName);

            using var checkCmd = db.CreateCommand();
            checkCmd.CommandText = "SELECT COUNT(*) FROM ZoneChannels WHERE ZoneId = $zoneId AND Position = $pos;";
            checkCmd.Parameters.Add(new SqliteParameter("$zoneId", zoneId));
            checkCmd.Parameters.Add(new SqliteParameter("$pos", otherPosition));
            var exists = Convert.ToInt32(checkCmd.ExecuteScalar()) > 0;
            if (!exists) return;

            using var tx = db.BeginTransaction();
            const int sentinel = -1;

            void SetPosition(int fromPosition, int toPosition)
            {
                using var cmd = db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE ZoneChannels SET Position = $to WHERE ZoneId = $zoneId AND Position = $from;";
                cmd.Parameters.Add(new SqliteParameter("$to", toPosition));
                cmd.Parameters.Add(new SqliteParameter("$zoneId", zoneId));
                cmd.Parameters.Add(new SqliteParameter("$from", fromPosition));
                cmd.ExecuteNonQuery();
            }

            SetPosition(position, sentinel);
            SetPosition(otherPosition, position);
            SetPosition(sentinel, otherPosition);

            tx.Commit();
            LoadChannels();
        }
        catch (Exception ex)
        {
            SummaryText.Text = $"Could not reorder channel: {ex.Message}";
        }
    }
}
