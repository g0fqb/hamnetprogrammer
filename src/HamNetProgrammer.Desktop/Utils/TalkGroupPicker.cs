using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Data.Sqlite;
using HamNetProgrammer.Core.Online;

namespace HamNetProgrammer.Desktop.Utils;

/// <summary>
/// Type-to-search talkgroup picker (the point of moving off CPS's scroll-through-hundreds picker),
/// with create-on-save for names that don't match anything - the Contacts table only holds
/// talkgroups already used somewhere in the codeplug, not a full canonical database, so typing a
/// new one is a first-class path, not an error. Shared between ChannelEditDialog and any other
/// place a talkgroup needs picking (e.g. RadioSettingsPage's APRS talkgroup).
///
/// Searches BOTH the local Contacts table AND, live, the full Brandmeister/TGIF/FreeDMR directory
/// (same source TalkGroupNetworkImporter.ImportAsync uses) - the local table alone only ever has
/// FreeDMR (the app's default sync network, deliberately, to avoid re-triggering the 2026-07-19/20
/// radio-freeze incident from syncing too many duplicate-dense entries to the ON-DEVICE list) -
/// but that restriction is about what gets WRITTEN to the radio, not what's searchable while
/// building a codeplug. Picking a remote-only result creates just that one local Contact row, the
/// same "insert on pick" pattern ContactPicker's Private-call search already uses against
/// radioid.net - never a bulk import of the other networks.
/// </summary>
public static class TalkGroupPicker
{
    public sealed record Option(long? Id, string Display, long? PendingDmrId = null, string? PendingName = null, string? PendingNetwork = null)
    {
        public bool IsPending => Id is null && PendingName is not null;
    }

    public sealed class Picker
    {
        public required FrameworkElement Container { get; init; }
        public required Func<SqliteConnection, long?> GetOrCreateId { get; init; }
    }

