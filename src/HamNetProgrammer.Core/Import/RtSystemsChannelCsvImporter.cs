using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace HamNetProgrammer.Core.Import;

/// <summary>
/// Imports an RT Systems channel-export CSV into a HamNetProgrammer SQLite codeplug.
///
/// This is one input format among possibly several - the internal schema is not modeled on
/// RT Systems' column layout. Zone membership isn't a column in this CSV; it's derived from the
/// "ZoneName-Suffix" convention already used in channel names (e.g. "Shark-UK", "Home-Eastmids").
/// Fields not yet promoted to their own typed columns are preserved in ExtraAttributesJson rather
/// than silently dropped.
/// </summary>
public static class RtSystemsChannelCsvImporter
{
    public sealed record ImportResult(
        int ChannelsImported,
        int ZonesCreated,
        int ContactsCreated,
        int RadioIdsCreated,
        int ScanListsCreated,
        int GroupListsCreated,
        IReadOnlyList<string> Warnings);

    // Known hotspot zone names (see project notes) - used to recognize truncated prefixes
    // like "fqbsta-Eastmids" (16-char channel name limit) as belonging to "fqbstar".
    private static readonly string[] KnownZoneNames = ["Shark", "Home", "fqbstar", "gb7in", "mb6nw", "shark4", "nljstar"];

    private static readonly string[] ExtraColumns =
    [
        "Squelch Mode", "Busy Lockout", "Optional Signal", "DTMF ID", "2 Tone", "2 Tone Decode",
        "5 Tone", "PTTID", "Offset Reverse", "Auto Scan", "DataACK Disable", "Call Confirmation",
        "SMS Confirmation", "Ranging", "Encryption ID", "Exclude Roaming", "Lone Worker", "DMR Mode",
        "Talk Around", "Send Alias", "APRS Rx", "APRS Ana Mute", "APRS Report", "APRS PTT Mode",
        "APRS Channel", "Comment", "Correct Frequency", "Offset Frequency", "Offset Direction",
    ];

    public static ImportResult Import(string csvPath, SqliteConnection db)
    {
        var lines = ReadAllLinesSharingAccess(csvPath);
        if (lines.Length == 0)
            throw new InvalidOperationException("CSV file is empty.");

        var header = SplitCsvLine(lines[0]);
        var columnIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < header.Length; i++)
        {
            var name = header[i].Trim();
            if (name.Length > 0 && !columnIndex.ContainsKey(name))
                columnIndex[name] = i;
        }

        string Get(string[] fields, string column) =>
            columnIndex.TryGetValue(column, out var idx) && idx < fields.Length ? fields[idx].Trim() : "";

        var warnings = new List<string>();
        var zoneIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var contactIds = new Dictionary<(string Name, string CallType), long>();
        var radioIdIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var scanListIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var groupListIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var zonePositions = new Dictionary<long, int>();

        using var transaction = db.BeginTransaction();
        var channelsImported = 0;

