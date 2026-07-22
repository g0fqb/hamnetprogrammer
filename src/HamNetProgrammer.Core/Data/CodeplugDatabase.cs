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

        -- IsActive controls whether a zone is included the next time the codeplug is written to the
        -- radio - lets a zone be parked (e.g. "Home" while travelling with only a "Travel" zone
        -- active) without deleting it. Defaults to active so existing zones are unaffected.
        CREATE TABLE IF NOT EXISTS Zones (
            Id INTEGER PRIMARY KEY,
            Name TEXT NOT NULL UNIQUE,
            IsActive INTEGER NOT NULL DEFAULT 1
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
        -- Network tags which talkgroup network (Brandmeister/TGIF/FreeDMR/etc.) an imported
        -- contact came from - NULL for manually-created contacts. Purely provenance, not a
        -- merge key: the radio's own TalkGroupList addresses a group call by DmrId alone, with
        -- no concept of "network" in the format, so imports dedupe strictly by DmrId regardless
        -- of source (see TalkGroupNetworkImporter) - whichever import claims a number first
        -- keeps it.
        CREATE TABLE IF NOT EXISTS Contacts (
            Id INTEGER PRIMARY KEY,
            Name TEXT NOT NULL,
            CallType TEXT NOT NULL DEFAULT 'Group',
            DmrId INTEGER NULL,
            Network TEXT NULL,
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
        -- Single row (Id always 1). Most fields ARE written to the radio (see
        -- AnyToneD878CodeplugEncoder.EncodeRadioSettings), which shares a flash erase block with
        -- data involved in a past incident (see project notes) and is spliced in read-modify-write
        -- rather than written standalone because of it. GpsMode, AprsSendingText, and
        -- GpsTemplateText are the exception: no independently-confirmed D878UV byte offset exists
        -- for them in either reference source checked, so they stay database-only (see
        -- RadioSettingsPage, which labels them "not written to radio yet" in the UI) rather than
        -- risk writing to an unverified location.
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
            GpsTemplateText TEXT NULL,
            SyncListsWithZones INTEGER NOT NULL DEFAULT 1,
            ScanListsEnabled INTEGER NOT NULL DEFAULT 1,
            GroupListsEnabled INTEGER NOT NULL DEFAULT 1,
            RoamingEnabled INTEGER NOT NULL DEFAULT 1
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

    private static readonly (string Column, string Definition)[] ZoneMigrationColumns =
    [
        ("IsActive", "INTEGER NOT NULL DEFAULT 1"),
    ];

    private static readonly (string Column, string Definition)[] RadioSettingsMigrationColumns =
    [
        ("SyncListsWithZones", "INTEGER NOT NULL DEFAULT 1"),
        ("ScanListsEnabled", "INTEGER NOT NULL DEFAULT 1"),
        ("GroupListsEnabled", "INTEGER NOT NULL DEFAULT 1"),
        ("RoamingEnabled", "INTEGER NOT NULL DEFAULT 1"),
        // Fingerprint (count, max Id) of the eligible Contacts as of the last "Sync Reference Data"
        // run - see AnyToneD878CodeplugEncoder.GetContactIndexFingerprint. Routine Write Codeplug no
        // longer writes the talkgroup list every time (it's large and changes rarely), so if either
        // value has since changed, some channel's ContactIndex may no longer match what's actually
        // on the radio - this is how that gets detected and warned about before writing, instead of
        // silently repeating the old TG1-fallback bug.
        ("LastSyncedContactCount", "INTEGER NOT NULL DEFAULT 0"),
        ("LastSyncedMaxContactId", "INTEGER NOT NULL DEFAULT 0"),
    ];

    private static readonly (string Column, string Definition)[] ContactsMigrationColumns =
    [
        ("Network", "TEXT NULL"),
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
        MigrateColumns(connection, "ScanLists", ScanListMigrationColumns);
        MigrateColumns(connection, "Zones", ZoneMigrationColumns);
        MigrateColumns(connection, "RadioSettings", RadioSettingsMigrationColumns);
        MigrateColumns(connection, "Contacts", ContactsMigrationColumns);
        SeedContactSyncFingerprintIfUnset(connection);
        return connection;
    }

    // Routine writes always included the talkgroup list before "Sync Reference Data" existed, so
    // an existing database upgrading to this version has (in practice) already had its current
    // contacts on the radio as of its last write - seeding to the live fingerprint here avoids a
    // false "not synced" warning on the very next write, rather than defaulting to (0, 0) and
    // treating every pre-existing install as never having synced anything. Idempotent: only fires
    // while both columns are still at their just-migrated default.
    private static void SeedContactSyncFingerprintIfUnset(SqliteConnection connection)
    {
        using var checkCmd = connection.CreateCommand();
        checkCmd.CommandText = "SELECT LastSyncedContactCount, LastSyncedMaxContactId FROM RadioSettings WHERE Id = 1;";
        using var reader = checkCmd.ExecuteReader();
        if (!reader.Read() || reader.GetInt64(0) != 0 || reader.GetInt64(1) != 0) return;
        reader.Close();

        var (count, maxId) = Radios.AnyTone.AnyToneD878CodeplugEncoder.GetContactIndexFingerprint(connection);
        if (count == 0 && maxId == 0) return;

        using var updateCmd = connection.CreateCommand();
        updateCmd.CommandText = "UPDATE RadioSettings SET LastSyncedContactCount = $count, LastSyncedMaxContactId = $maxId WHERE Id = 1;";
        updateCmd.Parameters.AddWithValue("$count", count);
        updateCmd.Parameters.AddWithValue("$maxId", maxId);
        updateCmd.ExecuteNonQuery();
    }

    // CREATE TABLE IF NOT EXISTS won't add new columns to a table that already exists (like the
    // working codeplug.db), so any column added after a table's initial release needs this
    // explicit, idempotent ADD COLUMN pass.
    private static void MigrateColumns(SqliteConnection connection, string table, (string Column, string Definition)[] columns)
    {
        var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var pragmaCmd = connection.CreateCommand())
        {
            pragmaCmd.CommandText = $"PRAGMA table_info({table});";
            using var reader = pragmaCmd.ExecuteReader();
            while (reader.Read())
                existingColumns.Add(reader.GetString(1));
        }

        foreach (var (column, definition) in columns)
        {
            if (existingColumns.Contains(column)) continue;
            using var alterCmd = connection.CreateCommand();
            alterCmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
            alterCmd.ExecuteNonQuery();
        }
    }
}
