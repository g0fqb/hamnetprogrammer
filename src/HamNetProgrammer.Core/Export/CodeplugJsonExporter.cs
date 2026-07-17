using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace HamNetProgrammer.Core.Export;

/// <summary>
/// Exports the current SQLite codeplug to a single portable JSON document - the first cut of the
/// "whole codeplug in one file" companion format discussed alongside SQLite as source of truth.
/// Read-only; does not touch the database.
/// </summary>
public static class CodeplugJsonExporter
{
    public sealed record ChannelDto(int Number, string Name, string Mode, double RxMHz, double TxMHz, int? ColorCode, int? TimeSlot, string? TalkGroup);
    public sealed record ZoneDto(string Name, List<ChannelDto> Channels);
    public sealed record ScanListDto(string Name, List<string> Channels);
    public sealed record GroupListDto(string Name, List<string> TalkGroups);
    public sealed record RoamingZoneDto(string TalkGroup, List<string> Members);
    public sealed record CodeplugDto(
        DateTimeOffset GeneratedAt,
        List<string> RadioIds,
        List<ZoneDto> Zones,
        List<ScanListDto> ScanLists,
        List<GroupListDto> GroupLists,
        List<RoamingZoneDto> RoamingZones,
        int TotalChannels,
        int UnzonedChannels);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static CodeplugDto Export(SqliteConnection db)
    {
        var radioIds = QueryStrings(db, "SELECT Callsign FROM RadioIds ORDER BY Callsign;");

        var zones = new List<ZoneDto>();
        foreach (var (zoneId, zoneName) in QueryIdNamePairs(db, "SELECT Id, Name FROM Zones ORDER BY Name;"))
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT c.ChannelNumber, c.Name, c.Mode, c.RxFrequencyHz, c.TxFrequencyHz, c.ColorCode, c.TimeSlot, ct.Name
                FROM ZoneChannels zc
                JOIN Channels c ON c.Id = zc.ChannelId
                LEFT JOIN Contacts ct ON ct.Id = c.ContactId
                WHERE zc.ZoneId = $zoneId
                ORDER BY zc.Position;
                """;
            cmd.Parameters.AddWithValue("$zoneId", zoneId);
            using var reader = cmd.ExecuteReader();
            var channels = new List<ChannelDto>();
            while (reader.Read())
            {
                channels.Add(new ChannelDto(
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt64(3) / 1_000_000.0,
                    reader.GetInt64(4) / 1_000_000.0,
                    reader.IsDBNull(5) ? null : reader.GetInt32(5),
                    reader.IsDBNull(6) ? null : reader.GetInt32(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7)));
            }
            zones.Add(new ZoneDto(zoneName, channels));
        }

        var scanLists = new List<ScanListDto>();
        foreach (var (scanListId, scanListName) in QueryIdNamePairs(db, "SELECT Id, Name FROM ScanLists ORDER BY Name;"))
        {
            var names = QueryStrings(db,
                "SELECT c.Name FROM ScanListChannels slc JOIN Channels c ON c.Id = slc.ChannelId WHERE slc.ScanListId = $id ORDER BY slc.Position;",
                ("$id", scanListId));
            scanLists.Add(new ScanListDto(scanListName, names));
        }

        var groupLists = new List<GroupListDto>();
        foreach (var (groupListId, groupListName) in QueryIdNamePairs(db, "SELECT Id, Name FROM GroupLists ORDER BY Name;"))
        {
            var names = QueryStrings(db,
                "SELECT ct.Name FROM GroupListContacts glc JOIN Contacts ct ON ct.Id = glc.ContactId WHERE glc.GroupListId = $id ORDER BY glc.Position;",
                ("$id", groupListId));
            groupLists.Add(new GroupListDto(groupListName, names));
        }

        var roamingZones = new List<RoamingZoneDto>();
        foreach (var (roamingZoneId, roamingZoneName) in QueryIdNamePairs(db, "SELECT Id, Name FROM RoamingZones ORDER BY Name;"))
        {
            var members = QueryStrings(db,
                """
                SELECT z.Name || ' / ' || c.Name
                FROM RoamingZoneChannels rzc
                JOIN Channels c ON c.Id = rzc.ChannelId
                JOIN ZoneChannels zc ON zc.ChannelId = c.Id
                JOIN Zones z ON z.Id = zc.ZoneId
                WHERE rzc.RoamingZoneId = $id
                ORDER BY rzc.Position;
                """,
                ("$id", roamingZoneId));
            roamingZones.Add(new RoamingZoneDto(roamingZoneName, members));
        }

        var totalChannels = QueryScalarInt(db, "SELECT count(*) FROM Channels;");
        var zonedChannels = QueryScalarInt(db, "SELECT count(DISTINCT ChannelId) FROM ZoneChannels;");

        return new CodeplugDto(
            DateTimeOffset.Now, radioIds, zones, scanLists, groupLists, roamingZones,
            totalChannels, totalChannels - zonedChannels);
    }

    public static void ExportToFile(SqliteConnection db, string path)
    {
        var dto = Export(db);
        File.WriteAllText(path, JsonSerializer.Serialize(dto, JsonOptions));
    }

    private static List<(long Id, string Name)> QueryIdNamePairs(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var results = new List<(long, string)>();
        while (reader.Read())
            results.Add((reader.GetInt64(0), reader.GetString(1)));
        return results;
    }

    private static List<string> QueryStrings(SqliteConnection db, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        using var reader = cmd.ExecuteReader();
        var results = new List<string>();
        while (reader.Read())
            if (!reader.IsDBNull(0))
                results.Add(reader.GetString(0));
        return results;
    }

    private static int QueryScalarInt(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