    // Matches a leading "TG12345" in typed text so a new talkgroup's real DMR ID can be captured
    // the same way the CSV importer does, e.g. "TG91 World-wide" -> DmrId 91.
    private static readonly Regex TalkGroupIdPattern = new(@"^TG(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private sealed record ContactRow(long Id, string Name, long? DmrId, string Network);

    /// <param name="preferredNetwork">Pre-selects this network in the filter instead of "All
    /// networks", when the caller already knows what it should be - e.g. a zone maps to one
    /// hotspot, which is configured for exactly one DMR network at a time (confirmed by the user,
    /// who runs several MMDVMs, each on a different network), so a new channel being added to an
    /// established zone should default to searching that zone's own network, not everything.</param>
    public static Picker Build(SqliteConnection db, long? initialContactId, string placeholder, Action<Option>? onChosen = null, string? preferredNetwork = null)
    {
        // Showing just the Name left the actual DMR ID and network both invisible - a real problem
        // for validation (e.g. telling "TG2348 SOARC" and "SOARC Sherwood Observatory" apart at a
        // glance, or knowing whether a result is even on the network your hotspot uses). Appends
        // "(TG<id>)" unless the name already visibly starts with it, and "[Network]" ("[Manually
        // added]" for a contact with no network tag - not "[Local]", which would be confusable
        // with DMR's own real "TG9 Local" talkgroup).
        var rows = new List<ContactRow>();
        using (var cmd = db.CreateCommand())
        {
            // CallType = 'Group' - this picker is for talkgroups specifically (Private contacts
            // have their own separate picker in ContactPicker); the previous unfiltered query let
            // Private contacts show up here too, which was never intended.
            cmd.CommandText = "SELECT Id, Name, DmrId, Network FROM Contacts WHERE CallType = 'Group' ORDER BY Name;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                rows.Add(new ContactRow(reader.GetInt64(0), reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetInt64(2), reader.IsDBNull(3) ? "Manually added" : reader.GetString(3)));
        }
        var localDmrIdsByNetwork = rows.Where(r => r.DmrId is not null).Select(r => (r.DmrId!.Value, r.Network)).ToHashSet();

        Option ToOption(ContactRow r) => new(r.Id, FormatDisplay(r.Name, r.DmrId, r.Network));
        Option ToPendingOption(NetworkTalkGroup t) => new(null, FormatDisplay(t.Name, t.DmrId, t.Network),
            t.DmrId, t.Name, t.Network);

        var options = new List<Option> { new(null, "(none)") };
        options.AddRange(rows.Select(ToOption));

        // Network filter as explicit RadioButtons rather than relying on someone noticing they can
        // type a network name to narrow results - a menu of clearly-labeled choices means nobody
        // can end up picking the wrong network's entry for the same number by accident. Seeded
        // from local data immediately, then widened to every network the live directory actually
        // has (Brandmeister/TGIF/FreeDMR) once that background fetch completes - see RefreshFilterChoices.
        var networkFilter = new RadioButtons();
        var currentNetworkChoices = new List<string>();
        void RefreshFilterChoices(IEnumerable<string> networks)
        {
            var previousSelection = networkFilter.SelectedItem as string;
            var choices = networks.Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            if (choices.SequenceEqual(currentNetworkChoices)) return;
            currentNetworkChoices = choices;

            networkFilter.Items.Clear();
            networkFilter.Items.Add("All networks");
            foreach (var network in choices) networkFilter.Items.Add(network);
            networkFilter.MaxColumns = choices.Count + 1;
            networkFilter.SelectedItem = previousSelection is not null && choices.Contains(previousSelection)
                ? previousSelection
                : preferredNetwork is not null && choices.Contains(preferredNetwork)
                    ? preferredNetwork
                    : "All networks";
        }
        RefreshFilterChoices(rows.Select(r => r.Network));

        var selected = options.FirstOrDefault(o => o.Id == initialContactId) ?? options[0];
        var box = new AutoSuggestBox
        {
            PlaceholderText = placeholder,
            Text = selected.Display == "(none)" ? "" : selected.Display,
            TextMemberPath = "Display",
            DisplayMemberPath = "Display",
        };

        bool PassesNetworkFilter(string network) =>
            networkFilter.SelectedItem is not string chosen || chosen == "All networks" || chosen == network;

        var searchToken = 0;
        async void RefreshSuggestions()
        {
            var query = box.Text.Trim();
            var myToken = ++searchToken;

            var localMatches = new List<Option>();
            if (string.IsNullOrEmpty(query)) localMatches.Add(options[0]); // "(none)"
            localMatches.AddRange(rows
                .Where(r => PassesNetworkFilter(r.Network))
                .Select(ToOption)
                .Where(o => string.IsNullOrEmpty(query) || o.Display.Contains(query, StringComparison.OrdinalIgnoreCase)));
            box.ItemsSource = localMatches.Take(30).ToList();

            if (query.Length < 2) return;

            var networkData = await NetworkTalkGroupCache.GetAsync();
            if (myToken != searchToken) return; // a newer keystroke already started a fresher search

            RefreshFilterChoices(rows.Select(r => r.Network).Concat(networkData.Select(t => t.Network)));

            var remoteMatches = networkData
                .Where(t => !localDmrIdsByNetwork.Contains((t.DmrId, t.Network)))
                .Where(t => PassesNetworkFilter(t.Network))
                .Where(t => t.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || t.DmrId.ToString().Contains(query))
                .Take(30)
                .Select(ToPendingOption);

            box.ItemsSource = localMatches.Concat(remoteMatches).Take(40).ToList();
        }

        box.TextChanged += (_, args) =>
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            RefreshSuggestions();
        };
        networkFilter.SelectionChanged += (_, _) => RefreshSuggestions();
        box.SuggestionChosen += (_, args) =>
        {
            if (args.SelectedItem is not Option chosen) return;
            selected = chosen;
            onChosen?.Invoke(chosen);
        };

        var container = new StackPanel { Spacing = 4 };
        container.Children.Add(networkFilter);
        container.Children.Add(box);

        return new Picker
        {
            Container = container,
            GetOrCreateId = liveDb =>
            {
                var typed = box.Text.Trim();
                if (typed.Length == 0) return null;

                if (string.Equals(typed, selected.Display, StringComparison.OrdinalIgnoreCase))
                {
                    if (selected.Id is { } existingId) return existingId;
                    if (selected.IsPending) return InsertPending(liveDb, selected);
                }

                var exact = options.FirstOrDefault(o => string.Equals(o.Display, typed, StringComparison.OrdinalIgnoreCase));
                if (exact is not null) return exact.Id;

                using var selectCmd = liveDb.CreateCommand();
                selectCmd.CommandText = "SELECT Id FROM Contacts WHERE Name = $name AND CallType = 'Group';";
                selectCmd.Parameters.Add(new SqliteParameter("$name", typed));
                if (selectCmd.ExecuteScalar() is long existingId2) return existingId2;

                var dmrIdMatch = TalkGroupIdPattern.Match(typed);
                int? dmrId = dmrIdMatch.Success && int.TryParse(dmrIdMatch.Groups[1].Value, out var parsed) ? parsed : null;

                using var insertCmd = liveDb.CreateCommand();
                insertCmd.CommandText = """
                    INSERT INTO Contacts (Name, CallType, DmrId) VALUES ($name, 'Group', $dmrId);
                    SELECT last_insert_rowid();
                    """;
                insertCmd.Parameters.Add(new SqliteParameter("$name", typed));
                insertCmd.Parameters.Add(new SqliteParameter("$dmrId", (object?)dmrId ?? DBNull.Value));
                return (long)insertCmd.ExecuteScalar()!;
            },
        };
    }

