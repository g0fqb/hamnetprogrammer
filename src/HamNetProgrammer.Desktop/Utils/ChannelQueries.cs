using Microsoft.Data.Sqlite;

namespace HamNetProgrammer.Desktop.Utils;

/// Shared channel lookups used by Zones, Scan Lists, and Roaming Zones - all three manage a
/// membership join table shaped the same way (OwnerId, ChannelId, Position).
public static class ChannelQueries
{
    public static List<ChannelPickerDialog.ChannelPickerRow> GetChannelsNotIn(
        SqliteConnection db, string membershipTable, string ownerColumn, long ownerId)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"""
            SELECT c.Id, c.ChannelNumber, c.Name, c.RxFrequencyHz
            FROM Channels c
            WHERE c.Id NOT IN (SELECT ChannelId FROM {membershipTable} WHERE {ownerColumn} = $ownerId)
            ORDER BY c.ChannelNumber;
            """;
        cmd.Parameters.Add(new SqliteParameter("$ownerId", ownerId));
        using var reader = cmd.ExecuteReader();

        var results = new List<ChannelPickerDialog.ChannelPickerRow>();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            var num = reader.GetInt32(1);
            var name = reader.GetString(2);
            var rxHz = reader.GetInt64(3);
            results.Add(new ChannelPickerDialog.ChannelPickerRow(id, num, name, (rxHz / 1_000_000.0).ToString("F5")));
        }
        return results;
    }

    public static List<ChannelPickerDialog.ChannelPickerRow> GetAllChannels(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT Id, ChannelNumber, Name, RxFrequencyHz FROM Channels ORDER BY ChannelNumber;";
        using var reader = cmd.ExecuteReader();

        var results = new List<ChannelPickerDialog.ChannelPickerRow>();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            var num = reader.GetInt32(1);
            var name = reader.GetString(2);
            var rxHz = reader.GetInt64(3);
            results.Add(new ChannelPickerDialog.ChannelPickerRow(id, num, name, (rxHz / 1_000_000.0).ToString("F5")));
        }
        return results;
    }
}
