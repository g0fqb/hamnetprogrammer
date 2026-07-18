using HamNetProgrammer.Core.Radios.AnyTone.Codecs;
using Microsoft.Data.Sqlite;

namespace HamNetProgrammer.Core.Radios.AnyTone;

public sealed record EncodedRegion(string Name, uint Address, byte[] Data);

/// <summary>
/// Builds the AT-D878UV memory write plan from the SQLite codeplug: Channels, Zones (+ names,
/// used bitmap, default A/B channel), ScanLists (+ used bitmap), GroupLists, RoamingZones (+
/// RoamingChannels, both used bitmaps), Contacts/TalkGroupList (+ used bitmap, control data), and
/// RadioIds (+ used bitmap).
///
/// Channel physical placement uses flat index = ChannelNumber - 1, preserving the user's existing
/// numbering (including intentional zone-boundary gaps) rather than compacting it away.
/// </summary>
public static class AnyToneD878CodeplugEncoder
{
    private const int ChannelsPerBank = 128;

    public static List<EncodedRegion> Build(SqliteConnection db)
    {
        var regions = new List<EncodedRegion>();

        var contactIndex = BuildContactIndex(db);
        var radioIdIndex = BuildRadioIdIndex(db);
        var scanListIndex = BuildScanListIndex(db);
        var groupListIndex = BuildGroupListIndex(db);

        regions.AddRange(EncodeChannels(db, contactIndex, radioIdIndex, scanListIndex, groupListIndex));
        regions.AddRange(EncodeZones(db));
        regions.AddRange(EncodeScanLists(db));
        regions.AddRange(EncodeGroupLists(db, contactIndex));
        regions.AddRange(EncodeRoaming(db));
        regions.AddRange(EncodeTalkGroups(db, contactIndex));
        regions.AddRange(EncodeRadioIds(db, radioIdIndex));
        regions.AddRange(EncodeRadioSettings(db));

        return regions;
    }

    private static Dictionary<long, uint> BuildContactIndex(SqliteConnection db)
    {
        var map = new Dictionary<long, uint>();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT Id FROM Contacts ORDER BY Id;";
        using var reader = cmd.ExecuteReader();
        uint i = 0;
        while (reader.Read())
            map[reader.GetInt64(0)] = i++;
        return map;
    }

    private static Dictionary<long, byte> BuildRadioIdIndex(SqliteConnection db)
    {
        var map = new Dictionary<long, byte>();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT Id FROM RadioIds ORDER BY Id;";
        using var reader = cmd.ExecuteReader();
        byte i = 0;
        while (reader.Read())
            map[reader.GetInt64(0)] = i++;
        return map;
    }

    // Must match EncodeScanLists' own ordering/indexing exactly, since channels reference this index.
    private static Dictionary<long, byte> BuildScanListIndex(SqliteConnection db)
    {
        var map = new Dictionary<long, byte>();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT Id FROM ScanLists ORDER BY Id;";
        using var reader = cmd.ExecuteReader();
        byte i = 0;
        while (reader.Read())
            map[reader.GetInt64(0)] = i++;
        return map;
    }

    // Must match EncodeGroupLists' own ordering/indexing exactly, since channels reference this index.
    private static Dictionary<long, byte> BuildGroupListIndex(SqliteConnection db)
    {
        var map = new Dictionary<long, byte>();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT Id FROM GroupLists ORDER BY Id;";
        using var reader = cmd.ExecuteReader();
        byte i = 0;
        while (reader.Read())
            map[reader.GetInt64(0)] = i++;
        return map;
    }

