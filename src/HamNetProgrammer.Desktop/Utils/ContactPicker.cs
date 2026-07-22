using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Data.Sqlite;
using HamNetProgrammer.Core.Online;

namespace HamNetProgrammer.Desktop.Utils;

/// <summary>
/// A channel's contact can be a Group Call (talkgroup) or a Private Call (a specific individual) -
/// the AT-D878UV format supports both per channel, this just wasn't wired up before (every contact
/// this app created was hardcoded Group). Wraps TalkGroupPicker (unchanged, Group mode) alongside a
/// new Private mode - search your own already-added Private contacts plus a live radioid.net search
/// (via the shared backend, see RadioIdNetworkSearch) - behind a two-way toggle, so the search
/// offered always matches what the channel actually needs.
/// </summary>
public static class ContactPicker
{
    public sealed class Picker
    {
        public required FrameworkElement Container { get; init; }
        public required Func<SqliteConnection, long?> GetOrCreateId { get; init; }

        /// <summary>Fires with the chosen contact's display text whenever a suggestion is picked in
        /// either mode - e.g. for NewChannelDialog's "suggest a channel name" behavior, which
        /// otherwise can't reach either inner AutoSuggestBox directly since Container is now a
        /// composite toggle+picker panel, not a single AutoSuggestBox.</summary>
        public Action<string>? OnDisplayChosen { get; set; }
    }

    private sealed record PrivateOption(long? Id, string Display, long? DmrId, RadioIdSearchResult? Remote);

    public static Picker Build(SqliteConnection db, long? initialContactId, string? preferredNetwork = null)
    {
        var initialIsPrivate = false;
        if (initialContactId is { } cid)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT CallType FROM Contacts WHERE Id = $id;";
            cmd.Parameters.Add(new SqliteParameter("$id", cid));
            initialIsPrivate = cmd.ExecuteScalar() as string == "Private";
        }

        Action<string>? forward = null;
        var groupPicker = TalkGroupPicker.Build(db, initialIsPrivate ? null : initialContactId,
            "Search, or type a new talkgroup...", onChosen: o => forward?.Invoke(o.Display), preferredNetwork: preferredNetwork);
        var privatePicker = BuildPrivatePicker(db, initialIsPrivate ? initialContactId : null,
            onChosen: o => forward?.Invoke(o.Display));

        var groupRadio = new RadioButton { Content = "Group Call", GroupName = "ContactCallType", IsChecked = !initialIsPrivate };
        var privateRadio = new RadioButton { Content = "Private Call", GroupName = "ContactCallType", IsChecked = initialIsPrivate };
        var toggleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        toggleRow.Children.Add(groupRadio);
        toggleRow.Children.Add(privateRadio);

        var slot = new Grid();
        slot.Children.Add(groupPicker.Container);
        slot.Children.Add(privatePicker.Container);
        privatePicker.Container.Visibility = initialIsPrivate ? Visibility.Visible : Visibility.Collapsed;
        groupPicker.Container.Visibility = initialIsPrivate ? Visibility.Collapsed : Visibility.Visible;

        groupRadio.Checked += (_, _) =>
        {
            groupPicker.Container.Visibility = Visibility.Visible;
            privatePicker.Container.Visibility = Visibility.Collapsed;
        };
        privateRadio.Checked += (_, _) =>
        {
            groupPicker.Container.Visibility = Visibility.Collapsed;
            privatePicker.Container.Visibility = Visibility.Visible;
        };

        var container = new StackPanel { Spacing = 6 };
        container.Children.Add(toggleRow);
        container.Children.Add(slot);