        for (var lineIndex = 1; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            if (string.IsNullOrWhiteSpace(line)) continue;
            var fields = SplitCsvLine(line);
            if (fields.Length < 2) continue;

            var channelNumberText = Get(fields, "Channel Number");
            if (!int.TryParse(channelNumberText, out var channelNumber))
            {
                warnings.Add($"Line {lineIndex + 1}: could not parse channel number '{channelNumberText}', skipped.");
                continue;
            }

            var name = Get(fields, "Name");
            var mode = Get(fields, "Operating Mode");
            if (!TryParseMhzToHz(Get(fields, "Receive Frequency"), out var rxHz) ||
                !TryParseMhzToHz(Get(fields, "Transmit Frequency"), out var txHz))
            {
                warnings.Add($"Line {lineIndex + 1} (channel {channelNumber}): could not parse frequency, skipped.");
                continue;
            }

            var bandwidth = NullIfEmpty(Get(fields, "Bandwidth"));
            var power = NullIfEmpty(Get(fields, "Tx Power"));
            var admit = NullIfEmpty(Get(fields, "Admit Criteria"));
            var toneMode = NullIfEmpty(Get(fields, "Tone Mode"));
            var ctcss = ParseLeadingDouble(Get(fields, "CTCSS"));
            var rxCtcss = ParseLeadingDouble(Get(fields, "Rx CTCSS"));
            var dcs = NullIfEmpty(Get(fields, "DCS"));
            var rxDcs = NullIfEmpty(Get(fields, "Rx DCS"));
            int? colorCode = int.TryParse(Get(fields, "RX ColorCode"), out var cc) ? cc : null;
            int? timeSlot = int.TryParse(Get(fields, "Repeater Slot"), out var ts) ? ts : null;

            var talkGroupName = Get(fields, "Talk Group");
            long? contactId = IsSetValue(talkGroupName)
                ? GetOrCreateContact(db, contactIds, talkGroupName, "Group", ParseDmrIdFromTalkGroupName(talkGroupName))
                : null;

            var radioIdName = Get(fields, "Radio ID");
            long? radioIdId = IsSetValue(radioIdName)
                ? GetOrCreateNamed(db, radioIdIds, "RadioIds", "Callsign", radioIdName)
                : null;

            var scanListName = Get(fields, "Scan List");
            long? scanListId = IsSetValue(scanListName)
                ? GetOrCreateNamed(db, scanListIds, "ScanLists", "Name", scanListName)
                : null;

            var groupListName = Get(fields, "Group List");
            long? groupListId = IsSetValue(groupListName)
                ? GetOrCreateNamed(db, groupListIds, "GroupLists", "Name", groupListName)
                : null;

            var extras = new Dictionary<string, string>();
            foreach (var col in ExtraColumns)
            {
                var value = Get(fields, col);
                if (!string.IsNullOrWhiteSpace(value))
                    extras[col] = value;
            }
            var extraJson = extras.Count > 0 ? JsonSerializer.Serialize(extras) : null;

            long channelId;
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO Channels
                        (ChannelNumber, Name, Mode, RxFrequencyHz, TxFrequencyHz, Bandwidth, Power, AdmitCriteria,
                         ColorCode, TimeSlot, ContactId, RadioIdId, ScanListId, GroupListId, ToneMode, CtcssHz, RxCtcssHz,
                         DcsCode, RxDcsCode, ExtraAttributesJson)
                    VALUES
                        ($channelNumber, $name, $mode, $rxHz, $txHz, $bandwidth, $power, $admit,
                         $colorCode, $timeSlot, $contactId, $radioIdId, $scanListId, $groupListId, $toneMode, $ctcss, $rxCtcss,
                         $dcs, $rxDcs, $extraJson);
                    SELECT last_insert_rowid();
                    """;
                cmd.Parameters.AddWithValue("$channelNumber", channelNumber);
                cmd.Parameters.AddWithValue("$name", name);
                cmd.Parameters.AddWithValue("$mode", mode);
                cmd.Parameters.AddWithValue("$rxHz", rxHz);
                cmd.Parameters.AddWithValue("$txHz", txHz);
                cmd.Parameters.AddWithValue("$bandwidth", (object?)bandwidth ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$power", (object?)power ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$admit", (object?)admit ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$colorCode", (object?)colorCode ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$timeSlot", (object?)timeSlot ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$contactId", (object?)contactId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$radioIdId", (object?)radioIdId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$scanListId", (object?)scanListId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$groupListId", (object?)groupListId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$toneMode", (object?)toneMode ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$ctcss", (object?)ctcss ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$rxCtcss", (object?)rxCtcss ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$dcs", (object?)dcs ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$rxDcs", (object?)rxDcs ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$extraJson", (object?)extraJson ?? DBNull.Value);

                channelId = (long)cmd.ExecuteScalar()!;
            }
            channelsImported++;

            var zonePrefix = ExtractZonePrefix(name, warnings, lineIndex);
            if (zonePrefix != null)
            {
                var zoneId = GetOrCreateNamed(db, zoneIds, "Zones", "Name", zonePrefix);
                var position = zonePositions.TryGetValue(zoneId, out var p) ? p + 1 : 1;
                zonePositions[zoneId] = position;

                using var zoneCmd = db.CreateCommand();
                zoneCmd.CommandText = "INSERT INTO ZoneChannels (ZoneId, ChannelId, Position) VALUES ($zoneId, $channelId, $position);";
                zoneCmd.Parameters.AddWithValue("$zoneId", zoneId);
                zoneCmd.Parameters.AddWithValue("$channelId", channelId);
                zoneCmd.Parameters.AddWithValue("$position", position);
                zoneCmd.ExecuteNonQuery();
            }
        }

        transaction.Commit();

        return new ImportResult(
            channelsImported, zoneIds.Count, contactIds.Count, radioIdIds.Count, scanListIds.Count, groupListIds.Count, warnings);
    }

    private static bool IsSetValue(string value) =>
        !string.IsNullOrWhiteSpace(value) && !value.Equals("None", StringComparison.OrdinalIgnoreCase);

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? ExtractZonePrefix(string channelName, List<string> warnings, int lineIndex)
    {
        var dashIndex = channelName.IndexOf('-');
        if (dashIndex <= 0) return null;

        var prefix = channelName[..dashIndex];

        foreach (var known in KnownZoneNames)
            if (string.Equals(known, prefix, StringComparison.OrdinalIgnoreCase))
                return known;

        var truncationMatches = KnownZoneNames.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (truncationMatches.Length == 1)
        {
            warnings.Add(
                $"Line {lineIndex + 1}: zone prefix '{prefix}' looks like a truncated '{truncationMatches[0]}' " +
                "(16-char channel name limit) - merged into that zone.");
            return truncationMatches[0];
        }

        return prefix;
    }

    private static double? ParseLeadingDouble(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var token = value.Split(' ')[0];
        return double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : null;
    }

    private static bool TryParseMhzToHz(string value, out long hz)
    {
        hz = 0;
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mhz)) return false;
        hz = (long)Math.Round(mhz * 1_000_000);
        return true;
    }

    private static long GetOrCreateNamed(SqliteConnection db, Dictionary<string, long> cache, string table, string column, string value)
    {
        if (cache.TryGetValue(value, out var existing)) return existing;

        using (var select = db.CreateCommand())
        {
            select.CommandText = $"SELECT Id FROM {table} WHERE {column} = $value;";
            select.Parameters.AddWithValue("$value", value);
            if (select.ExecuteScalar() is long found)
            {
                cache[value] = found;
                return found;
            }
        }

        using var insert = db.CreateCommand();
        insert.CommandText = $"INSERT INTO {table} ({column}) VALUES ($value); SELECT last_insert_rowid();";
        insert.Parameters.AddWithValue("$value", value);
        var newId = (long)insert.ExecuteScalar()!;
        cache[value] = newId;
        return newId;
    }

    // Matches the numeric ID embedded in RT Systems talkgroup labels, e.g. "TG2350 United K" -> 2350.
    // Capturing this now means ad-hoc contacts created from this CSV can later be reconciled by ID
    // against a real talkgroup database instead of staying as free-text duplicates - see the
    // "searchable talkgroup list" follow-up.
    private static readonly Regex TalkGroupIdPattern = new(@"^TG(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static int? ParseDmrIdFromTalkGroupName(string talkGroupName)
    {
        var match = TalkGroupIdPattern.Match(talkGroupName);
        return match.Success && int.TryParse(match.Groups[1].Value, out var id) ? id : null;
    }

    private static long GetOrCreateContact(SqliteConnection db, Dictionary<(string, string), long> cache, string name, string callType, int? dmrId)
    {
        var key = (name, callType);
        if (cache.TryGetValue(key, out var existing)) return existing;

        using (var select = db.CreateCommand())
        {
            select.CommandText = "SELECT Id FROM Contacts WHERE Name = $name AND CallType = $callType;";
            select.Parameters.AddWithValue("$name", name);
            select.Parameters.AddWithValue("$callType", callType);
            if (select.ExecuteScalar() is long found)
            {
                cache[key] = found;
                return found;
            }
        }

        using var insert = db.CreateCommand();
        insert.CommandText = """
            INSERT INTO Contacts (Name, CallType, DmrId) VALUES ($name, $callType, $dmrId);
            SELECT last_insert_rowid();
            """;
        insert.Parameters.AddWithValue("$name", name);
        insert.Parameters.AddWithValue("$callType", callType);
        insert.Parameters.AddWithValue("$dmrId", (object?)dmrId ?? DBNull.Value);
        var newId = (long)insert.ExecuteScalar()!;
        cache[key] = newId;
        return newId;
    }

    private static string[] ReadAllLinesSharingAccess(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
            lines.Add(line);
        return lines.ToArray();
    }

    private static string[] SplitCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                    else inQuotes = false;
                }
                else current.Append(c);
            }
            else
            {
                if (c == '"') inQuotes = true;
                else if (c == ',') { fields.Add(current.ToString()); current.Clear(); }
                else current.Append(c);
            }
        }
        fields.Add(current.ToString());
        return fields.ToArray();
    }
}
