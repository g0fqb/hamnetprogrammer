using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using HamNetProgrammer.Core.Data;
using HamNetProgrammer.Core.Online;
using HamNetProgrammer.Desktop.Utils;
using Microsoft.Data.Sqlite;

namespace HamNetProgrammer.Desktop.Views;

/// <summary>
/// Browse/search Contacts (talkgroups and your own individual contacts). One search box with a
/// Talkgroups/My Contacts toggle, not two separate boxes each with their own results list - the
/// earlier two-box layout was confusing about which box fed which results. My Contacts mode
/// searches your own already-added Private contacts locally, plus radioid.net live for anyone not
/// added yet. Talkgroups refresh automatically on app launch (see App.xaml.cs) - there's no manual
/// import button here anymore; it looked like it should prompt for a CSV, when it was really just
/// pulling from the shared backend.
/// </summary>
public sealed partial class ContactsPage : Page
{
    private sealed record ContactRow(long? Id, string Name, string CallType, string DmrIdDisplay, string Detail,
        RadioIdSearchResult? Remote, NetworkTalkGroup? PendingTalkGroup = null)
    {
        public Visibility AddButtonVisibility => Remote is not null || PendingTalkGroup is not null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility RemoveButtonVisibility => Id is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    private const int MaxResults = 200;
    private int _searchToken;
    private List<string> _networkFilterChoices = [];

    private bool MyContactsMode => MyContactsModeRadio.IsChecked == true;
    private string? SelectedNetwork => NetworkFilter.SelectedItem as string is { } n && n != "All networks" ? n : null;

    public ContactsPage()
    {
        this.InitializeComponent();
        this.NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Enabled;
        LoadSummary();
        RefreshNetworkFilterChoices();
        RunSearch();
        _ = LoadFullNetworkFilterChoicesAsync();
    }

    /// <summary>Widens the network filter to every network the live directory actually has
    /// (Brandmeister/TGIF/FreeDMR), proactively on page load - previously this only happened as a
    /// side effect of the first 2+-character search actually running (see
    /// RunTalkgroupSearchAsync), so opening the page and just looking at the filter without typing
    /// anything only ever showed "FreeDMR" as a choice, with no way to even select the other two.</summary>
    private async Task LoadFullNetworkFilterChoicesAsync()
    {
        var networkData = await NetworkTalkGroupCache.GetAsync();
        if (networkData.Count > 0)
            RefreshNetworkFilterChoices(networkData.Select(t => t.Network).Append("Manually added"));
    }

    /// <summary>Seeded from local data immediately, widened to every network the live directory
    /// actually has once NetworkTalkGroupCache's background fetch completes - mirrors
    /// TalkGroupPicker's identical logic (kept a separate copy since this page's RadioButtons and
    /// SelectionChanged wiring differ enough from the picker's that sharing the method wasn't
    /// worth the indirection).</summary>
    private void RefreshNetworkFilterChoices(IEnumerable<string>? networks = null)
    {
        List<string> choices;
        if (networks is not null)
        {
            choices = networks.Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }
        else
        {
            using var db = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT COALESCE(Network, 'Manually added') FROM Contacts WHERE CallType = 'Group';";
            using var reader = cmd.ExecuteReader();
            choices = [];
            while (reader.Read()) choices.Add(reader.GetString(0));
            choices.Sort(StringComparer.OrdinalIgnoreCase);
        }

        if (choices.SequenceEqual(_networkFilterChoices)) return;
        var previousSelection = NetworkFilter.SelectedItem as string;
        _networkFilterChoices = choices;

        NetworkFilter.Items.Clear();
        NetworkFilter.Items.Add("All networks");
        foreach (var network in choices) NetworkFilter.Items.Add(network);
        NetworkFilter.MaxColumns = choices.Count + 1;
        NetworkFilter.SelectedItem = previousSelection is not null && (previousSelection == "All networks" || choices.Contains(previousSelection))
            ? previousSelection
            : "All networks";
    }

    private void OnNetworkFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SearchBox is null) return; // guard against firing during InitializeComponent
        RunSearch();
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

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) => RunSearch();

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        // Guard against firing during InitializeComponent, before SearchBox/DetailColumnHeader exist.
        if (SearchBox is null) return;

        SearchBox.PlaceholderText = MyContactsMode
            ? "Type a callsign or name..."
            : "Type a name or talkgroup number...";
        DetailColumnHeader.Text = MyContactsMode ? "Country" : "Network";
        NetworkFilter.Visibility = MyContactsMode ? Visibility.Collapsed : Visibility.Visible;
        RunSearch();
    }

    private void RunSearch()
    {
        if (MyContactsMode) RunMyContactsSearchAsync(SearchBox.Text);
        else RunTalkgroupSearchAsync(SearchBox.Text);
    }

    private List<ContactRow> QueryLocalTalkgroups(string trimmedQuery, string? network)
    {
        using var db = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);
        using var cmd = db.CreateCommand();

        var conditions = new List<string> { "CallType = 'Group'" };
        if (trimmedQuery.Length > 0) conditions.Add("(Name LIKE $pattern OR CAST(DmrId AS TEXT) LIKE $pattern)");
        // "Manually added" is a display-only label for NULL, not a real stored value - see
        // RefreshNetworkFilterChoices - so selecting it needs "IS NULL", not a literal string match.
        if (network == "Manually added") conditions.Add("Network IS NULL");
        else if (network is not null) conditions.Add("Network = $network");
        cmd.CommandText = $"SELECT Id, Name, DmrId, Network FROM Contacts WHERE {string.Join(" AND ", conditions)} ORDER BY Name LIMIT $limit;";
        if (trimmedQuery.Length > 0) cmd.Parameters.Add(new SqliteParameter("$pattern", $"%{trimmedQuery}%"));
        if (network is not null && network != "Manually added") cmd.Parameters.Add(new SqliteParameter("$network", network));
        cmd.Parameters.Add(new SqliteParameter("$limit", MaxResults));

        using var reader = cmd.ExecuteReader();
        var rows = new List<ContactRow>();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            var name = reader.GetString(1);
            var dmrId = reader.IsDBNull(2) ? "-" : reader.GetInt64(2).ToString();
            var rowNetwork = reader.IsDBNull(3) ? "Manually added" : reader.GetString(3);
            rows.Add(new ContactRow(id, name, "Group", dmrId, rowNetwork, null));
        }
        return rows;
    }

    /// <summary>Local matches show immediately, then a debounced live search against the full
    /// Brandmeister/TGIF/FreeDMR directory (NetworkTalkGroupCache - the local table alone only
    /// ever has FreeDMR, deliberately, see the page's intro text) supplements with anyone matching
    /// who isn't already local - same "local first, remote supplements" shape as
    /// RunMyContactsSearchAsync, deduped by (DmrId, Network) rather than display text.</summary>
    private async void RunTalkgroupSearchAsync(string query)
    {
        var trimmed = query.Trim();
        var network = SelectedNetwork;
        var myToken = ++_searchToken;

        List<ContactRow> localRows;
        try
        {
            localRows = QueryLocalTalkgroups(trimmed, network);
        }
        catch (Exception ex)
        {
            SearchStatusText.Text = $"Search failed: {ex.Message}";
            return;
        }
        ResultsListView.ItemsSource = localRows;
        SearchStatusText.Text = $"{localRows.Count} talkgroup(s).";

        if (trimmed.Length < 2) return;

        try { await Task.Delay(350); } catch { return; }
        if (myToken != _searchToken) return;

        SearchStatusText.Text = $"{localRows.Count} talkgroup(s) - searching the full network directory...";
        var networkData = await NetworkTalkGroupCache.GetAsync();
        if (myToken != _searchToken) return;

        RefreshNetworkFilterChoices(networkData.Select(t => t.Network));

        var localKeys = localRows
            .Select(r => (DmrId: long.TryParse(r.DmrIdDisplay, out var id) ? id : (long?)null, Network: r.Detail))
            .Where(k => k.DmrId is not null)
            .ToHashSet();

        var remoteRows = networkData
            .Where(t => network is null || t.Network == network)
            .Where(t => !localKeys.Contains((t.DmrId, t.Network)))
            .Where(t => t.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase) || t.DmrId.ToString().Contains(trimmed))
            .Take(MaxResults)
            .Select(t => new ContactRow(null, t.Name, "Group", t.DmrId.ToString(), t.Network, null, t));

        var combined = localRows.Concat(remoteRows).ToList();
        ResultsListView.ItemsSource = combined;
        SearchStatusText.Text = combined.Count == localRows.Count
            ? $"{localRows.Count} talkgroup(s)."
            : $"{localRows.Count} talkgroup(s), {combined.Count - localRows.Count} more found on the network.";
    }

    private List<ContactRow> QueryLocalPrivateContacts(string trimmedQuery)
    {
        using var db = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);
        using var cmd = db.CreateCommand();
        var conditions = new List<string> { "CallType = 'Private'" };
        if (trimmedQuery.Length > 0) conditions.Add("(Name LIKE $pattern OR CAST(DmrId AS TEXT) LIKE $pattern)");
        cmd.CommandText = $"SELECT Id, Name, DmrId, Network FROM Contacts WHERE {string.Join(" AND ", conditions)} ORDER BY Name LIMIT $limit;";
        if (trimmedQuery.Length > 0) cmd.Parameters.Add(new SqliteParameter("$pattern", $"%{trimmedQuery}%"));
        cmd.Parameters.Add(new SqliteParameter("$limit", MaxResults));

        using var reader = cmd.ExecuteReader();
        var rows = new List<ContactRow>();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            var name = reader.GetString(1);
            var dmrId = reader.IsDBNull(2) ? null : (long?)reader.GetInt64(2);
            var network = reader.IsDBNull(3) ? "-" : reader.GetString(3);
            rows.Add(new ContactRow(id, name, "Private", dmrId?.ToString() ?? "-", network, null));
        }
        return rows;
    }

    /// <summary>My Contacts mode: your own already-added Private contacts show immediately (no
    /// network needed), then a debounced radioid.net search supplements with anyone matching who
    /// isn't already in your list - deduped by DMR ID, not by display text (display text differs
    /// between a freshly-searched remote result and how the same person's Name ends up stored once
    /// added, so comparing text was unreliable).</summary>
    private async void RunMyContactsSearchAsync(string query)
    {
        var trimmed = query.Trim();
        var myToken = ++_searchToken;

        List<ContactRow> localRows;
        try
        {
            localRows = QueryLocalPrivateContacts(trimmed);
        }
        catch (Exception ex)
        {
            SearchStatusText.Text = $"Search failed: {ex.Message}";
            return;
        }
        ResultsListView.ItemsSource = localRows;
        SearchStatusText.Text = $"{localRows.Count} of your contact(s).";

        if (trimmed.Length < 2) return;

        try { await Task.Delay(350); } catch { return; }
        if (myToken != _searchToken) return;

        SearchStatusText.Text = $"{localRows.Count} of your contact(s) - searching radioid.net...";
        var remoteResults = await RadioIdNetworkSearch.SearchAsync(trimmed);
        if (myToken != _searchToken) return;

        var localDmrIds = localRows
            .Select(r => long.TryParse(r.DmrIdDisplay, out var id) ? id : (long?)null)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .ToHashSet();

        var remoteRows = remoteResults
            .Where(r => !localDmrIds.Contains(r.DmrId))
            .Select(r => new ContactRow(null, $"{r.Callsign} - {r.Name}", "Private", r.DmrId.ToString(), r.Country, r));

        var combined = localRows.Concat(remoteRows).ToList();
        ResultsListView.ItemsSource = combined;
        SearchStatusText.Text = combined.Count == localRows.Count
            ? $"{localRows.Count} of your contact(s)."
            : $"{localRows.Count} of your contact(s), {combined.Count - localRows.Count} more found on radioid.net.";
    }

    private void OnAddClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ContactRow row) return;
        if (row.Remote is null && row.PendingTalkGroup is null) return;

        try
        {
            using var db = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);
            if (row.Remote is { } remote)
            {
                ContactPicker.InsertPrivateContact(db, remote);
                SearchStatusText.Text = $"Added {remote.Callsign} as a Private contact.";
            }
            else if (row.PendingTalkGroup is { } tg)
            {
                TalkGroupPicker.InsertNetworkTalkGroup(db, tg);
                SearchStatusText.Text = $"Added '{tg.Name}' (TG{tg.DmrId}, {tg.Network}).";
            }
            LoadSummary();
            RunSearch();
        }
        catch (Exception ex)
        {
            SearchStatusText.Text = $"Could not add: {ex.Message}";
        }
    }

    private async void OnRemoveClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ContactRow row || row.Id is null) return;

        var dialog = new ContentDialog
        {
            Title = "Remove Contact",
            Content = $"Remove '{row.Name}'? Any channel currently using it will need a different contact picked before it can be saved again.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            using var db = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);
            using var cmd = db.CreateCommand();
            cmd.CommandText = "DELETE FROM Contacts WHERE Id = $id;";
            cmd.Parameters.Add(new SqliteParameter("$id", row.Id));
            cmd.ExecuteNonQuery();
            LoadSummary();
            RunSearch();
        }
        catch (SqliteException)
        {
            SearchStatusText.Text = $"Could not remove '{row.Name}': it's still used by one or more channels.";
        }
        catch (Exception ex)
        {
            SearchStatusText.Text = $"Could not remove '{row.Name}': {ex.Message}";
        }
    }
}