        var picker = new Picker
        {
            Container = container,
            GetOrCreateId = liveDb => privateRadio.IsChecked == true
                ? privatePicker.GetOrCreateId(liveDb)
                : groupPicker.GetOrCreateId(liveDb),
        };
        forward = display => picker.OnDisplayChosen?.Invoke(display);
        return picker;
    }

    private static Picker BuildPrivatePicker(SqliteConnection db, long? initialContactId, Action<PrivateOption>? onChosen = null)
    {
        var localOptions = new List<PrivateOption> { new(null, "(none)", null, null) };
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT Id, Name, DmrId FROM Contacts WHERE CallType = 'Private' ORDER BY Name;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var dmrId = reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2);
                var dmrIdSuffix = dmrId is { } id ? $" ({id})" : "";
                localOptions.Add(new PrivateOption(reader.GetInt64(0), $"{reader.GetString(1)}{dmrIdSuffix}", dmrId, null));
            }
        }

        var selected = localOptions.FirstOrDefault(o => o.Id == initialContactId) ?? localOptions[0];
        var box = new AutoSuggestBox
        {
            PlaceholderText = "Search callsign or name (e.g. radioid.net)...",
            Text = selected.Display == "(none)" ? "" : selected.Display,
            TextMemberPath = "Display",
            DisplayMemberPath = "Display",
        };

        var searchToken = 0;
        box.TextChanged += async (sender, args) =>
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            var query = sender.Text.Trim();
            var myToken = ++searchToken;

            var localMatches = string.IsNullOrEmpty(query)
                ? localOptions.Take(15).ToList()
                : localOptions.Where(o => o.Display.Contains(query, StringComparison.OrdinalIgnoreCase)).Take(15).ToList();
            sender.ItemsSource = localMatches;

            if (query.Length < 2) return;

            // Debounce - wait for typing to pause before hitting the network, and drop the result
            // if a newer keystroke has already started a fresher search.
            try { await Task.Delay(350); } catch { return; }
            if (myToken != searchToken) return;

            var remoteResults = await RadioIdNetworkSearch.SearchAsync(query);
            if (myToken != searchToken) return;

            var localDmrIds = localOptions.Where(o => o.DmrId is not null).Select(o => o.DmrId!.Value).ToHashSet();
            var remoteOptions = remoteResults
                .Where(r => !localDmrIds.Contains(r.DmrId))
                .Select(r => new PrivateOption(null, $"{r.Callsign} - {r.Name} ({r.DmrId}) [{r.Country}]", r.DmrId, r));

            sender.ItemsSource = localMatches.Concat(remoteOptions).ToList();
        };
        box.SuggestionChosen += (_, args) =>
        {
            if (args.SelectedItem is not PrivateOption chosen) return;
            selected = chosen;
            onChosen?.Invoke(chosen);
        };

        return new Picker
        {
            Container = box,
            GetOrCreateId = liveDb =>
            {
                var typed = box.Text.Trim();
                if (typed.Length == 0) return null;

                if (string.Equals(typed, selected.Display, StringComparison.OrdinalIgnoreCase))
                {
                    if (selected.Id is { } existingId) return existingId;
                    if (selected.Remote is { } remote) return InsertPrivateContact(liveDb, remote);
                    return null;
                }

                var exactLocal = localOptions.FirstOrDefault(o => string.Equals(o.Display, typed, StringComparison.OrdinalIgnoreCase));
                return exactLocal?.Id;
            },
        };
    }

    /// <summary>Public so ContactsPage's standalone "Find an Individual" search/add (not tied to
    /// any channel) can reuse the exact same insert-or-reuse-existing, disambiguate-on-collision
    /// logic rather than duplicating it.</summary>
    public static long InsertPrivateContact(SqliteConnection db, RadioIdSearchResult remote)
    {
        var name = string.IsNullOrWhiteSpace(remote.Name) ? remote.Callsign : $"{remote.Callsign} {remote.Name}";

        using (var existingCmd = db.CreateCommand())
        {
            existingCmd.CommandText = "SELECT Id FROM Contacts WHERE DmrId = $dmrId AND CallType = 'Private' LIMIT 1;";
            existingCmd.Parameters.Add(new SqliteParameter("$dmrId", remote.DmrId));
            if (existingCmd.ExecuteScalar() is long existingId) return existingId;
        }

        // Disambiguate against Contacts.UNIQUE(Name, CallType) - two different individuals could
        // in principle compose the same display name (a reused/reassigned callsign in radioid.net's
        // own historical data, for example).
        using (var nameCheckCmd = db.CreateCommand())
        {
            nameCheckCmd.CommandText = "SELECT COUNT(*) FROM Contacts WHERE Name = $name AND CallType = 'Private';";
            nameCheckCmd.Parameters.Add(new SqliteParameter("$name", name));
            if (Convert.ToInt64(nameCheckCmd.ExecuteScalar()) > 0)
                name = $"{name} ({remote.DmrId})";
        }

        using var insertCmd = db.CreateCommand();
        insertCmd.CommandText = """
            INSERT INTO Contacts (Name, CallType, DmrId, Network) VALUES ($name, 'Private', $dmrId, 'radioid.net');
            SELECT last_insert_rowid();
            """;
        insertCmd.Parameters.Add(new SqliteParameter("$name", name));
        insertCmd.Parameters.Add(new SqliteParameter("$dmrId", remote.DmrId));
        return (long)insertCmd.ExecuteScalar()!;
    }
}
