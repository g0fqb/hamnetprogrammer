using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using HamNetProgrammer.Core.Data;
using HamNetProgrammer.Desktop.Utils;
using Microsoft.Data.Sqlite;

namespace HamNetProgrammer.Desktop.Views;

public sealed partial class ScanListsPage : Page
{
    public sealed record MemberRow(int Position, string Name, string FrequencyMHz);

    public ScanListsPage()
    {
        this.InitializeComponent();
        LoadScanLists();
    }

    private string? SelectedScanListName => ScanListListView.SelectedItem as string;

    private void LoadScanLists(string? selectName = null)
    {
        try
        {
            using var db = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT Name FROM ScanLists ORDER BY Name;";
            using var reader = cmd.ExecuteReader();

            var names = new List<string>();
            while (reader.Read())
                names.Add(reader.GetString(0));

            ScanListListView.ItemsSource = names;
            SummaryText.Text = $"{names.Count} scan lists ({AppPaths.CodeplugDbPath})";

            if (names.Count > 0)
            {
                var indexToSelect = selectName is not null ? names.IndexOf(selectName) : 0;
                ScanListListView.SelectedIndex = indexToSelect >= 0 ? indexToSelect : 0;
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
        if (SelectedScanListName is not { } name)
        {
            MemberListView.ItemsSource = null;
            return;
        }

        try
        {
            using var db = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT slc.Position, c.Name, c.RxFrequencyHz
                FROM ScanLists sl
                JOIN ScanListChannels slc ON slc.ScanListId = sl.Id
                JOIN Channels c ON c.Id = slc.ChannelId
                WHERE sl.Name = $name
                ORDER BY slc.Position;
                """;
            cmd.Parameters.Add(new SqliteParameter("$name", name));
            using var reader = cmd.ExecuteReader();

            var rows = new List<MemberRow>();
            while (reader.Read())
            {
                var position = reader.GetInt32(0);
                var chName = reader.GetString(1);
                var rxHz = reader.GetInt64(2);
                rows.Add(new MemberRow(position, chName, (rxHz / 1_000_000.0).ToString("F5")));
            }

            MemberListView.ItemsSource = rows;
        }
        catch (Exception ex)
        {
            SummaryText.Text = $"Could not load scan list '{name}': {ex.Message}";
        }
    }

    private static long GetScanListId(SqliteConnection db, string name)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT Id FROM ScanLists WHERE Name = $name;";
        cmd.Parameters.Add(new SqliteParameter("$name", name));
        return (long)cmd.ExecuteScalar()!;
    }

    private async void OnNewClicked(object sender, RoutedEventArgs e)
    {
        var name = await TextInputDialog.ShowAsync(this.XamlRoot, "New Scan List", "Scan list name", "");
        if (string.IsNullOrWhiteSpace(name)) return;

        try
        {
            using var db = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);
            using var cmd = db.CreateCommand();
            cmd.CommandText = "INSERT INTO ScanLists (Name) VALUES ($name);";
            cmd.Parameters.Add(new SqliteParameter("$name", name.Trim()));
            cmd.ExecuteNonQuery();
            LoadScanLists(name.Trim());
        }
        catch (Exception ex)
        {
            SummaryText.Text = $"Could not create scan list: {ex.Message}";
        }
    }

    private async void OnRenameClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedScanListName is not { } name) return;

        var newName = await TextInputDialog.ShowAsync(this.XamlRoot, "Rename Scan List", "Scan list name", name);
        if (string.IsNullOrWhiteSpace(newName) || newName.Trim() == name) return;

        try
        {
            using var db = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);
            using var cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE ScanLists SET Name = $newName WHERE Name = $oldName;";
            cmd.Parameters.Add(new SqliteParameter("$newName", newName.Trim()));
            cmd.Parameters.Add(new SqliteParameter("$oldName", name));
            cmd.ExecuteNonQuery();
            LoadScanLists(newName.Trim());
        }
        catch (Exception ex)
        {
            SummaryText.Text = $"Could not rename scan list: {ex.Message}";
        }
    }

    private async void OnDeleteClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedScanListName is not { } name) return;

        var dialog = new ContentDialog
        {
            Title = "Delete Scan List",
            Content = $"Delete scan list '{name}' and its channel membership? Channels themselves are not deleted.",
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
            cmd.CommandText = "DELETE FROM ScanLists WHERE Name = $name;";
            cmd.Parameters.Add(new SqliteParameter("$name", name));
            cmd.ExecuteNonQuery();
            LoadScanLists();
        }
        catch (Exception ex)
        {
            SummaryText.Text = $"Could not delete scan list: {ex.Message}";
        }
    }

    private async void OnSettingsClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedScanListName is not { } name) return;
        using var db = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);
        var scanListId = GetScanListId(db, name);
        await ScanListSettingsDialog.ShowAsync(this.XamlRoot, scanListId);
    }

    private async void OnAddChannelClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedScanListName is not { } name) return;

        using var db = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);
        var scanListId = GetScanListId(db, name);

        var candidates = ChannelQueries.GetChannelsNotIn(db, "ScanListChannels", "ScanListId", scanListId);
        var picked = await ChannelPickerDialog.ShowAsync(this.XamlRoot, candidates);
        if (picked is null) return;

        try
        {
            using var insertCmd = db.CreateCommand();
            insertCmd.CommandText = """
                INSERT INTO ScanListChannels (ScanListId, ChannelId, Position)
                VALUES ($id, $channelId, (SELECT COALESCE(MAX(Position) + 1, 0) FROM ScanListChannels WHERE ScanListId = $id));
                """;
            insertCmd.Parameters.Add(new SqliteParameter("$id", scanListId));
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
        if (SelectedScanListName is not { } name) return;
        if ((sender as Button)?.Tag is not int position) return;

        try
        {
            using var db = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);
            using var tx = db.BeginTransaction();
            var scanListId = GetScanListId(db, name);

            using (var deleteCmd = db.CreateCommand())
            {
                deleteCmd.Transaction = tx;
                deleteCmd.CommandText = "DELETE FROM ScanListChannels WHERE ScanListId = $id AND Position = $position;";
                deleteCmd.Parameters.Add(new SqliteParameter("$id", scanListId));
                deleteCmd.Parameters.Add(new SqliteParameter("$position", position));
                deleteCmd.ExecuteNonQuery();
            }

            using (var shiftCmd = db.CreateCommand())
            {
                shiftCmd.Transaction = tx;
                shiftCmd.CommandText = "UPDATE ScanListChannels SET Position = Position - 1 WHERE ScanListId = $id AND Position > $position;";
                shiftCmd.Parameters.Add(new SqliteParameter("$id", scanListId));
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
        if (SelectedScanListName is not { } name) return;
        if ((sender as Button)?.Tag is not int position) return;
        var otherPosition = position + direction;
        if (otherPosition < 0) return;

        try
        {
            using var db = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);
            var scanListId = GetScanListId(db, name);

            using var checkCmd = db.CreateCommand();
            checkCmd.CommandText = "SELECT COUNT(*) FROM ScanListChannels WHERE ScanListId = $id AND Position = $pos;";
            checkCmd.Parameters.Add(new SqliteParameter("$id", scanListId));
            checkCmd.Parameters.Add(new SqliteParameter("$pos", otherPosition));
            var exists = Convert.ToInt32(checkCmd.ExecuteScalar()) > 0;
            if (!exists) return;

            using var tx = db.BeginTransaction();
            const int sentinel = -1;

            void SetPosition(int fromPosition, int toPosition)
            {
                using var cmd = db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE ScanListChannels SET Position = $to WHERE ScanListId = $id AND Position = $from;";
                cmd.Parameters.Add(new SqliteParameter("$to", toPosition));
                cmd.Parameters.Add(new SqliteParameter("$id", scanListId));
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
