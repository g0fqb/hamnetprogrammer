using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Data.Sqlite;

namespace HamNetProgrammer.Desktop.Utils;

/// <summary>
/// Type-to-search talkgroup picker (the point of moving off CPS's scroll-through-hundreds picker),
/// with create-on-save for names that don't match anything - the Contacts table only holds
/// talkgroups already used somewhere in the codeplug, not a full canonical database, so typing a
/// new one is a first-class path, not an error. Shared between ChannelEditDialog and any other
/// place a talkgroup needs picking (e.g. RadioSettingsPage's APRS talkgroup).
/// </summary>
public static class TalkGroupPicker
{
    public sealed record Option(long? Id, string Display);

    public sealed class Picker
    {
        public required FrameworkElement Container { get; init; }
        public required Func<SqliteConnection, long?> GetOrCreateId { get; init; }
    }

    // Matches a leading "TG12345" in typed text so a new talkgroup's real DMR ID can be captured
    // the same way the CSV importer does, e.g. "TG91 World-wide" -> DmrId 91.
    private static readonly Regex TalkGroupIdPattern = new(@"^TG(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static Picker Build(SqliteConnection db, long? initialContactId, string placeholder)
    {
        var options = new List<Option> { new(null, "(none)") };
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT Id, Name FROM Contacts ORDER BY Name;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                options.Add(new Option(reader.GetInt64(0), reader.GetString(1)));
        }

        var selected = options.FirstOrDefault(o => o.Id == initialContactId) ?? options[0];
        var box = new AutoSuggestBox
        {
            PlaceholderText = placeholder,
            Text = selected.Display == "(none)" ? "" : selected.Display,
            TextMemberPath = "Display",
            DisplayMemberPath = "Display",
        };

        box.TextChanged += (sender, args) =>
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            var query = sender.Text.Trim();
            sender.ItemsSource = string.IsNullOrEmpty(query)
                ? options.Take(30).ToList()
                : options.Where(o => o.Display.Contains(query, StringComparison.OrdinalIgnoreCase)).Take(30).ToList();
        };
        box.SuggestionChosen += (_, args) =>
        {
            if (args.SelectedItem is Option chosen) selected = chosen;
        };

        return new Picker
        {
            Container = box,
            GetOrCreateId = liveDb =>
            {
                var typed = box.Text.Trim();
                if (typed.Length == 0) return null;

                if (string.Equals(typed, selected.Display, StringComparison.OrdinalIgnoreCase))
                    return selected.Id;

                var exact = options.FirstOrDefault(o => string.Equals(o.Display, typed, StringComparison.OrdinalIgnoreCase));
                if (exact is not null) return exact.Id;

                using var selectCmd = liveDb.CreateCommand();
                selectCmd.CommandText = "SELECT Id FROM Contacts WHERE Name = $name AND CallType = 'Group';";
                selectCmd.Parameters.Add(new SqliteParameter("$name", typed));
                if (selectCmd.ExecuteScalar() is long existingId) return existingId;

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
}
