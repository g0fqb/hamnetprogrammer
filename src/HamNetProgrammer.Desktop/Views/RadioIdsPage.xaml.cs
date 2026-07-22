using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using HamNetProgrammer.Core.Data;
using HamNetProgrammer.Desktop.Utils;
using Microsoft.Data.Sqlite;

namespace HamNetProgrammer.Desktop.Views;

/// <summary>
/// Management page for the RadioIds table (callsign + DMR ID). Previously the only way a row got
/// in here was reading it back off a real radio's existing programming - no page anywhere let a
/// user set one up from scratch. Channels already pick a RadioIdId via a combo box populated
/// straight from this table (see ChannelEditDialog), so rows created here show up there
/// immediately with no further wiring needed.
/// </summary>
public sealed partial class RadioIdsPage : Page
{
    private sealed record RadioIdRow(long Id, string Callsign, string DmrIdDisplay);

    public RadioIdsPage()
    {
        this.InitializeComponent();
        this.NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Enabled;
        Load();
    }

    private void Load()
    {
        try
        {
            using var db = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT Id, Callsign, DmrId FROM RadioIds ORDER BY Callsign;";
            using var reader = cmd.ExecuteReader();

            var rows = new List<RadioIdRow>();
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                var callsign = reader.GetString(1);
                var dmrId = reader.IsDBNull(2) ? "(none)" : reader.GetInt64(2).ToString();
                rows.Add(new RadioIdRow(id, callsign, dmrId));
            }

            RadioIdListView.ItemsSource = rows;
            SummaryText.Text = $"{rows.Count} radio ID(s) ({AppPaths.CodeplugDbPath})";
        }
        catch (Exception ex)
        {
            SummaryText.Text = $"Could not open codeplug database: {ex.Message}";
        }
    }

    private async void OnNewClicked(object sender, RoutedEventArgs e)
    {
        var result = await RadioIdEditDialog.ShowAsync(this.XamlRoot);
        if (result is null) return;

        try
        {
            using var db = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);
            using var cmd = db.CreateCommand();
            cmd.CommandText = "INSERT INTO RadioIds (Callsign, DmrId) VALUES ($callsign, $dmrId);";
            cmd.Parameters.Add(new SqliteParameter("$callsign", result.Callsign));
            cmd.Parameters.Add(new SqliteParameter("$dmrId", (object?)result.DmrId ?? DBNull.Value));
            cmd.ExecuteNonQuery();
            Load();
        }
        catch (Exception ex)
        {
            SummaryText.Text = $"Could not add radio ID: {ex.Message}";
        }
    }

    private async void OnEditClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not long id) return;

        try
        {
            using var db = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);
            string callsign;
            uint? dmrId;
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "SELECT Callsign, DmrId FROM RadioIds WHERE Id = $id;";
                cmd.Parameters.Add(new SqliteParameter("$id", id));
                using var reader = cmd.ExecuteReader();
                if (!reader.Read()) return;
                callsign = reader.GetString(0);
                dmrId = reader.IsDBNull(1) ? null : (uint)reader.GetInt64(1);
            }

            var result = await RadioIdEditDialog.ShowAsync(this.XamlRoot, callsign, dmrId);
            if (result is null) return;

            using var updateCmd = db.CreateCommand();
            updateCmd.CommandText = "UPDATE RadioIds SET Callsign = $callsign, DmrId = $dmrId WHERE Id = $id;";
            updateCmd.Parameters.Add(new SqliteParameter("$callsign", result.Callsign));
            updateCmd.Parameters.Add(new SqliteParameter("$dmrId", (object?)result.DmrId ?? DBNull.Value));
            updateCmd.Parameters.Add(new SqliteParameter("$id", id));
            updateCmd.ExecuteNonQuery();
            Load();
        }
        catch (Exception ex)
        {
            SummaryText.Text = $"Could not save radio ID: {ex.Message}";
        }
    }

    private async void OnDeleteClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not long id) return;

        using var db = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);
        using var lookupCmd = db.CreateCommand();
        lookupCmd.CommandText = "SELECT Callsign FROM RadioIds WHERE Id = $id;";
        lookupCmd.Parameters.Add(new SqliteParameter("$id", id));
        var callsign = lookupCmd.ExecuteScalar() as string;
        if (callsign is null) return;

        var dialog = new ContentDialog
        {
            Title = "Delete Radio ID",
            Content = $"Delete radio ID '{callsign}'? Any channel currently using it will need a different Radio ID picked before it can be saved again.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            using var deleteCmd = db.CreateCommand();
            deleteCmd.CommandText = "DELETE FROM RadioIds WHERE Id = $id;";
            deleteCmd.Parameters.Add(new SqliteParameter("$id", id));
            deleteCmd.ExecuteNonQuery();
            Load();
        }
        catch (SqliteException)
        {
            SummaryText.Text = $"Could not delete '{callsign}': it's still used by one or more channels.";
        }
        catch (Exception ex)
        {
            SummaryText.Text = $"Could not delete radio ID: {ex.Message}";
        }
    }
}
