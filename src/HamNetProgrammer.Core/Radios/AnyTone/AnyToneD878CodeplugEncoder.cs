using HamNetProgrammer.Core.Radios.AnyTone.Codecs;
using Microsoft.Data.Sqlite;

namespace HamNetProgrammer.Core.Radios.AnyTone;

public sealed record EncodedRegion(string Name, uint Address, byte[] Data);

/// <summary>
/// Builds the AT-D878UV memory write plan from the SQLite codeplug: Channels, Zones (+ names,
/// used bitmap, default A/B channel), ScanLists (+ used bitmap), Contacts/TalkGroupList
/// (+ used bitmap, control data), and RadioIds (+ used bitmap).
///
/// Deliberately scoped to what the zone/scan-list builders actually populate. GroupLists and
/// RoamingZones are not encoded yet - channels are written with GroupListIndex unset (0xFF/none,
/// the CPS default), and roaming is a separate structure not referenced by the channel record's
/// core fields, so omitting it doesn't break anything already written.
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

        regions.AddRange(EncodeChannels(db, contactIndex, radioIdIndex, scanListIndex));
        regions.AddRange(EncodeZones(db));
        regions.AddRange(EncodeScanLists(db));
        regions.AddRange(EncodeTalkGroups(db, contactIndex));
        regions.AddRange(EncodeRadioIds(db, radioIdIndex));

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

    private static IEnumerable<EncodedRegion> EncodeChannels(SqliteConnection db, Dictionary<long, uint> contactIndex, Dictionary<long, byte> radioIdIndex, Dictionary<long, byte> scanListIndex)
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
                GroupListIndex: null, // GroupLists aren't encoded yet - CPS default (none) is safe here
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

        using var zonesCmd = db.CreateCommand();
        zonesCmd.CommandText = "SELECT Id, Name FROM Zones ORDER BY Id;";
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
        cmd.CommandText = "SELECT Id, Name FROM ScanLists ORDER BY Id;";
        using var reader = cmd.ExecuteReader();

        var index = 0;
        while (reader.Read())
        {
            var scanListId = reader.GetInt64(0);
            var name = reader.GetString(1);

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
            var data = ScanListRecordCodec.Encode(new ScanListRecord(name, members));
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
