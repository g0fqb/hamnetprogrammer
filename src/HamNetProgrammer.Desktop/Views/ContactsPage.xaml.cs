using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using HamNetProgrammer.Core.Data;
using HamNetProgrammer.Core.Online;
using HamNetProgrammer.Desktop.Utils;
using Microsoft.Data.Sqlite;

namespace HamNetProgrammer.Desktop.Views;

/// <summary>
/// Browse/search/import for Contacts (digital talkgroups) - phase 2 of the talkgroup work, after
/// the ad-hoc create-on-save picker in ChannelEditDialog/TalkGroupPicker. Import buttons pull real
/// talkgroup lists from Brandmeister/TGIF/FreeDMR's public APIs via TalkGroupNetworkImporter;
/// search is a live SQL query rather than loading all rows into memory, since a full import across
/// all three networks is several thousand contacts.
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
        ImportBrandmeisterButton.IsEnabled = !busy;
        ImportTgifButton.IsEnabled = !busy;
        ImportFreeDmrButton.IsEnabled = !busy;
        ImportStatusText.Text = status;
    }

    private async void OnImportBrandmeisterClicked(object sender, RoutedEventArgs e) =>
        await RunImportAsync("Brandmeister", db => TalkGroupNetworkImporter.ImportBrandmeisterAsync(db));

    private async void OnImportTgifClicked(object sender, RoutedEventArgs e) =>
        await RunImportAsync("TGIF", db => TalkGroupNetworkImporter.ImportTgifAsync(db));

    private async void OnImportFreeDmrClicked(object sender, RoutedEventArgs e) =>
        await RunImportAsync("FreeDMR", db => TalkGroupNetworkImporter.ImportFreeDmrAsync(db));

    private async Task RunImportAsync(string network, Func<SqliteConnection, Task<TalkGroupImportResult>> import)
    {
        if (_busy) return;
        SetBusy(true, $"Importing from {network}...");

        try
        {
            var result = await Task.Run(async () =>
            {
                using var db = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);
                return await import(db);
            });

            var message = $"{result.Network}: {result.Added} added, {result.Updated} updated, {result.Unchanged} unchanged" +
                          (result.Warnings.Count > 0 ? $", {result.Warnings.Count} skipped" : "");
            SetBusy(false, message);
            LoadSummary();
            RunSearch(SearchBox.Text);
        }
        catch (Exception ex)
        {
            SetBusy(false, $"Import from {network} failed: {ex.Message}");
        }
    }
}
