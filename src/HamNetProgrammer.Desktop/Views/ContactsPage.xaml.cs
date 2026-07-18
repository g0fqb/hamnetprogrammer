using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using HamNetProgrammer.Core.Data;
using HamNetProgrammer.Core.Online;
using HamNetProgrammer.Desktop.Utils;
using Microsoft.Data.Sqlite;

namespace HamNetProgrammer.Desktop.Views;

/// <summary>
/// Browse/search/import for Contacts (digital talkgroups) - phase 2 of the talkgroup work, after
/// the ad-hoc create-on-save picker in ChannelEditDialog/TalkGroupPicker. The Import button pulls
/// the shared, pre-merged talkgroup list from our backend (see TalkGroupNetworkImporter) rather
/// than hitting Brandmeister/TGIF/FreeDMR directly, so every install stays consistent; search is a
/// live SQL query rather than loading all rows into memory, since a full import is several
/// thousand contacts.
/// </summary>
public sealed partial class ContactsPage : Page
{
    private sealed record ContactRow(string Name, string DmrIdDisplay, string NetworkDisplay);

    private const int MaxResults = 200;
    private bool _busy;

    public ContactsPage()
    {
        this.InitializeComponent();
        this.NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Enabled;
        LoadSummary();
        RunSearch("");
    }

    private void LoadSummary()
    {
        try
        {
            using var db = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*), COUNT(Network) FROM Contacts;";
            using var reader = cmd.ExecuteReader();
            reader.Read();
            var total = reader.GetInt32(0);
            var imported = reader.GetInt32(1);
            SummaryText.Text = $"{total} contacts ({imported} imported, {total - imported} manual)";
        }
        catch (Exception ex)
        {
            SummaryText.Text = $"Could not open codeplug database: {ex.Message}";
        }
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) => RunSearch(SearchBox.Text);

    private void RunSearch(string query)
    {
        try
        {
            using var db = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);
            using var cmd = db.CreateCommand();
            var trimmed = query.Trim();
            if (trimmed.Length == 0)
            {
                cmd.CommandText = "SELECT Name, DmrId, Network FROM Contacts ORDER BY Name LIMIT $limit;";
            }
            else
            {
                cmd.CommandText = """
                    SELECT Name, DmrId, Network FROM Contacts
                    WHERE Name LIKE $pattern OR CAST(DmrId AS TEXT) LIKE $pattern
                    ORDER BY Name LIMIT $limit;
                    """;
                cmd.Parameters.Add(new SqliteParameter("$pattern", $"%{trimmed}%"));
            }
            cmd.Parameters.Add(new SqliteParameter("$limit", MaxResults));

            using var reader = cmd.ExecuteReader();
            var rows = new List<ContactRow>();
            while (reader.Read())
            {
                var name = reader.GetString(0);
                var dmrId = reader.IsDBNull(1) ? "-" : reader.GetInt64(1).ToString();
                var network = reader.IsDBNull(2) ? "-" : reader.GetString(2);
                rows.Add(new ContactRow(name, dmrId, network));
            }
            ResultsListView.ItemsSource = rows;
        }
        catch (Exception ex)
        {
            SummaryText.Text = $"Search failed: {ex.Message}";
        }
    }

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        ImportButton.IsEnabled = !busy;
        ImportStatusText.Text = status;
    }

    private async void OnImportClicked(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true, "Importing talkgroups...");

        try
        {
            var result = await Task.Run(async () =>
            {
                using var db = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);
                return await TalkGroupNetworkImporter.ImportAsync(db);
            });

            var message = $"{result.Added} added, {result.Updated} updated, {result.Unchanged} unchanged" +
                          (result.Warnings.Count > 0 ? $", {result.Warnings.Count} skipped" : "");
            SetBusy(false, message);
            LoadSummary();
            RunSearch(SearchBox.Text);
        }
        catch (Exception ex)
        {
            SetBusy(false, $"Import failed: {ex.Message}");
        }
    }
}
