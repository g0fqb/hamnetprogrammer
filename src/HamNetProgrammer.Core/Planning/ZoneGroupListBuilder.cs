using Microsoft.Data.Sqlite;

namespace HamNetProgrammer.Core.Planning;

public sealed record ZoneGroupListResult(int ZonesProcessed, int ChannelsLinked, IReadOnlyList<string> Warnings);

/// <summary>
/// Builds one receive Group List per zone, containing the distinct talkgroups used by that
/// zone's channels (in first-seen/channel-position order), and points each channel's
/// GroupListId at it. Existing group lists with a matching zone name are reused and their
/// membership replaced.
/// </summary>
public static class ZoneGroupListBuilder
{
    // AT-D878UV limit: up to 64 talkgroup entries per receive Group List.
    private const int MaxContactsPerGroupList = 64;

    public static ZoneGroupListResult BuildFromZones(SqliteConnection db)
    {
        var warnings = new List<string>();
        var zonesProcessed = 0;
        var channelsLinked = 0;

        using var transaction = db.BeginTransaction();

        var zones = new List<(long Id, string Name)>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT Id, Name FROM Zones ORDER BY Name;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                zones.Add((reader.GetInt64(0), reader.GetString(1)));
        }

        foreach (var zone in zones)
        {
            var channelIds = new List<long>();
            var contactIds = new List<long>();
            var seenContacts = new HashSet<long>();

            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT c.Id, c.ContactId
                    FROM ZoneChannels zc
                    JOIN Channels c ON c.Id = zc.ChannelId
                    WHERE zc.ZoneId = $zoneId
                    ORDER BY zc.Position;
                    """;
                cmd.Parameters.AddWithValue("$zoneId", zone.Id);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    channelIds.Add(reader.GetInt64(0));
                    if (reader.IsDBNull(1)) continue;
                    var contactId = reader.GetInt64(1);
                    if (seenContacts.Add(contactId))
                        contactIds.Add(contactId);
                }
            }

            if (channelIds.Count == 0) continue;

            if (contactIds.Count == 0)
            {
                warnings.Add($"Zone '{zone.Name}' has no channels with a talkgroup assigned - skipped.");
                continue;
            }

            if (contactIds.Count > MaxContactsPerGroupList)
            {
                warnings.Add(
                    $"Zone '{zone.Name}' has {contactIds.Count} distinct talkgroups, over the AT-D878UV's " +
                    $"{MaxContactsPerGroupList}-entry group list limit - truncating to the first {MaxContactsPerGroupList}.");
                contactIds = contactIds.Take(MaxContactsPerGroupList).ToList();
            }

            long groupListId;
            using (var select = db.CreateCommand())
            {
                select.CommandText = "SELECT Id FROM GroupLists WHERE Name = $name;";
                select.Parameters.AddWithValue("$name", zone.Name);
                groupListId = select.ExecuteScalar() is long found ? found : InsertGroupList(db, zone.Name);
            }

            using (var clear = db.CreateCommand())
            {
                clear.CommandText = "DELETE FROM GroupListContacts WHERE GroupListId = $groupListId;";
                clear.Parameters.AddWithValue("$groupListId", groupListId);
                clear.ExecuteNonQuery();
            }

            for (var i = 0; i < contactIds.Count; i++)
            {
                using var insert = db.CreateCommand();
                insert.CommandText = "INSERT INTO GroupListContacts (GroupListId, ContactId, Position) VALUES ($groupListId, $contactId, $position);";
                insert.Parameters.AddWithValue("$groupListId", groupListId);
                insert.Parameters.AddWithValue("$contactId", contactIds[i]);
                insert.Parameters.AddWithValue("$position", i + 1);
                insert.ExecuteNonQuery();
            }

            foreach (var channelId in channelIds)
            {
                using (var checkCmd = db.CreateCommand())
                {
                    checkCmd.CommandText = "SELECT GroupListId FROM Channels WHERE Id = $channelId;";
                    checkCmd.Parameters.AddWithValue("$channelId", channelId);
                    if (checkCmd.ExecuteScalar() is long current && current != groupListId)
                        warnings.Add($"Channel {channelId} already had a different group list assigned - overwritten with zone '{zone.Name}'.");
                }

                using var update = db.CreateCommand();
                update.CommandText = "UPDATE Channels SET GroupListId = $groupListId WHERE Id = $channelId;";
                update.Parameters.AddWithValue("$groupListId", groupListId);
                update.Parameters.AddWithValue("$channelId", channelId);
                update.ExecuteNonQuery();
                channelsLinked++;
            }

            zonesProcessed++;
        }

        transaction.Commit();
        return new ZoneGroupListResult(zonesProcessed, channelsLinked, warnings);
    }

    private static long InsertGroupList(SqliteConnection db, string name)
    {
        using var insert = db.CreateCommand();
        insert.CommandText = "INSERT INTO GroupLists (Name) VALUES ($name); SELECT last_insert_rowid();";
        insert.Parameters.AddWithValue("$name", name);
        return (long)insert.ExecuteScalar()!;
    }
}