    private static IEnumerable<EncodedRegion> EncodeChannels(SqliteConnection db, Dictionary<long, uint> contactIndex, Dictionary<long, byte> radioIdIndex, Dictionary<long, byte> scanListIndex, Dictionary<long, byte> groupListIndex)
    {
        var banks = new Dictionary<int, byte[]>();
        int BankLength(int bank) => bank == 31 ? 2176 : ChannelsPerBank * 64;

        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT ChannelNumber, Name, Mode, RxFrequencyHz, TxFrequencyHz, Bandwidth, Power,
                   ColorCode, TimeSlot, ContactId, RadioIdId, ScanListId, GroupListId
            FROM Channels;
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var channelNumber = reader.GetInt32(0);
            var flatIndex = (uint)(channelNumber - 1);
            var bank = (int)(flatIndex / ChannelsPerBank);
            var slot = (int)(flatIndex % ChannelsPerBank);

            if (bank is < 0 or > 31)
                throw new InvalidOperationException($"Channel {channelNumber} (flat index {flatIndex}) is out of the AT-D878UV's channel range.");

            var record = new ChannelRecord(
                RxFrequencyHz: reader.GetInt64(3),
                TxFrequencyHz: reader.GetInt64(4),
                Is25kHz: !reader.IsDBNull(5) && reader.GetString(5).Contains("25"),
                PowerLevel: PowerLevelFromText(reader.IsDBNull(6) ? null : reader.GetString(6)),
                IsDigital: reader.GetString(2).Equals("Digital", StringComparison.OrdinalIgnoreCase),
                ColorCode: reader.IsDBNull(7) ? (byte)1 : (byte)reader.GetInt32(7),
                TimeSlot: reader.IsDBNull(8) ? (byte)1 : (byte)reader.GetInt32(8),
                ContactIndex: reader.IsDBNull(9) ? 0 : contactIndex.GetValueOrDefault(reader.GetInt64(9)),
                RadioIdIndex: reader.IsDBNull(10) ? (byte)0 : radioIdIndex.GetValueOrDefault(reader.GetInt64(10)),
                ScanListIndex: reader.IsDBNull(11) ? null : scanListIndex.GetValueOrDefault(reader.GetInt64(11)),
                GroupListIndex: reader.IsDBNull(12) ? null : groupListIndex.GetValueOrDefault(reader.GetInt64(12)),
                Name: reader.GetString(1));

            if (!banks.TryGetValue(bank, out var buffer))
                banks[bank] = buffer = new byte[BankLength(bank)];

            ChannelRecordCodec.Encode(record).CopyTo(buffer, slot * 64);
        }

