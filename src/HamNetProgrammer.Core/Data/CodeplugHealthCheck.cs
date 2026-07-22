using Microsoft.Data.Sqlite;

namespace HamNetProgrammer.Core.Data;

public sealed record HealthCheckFinding(string Category, string Summary, IReadOnlyList<string> Details);

/// <summary>
/// A small, deliberately narrow set of high-confidence structural checks against the current
/// codeplug database - the first cut of a "tidy up after Read Codeplug" tool. Built after a real
/// Read Codeplug run against years-old radio state surfaced a genuine 154-channel leftover
/// duplicate block that was only found by hand-written SQL - the whole point of this class is to
/// make that kind of thing visible without needing SQL.
///
/// Deliberately narrow by design (user's explicit choice, 2026-07-22): only checks with no
/// meaningful false-positive risk are included here. This project's own history has a cautionary
/// example of the alternative (TalkGroupAuditor's channel-name-matching check, built, tested, and
/// dropped after 453 false positives from abbreviation conventions) - grow this list incrementally
/// from real user feedback, don't front-load speculative checks.
///
/// A "channels not in any zone" check was tried and dropped the same day, for the same reason:
/// tested against the real live database, it flagged all 573 real channels, because this app's own
/// repeater-book convention (see the README/project history - "zones first, repeaters last") keeps
/// those channels deliberately out of any zone as a flat reference list. That's the intended shape,
/// not a data problem - a "high confidence" check still needs to actually mean something for how
/// this app's data is used, not just be deterministic/non-fuzzy.
/// </summary>
public static class CodeplugHealthCheck
{
    public static List<HealthCheckFinding> Run(SqliteConnection db)
    {
        var findings = new List<HealthCheckFinding>();

        AddIfAny(findings, FindDuplicateLookingChannels(db));
        AddIfAny(findings, FindScanListDriftFromZones(db));
        AddIfAny(findings, FindGroupListDriftFromZones(db));
        AddIfAny(findings, FindDuplicateTalkGroups(db));

        return findings;
    }

    private static void AddIfAny(List<HealthCheckFinding> findings, HealthCheckFinding? finding)
    {
        if (finding is not null) findings.Add(finding);
    }

    // ---- Channels that look like an exact duplicate of another, under a different number ----