    private static string FormatDisplay(string name, long? dmrId, string network) =>
        dmrId is { } id && !name.Contains($"TG{id}", StringComparison.OrdinalIgnoreCase)
            ? $"{name} (TG{id}) [{network}]"
            : $"{name} [{network}]";

    private static long InsertPending(SqliteConnection db, Option pending) =>
        InsertNetworkTalkGroup(db, new NetworkTalkGroup(pending.PendingDmrId!.Value, pending.PendingName!, pending.PendingNetwork!));

    /// <summary>A remote-only (not-yet-local) network talkgroup was picked - create the one local
    /// Contact row for it (or reuse an existing one if a concurrent insert/another picker beat
    /// this to it), never a bulk import of the network it came from. Public so ContactsPage's
    /// standalone talkgroup search/add (not tied to any channel) can reuse the exact same
    /// insert-or-reuse-existing, disambiguate-on-collision logic - the Contact-row equivalent of
    /// ContactPicker.InsertPrivateContact.</summary>
    public static long InsertNetworkTalkGroup(SqliteConnection db, NetworkTalkGroup tg)
    {
        using (var existingCmd = db.CreateCommand())
        {
            existingCmd.CommandText = "SELECT Id FROM Contacts WHERE DmrId = $dmrId AND Network = $network AND CallType = 'Group' LIMIT 1;";
            existingCmd.Parameters.Add(new SqliteParameter("$dmrId", tg.DmrId));
            existingCmd.Parameters.Add(new SqliteParameter("$network", tg.Network));
            if (existingCmd.ExecuteScalar() is long existingId) return existingId;
        }

        var name = tg.Name;
        using (var nameCheckCmd = db.CreateCommand())
        {
            nameCheckCmd.CommandText = "SELECT COUNT(*) FROM Contacts WHERE Name = $name AND CallType = 'Group';";
            nameCheckCmd.Parameters.Add(new SqliteParameter("$name", name));
            if (Convert.ToInt64(nameCheckCmd.ExecuteScalar()) > 0)
                name = $"{name} (TG{tg.DmrId})";
        }

        using var insertCmd = db.CreateCommand();
        insertCmd.CommandText = """
            INSERT INTO Contacts (Name, CallType, DmrId, Network) VALUES ($name, 'Group', $dmrId, $network);
            SELECT last_insert_rowid();
            """;
        insertCmd.Parameters.Add(new SqliteParameter("$name", name));
        insertCmd.Parameters.Add(new SqliteParameter("$dmrId", tg.DmrId));
        insertCmd.Parameters.Add(new SqliteParameter("$network", tg.Network));
        return (long)insertCmd.ExecuteScalar()!;
    }
}
