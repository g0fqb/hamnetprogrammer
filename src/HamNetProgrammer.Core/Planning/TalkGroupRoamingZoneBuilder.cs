using Microsoft.Data.Sqlite;

namespace HamNetProgrammer.Core.Planning;

public sealed record TalkGroupRoamingZoneResult(int RoamingZonesProcessed, int ChannelsLinked, IReadOnlyList<string> Warnings);

/// <summary>
/// Builds one roaming zone per talkgroup, containing every zoned channel that carries that
/// talkgroup across all hotspot zones - the transpose of <see cref="ZoneScanListBuilder"/> and
/// <see cref="ZoneGroupListBuilder"/>, which group by zone. This lets the radio auto-roam between
/// hotspots (Shark/Home/fqbstar/...) while chasing a single talkgroup.
/// </summary>
public static class TalkGroupRoamingZoneBuilder
{
    // Conservative estimate from the reverse-engineered memory layout doc (unconfirmed exact cap).
    private const int MaxChannelsPerRoamingZone = 64;

    public static TalkGroupRoamingZoneResult BuildFromZones(SqliteConnection db)
    {
        var warnings = new List<string>();
        var roamingZonesProcessed = 0;
        var channelsLinked = 0;

        using var transaction = db.BeginTransaction();

        // Group every zoned channel by its talkgroup, ordered by zone name then in-zone position
        // for determinism, so each talkgroup's roaming zone lists its hotspots consistently.
        var byContact = new Dictionary<long, (string ContactName, List<long> ChannelIds)>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                SELECT c.Id AS ContactId, c.Name AS ContactName, ch.Id AS ChannelId
                FROM ZoneChannels zc
                JOIN Zones z ON z.Id = zc.ZoneId
                JOIN Channels ch ON ch.Id = zc.ChannelId
                JOIN Contacts c ON c.Id = ch.ContactId
                ORDER BY c.Name, z.Name, zc.Position;
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var contactId = reader.GetInt64(0);
                var contactName = reader.GetString(1);
                var channelId = reader.GetInt64(2);

                if (!byContact.TryGetValue(contactId, out var entry))
                {
                    entry = (contactName, new List<long>());
                    byContact[contactId] = entry;
                }
                entry.ChannelIds.Add(channelId);
            }
        }

        foreach (var (contactName, channelIds) in byContact.Values)
        {
            if (channelIds.Count < 2)
                continue; // Only one hotspot carries this talkgroup - nothing to roam between.

            var members = channelIds;
            if (members.Count > MaxChannelsPerRoamingZone)
            {
                warnings.Add(
                    $"Talkgroup '{contactName}' spans {members.Count} channels, over the estimated " +
                    $"{MaxChannelsPerRoamingZone}-channel roaming zone limit - truncating to the first {MaxChannelsPerRoamingZone}.");
                members = members.Take(MaxChannelsPerRoamingZone).ToList();
            }

            long roamingZoneId;
            using (var select = db.CreateCommand())
            {
                select.CommandText = "SELECT Id FROM RoamingZones WHERE Name = $name;";
                select.Parameters.AddWithValue("$name", contactName);
                roamingZoneId = select.ExecuteScalar() is long found ? found : InsertRoamingZone(db, contactName);
            }

            using (var clear = db.CreateCommand())
            {
                clear.CommandText = "DELETE FROM RoamingZoneChannels WHERE RoamingZoneId = $roamingZoneId;";
                clear.Parameters.AddWithValue("$roamingZoneId", roamingZoneId);
                clear.ExecuteNonQuery();
            }

            for (var i = 0; i < members.Count; i++)
            {
                using var insert = db.CreateCommand();
                insert.CommandText = "INSERT INTO RoamingZoneChannels (RoamingZoneId, ChannelId, Position) VALUES ($roamingZoneId, $channelId, $position);";
                insert.Parameters.AddWithValue("$roamingZoneId", roamingZoneId);
                insert.Parameters.AddWithValue("$channelId", members[i]);
                insert.Parameters.AddWithValue("$position", i + 1);
                insert.ExecuteNonQuery();
            }

            channelsLinked += members.Count;
            roamingZonesProcessed++;
        }

        transaction.Commit();
        return new TalkGroupRoamingZoneResult(roamingZonesProcessed, channelsLinked, warnings);
    }

    private static long InsertRoamingZone(SqliteConnection db, string name)
    {
        using var insert = db.CreateCommand();
        insert.CommandText = "INSERT INTO RoamingZones (Name) VALUES ($name); SELECT last_insert_rowid();";
        insert.Parameters.AddWithValue("$name", name);
        return (long)insert.ExecuteScalar()!;
    }
}
