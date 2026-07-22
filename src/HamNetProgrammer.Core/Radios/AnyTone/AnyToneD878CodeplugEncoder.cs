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
    public const int ChannelsPerBank = 128;
    public const uint ChannelBankBaseAddress = 0x00800000u;
    public const uint ChannelBankStride = 0x40000u;

    // Shared with AnyToneD878MemoryMap, which needs the exact same length to dump/restore a
    // ChannelBank[] region byte-for-byte - see EncodeChannels' remarks on why channels and TX
    // Color Code must always be read/written together as one region.
    public static int ChannelBankChannelsLength(int bank) => bank == 31 ? 2176 : ChannelsPerBank * 64;

    // TalkGroupRecordCodec's DMR ID field is 3-byte BCD (confirmed against real device dumps) -
    // a hard hardware ceiling on this radio, not a project limitation. TGIF in particular hosts
    // "personal reflector" talkgroups well past this (7-8 digit numbers; DMR's reserved "All
    // Call" ID 16777215 is another), all legitimately real talkgroups elsewhere, just ones this
    // specific radio's TalkGroupList format cannot represent at all - no CPS could write them
    // here either.
    private const long MaxTalkGroupDmrId = 999_999;

    // Whether a routine Write Codeplug also pushes the talkgroup list (Contacts/TalkGroupList) -
    // defaults to false. That list is now 5,718+ entries and changes rarely, while routine writes
    // (renaming a channel, tweaking a zone) happen often - bundling the two meant every small edit
    // re-uploaded the whole reference list. See BuildTalkGroupsOnly for the separate, deliberate
    // "Sync Reference Data" path this now lives in instead (RadioPage).
    public static List<EncodedRegion> Build(SqliteConnection db, List<string>? warnings = null, bool includeTalkGroups = false)
    {
        var regions = new List<EncodedRegion>();

        var (scanListsEnabled, groupListsEnabled, roamingEnabled) = ReadListFeatureFlags(db);

        var contactIndex = BuildContactIndex(db, warnings);
        var radioIdIndex = BuildRadioIdIndex(db);
        var scanListIndex = BuildScanListIndex(db);
        var groupListIndex = BuildGroupListIndex(db);

        regions.AddRange(EncodeChannels(db, contactIndex, radioIdIndex, scanListIndex, groupListIndex, scanListsEnabled, groupListsEnabled, warnings));
        regions.AddRange(EncodeZones(db));
        if (scanListsEnabled) regions.AddRange(EncodeScanLists(db));
        if (groupListsEnabled) regions.AddRange(EncodeGroupLists(db, contactIndex));
        if (roamingEnabled) regions.AddRange(EncodeRoaming(db));
        if (includeTalkGroups) regions.AddRange(EncodeTalkGroups(db, contactIndex));
        regions.AddRange(EncodeRadioIds(db, radioIdIndex));
        regions.AddRange(EncodeRadioSettings(db));

        return regions;
    }

    /// <summary>The talkgroup-list half of a write, on its own - what "Sync Reference Data" uses.
    /// contactIndex still has to be recomputed here (it's derived purely from the Contacts table,
    /// same result either way) since routine Write Codeplug no longer computes it for this purpose.</summary>
    public static List<EncodedRegion> BuildTalkGroupsOnly(SqliteConnection db, List<string>? warnings = null)
    {
        var contactIndex = BuildContactIndex(db, warnings);
        return EncodeTalkGroups(db, contactIndex).ToList();
    }

    /// <summary>A cheap fingerprint of "what BuildContactIndex would currently produce" - (count,
    /// max Id) among contacts eligible for the talkgroup list. Every eligible contact gets a
    /// sequential index by Id order, so ANY add/remove (not just ones a channel happens to
    /// reference) shifts every later contact's index - comparing this against what was true as of
    /// the last Sync Reference Data run is how a stale write gets caught before it happens, rather
    /// than producing the old TG1-fallback-style bug silently.</summary>
    public static (long Count, long MaxId) GetContactIndexFingerprint(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*), MAX(Id) FROM Contacts WHERE DmrId IS NULL OR DmrId <= {MaxTalkGroupDmrId};";
        using var reader = cmd.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(1)) return (0, 0);
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    // "Disabled" means don't write this feature's data to the radio at all - not merely "don't
    // auto-sync membership from zones" (that's the separate SyncListsWithZones setting). A
    // disabled feature's regions (including its own "Used" bitmap) are omitted from the write plan
    // entirely, leaving whatever the radio already has for it untouched; channels referencing a
    // disabled feature get that reference dropped (null/none) rather than pointing at a slot that
    // was never written this session.
    private static (bool ScanListsEnabled, bool GroupListsEnabled, bool RoamingEnabled) ReadListFeatureFlags(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT ScanListsEnabled, GroupListsEnabled, RoamingEnabled FROM RadioSettings WHERE Id = 1;";
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return (true, true, true);
        return (reader.GetInt64(0) != 0, reader.GetInt64(1) != 0, reader.GetInt64(2) != 0);
    }

    // Contacts whose DmrId won't fit the radio's 3-byte BCD talkgroup field are deliberately
    // excluded here rather than included with a wrong/truncated number - any channel referencing
    // one of these gets flagged in EncodeChannels instead of silently pointing at contact index 0
    // (a real contact, not a "none" sentinel - ContactIndex has no such sentinel in this codec).
    private static Dictionary<long, uint> BuildContactIndex(SqliteConnection db, List<string>? warnings)
    {
        var map = new Dictionary<long, uint>();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT Id, Name, DmrId FROM Contacts ORDER BY Id;";
        using var reader = cmd.ExecuteReader();
        uint i = 1; // 0 is reserved for NoContactIndex - real contacts start at 1.
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            if (!reader.IsDBNull(2) && reader.GetInt64(2) > MaxTalkGroupDmrId)
            {
                warnings?.Add($"Talkgroup '{reader.GetString(1)}' (DmrId {reader.GetInt64(2)}) exceeds this radio's 6-digit talkgroup limit and won't be written.");
                continue;
            }
            map[id] = i++;
        }
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

    // The "no contact" sentinel for ContactIndex is 0, not a made-up out-of-range value -
    // confirmed against qdmr's own AnytoneCodeplug::ChannelElement::fromChannel (anytone_codeplug.cc:773,
    // `setContactIndex(0)` when the channel has no contact), which qdmr uses across real,
    // hardware-tested writes to this exact radio family. Whatever ends up encoded at contact
    // index 0 in TalkGroupList is therefore never actually referenced by a "no contact" channel -
    // the firmware treats index 0 itself as the sentinel, not as a real contact lookup.
    private const uint NoContactIndex = 0;

    // TX Color Code lives in a SEPARATE per-channel table, not the 64-byte channel record
    // itself - discovered 2026-07-21 via a live RF decode (DSDPlus) proving the radio
    // transmits a different color code than the per-channel byte this encoder writes, then
    // pinpointed by a clean before/after USB write capture: one byte per channel, at the
    // SAME 64-byte stride as the primary channel record, offset +3, starting immediately
    // after each bank's primary 8192-byte region (i.e. bankBase + 0x2000 + slot*64 + 3).
    // AnyTone firmware V3.06+ (2025-1-23) split Color Code into separate RX/TX fields (CPS
    // UI, not modeled by qdmr) - this encoder only ever wrote the RX one (channel record
    // byte 32). Critically, this table sits in the SAME 256KB flash erase block as the
    // primary channel bank (both bank-base-aligned), so every Channels[] write already
    // erases it - this encoder just never knew to repopulate it, silently leaving TX Color
    // Code erased (reads back as 0xF nibble = 15, a plausible-looking but wrong value the
    // radio then actually transmits on). Defaults to the SAME value as RX Color Code,
    // matching RT Systems' own default behavior (and AnyTone CPS's own migration prompt
    // when loading a codeplug from before this field existed).
    public const int TxColorCodeTableOffset = 0x2000;
    private const int TxColorCodeByteOffsetInSlot = 3;

    private static IEnumerable<EncodedRegion> EncodeChannels(SqliteConnection db, Dictionary<long, uint> contactIndex, Dictionary<long, byte> radioIdIndex, Dictionary<long, byte> scanListIndex, Dictionary<long, byte> groupListIndex, bool scanListsEnabled, bool groupListsEnabled, List<string>? warnings)
    {
        var banks = new Dictionary<int, byte[]>();
        var txColorCodeBanks = new Dictionary<int, byte[]>();
        int BankLength(int bank) => ChannelBankChannelsLength(bank);

        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT ChannelNumber, Name, Mode, RxFrequencyHz, TxFrequencyHz, Bandwidth, Power,
                   ColorCode, TimeSlot, ContactId, RadioIdId, ScanListId, GroupListId,
                   ExtraAttributesJson
            FROM Channels;
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var channelNumber = reader.GetInt32(0);
            var channelName = reader.GetString(1);
            var flatIndex = (uint)(channelNumber - 1);
            var bank = (int)(flatIndex / ChannelsPerBank);
            var slot = (int)(flatIndex % ChannelsPerBank);

            if (bank is < 0 or > 31)
                throw new InvalidOperationException($"Channel {channelNumber} (flat index {flatIndex}) is out of the AT-D878UV's channel range.");

            uint contactIdx = NoContactIndex;
            if (!reader.IsDBNull(9))
            {
                var contactId = reader.GetInt64(9);
                if (contactIndex.TryGetValue(contactId, out var found))
                    contactIdx = found;
                else
                    warnings?.Add($"Channel '{channelName}' (channel {channelNumber}) references a talkgroup that can't be written to this radio (number too large) - its digital contact will be left unset.");
            }

            var record = new ChannelRecord(
                RxFrequencyHz: reader.GetInt64(3),
                TxFrequencyHz: reader.GetInt64(4),
                Is25kHz: !reader.IsDBNull(5) && reader.GetString(5).Contains("25"),
                PowerLevel: PowerLevelFromText(reader.IsDBNull(6) ? null : reader.GetString(6)),
                IsDigital: reader.GetString(2).Equals("Digital", StringComparison.OrdinalIgnoreCase),
                ColorCode: reader.IsDBNull(7) ? (byte)1 : (byte)reader.GetInt32(7),
                TimeSlot: reader.IsDBNull(8) ? (byte)1 : (byte)reader.GetInt32(8),
                ContactIndex: contactIdx,
                RadioIdIndex: reader.IsDBNull(10) ? (byte)0 : radioIdIndex.GetValueOrDefault(reader.GetInt64(10)),
                ScanListIndex: !scanListsEnabled || reader.IsDBNull(11) ? null : scanListIndex.GetValueOrDefault(reader.GetInt64(11)),
                GroupListIndex: !groupListsEnabled || reader.IsDBNull(12) ? null : groupListIndex.GetValueOrDefault(reader.GetInt64(12)),
                Name: channelName,
                ThroughMode: IsDmoSimplex(reader.IsDBNull(13) ? null : reader.GetString(13)));

            if (!banks.TryGetValue(bank, out var buffer))
                banks[bank] = buffer = new byte[BankLength(bank)];

            ChannelRecordCodec.Encode(record).CopyTo(buffer, slot * 64);

            if (!txColorCodeBanks.TryGetValue(bank, out var txBuffer))
                txColorCodeBanks[bank] = txBuffer = new byte[BankLength(bank)];
            txBuffer[slot * 64 + TxColorCodeByteOffsetInSlot] = record.ColorCode;
        }

        // Re-enabled 2026-07-22, but NOT as a separate region like the disabled attempt on
        // 2026-07-21: Channels[bank] and its TX Color Code table share the same 256KB flash
        // erase block (see the remarks above), and WriteMemory only commits atomically at
        // EndProgrammingSession - so a LATER, separate session touching that same block (as
        // WriteRegionChunkedAndVerify would do, same as it already does for TalkGroupList)
        // would re-erase whatever the earlier session had just committed to it, exactly like
        // the ZoneChannelDefaults/GeneralUsedBitmapsBlock disturb found on 2026-07-19.
        // TalkGroupList is safe with that isolated-session pattern only because nothing else
        // shares its block; channels don't have that luxury. Instead, both halves are combined
        // into ONE region per bank here (channel table at offset 0, color code table at
        // TxColorCodeTableOffset, immediately contiguous) so they always commit together in a
        // single session - see AnyToneD878CodeplugWriter's ChannelBank[] handling, which pulls
        // this whole region out of the big bundled write (fixing the original ~205KB->230KB
        // session-size overflow too) and gives each bank its own small isolated write+verify.
        foreach (var (bank, buffer) in banks)
        {
            var combined = new byte[TxColorCodeTableOffset + BankLength(bank)];
            buffer.CopyTo(combined, 0);
            if (txColorCodeBanks.TryGetValue(bank, out var txBuffer))
                txBuffer.CopyTo(combined, TxColorCodeTableOffset);
            yield return new EncodedRegion($"ChannelBank[{bank}]", ChannelBankBaseAddress + (uint)bank * ChannelBankStride, combined);
        }
    }

    private static byte PowerLevelFromText(string? power) => power?.ToLowerInvariant() switch
    {
        "low" => 0,
        "mid" or "medium" => 1,
        "high" => 2,
        "turbo" => 3,
        _ => 2,
    };

    // True when the imported RT Systems "DMR Mode" attribute is "DMO Simplex" (Direct Mode
    // Operation) rather than "Repeater" - i.e. a simplex hotspot channel that needs throughMode set.
    // See ChannelRecord.ThroughMode's remarks for why this matters (the MMDVM-hotspot fix).
    private static bool IsDmoSimplex(string? extraAttributesJson)
    {
        if (string.IsNullOrEmpty(extraAttributesJson))
            return false;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(extraAttributesJson);
            return doc.RootElement.TryGetProperty("DMR Mode", out var mode)
                && mode.GetString()?.Contains("Simplex", StringComparison.OrdinalIgnoreCase) == true;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

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

    // "Work mode MEM zone A/B index" per qdmr's d878uv_generalsettings.txt - the currently
    // selected zone for VFO A/B. Confirmed via real-hardware dump diff (2026-07-18): this was the
    // only byte that changed inside PowerOnAndOptionalSettings across an RT Systems write that
    // fixed a non-responsive zone-scroll rocker switch, sitting at exactly the last valid zone
    // index beforehand. The actual root cause traced to a different bug (see
    // GeneralUsedBitmapsBlockAddress's remarks), but this encoder never explicitly sets this byte
    // either way - the writer only clamps it if it's gone stale/out-of-range, as defense in depth.
    public const int WorkModeZoneAIndexOffset = 0x001F;
    public const int WorkModeZoneBIndexOffset = 0x0020;

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
    // USED EXTENT, not the full 0x40000 erase block (2026-07-21). Writing the full 256KB in one
    // session was proven on real hardware to be unreliable - the write either hangs the radio past
    // the 45s re-enumerate timeout or silently fails to commit (same >~100KB single-session ceiling
    // the talkgroup list hit). The flash still erases the WHOLE 256KB block when any byte in it is
    // written, but we only need to REPROGRAM up to the last meaningful address: RoamingZones ends at
    // offset 0x3000+0x2000 = 0x5000, and official AnyTone CPS's own USB capture writes nothing in
    // this block past 0x1043080. Everything beyond 0x5000 is 0xFF/unused, exactly as CPS leaves it,
    // so erasing it is correct. Reprogramming ~20KB instead of 256KB commits reliably in one session.
    public const int RoamingBlockLength = 0x5000;
    public static readonly string[] RoamingBlockRegionNames = ["RoamingChannels", "RoamingChannelsUsed", "RoamingZonesUsed", "RoamingZones"];
    public const int RoamingChannelsOffset = 0x0000;
    public const int RoamingChannelsUsedOffset = 0x2000;
    public const int RoamingZonesUsedOffset = 0x2080;
    public const int RoamingZonesOffset = 0x3000;

    // ZonesUsed (0x024c1300), ScanListsUsed (0x024c1340), and RadioIdListUsed (0x024c1320) all
    // live inside a THIRD shared 256KB flash erase block (0x024C0000-0x024FFFFF), alongside
    // FiveTone/TwoTone/Alarm/Encryption/AutoRepeater data this encoder never writes. Discovered
    // the hard way on real hardware (2026-07-18) via a corruption symptom (the AT-D878UV's
    // zone-scroll rocker switch stopped working) that had nothing obviously to do with these
    // regions: comparing memory dumps bracketing every write-codeplug run since 2026-07-17 showed
    // ZonesUsed and ScanListsUsed sitting at all-0xFF (erased) the entire time, while
    // RadioIdListUsed - written LAST in Build()'s call order - stayed correct. This means writing
    // multiple standalone regions into the SAME shared block, even within one session, does NOT
    // merge safely as previously assumed (see ZoneChannelDefaultsBlockAddress's remarks) - each
    // later write silently re-erased the block and wiped the earlier ones. Same remedy as the
    // other two shared blocks: splice into one live-read/write-back instead of three standalone
    // writes.
    public const uint GeneralUsedBitmapsBlockAddress = 0x024C0000;
    // USED EXTENT, not the full 0x40000 erase block (2026-07-21) - see RoamingBlockLength's remarks
    // for the full rationale. This block is the WORST offender: at the full 256KB it consistently
    // failed to verify (3/3 attempts) and hung the radio past the 45s re-enumerate timeout. The real
    // content runs from 0x024C0000 (FiveTone/TwoTone/DTMF tables, AlarmSettings, the used-bitmaps,
    // EncryptionIds a.k.a. qdmr's channelBitmap at 0x1500, EncryptionKeys, AutoRepeaterOffsets) up
    // to AesEncryptionKeys ending ~0x024C7FC0; official AnyTone CPS writes nothing past 0x024C8020.
    // 0x8100 covers all of it with margin; the 0xFF tail beyond is erased exactly as CPS leaves it.
    // Reprogramming ~33KB instead of 256KB commits reliably in one session, and (critically) still
    // preserves everything this encoder doesn't model - the whole point of the read-modify-write.
    public const int GeneralUsedBitmapsBlockLength = 0x8100;
    public static readonly string[] GeneralUsedBitmapsBlockRegionNames = ["ZonesUsed", "ScanListsUsed", "RadioIdListUsed"];

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

    // The talkgroup list is NOT one flat contiguous array - confirmed against qdmr's reverse-
    // engineered memory map (d868uv_codeplug.hh/.cc, the shared base class the D878UV also uses):
    // contacts are organized into banks of up to 1000, with banks spaced 0x40000 (256KB) apart:
    //   bank = index / 1000;  addr = ContactBanksBaseAddress + bank*BetweenContactBanks + (index%1000)*100
    // ("bank_addr = contactBanks() + (i/contactsPerBank())*betweenContactBanks(); addr = bank_addr
    // + (i%contactsPerBank())*ContactElement::size()" in d868uv_codeplug.cc). Treating this as one
    // flat array (writing every record contiguously from 0x02680000) is what caused real,
    // reproducible data placement corruption on hardware 2026-07-19 - any index >= 1000 lands at a
    // wildly wrong physical address once real bank 1 (0x026C0000) doesn't line up with where a
    // flat model would put contact #1000 (0x02680000 + 100,000). This only ever surfaced once the
    // talkgroup count grew past 1000 - smaller earlier syncs never exercised the bug.
    private const int ContactsPerBank = 1000;
    private const uint ContactBanksBaseAddress = 0x02680000;
    private const uint BetweenContactBanks = 0x00040000;

    private static IEnumerable<EncodedRegion> EncodeTalkGroups(SqliteConnection db, Dictionary<long, uint> contactIndex)
    {
        var usedBuffer = new byte[1264];
        Array.Fill(usedBuffer, (byte)0xFF); // inverted convention: 1 = not used

        var controlBuffer = new byte[10000 * 4];
        Array.Fill(controlBuffer, (byte)0xFF); // 0xFFFFFFFF = empty slot

        // Real DMR-ID-to-contact-index lookup table the radio actually uses for call routing.
        // This project originally wrote a guessed version of this table at 0x04340000, based only
        // on an ambiguous doc phrase never independently confirmed - that's the base D868UV class's
        // address (qdmr's d868uv_codeplug.hh contactIdTable()). The D878UV II Plus (this radio)
        // overrides it to 0x04800000 (qdmr's d878uv2_codeplug.hh) - a real per-model difference
        // this project missed, causing a real-hardware TX-misrouting bug (channel display correct,
        // actual transmitted talkgroup wrong) on 2026-07-20: nothing had ever written correct data
        // to the address this exact model actually reads, leaving stale/unmanaged data driving call
        // routing regardless of what the rest of this encoder correctly wrote. Confirmed against RT
        // Systems' own captured USB traffic: 784 sequential 16-byte writes (=1,568 entries, matching
        // RT Systems' known list size) at 0x04800000, decoded entries matching this exact key format
        // (BCD-shifted-by-1-plus-group-flag, sorted ascending). See [[project_anytone_878_codeplug]].
        //
        // Sorted ascending by key; qdmr (lib/d868uv_codeplug.cc encodeContacts, shared by the II
        // Plus subclass) always appends one guaranteed 0xFFFFFFFF/0xFFFFFFFF terminator beyond the
        // real entries (from its initial memset), regardless of whether the real count is odd or
        // even - matched here rather than only padding for 16-byte alignment.
        var offsetEntries = new List<(uint Key, uint Position)>();

        // Each bank gets a full-size (1000-record/100,000-byte) buffer the first time any index in
        // it is seen - simpler than trimming to the exact used size per bank, and 100,000 bytes is
        // comfortably under the size that's separately confirmed safe to write in one session
        // (a smaller single-session write worked historically; ~558,000 bytes in one region did
        // not - see WriteRegionChunkedAndVerify's remarks).
        var banks = new Dictionary<int, byte[]>();

        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT Id, Name, DmrId, CallType FROM Contacts ORDER BY Id;";
        using var reader = cmd.ExecuteReader();
        var syntheticId = 999_901u; // placeholder range for contacts with no real DMR ID (e.g. PARROT, Echo Test)
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            // Contacts excluded from contactIndex (DmrId too large for this radio's 3-byte BCD
            // field - see BuildContactIndex) are skipped here too, rather than crashing on a
            // missing key - already warned about in BuildContactIndex.
            if (!contactIndex.TryGetValue(id, out var indexValue)) continue;
            var index = (int)indexValue;
            var name = reader.GetString(1);
            var isGroupCall = reader.GetString(3) != "Private";
            // Contacts without a real DMR ID (non-numeric names like "PARROT") get a distinct
            // synthetic placeholder rather than all sharing 0 - the offset table below is keyed
            // by this value, and duplicate keys there would collide/be ambiguous to the firmware.
            var dmrId = reader.IsDBNull(2) ? syntheticId++ : (uint)reader.GetInt64(2);

            var bank = index / ContactsPerBank;
            var slot = index % ContactsPerBank;
            if (!banks.TryGetValue(bank, out var buffer))
                banks[bank] = buffer = new byte[ContactsPerBank * 100];

            TalkGroupRecordCodec.Encode(new TalkGroupRecord(name, dmrId, isGroupCall)).CopyTo(buffer, slot * 100);
            ClearBit(usedBuffer, index); // 0 = used, per the inverted convention confirmed against real data
            WriteUInt32LE(controlBuffer, index * 4, (uint)index);

            var bcdKey = BcdDigitsAsHexValue(dmrId, 4);
            var shiftedKey = (bcdKey << 1) | (isGroupCall ? 1u : 0u);
            offsetEntries.Add((shiftedKey, (uint)index));
        }

        offsetEntries.Sort((a, b) => a.Key.CompareTo(b.Key));
        offsetEntries.Add((0xFFFFFFFF, 0xFFFFFFFF));
        if (offsetEntries.Count % 2 != 0)
            offsetEntries.Add((0xFFFFFFFF, 0xFFFFFFFF));

        var contactIdTableBuffer = new byte[offsetEntries.Count * 8];
        for (var i = 0; i < offsetEntries.Count; i++)
        {
            WriteUInt32LE(contactIdTableBuffer, i * 8, offsetEntries[i].Key);
            WriteUInt32LE(contactIdTableBuffer, i * 8 + 4, offsetEntries[i].Position);
        }

        yield return new EncodedRegion("TalkGroupListUsed", 0x02640000, usedBuffer);
        yield return new EncodedRegion("TalkGroupsControlData", 0x02600000, controlBuffer);
        yield return new EncodedRegion("ContactIdTable", 0x04800000, contactIdTableBuffer);
        foreach (var (bank, buffer) in banks.OrderBy(kv => kv.Key))
            yield return new EncodedRegion($"TalkGroupList[{bank}]", ContactBanksBaseAddress + (uint)bank * BetweenContactBanks, buffer);
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
