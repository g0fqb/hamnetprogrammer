using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using HamNetProgrammer.Core.Data;
using HamNetProgrammer.Desktop.Utils;
using Microsoft.Data.Sqlite;

namespace HamNetProgrammer.Desktop.Views;

public sealed partial class RoamingZonesPage : Page
{
    public sealed record MemberRow(int Position, string Name, string FrequencyMHz, string TalkGroup, string ColorCodeSlot);

    public RoamingZonesPage()
    {
        this.InitializeComponent();
        LoadRoamingZones();
    }

    private string? SelectedRoamingZoneName => RoamingZoneListView.SelectedItem as string;

    private void LoadRoamingZones(string? selectName = null)
    {
        try
        {
            using var db = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT Name FROM RoamingZones ORDER BY Name;";
            using var reader = cmd.ExecuteReader();

            var names = new List<string>();
            while (reader.Read())
                names.Add(reader.GetString(0));

            RoamingZoneListView.ItemsSource = names;
            SummaryText.Text = $"{names.Count} roaming zones ({AppPaths.CodeplugDbPath})";

            if (names.Count > 0)
            {
                var indexToSelect = selectName is not null ? names.IndexOf(selectName) : 0;
                RoamingZoneListView.SelectedIndex = indexToSelect >= 0 ? indexToSelect : 0;
            }
            else
            {
                MemberListView.ItemsSource = null;
            }
        }
        catch (Exception ex)
        {
            SummaryText.Text = $"Could not open codeplug database: {ex.Message}";
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => LoadMembers();

    private void LoadMembers()
    {
        if (SelectedRoamingZoneName is not { } name)
        {
            MemberListView.ItemsSource = null;
            return;
        }

        try
        {
            using var db = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT rzc.Position, c.Name, c.RxFrequencyHz, ct.Name AS TalkGroup, c.ColorCode, c.TimeSlot
                FROM RoamingZones rz
                JOIN RoamingZoneChannels rzc ON rzc.RoamingZoneId = rz.Id
                JOIN Channels c ON c.Id = rzc.ChannelId
                LEFT JOIN Contacts ct ON ct.Id = c.ContactId
                WHERE rz.Name = $name
                ORDER BY rzc.Position;
                """;
            cmd.Parameters.Add(new SqliteParameter("$name", name));
            using var reader = cmd.ExecuteReader();

            var rows = new List<MemberRow>();
            while (reader.Read())
            {
                var position = reader.GetInt32(0);
                var chName = reader.GetString(1);
                var rxHz = reader.GetInt64(2);
                var talkGroup = reader.IsDBNull(3) ? "-" : reader.GetString(3);
                var colorCode = reader.IsDBNull(4) ? "-" : reader.GetInt32(4).ToString();
                var timeSlot = reader.IsDBNull(5) ? "-" : reader.GetInt32(5).ToString();

                rows.Add(new MemberRow(position, chName, (rxHz / 1_000_000.0).ToString("F5"), talkGroup, $"{colorCode}/{timeSlot}"));
            }

            MemberListView.ItemsSource = rows;
        }
        catch (Exception ex)
        {
            SummaryText.Text = $"Could not load roaming zone '{name}': {ex.Message}";
        }
    }

    private static long GetRoamingZoneId(SqliteConnection db, string name)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT Id FROM RoamingZones WHERE Name = $name;";
        cmd.Parameters.Add(new SqliteParameter("$name", name));
        return (long)cmd.ExecuteScalar()!;
    }

    private async void OnNewClicked(object sender, RoutedEventArgs e)
    {
        var name = await TextInputDialog.ShowAsync(this.XamlRoot, "New Roaming Zone", "Roaming zone name", "");
        if (string.IsNullOrWhiteSpace(name)) return;

        try
        {
            using var db = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);
            using var cmd = db.CreateCommand();
            cmd.CommandText = "INSERT INTO RoamingZones (Name) VALUES ($name);";
            cmd.Parameters.Add(new SqliteParameter("$name", name.Trim()));
            cmd.ExecuteNonQuery();
            LoadRoamingZones(name.Trim());
        }
        catch (Exception ex)
        {
            SummaryText.Text = $"Could not create roaming zone: {ex.Message}";
        }
    }

    private async void OnRenameClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedRoamingZoneName is not { } name) return;

        var newName = await TextInputDialog.ShowAsync(this.XamlRoot, "Rename Roaming Zone", "Roaming zone name", name);
        if (string.IsNullOrWhiteSpace(newName) || newName.Trim() == name) return;

        try
        {
            using var db = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);
            using var cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE RoamingZones SET Name = $newName WHERE Name = $oldName;";
            cmd.Parameters.Add(new SqliteParameter("$newName", newName.Trim()));
            cmd.Parameters.Add(new SqliteParameter("$oldName", name));
            cmd.ExecuteNonQuery();
            LoadRoamingZones(newName.Trim());
        }
        catch (Exception ex)
        {
            SummaryText.Text = $"Could not rename roaming zone: {ex.Message}";
        }
    }

    private async void OnDeleteClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedRoamingZoneName is not { } name) return;

        var dialog = new ContentDialog
        {
            Title = "Delete Roaming Zone",
            Content = $"Delete roaming zone '{name}' and its channel membership? Channels themselves are not deleted.",
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
            cmd.CommandText = "DELETE FROM RoamingZones WHERE Name = $name;";
            cmd.Parameters.Add(new SqliteParameter("$name", name));
            cmd.ExecuteNonQuery();
            LoadRoamingZones();
        }
        catch (Exception ex)
        {
            SummaryText.Text = $"Could not delete roaming zone: {ex.Message}";
        }
    }

    private async void OnAddChannelClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedRoamingZoneName is not { } name) return;

        using var db = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);
        var roamingZoneId = GetRoamingZoneId(db, name);

        var candidates = ChannelQueries.GetChannelsNotIn(db, "RoamingZoneChannels", "RoamingZoneId", roamingZoneId);
        var picked = await ChannelPickerDialog.ShowAsync(this.XamlRoot, candidates);
        if (picked is null) return;

        try
        {
            using var insertCmd = db.CreateCommand();
            insertCmd.CommandText = """
                INSERT INTO RoamingZoneChannels (RoamingZoneId, ChannelId, Position)
                VALUES ($id, $channelId, (SELECT COALESCE(MAX(Position) + 1, 0) FROM RoamingZoneChannels WHERE RoamingZoneId = $id));
                """;
            insertCmd.Parameters.Add(new SqliteParameter("$id", roamingZoneId));
            insertCmd.Parameters.Add(new SqliteParameter("$channelId", picked.ChannelId));
            insertCmd.ExecuteNonQuery();
            LoadMembers();
        }
        catch (Exception ex)
        {
            SummaryText.Text = $"Could not add channel: {ex.Message}";
        }
    }

    private void OnRemoveChannelClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedRoamingZoneName is not { } name) return;
        if ((sender as Button)?.Tag is not int position) return;

        try
        {
            using var db = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);
            using var tx = db.BeginTransaction();
            var roamingZoneId = GetRoamingZoneId(db, name);

            using (var deleteCmd = db.CreateCommand())
            {
                deleteCmd.Transaction = tx;
                deleteCmd.CommandText = "DELETE FROM RoamingZoneChannels WHERE RoamingZoneId = $id AND Position = $position;";
                deleteCmd.Parameters.Add(new SqliteParameter("$id", roamingZoneId));
                deleteCmd.Parameters.Add(new SqliteParameter("$position", position));
                deleteCmd.ExecuteNonQuery();
            }

            using (var shiftCmd = db.CreateCommand())
            {
                shiftCmd.Transaction = tx;
                shiftCmd.CommandText = "UPDATE RoamingZoneChannels SET Position = Position - 1 WHERE RoamingZoneId = $id AND Position > $position;";
                shiftCmd.Parameters.Add(new SqliteParameter("$id", roamingZoneId));
                shiftCmd.Parameters.Add(new SqliteParameter("$position", position));
                shiftCmd.ExecuteNonQuery();
            }

            tx.Commit();
            LoadMembers();
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
        if (SelectedRoamingZoneName is not { } name) return;
        if ((sender as Button)?.Tag is not int position) return;
        var otherPosition = position + direction;
        if (otherPosition < 0) return;

        try
        {
            using var db = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);
            var roamingZoneId = GetRoamingZoneId(db, name);

            using var checkCmd = db.CreateCommand();
            checkCmd.CommandText = "SELECT COUNT(*) FROM RoamingZoneChannels WHERE RoamingZoneId = $id AND Position = $pos;";
            checkCmd.Parameters.Add(new SqliteParameter("$id", roamingZoneId));
            checkCmd.Parameters.Add(new SqliteParameter("$pos", otherPosition));
            var exists = Convert.ToInt32(checkCmd.ExecuteScalar()) > 0;
            if (!exists) return;

            using var tx = db.BeginTransaction();
            const int sentinel = -1;

            void SetPosition(int fromPosition, int toPosition)
            {
                using var cmd = db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE RoamingZoneChannels SET Position = $to WHERE RoamingZoneId = $id AND Position = $from;";
                cmd.Parameters.Add(new SqliteParameter("$to", toPosition));
                cmd.Parameters.Add(new SqliteParameter("$id", roamingZoneId));
                cmd.Parameters.Add(new SqliteParameter("$from", fromPosition));
                cmd.ExecuteNonQuery();
            }

            SetPosition(position, sentinel);
            SetPosition(otherPosition, position);
            SetPosition(sentinel, otherPosition);

            tx.Commit();
            LoadMembers();
        }
        catch (Exception ex)
        {
            SummaryText.Text = $"Could not reorder channel: {ex.Message}";
        }
    }
}
