using System.Collections.Generic;
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

        -- Priority/timing/revert fields per the AT-D878UV scan list record layout (confirmed against
        -- both anytone-flash-tools and qdmr's independent reverse-engineering - see
        -- Radios/AnyTone/Codecs/ScanListRecordCodec.cs). All nullable: NULL means "off"/device default
        -- (Priority channels off, Look Back 0.5s, Dropout/Dwell 0.1s, Revert = Selected).
        CREATE TABLE IF NOT EXISTS ScanLists (
            Id INTEGER PRIMARY KEY,
            Name TEXT NOT NULL UNIQUE,
            PriorityChannel1Id INTEGER NULL REFERENCES Channels(Id),
            PriorityChannel2Id INTEGER NULL REFERENCES Channels(Id),
            LookBackTimeA REAL NULL,
            LookBackTimeB REAL NULL,
            DropoutDelayTime REAL NULL,
            DwellTime REAL NULL,
            RevertMode TEXT NULL
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

        -- Radio-wide settings (not per-channel/zone) - GPS and APRS for now, more sections planned.
        -- Single row (Id always 1). Database-only for now: the AT-D878UV's documented byte layout
        -- for this region is ambiguous in places and shares a flash erase block with data involved
        -- in a past incident (see project notes) - not wired into the radio write path until the
        -- exact offsets are hardware-verified.
        CREATE TABLE IF NOT EXISTS RadioSettings (
            Id INTEGER PRIMARY KEY CHECK (Id = 1),
            GpsEnabled INTEGER NULL,
            GpsMode TEXT NULL,
            AprsReportType TEXT NULL,
            AprsCallsign TEXT NULL,
            AprsCallsignSsid INTEGER NULL,
            AprsDestCallsign TEXT NULL,
            AprsDestSsid INTEGER NULL,
            AprsSignalPath TEXT NULL,
            AprsAutoTxIntervalSeconds INTEGER NULL,
            AprsReportChannelId INTEGER NULL REFERENCES Channels(Id),
            AprsTalkGroupId INTEGER NULL REFERENCES Contacts(Id),
            AprsCallType TEXT NULL,
            AprsSlot INTEGER NULL,
            AprsFixedLocationBeacon INTEGER NULL,
            AprsLatitudeDegree INTEGER NULL,
            AprsLatitudeMinute INTEGER NULL,
            AprsLatitudeSign TEXT NULL,
            AprsLongitudeDegree INTEGER NULL,
            AprsLongitudeMinute INTEGER NULL,
            AprsLongitudeSign TEXT NULL,
            AprsSendingText TEXT NULL,
            GpsTemplateText TEXT NULL
        );
        INSERT OR IGNORE INTO RadioSettings (Id) VALUES (1);
        """;

    // Columns added to ScanLists after its initial release - CREATE TABLE IF NOT EXISTS won't add
    // these to a database that already has the table, so existing databases (like the working
    // codeplug.db) need an explicit, idempotent ADD COLUMN pass.
    private static readonly (string Column, string Definition)[] ScanListMigrationColumns =
    [
        ("PriorityChannel1Id", "INTEGER NULL REFERENCES Channels(Id)"),
        ("PriorityChannel2Id", "INTEGER NULL REFERENCES Channels(Id)"),
        ("LookBackTimeA", "REAL NULL"),
        ("LookBackTimeB", "REAL NULL"),
        ("DropoutDelayTime", "REAL NULL"),
        ("DwellTime", "REAL NULL"),
        ("RevertMode", "TEXT NULL"),
    ];

    public static SqliteConnection OpenOrCreate(string path)
    {
        var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = Schema;
            cmd.ExecuteNonQuery();
        }
        MigrateScanListColumns(connection);
        return connection;
    }

    private static void MigrateScanListColumns(SqliteConnection connection)
    {
        var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var pragmaCmd = connection.CreateCommand())
        {
            pragmaCmd.CommandText = "PRAGMA table_info(ScanLists);";
            using var reader = pragmaCmd.ExecuteReader();
            while (reader.Read())
                existingColumns.Add(reader.GetString(1));
        }

        foreach (var (column, definition) in ScanListMigrationColumns)
        {
            if (existingColumns.Contains(column)) continue;
            using var alterCmd = connection.CreateCommand();
            alterCmd.CommandText = $"ALTER TABLE ScanLists ADD COLUMN {column} {definition};";
            alterCmd.ExecuteNonQuery();
        }
    }
}