        foreach (var (bank, buffer) in banks)
            yield return new EncodedRegion($"Channels[{bank}]", 0x00800000u + (uint)(bank * 0x40000), buffer);
    }

    private static byte PowerLevelFromText(string? power) => power?.ToLowerInvariant() switch
    {
        "low" => 0,
        "mid" or "medium" => 1,
        "high" => 2,
        "turbo" => 3,
        _ => 2,
    };

    // ZoneAChannel (0x02500100) and ZoneBChannel (0x02500300) - the default displayed channel per
    // zone - are NOT emitted here. They live inside a 256KB flash erase block (0x02500000-
    // 0x0253FFFF) shared with several other settings sections (power-on message, APRS, DTMF list,
    // etc.) that this encoder does not otherwise know about or intend to touch. Writing this pair
    // of small regions in isolation erases the ENTIRE containing block and only reprograms these
    // 1024 bytes, silently wiping everything else sharing it - confirmed the hard way on real
    // hardware (2026-07-17): it wiped the power-on password setting, welcome message, and APRS
    // config. AnyToneD878CodeplugWriter handles this pair specially: it reads the live device's
    // current content for that whole block first, splices in zeroed Zone A/B Channel data (every
    // zone defaults to its first channel), and writes the merged result back as one region -
    // preserving whatever else is actually on the user's radio rather than guessing or omitting it.
    public const uint ZoneChannelDefaultsBlockAddress = 0x02500000;
    public const int ZoneChannelDefaultsBlockLength = 0x1900; // covers every documented section up to AnalogAprsList
    public const int ZoneAChannelOffset = 0x0100;
    public const int ZoneBChannelOffset = 0x0300;

    private static IEnumerable<EncodedRegion> EncodeZones(SqliteConnection db)
    {
        var zonesBuffer = new byte[ZoneRecordCodec.MaxMembers > 0 ? 250 * 512 : 0];
        var namesBuffer = new byte[250 * 32];
        var usedBuffer = new byte[32];

        // Only active zones are written - see the Zones.IsActive remarks in CodeplugDatabase for why
        // (parking a zone, e.g. "Home" while a "Travel" zone is active, without deleting it).
        using var zonesCmd = db.CreateCommand();
        zonesCmd.CommandText = "SELECT Id, Name FROM Zones WHERE IsActive = 1 ORDER BY Id;";
        using var zonesReader = zonesCmd.ExecuteReader();

        var zoneIndex = 0;
        while (zonesReader.Read())
        {
            var zoneId = zonesReader.GetInt64(0);
            var zoneName = zonesReader.GetString(1);

            using var membersCmd = db.CreateCommand();
            membersCmd.CommandText = """
                SELECT c.ChannelNumber
                FROM ZoneChannels zc
                JOIN Channels c ON c.Id = zc.ChannelId
                WHERE zc.ZoneId = $zoneId
                ORDER BY zc.Position;
                """;
            membersCmd.Parameters.AddWithValue("$zoneId", zoneId);
            using var membersReader = membersCmd.ExecuteReader();
            var members = new List<uint>();
            while (membersReader.Read())
                members.Add((uint)(membersReader.GetInt32(0) - 1));

            ZoneRecordCodec.Encode(members).CopyTo(zonesBuffer, zoneIndex * 512);
            AsciiFieldCodec.Encode(zoneName, 32).CopyTo(namesBuffer, zoneIndex * 32);
            SetBit(usedBuffer, zoneIndex);

            zoneIndex++;
        }

        yield return new EncodedRegion("Zones", 0x01000000, zonesBuffer);
        yield return new EncodedRegion("ZoneNames", 0x02540000, namesBuffer);
        yield return new EncodedRegion("ZonesUsed", 0x024c1300, usedBuffer);
    }

    private static IEnumerable<EncodedRegion> EncodeScanLists(SqliteConnection db)
    {
        var usedBuffer = new byte[32];
        var scanListRegions = new List<EncodedRegion>();

        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT sl.Id, sl.Name,
                   p1.ChannelNumber, p2.ChannelNumber,
                   sl.LookBackTimeA, sl.LookBackTimeB, sl.DropoutDelayTime, sl.DwellTime, sl.RevertMode
            FROM ScanLists sl
            LEFT JOIN Channels p1 ON p1.Id = sl.PriorityChannel1Id
            LEFT JOIN Channels p2 ON p2.Id = sl.PriorityChannel2Id
            ORDER BY sl.Id;
            """;
        using var reader = cmd.ExecuteReader();

        var index = 0;
        while (reader.Read())
        {
            var scanListId = reader.GetInt64(0);
            var name = reader.GetString(1);
            uint? priority1 = reader.IsDBNull(2) ? null : (uint)(reader.GetInt32(2) - 1);
            uint? priority2 = reader.IsDBNull(3) ? null : (uint)(reader.GetInt32(3) - 1);
            var lookBackA = reader.IsDBNull(4) ? 0.5 : reader.GetDouble(4);
            var lookBackB = reader.IsDBNull(5) ? 0.5 : reader.GetDouble(5);
            var dropout = reader.IsDBNull(6) ? 0.1 : reader.GetDouble(6);
            var dwell = reader.IsDBNull(7) ? 0.1 : reader.GetDouble(7);
            var revertMode = reader.IsDBNull(8) || !Enum.TryParse<ScanListRevertMode>(reader.GetString(8), out var parsedRevert)
                ? ScanListRevertMode.Selected
                : parsedRevert;

            using var membersCmd = db.CreateCommand();
            membersCmd.CommandText = """
                SELECT c.ChannelNumber
                FROM ScanListChannels slc
                JOIN Channels c ON c.Id = slc.ChannelId
                WHERE slc.ScanListId = $id
                ORDER BY slc.Position;
                """;
            membersCmd.Parameters.AddWithValue("$id", scanListId);
            using var membersReader = membersCmd.ExecuteReader();
            var members = new List<uint>();
            while (membersReader.Read())
                members.Add((uint)(membersReader.GetInt32(0) - 1));

            var address = ScanListAddress(index);
            var data = ScanListRecordCodec.Encode(new ScanListRecord(
                name, members, priority1, priority2, lookBackA, lookBackB, dropout, dwell, revertMode));
            scanListRegions.Add(new EncodedRegion($"ScanList[{index + 1}]", address, data));
            SetBit(usedBuffer, index);
            index++;
        }

        foreach (var r in scanListRegions) yield return r;
        yield return new EncodedRegion("ScanListsUsed", 0x024c1340, usedBuffer);
    }

    // Matches AnyToneD878MemoryMap's scanlist addressing: 16 slots per 0x40000-aligned column, 0x200 apart.
    private static uint ScanListAddress(int index0Based) =>
        0x01080000u + (uint)(index0Based / 16 * 0x40000) + (uint)(index0Based % 16 * 0x200);

    // Unlike ScanList/RoamingChannels/PrefabSms, all 250 possible group list slots (250*512=
    // 0x1F400 bytes) fit inside a single clean 0x40000-aligned erase block with no other named
    // region sharing it - the same shape as Zones (also 250*512 bytes, already write-tested on
    // real hardware repeatedly without incident), not the sparse-multi-block pattern those other
    // sections use. Per the doc, "empty groups will not be written" - only DB rows get a region,
    // same convention EncodeScanLists already uses, so untouched higher slots are simply left as
    // whatever was on the radio before (consistent with how ScanLists already behaves).
    private static IEnumerable<EncodedRegion> EncodeGroupLists(SqliteConnection db, Dictionary<long, uint> contactIndex)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT Id, Name FROM GroupLists ORDER BY Id;";
        using var reader = cmd.ExecuteReader();

        var index = 0;
        while (reader.Read())
        {
            var groupListId = reader.GetInt64(0);
            var name = reader.GetString(1);

            using var membersCmd = db.CreateCommand();
            membersCmd.CommandText = """
                SELECT c.Id
                FROM GroupListContacts glc
                JOIN Contacts c ON c.Id = glc.ContactId
                WHERE glc.GroupListId = $id
                ORDER BY glc.Position;
                """;
            membersCmd.Parameters.AddWithValue("$id", groupListId);
            using var membersReader = membersCmd.ExecuteReader();
            var members = new List<uint>();
            while (membersReader.Read())
                members.Add(contactIndex.GetValueOrDefault(membersReader.GetInt64(0)));

            var address = 0x02980000u + (uint)(index * 512);
            var data = GroupListRecordCodec.Encode(new GroupListRecord(name, members));
            yield return new EncodedRegion($"GroupList[{index + 1}]", address, data);
            index++;
        }
    }

    // RoamingChannels/RoamingChannelsUsed/RoamingZonesUsed/RoamingZones all live inside the SAME
    // 256KB flash erase block (0x01040000-0x0107FFFF) - confirmed by inspecting the memory map:
    // RoamingZones ends at 0x01045000, but the block runs to 0x0107FFFF, leaving ~237KB completely
    // undocumented within it. This is exactly the ZoneAChannel/ZoneBChannel shared-block pattern
    // that caused a real corruption incident (see ZoneChannelDefaultsBlockAddress's remarks) -
    // writing any of these four regions standalone would erase the whole block and blank that
    // undocumented tail. AnyToneD878CodeplugWriter handles this the same way: these four regions
    // are emitted here at their real addresses (so names/addresses match the baseline dump for
    // Restore), but the writer intercepts them, removes them from the direct-write list, and
    // splices their bytes into a live read of the ENTIRE containing block before writing it back
    // as one region - unlike the existing Zone A/B fix, which only covers documented sub-regions
    // up to 0x1900 bytes and leaves its own block's tail as a smaller, still-open, deliberately
    // separate risk (not repeated here now that the failure mode is fully understood).
    public const uint RoamingBlockAddress = 0x01040000;
    public const int RoamingBlockLength = 0x40000; // the FULL erase block, not just documented sections
    public static readonly string[] RoamingBlockRegionNames = ["RoamingChannels", "RoamingChannelsUsed", "RoamingZonesUsed", "RoamingZones"];
    public const int RoamingChannelsOffset = 0x0000;
    public const int RoamingChannelsUsedOffset = 0x2000;
    public const int RoamingZonesUsedOffset = 0x2080;
    public const int RoamingZonesOffset = 0x3000;

    private static IEnumerable<EncodedRegion> EncodeRoaming(SqliteConnection db)
    {
        var channelIndex = BuildRoamingChannelIndex(db);

        var channelsBuffer = new byte[250 * 32];
        var channelsUsedBuffer = new byte[32];

        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                SELECT c.Id, c.Name, c.RxFrequencyHz, c.TxFrequencyHz, c.ColorCode, c.TimeSlot
                FROM Channels c
                WHERE c.Id IN (SELECT DISTINCT ChannelId FROM RoamingZoneChannels)
                ORDER BY c.Id;
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var index = channelIndex[reader.GetInt64(0)];
                var name = reader.GetString(1);
                var rxHz = reader.GetInt64(2);
                var txHz = reader.GetInt64(3);
                var colorCode = reader.IsDBNull(4) ? (byte)1 : (byte)reader.GetInt32(4);
                var slot = reader.IsDBNull(5) ? (byte)0 : (byte)(reader.GetInt32(5) == 2 ? 1 : 0);

                RoamingChannelRecordCodec.Encode(new RoamingChannelRecord(rxHz, txHz, colorCode, slot, name))
                    .CopyTo(channelsBuffer, index * 32);
                SetBit(channelsUsedBuffer, index);
            }
        }

        var zonesBuffer = new byte[64 * 128];
        var zonesUsedBuffer = new byte[16];

        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT Id, Name FROM RoamingZones ORDER BY Id;";
            using var reader = cmd.ExecuteReader();
            var zoneIndex = 0;
            while (reader.Read())
            {
                if (zoneIndex >= 64) break; // hardware maximum

                var roamingZoneId = reader.GetInt64(0);
                var name = reader.GetString(1);

                using var membersCmd = db.CreateCommand();
                membersCmd.CommandText = """
                    SELECT rzc.ChannelId
                    FROM RoamingZoneChannels rzc
                    WHERE rzc.RoamingZoneId = $id
                    ORDER BY rzc.Position;
                    """;
                membersCmd.Parameters.AddWithValue("$id", roamingZoneId);
                using var membersReader = membersCmd.ExecuteReader();
                var members = new List<byte>();
                while (membersReader.Read())
                    members.Add(channelIndex[membersReader.GetInt64(0)]);

                RoamingZoneRecordCodec.Encode(new RoamingZoneRecord(name, members)).CopyTo(zonesBuffer, zoneIndex * 128);
                SetBit(zonesUsedBuffer, zoneIndex);
                zoneIndex++;
            }
        }

        yield return new EncodedRegion("RoamingChannels", 0x01040000, channelsBuffer);
        yield return new EncodedRegion("RoamingChannelsUsed", 0x01042000, channelsUsedBuffer);
        yield return new EncodedRegion("RoamingZonesUsed", 0x01042080, zonesUsedBuffer);
        yield return new EncodedRegion("RoamingZones", 0x01043000, zonesBuffer);
    }

    // Distinct channels referenced by any roaming zone, in a stable order - must match
    // EncodeRoaming's own indexing exactly, since roaming zone records reference this index.
    private static Dictionary<long, byte> BuildRoamingChannelIndex(SqliteConnection db)
    {
        var map = new Dictionary<long, byte>();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT ChannelId FROM RoamingZoneChannels ORDER BY ChannelId;";
        using var reader = cmd.ExecuteReader();
        byte i = 0;
        while (reader.Read())
            map[reader.GetInt64(0)] = i++;
        return map;
    }

    // Marks a region as belonging inside the shared 0x02500000-0x02501900 flash block (see
    // ZoneChannelDefaultsBlockAddress's remarks) - AnyToneD878CodeplugWriter pulls out every
    // region whose name has this prefix and splices it into the same live-read/write-back buffer
    // it already uses for Zone A/B Channel defaults, rather than writing any of them standalone.
    public const string SharedBlockRegionPrefix = "RadioSettings.";

    // Byte offsets confirmed against qdmr's d878uv_generalsettings.txt and d878uv_aprssetting.txt
    // (struct layouts generated from the same C++ code qdmr uses to successfully program real
    // AT-D878UV radios - a stronger source than anytone-flash-tools' hand-annotated hex dump for
    // this particular section, whose row-header formatting doesn't parse unambiguously byte-by-
    // byte). Both structs live inside the shared block already covered by
    // ZoneChannelDefaultsBlockLength, so no new read-modify-write mechanism is needed - only new
    // sub-ranges spliced into the existing one.
    //
    // Deliberately scoped to Digital APRS reporting via "DMR APRS System 0" (fields for systems
    // 1-7, and the separate Analog/FM APRS fields, exist on the radio but aren't modeled in the
    // RadioSettings schema - out of scope for a hotspot-focused first cut). GPS Mode (GPS/BDS/
    // GPS+BDS), present in the RadioSettings schema, has no confirmed byte offset in either
    // independent source checked for this model - likely a D578UV-only field - so it is
    // deliberately never written here despite existing as a UI/database field.
    private const int GpsEnabledOffset = 0x0028;
    private const int AprsAutoTxIntervalOffset = 0x100B;
    private const int AprsLocationOffset = 0x100D; // fixed flag, lat deg/min/sec/sign, lon deg/min/sec/sign (9 bytes)
    private const int AprsDestCallOffset = 0x1016; // dest call (6) + dest SSID (1) + source call (6) + source SSID (1) = 14 bytes
    private const int AprsPathOffset = 0x1024; // 20 bytes ASCII
    private const int AprsReportChannelOffset = 0x1040; // uint16 LE, 0x0fa2 = "Selected" (current channel)
    private const int AprsTalkGroupOffset = 0x1050; // 4-byte BCD DMR ID
    private const int AprsCallTypeOffset = 0x1070; // 0=Private, 1=Group, 2=All
    private const int AprsSlotOffset = 0x1079; // 0=Channel, 1=TS1, 2=TS2
    private const ushort AprsReportChannelSelected = 0x0fa2;

    private static IEnumerable<EncodedRegion> EncodeRadioSettings(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT GpsEnabled, AprsReportType, AprsCallsign, AprsCallsignSsid, AprsDestCallsign, AprsDestSsid,
                   AprsSignalPath, AprsAutoTxIntervalSeconds, AprsReportChannelId, AprsTalkGroupId, AprsCallType,
                   AprsSlot, AprsFixedLocationBeacon, AprsLatitudeDegree, AprsLatitudeMinute, AprsLatitudeSign,
                   AprsLongitudeDegree, AprsLongitudeMinute, AprsLongitudeSign
            FROM RadioSettings WHERE Id = 1;
            """;
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) yield break;

        var gpsEnabled = !reader.IsDBNull(0) && reader.GetInt64(0) != 0;
        yield return new EncodedRegion($"{SharedBlockRegionPrefix}GpsEnabled", 0x02500000u + GpsEnabledOffset, [(byte)(gpsEnabled ? 1 : 0)]);

        var reportType = reader.IsDBNull(1) ? "Off" : reader.GetString(1);
        if (reportType != "Digital") yield break; // Analog/Off deliberately not written - see remarks above.

        var callsign = reader.IsDBNull(2) ? "" : reader.GetString(2);
        var callsignSsid = reader.IsDBNull(3) ? (byte)0 : (byte)reader.GetInt64(3);
        var destCallsign = reader.IsDBNull(4) ? "" : reader.GetString(4);
        var destSsid = reader.IsDBNull(5) ? (byte)0 : (byte)reader.GetInt64(5);
        var signalPath = reader.IsDBNull(6) ? "" : reader.GetString(6);
        var autoTxSeconds = reader.IsDBNull(7) ? 0 : reader.GetInt64(7);
        long? reportChannelId = reader.IsDBNull(8) ? null : reader.GetInt64(8);
        long? talkGroupContactId = reader.IsDBNull(9) ? null : reader.GetInt64(9);
        var callType = reader.IsDBNull(10) ? "Group" : reader.GetString(10);
        int? slot = reader.IsDBNull(11) ? null : reader.GetInt32(11);
        var fixedLocation = !reader.IsDBNull(12) && reader.GetInt64(12) != 0;
        var latDegree = reader.IsDBNull(13) ? 0 : reader.GetInt64(13);
        var latMinute = reader.IsDBNull(14) ? 0 : reader.GetInt64(14);
        var latSign = reader.IsDBNull(15) ? "N" : reader.GetString(15);
        var lonDegree = reader.IsDBNull(16) ? 0 : reader.GetInt64(16);
        var lonMinute = reader.IsDBNull(17) ? 0 : reader.GetInt64(17);
        var lonSign = reader.IsDBNull(18) ? "E" : reader.GetString(18);

        if (talkGroupContactId is null)
            yield break; // Nothing to report to - leave whatever's already on the radio untouched.

        long? talkGroupDmrId;
        using (var tgCmd = db.CreateCommand())
        {
            tgCmd.CommandText = "SELECT DmrId FROM Contacts WHERE Id = $id;";
            tgCmd.Parameters.AddWithValue("$id", talkGroupContactId.Value);
            var result = tgCmd.ExecuteScalar();
            talkGroupDmrId = result is DBNull or null ? null : (long)result;
        }
        if (talkGroupDmrId is null)
            yield break; // Talkgroup has no real DMR ID (e.g. a synthetic placeholder) - can't address it.

        var identity = new byte[14];
        AsciiFieldCodec.Encode(destCallsign, 6).CopyTo(identity, 0);
        identity[6] = destSsid;
        AsciiFieldCodec.Encode(callsign, 6).CopyTo(identity, 7);
        identity[13] = callsignSsid;
        yield return new EncodedRegion($"{SharedBlockRegionPrefix}AprsIdentity", 0x02500000u + AprsDestCallOffset, identity);

        yield return new EncodedRegion($"{SharedBlockRegionPrefix}AprsPath", 0x02500000u + AprsPathOffset, AsciiFieldCodec.Encode(signalPath, 20));

        var autoTxRaw = (byte)Math.Clamp(autoTxSeconds / 30, 0, 255);
        yield return new EncodedRegion($"{SharedBlockRegionPrefix}AprsAutoTxInterval", 0x02500000u + AprsAutoTxIntervalOffset, [autoTxRaw]);

        var location = new byte[9];
        location[0] = (byte)(fixedLocation ? 1 : 0);
        location[1] = (byte)latDegree;
        location[2] = (byte)latMinute;
        location[3] = 0; // Latitude seconds - not modeled in the schema, defaults to 0.
        location[4] = (byte)(latSign == "S" ? 1 : 0);
        location[5] = (byte)lonDegree;
        location[6] = (byte)lonMinute;
        location[7] = 0; // Longitude seconds - not modeled in the schema, defaults to 0.
        location[8] = (byte)(lonSign == "W" ? 1 : 0);
        yield return new EncodedRegion($"{SharedBlockRegionPrefix}AprsLocation", 0x02500000u + AprsLocationOffset, location);

        ushort reportChannelRaw = AprsReportChannelSelected;
        if (reportChannelId is not null)
        {
            using var chCmd = db.CreateCommand();
            chCmd.CommandText = "SELECT ChannelNumber FROM Channels WHERE Id = $id;";
            chCmd.Parameters.AddWithValue("$id", reportChannelId.Value);
            if (chCmd.ExecuteScalar() is long channelNumber)
                reportChannelRaw = (ushort)(channelNumber - 1);
        }
        yield return new EncodedRegion($"{SharedBlockRegionPrefix}AprsReportChannel", 0x02500000u + AprsReportChannelOffset,
            [(byte)reportChannelRaw, (byte)(reportChannelRaw >> 8)]);

        yield return new EncodedRegion($"{SharedBlockRegionPrefix}AprsTalkGroup", 0x02500000u + AprsTalkGroupOffset, BcdCodec.Encode(talkGroupDmrId.Value, 4));

        byte callTypeRaw = callType switch { "Private" => 0, "All" => 2, _ => 1 };
        yield return new EncodedRegion($"{SharedBlockRegionPrefix}AprsCallType", 0x02500000u + AprsCallTypeOffset, [callTypeRaw]);

        byte slotRaw = slot switch { 1 => 1, 2 => 2, _ => 0 };
        yield return new EncodedRegion($"{SharedBlockRegionPrefix}AprsSlot", 0x02500000u + AprsSlotOffset, [slotRaw]);
    }

    private static IEnumerable<EncodedRegion> EncodeTalkGroups(SqliteConnection db, Dictionary<long, uint> contactIndex)
    {
        var contactCount = contactIndex.Count;
        // Only the used portion of the list is written - see the writer method's remarks for why.
        // Rounded up to a 16-byte boundary since writes must be in exactly-16-byte chunks; the
        // few trailing pad bytes beyond the last real record stay zero, which is harmless since
        // the used bitmap is the authority on which slots are real.
        var listLength = ((contactCount * 100) + 15) / 16 * 16;
        var listBuffer = new byte[listLength];
        var usedBuffer = new byte[1264];
        Array.Fill(usedBuffer, (byte)0xFF); // inverted convention: 1 = not used

        var controlBuffer = new byte[10000 * 4];
        Array.Fill(controlBuffer, (byte)0xFF); // 0xFFFFFFFF = empty slot

        // The doc notes "more management information at 0x04340000 when writing" talk groups.
        // The actual root cause of TalkGroupList writes being silently ACKed-but-discarded turned
        // out to be size (writing the full 10,000-slot/1MB region in one go, versus writing only
        // the used ~23 entries, which is what fixed it) - this offset table was tried first and
        // kept since it's cheap and doc-specified, but wasn't independently proven necessary once
        // the real fix (writing less data) was found. Each entry is the DMR ID BCD-digits-as-
        // hex-value, left-shifted 1 bit with bit0 set for group calls, plus the 0-based list
        // position, both 4-byte little-endian, sorted ascending by the key.
        var offsetEntries = new List<(uint Key, uint Position)>();

        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT Id, Name, DmrId FROM Contacts ORDER BY Id;";
        using var reader = cmd.ExecuteReader();
        var syntheticId = 999_901u; // placeholder range for contacts with no real DMR ID (e.g. PARROT, Echo Test)
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            var index = (int)contactIndex[id];
            var name = reader.GetString(1);
            // Contacts without a real DMR ID (non-numeric names like "PARROT") get a distinct
            // synthetic placeholder rather than all sharing 0 - the offset table below is keyed
            // by this value, and duplicate keys there would collide/be ambiguous to the firmware.
            var dmrId = reader.IsDBNull(2) ? syntheticId++ : (uint)reader.GetInt64(2);

            TalkGroupRecordCodec.Encode(new TalkGroupRecord(name, dmrId)).CopyTo(listBuffer, index * 100);
            ClearBit(usedBuffer, index); // 0 = used, per the inverted convention confirmed against real data
            WriteUInt32LE(controlBuffer, index * 4, (uint)index);

            var bcdKey = BcdDigitsAsHexValue(dmrId, 4);
            var shiftedKey = (bcdKey << 1) | 1u; // TalkGroupRecordCodec always writes Group Call entries
            offsetEntries.Add((shiftedKey, (uint)index));
        }

        offsetEntries.Sort((a, b) => a.Key.CompareTo(b.Key));
        if (offsetEntries.Count % 2 != 0)
            offsetEntries.Add((0xFFFFFFFF, 0xFFFFFFFF)); // pad to a full 16-byte (2-entry) write chunk, per doc

        var offsetBuffer = new byte[offsetEntries.Count * 8];
        for (var i = 0; i < offsetEntries.Count; i++)
        {
            WriteUInt32LE(offsetBuffer, i * 8, offsetEntries[i].Key);
            WriteUInt32LE(offsetBuffer, i * 8 + 4, offsetEntries[i].Position);
        }

        yield return new EncodedRegion("TalkGroupListUsed", 0x02640000, usedBuffer);
        yield return new EncodedRegion("TalkGroupsControlData", 0x02600000, controlBuffer);
        yield return new EncodedRegion("TalkGroupOffsets", 0x04340000, offsetBuffer);
        yield return new EncodedRegion("TalkGroupList", 0x02680000, listBuffer);
    }

    private static uint BcdDigitsAsHexValue(long decimalValue, int byteCount)
    {
        var bcdBytes = BcdCodec.Encode(decimalValue, byteCount);
        uint result = 0;
        foreach (var b in bcdBytes) result = (result << 8) | b;
        return result;
    }

    private static IEnumerable<EncodedRegion> EncodeRadioIds(SqliteConnection db, Dictionary<long, byte> radioIdIndex)
    {
        var listBuffer = new byte[250 * 32];
        var usedBuffer = new byte[32];

        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT Id, Callsign, DmrId FROM RadioIds ORDER BY Id;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            var index = radioIdIndex[id];
            var callsign = reader.GetString(1);
            var dmrId = reader.IsDBNull(2) ? 0u : (uint)reader.GetInt64(2);

            RadioIdRecordCodec.Encode(new RadioIdRecord(dmrId, callsign)).CopyTo(listBuffer, index * 32);
            SetBit(usedBuffer, index);
        }

        yield return new EncodedRegion("RadioIdList", 0x02580000, listBuffer);
        yield return new EncodedRegion("RadioIdListUsed", 0x024c1320, usedBuffer);
    }

    private static void SetBit(byte[] buffer, int index0Based) => buffer[index0Based / 8] |= (byte)(1 << (index0Based % 8));
    private static void ClearBit(byte[] buffer, int index0Based) => buffer[index0Based / 8] &= (byte)~(1 << (index0Based % 8));

    private static void WriteUInt32LE(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }
}
