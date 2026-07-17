using Microsoft.Data.Sqlite;

namespace HamNetProgrammer.Core.Data;

/// <summary>
/// Owns the SQLite schema for a HamNetProgrammer codeplug. This is the tool's source of truth -
/// an open, inspectable replacement for CPS's binary blob and RT Systems' fragile flat CSVs.
///
/// Foreign keys reference stable SQLite row IDs, not channel numbers or names, specifically so
/// that renumbering/restructuring channels doesn't silently break Zone/ScanList membership the
/// way it does when a CSV gets reimported into Anytone CPS.
/// </summary>
public static class CodeplugDatabase
{
    private const string Schema = """
        PRAGMA foreign_keys = ON;

        CREATE TABLE IF NOT EXISTS Zones (
            Id INTEGER PRIMARY KEY,
            Name TEXT NOT NULL UNIQUE
        );

        CREATE TABLE IF NOT EXISTS ScanLists (
            Id INTEGER PRIMARY KEY,
            Name TEXT NOT NULL UNIQUE
        );

        CREATE TABLE IF NOT EXISTS GroupLists (
            Id INTEGER PRIMARY KEY,
            Name TEXT NOT NULL UNIQUE
        );

        CREATE TABLE IF NOT EXISTS RadioIds (
            Id INTEGER PRIMARY KEY,
            Callsign TEXT NOT NULL UNIQUE,
            DmrId INTEGER NULL
        );

        -- Talkgroups today; individual DMR contacts will use the same table once imported.
        CREATE TABLE IF NOT EXISTS Contacts (
            Id INTEGER PRIMARY KEY,
            Name TEXT NOT NULL,
            CallType TEXT NOT NULL DEFAULT 'Group',
            DmrId INTEGER NULL,
            UNIQUE(Name, CallType)
        );

        CREATE TABLE IF NOT EXISTS Channels (
            Id INTEGER PRIMARY KEY,
            ChannelNumber INTEGER NOT NULL UNIQUE,
            Name TEXT NOT NULL,
            Mode TEXT NOT NULL,
            RxFrequencyHz INTEGER NOT NULL,
            TxFrequencyHz INTEGER NOT NULL,
            Bandwidth TEXT NULL,
            Power TEXT NULL,
            AdmitCriteria TEXT NULL,
            ColorCode INTEGER NULL,
            TimeSlot INTEGER NULL,
            ContactId INTEGER NULL REFERENCES Contacts(Id),
            RadioIdId INTEGER NULL REFERENCES RadioIds(Id),
            ScanListId INTEGER NULL REFERENCES ScanLists(Id),
            GroupListId INTEGER NULL REFERENCES GroupLists(Id),
            ToneMode TEXT NULL,
            CtcssHz REAL NULL,
            RxCtcssHz REAL NULL,
            DcsCode TEXT NULL,
            RxDcsCode TEXT NULL,
            ExtraAttributesJson TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS ZoneChannels (
            ZoneId INTEGER NOT NULL REFERENCES Zones(Id) ON DELETE CASCADE,
            ChannelId INTEGER NOT NULL REFERENCES Channels(Id) ON DELETE CASCADE,
            Position INTEGER NOT NULL,
            PRIMARY KEY (ZoneId, Position)
        );

        CREATE TABLE IF NOT EXISTS ScanListChannels (
            ScanListId INTEGER NOT NULL REFERENCES ScanLists(Id) ON DELETE CASCADE,
            ChannelId INTEGER NOT NULL REFERENCES Channels(Id) ON DELETE CASCADE,
            Position INTEGER NOT NULL,
            PRIMARY KEY (ScanListId, Position)
        );

        CREATE TABLE IF NOT EXISTS GroupListContacts (
            GroupListId INTEGER NOT NULL REFERENCES GroupLists(Id) ON DELETE CASCADE,
            ContactId INTEGER NOT NULL REFERENCES Contacts(Id) ON DELETE CASCADE,
            Position INTEGER NOT NULL,
            PRIMARY KEY (GroupListId, Position)
        );

        -- A roaming zone groups the SAME talkgroup's channels across different hotspot zones
        -- (the transpose of a Zone, which groups different talkgroups on one hotspot). Roaming
        -- channel records (rx/tx/colorcode/slot/name) are derived from the referenced Channel at
        -- encode time rather than duplicated here.
        CREATE TABLE IF NOT EXISTS RoamingZones (
            Id INTEGER PRIMARY KEY,
            Name TEXT NOT NULL UNIQUE
        );

        CREATE TABLE IF NOT EXISTS RoamingZoneChannels (
            RoamingZoneId INTEGER NOT NULL REFERENCES RoamingZones(Id) ON DELETE CASCADE,
            ChannelId INTEGER NOT NULL REFERENCES Channels(Id) ON DELETE CASCADE,
            Position INTEGER NOT NULL,
            PRIMARY KEY (RoamingZoneId, Position)
        );

        -- Supports type-to-search talkgroup lookup (the point of moving off CPS's scroll-through-hundreds
        -- picker) once a full talkgroup database is imported into Contacts.
        CREATE INDEX IF NOT EXISTS IX_Contacts_Name ON Contacts(Name);
        CREATE INDEX IF NOT EXISTS IX_Contacts_DmrId ON Contacts(DmrId);
        """;

    public static SqliteConnection OpenOrCreate(string path)
    {
        var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = Schema;
            cmd.ExecuteNonQuery();
        }
        return connection;
    }
}
