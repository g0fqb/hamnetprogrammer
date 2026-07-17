using Microsoft.Data.Sqlite;

namespace HamNetProgrammer.Core.Planning;

public sealed record ZoneScanListResult(int ZonesProcessed, int ChannelsLinked, IReadOnlyList<string> Warnings);

/// <summary>
/// Builds one scan list per zone, containing that zone's channels in zone order, and points
/// each channel's ScanListId at it - so scanning while on a zone covers every talkgroup in it.
/// Existing scan lists with a matching zone name are reused and their membership replaced.
/// </summary>
public static class ZoneScanListBuilder
{
    // AT-D878UV limit: up to 50 channels per scan list.
    private const int MaxChannelsPerScanList = 50;

    public static ZoneScanListResult BuildFromZones(SqliteConnection db)
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
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "SELECT ChannelId FROM ZoneChannels WHERE ZoneId = $zoneId ORDER BY Position;";
                cmd.Parameters.AddWithValue("$zoneId", zone.Id);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    channelIds.Add(reader.GetInt64(0));
            }

            if (channelIds.Count == 0) continue;

            if (channelIds.Count > MaxChannelsPerScanList)
            {
                warnings.Add(
                    $"Zone '{zone.Name}' has {channelIds.Count} channels, over the AT-D878UV's " +
                    $"{MaxChannelsPerScanList}-channel scan list limit - truncating to the first {MaxChannelsPerScanList}.");
                channelIds = channelIds.Take(MaxChannelsPerScanList).ToList();
            }

            long scanListId;
            using (var select = db.CreateCommand())
            {
                select.CommandText = "SELECT Id FROM ScanLists WHERE Name = $name;";
                select.Parameters.AddWithValue("$name", zone.Name);
                scanListId = select.ExecuteScalar() is long found ? found : InsertScanList(db, zone.Name);
            }

            using (var clear = db.CreateCommand())
            {
                clear.CommandText = "DELETE FROM ScanListChannels WHERE ScanListId = $scanListId;";
                clear.Parameters.AddWithValue("$scanListId", scanListId);
                clear.ExecuteNonQuery();
            }

            for (var i = 0; i < channelIds.Count; i++)
            {
                using var insert = db.CreateCommand();
                insert.CommandText = "INSERT INTO ScanListChannels (ScanListId, ChannelId, Position) VALUES ($scanListId, $channelId, $position);";
                insert.Parameters.AddWithValue("$scanListId", scanListId);
                insert.Parameters.AddWithValue("$channelId", channelIds[i]);
                insert.Parameters.AddWithValue("$position", i + 1);
                insert.ExecuteNonQuery();
            }

            foreach (var channelId in channelIds)
            {
                using (var checkCmd = db.CreateCommand())
                {
                    checkCmd.CommandText = "SELECT ScanListId FROM Channels WHERE Id = $channelId;";
                    checkCmd.Parameters.AddWithValue("$channelId", channelId);
                    if (checkCmd.ExecuteScalar() is long current && current != scanListId)
                        warnings.Add($"Channel {channelId} already had a different scan list assigned - overwritten with zone '{zone.Name}'.");
                }

                using var update = db.CreateCommand();
                update.CommandText = "UPDATE Channels SET ScanListId = $scanListId WHERE Id = $channelId;";
                update.Parameters.AddWithValue("$scanListId", scanListId);
                update.Parameters.AddWithValue("$channelId", channelId);
                update.ExecuteNonQuery();
                channelsLinked++;
            }

            zonesProcessed++;
        }

        transaction.Commit();
        return new ZoneScanListResult(zonesProcessed, channelsLinked, warnings);
    }

    private static long InsertScanList(SqliteConnection db, string name)
    {
        using var insert = db.CreateCommand();
        insert.CommandText = "INSERT INTO ScanLists (Name) VALUES ($name); SELECT last_insert_rowid();";
        insert.Parameters.AddWithValue("$name", name);
        return (long)insert.ExecuteScalar()!;
    }
}