    private static HealthCheckFinding? FindDuplicateLookingChannels(SqliteConnection db)
    {
        var details = new List<string>();
        using var cmd = db.CreateCommand();
        // Same name, frequencies, colour code, timeslot, and contact - different ChannelNumber. This
        // exact pattern (a whole zone re-appearing at a different channel-number offset) is how the
        // 2026-07-22 leftover test-write block was found - real hardware confirmed, not speculative.
        cmd.CommandText = """
            SELECT Name, RxFrequencyHz, TxFrequencyHz, ColorCode, TimeSlot,
                   GROUP_CONCAT(ChannelNumber, ', ') AS Numbers, COUNT(*) AS Cnt
            FROM Channels
            GROUP BY Name, RxFrequencyHz, TxFrequencyHz, ColorCode, TimeSlot, IFNULL(ContactId, -1)
            HAVING Cnt > 1
            ORDER BY Cnt DESC, Name;
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            details.Add($"'{reader.GetString(0)}' appears {reader.GetInt64(6)}x, at channels {reader.GetString(5)}");

        if (details.Count == 0) return null;
        return new HealthCheckFinding("Duplicate-looking Channels", $"{details.Count} group(s) of channels are identical in every field except channel number - likely leftover duplicates, not real distinct channels.", details);
    }

    // ---- Scan Lists that have drifted from their same-named Zone's membership ----

    private static HealthCheckFinding? FindScanListDriftFromZones(SqliteConnection db)
    {
        var details = new List<string>();
        using var cmd = db.CreateCommand();
        // ZoneScanListBuilder names a zone's auto-built scan list identically to the zone itself
        // (confirmed in ZoneScanListBuilder.cs) - so a Zone/ScanList pair sharing a name is expected
        // to share membership too. A mismatch means they've drifted apart (e.g. edited independently,
        // or "Sync Lists with Zones" was off during some edits).
        cmd.CommandText = """
            SELECT z.Id, z.Name, sl.Id
            FROM Zones z JOIN ScanLists sl ON sl.Name = z.Name
            WHERE z.IsActive = 1;
            """;
        var pairs = new List<(long ZoneId, string Name, long ScanListId)>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
                pairs.Add((reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2)));
        }

        foreach (var (zoneId, name, scanListId) in pairs)
        {
            var zoneChannels = GetChannelIds(db, "ZoneChannels", "ZoneId", zoneId);
            var scanListChannels = GetChannelIds(db, "ScanListChannels", "ScanListId", scanListId);
            var missing = zoneChannels.Except(scanListChannels).Count();
            var extra = scanListChannels.Except(zoneChannels).Count();
            if (missing > 0 || extra > 0)
                details.Add($"'{name}': scan list is missing {missing} of the zone's channels and has {extra} extra.");
        }

        if (details.Count == 0) return null;
        return new HealthCheckFinding("Scan List / Zone Drift", $"{details.Count} scan list(s) no longer match their zone's channel membership.", details);
    }

    // ---- Group Lists that have drifted from their same-named Zone's distinct talkgroups ----

    private static HealthCheckFinding? FindGroupListDriftFromZones(SqliteConnection db)
    {
        var details = new List<string>();
        using var cmd = db.CreateCommand();
        // Same naming convention as scan lists - confirmed in ZoneGroupListBuilder.cs.
        cmd.CommandText = """
            SELECT z.Id, z.Name, gl.Id
            FROM Zones z JOIN GroupLists gl ON gl.Name = z.Name
            WHERE z.IsActive = 1;
            """;
        var pairs = new List<(long ZoneId, string Name, long GroupListId)>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
                pairs.Add((reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2)));
        }

        foreach (var (zoneId, name, groupListId) in pairs)
        {
            var zoneContacts = GetZoneDistinctContactIds(db, zoneId);
            var groupListContacts = GetContactIds(db, groupListId);
            var missing = zoneContacts.Except(groupListContacts).Count();
            var extra = groupListContacts.Except(zoneContacts).Count();
            if (missing > 0 || extra > 0)
                details.Add($"'{name}': group list is missing {missing} of the zone's talkgroups and has {extra} extra.");
        }

        if (details.Count == 0) return null;
        return new HealthCheckFinding("Group List / Zone Drift", $"{details.Count} group list(s) no longer match their zone's distinct talkgroups.", details);
    }

    // ---- Duplicate talkgroups (reuses the existing, already-proven detector) ----

    private static HealthCheckFinding? FindDuplicateTalkGroups(SqliteConnection db)
    {
        var groups = DuplicateTalkGroupMerger.FindDuplicates(db);
        if (groups.Count == 0) return null;

        var details = groups.Select(g =>
            $"TG{g.DmrId}: {string.Join(", ", g.Contacts.Select(c => $"'{c.Name}'{(c.Network is null ? " (manual)" : $" ({c.Network})")}"))}").ToList();
        return new HealthCheckFinding("Duplicate Talkgroups", $"{groups.Count} DMR ID(s) have more than one contact - use \"Merge Duplicate Talkgroups\" to clean these up.", details);
    }

    private static HashSet<long> GetChannelIds(SqliteConnection db, string table, string keyColumn, long keyValue)
    {
        var ids = new HashSet<long>();
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"SELECT ChannelId FROM {table} WHERE {keyColumn} = $key;";
        cmd.Parameters.AddWithValue("$key", keyValue);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetInt64(0));
        return ids;
    }

    private static HashSet<long> GetContactIds(SqliteConnection db, long groupListId)
    {
        var ids = new HashSet<long>();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT ContactId FROM GroupListContacts WHERE GroupListId = $id;";
        cmd.Parameters.AddWithValue("$id", groupListId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetInt64(0));
        return ids;
    }

    private static HashSet<long> GetZoneDistinctContactIds(SqliteConnection db, long zoneId)
    {
        var ids = new HashSet<long>();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT c.ContactId FROM ZoneChannels zc
            JOIN Channels c ON c.Id = zc.ChannelId
            WHERE zc.ZoneId = $id AND c.ContactId IS NOT NULL;
            """;
        cmd.Parameters.AddWithValue("$id", zoneId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetInt64(0));
        return ids;
    }
}
