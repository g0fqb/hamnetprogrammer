using HamNetProgrammer.Core.Radios.AnyTone.Codecs;
using Microsoft.Data.Sqlite;

namespace HamNetProgrammer.Core.Radios.AnyTone;

/// <summary>NewItemLabels names exactly what was added (not just a count) - a first read against a
/// radio with years of CPS/RT Systems history needs to be reviewable at a glance, not trusted
/// blind (see project history: a first real read surfaced a 154-channel leftover duplicate block
/// from earlier test writes, invisible in a bare count).</summary>
public sealed record ImportCategoryResult(string Category, int Matched, int New, int Skipped, IReadOnlyList<string> NewItemLabels);

public sealed record ImportResult(IReadOnlyList<ImportCategoryResult> Categories, IReadOnlyList<string> Warnings);

/// <summary>
/// Decodes a full radio memory dump back into the SQLite codeplug database - the reverse of
/// AnyToneD878CodeplugEncoder, for a user who already has a working radio and wants its
/// configuration as a starting point instead of building one from scratch.
///
/// "Merge, never delete" by design (user's explicit choice, 2026-07-22): top-level records
/// (Zones/Channels/Contacts/RadioIds/ScanLists/GroupLists/RoamingZones) that exist in the database
/// but aren't on the connected radio are left alone - they may belong to a different radio sharing
/// this same codeplug (e.g. two D878UVs kept in sync). A record that IS matched (by the natural key
/// noted per step below) has its own fields, and any membership it owns, replaced with exactly
/// what's on the radio - otherwise reading the same radio twice could never reflect a change made
/// from its own keypad.
///
/// Decode order matches AnyToneD878CodeplugEncoder's own index-assignment order, reversed: records
/// that other records reference by index (RadioIds, Contacts, then Channels) are decoded first, so
/// the index->DB-row maps needed to resolve later records' membership already exist by the time
/// they're needed. Scan Lists and Group Lists are decoded in two passes each (metadata first,
/// membership after Channels exist), for the same reason.
/// </summary>
public static class AnyToneD878CodeplugImporter
{
    public static ImportResult Import(SqliteConnection db, DumpReader dump)
    {
        var warnings = new List<string>();
        var categories = new List<ImportCategoryResult>();

        using var tx = db.BeginTransaction();

        var radioIdIndex = ImportRadioIds(db, tx, dump, categories);
        var contactIndex = ImportContacts(db, tx, dump, categories);
        var (scanListIndex, decodedScanLists) = ImportScanListsMetadata(db, tx, dump, categories);
        var (groupListIndex, decodedGroupLists) = ImportGroupListsMetadata(db, tx, dump, categories);
        var channelIdByFlatIndex = ImportChannels(db, tx, dump, contactIndex, radioIdIndex, scanListIndex, groupListIndex, categories, warnings);
        ImportZones(db, tx, dump, channelIdByFlatIndex, categories, warnings);
        ImportScanListMembership(db, tx, decodedScanLists, channelIdByFlatIndex, warnings);
        ImportGroupListMembership(db, tx, decodedGroupLists, contactIndex, warnings);
        ImportRoaming(db, tx, dump, categories, warnings);

        tx.Commit();
        return new ImportResult(categories, warnings);
    }

    private static bool IsBitSet(ReadOnlySpan<byte> buffer, int index0Based) =>
        (buffer[index0Based / 8] & (1 << (index0Based % 8))) != 0;

    private static bool IsAllOnes(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
            if (b != 0xFF) return false;
        return true;
    }

    // ---- Radio IDs ----

    private static Dictionary<int, long> ImportRadioIds(SqliteConnection db, SqliteTransaction tx, DumpReader dump, List<ImportCategoryResult> categories)
    {
        var map = new Dictionary<int, long>();
        if (!dump.HasRegion("RadioIdListUsed") || !dump.HasRegion("RadioIdList")) return map;

        var used = dump.GetRegion("RadioIdListUsed").ToArray();
        int matched = 0, created = 0;
        var newLabels = new List<string>();

        for (var i = 0; i < 250; i++)
        {
            if (!IsBitSet(used, i)) continue;
            var record = RadioIdRecordCodec.Decode(dump.GetRadioIdRecord(i));
            var callsign = record.Callsign.Trim();
            if (callsign.Length == 0) continue;

            using var find = db.CreateCommand();
            find.Transaction = tx;
            find.CommandText = "SELECT Id FROM RadioIds WHERE Callsign = $cs;";
            find.Parameters.AddWithValue("$cs", callsign);
            var existing = find.ExecuteScalar();

            long id;
            if (existing is long existingId)
            {
                id = existingId;
                using var upd = db.CreateCommand();
                upd.Transaction = tx;
                upd.CommandText = "UPDATE RadioIds SET DmrId = $dmr WHERE Id = $id;";
                upd.Parameters.AddWithValue("$dmr", record.DmrId == 0 ? DBNull.Value : record.DmrId);
                upd.Parameters.AddWithValue("$id", id);
                upd.ExecuteNonQuery();
                matched++;
            }
            else
            {
                using var ins = db.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = "INSERT INTO RadioIds (Callsign, DmrId) VALUES ($cs, $dmr); SELECT last_insert_rowid();";
                ins.Parameters.AddWithValue("$cs", callsign);
                ins.Parameters.AddWithValue("$dmr", record.DmrId == 0 ? DBNull.Value : record.DmrId);
                id = (long)ins.ExecuteScalar()!;
                created++;
                newLabels.Add(record.DmrId == 0 ? callsign : $"{callsign} (DMR ID {record.DmrId})");
            }
            map[i] = id;
        }

        categories.Add(new ImportCategoryResult("Radio IDs", matched, created, 0, newLabels));
        return map;
    }

    // ---- Contacts / TalkGroups ----

    // A real DMR ID landing in this range would be misclassified as synthetic too - the same
    // inherent ambiguity already exists on the write side (AnyToneD878CodeplugEncoder.EncodeTalkGroups
    // has no separate "is synthetic" flag on the wire), not something this importer can resolve
    // differently. Astronomically unlikely in practice for real amateur/commercial DMR IDs.
    private const uint SyntheticDmrIdThreshold = 999_901;

    private static Dictionary<int, long> ImportContacts(SqliteConnection db, SqliteTransaction tx, DumpReader dump, List<ImportCategoryResult> categories)
    {
        var map = new Dictionary<int, long>();
        if (!dump.HasRegion("TalkGroupListUsed")) return map;

        var used = dump.GetRegion("TalkGroupListUsed").ToArray();
        int matched = 0, created = 0, skipped = 0;
        var newLabels = new List<string>();

        for (var i = 0; i < 10_000; i++)
        {
            // Inverted convention (see AnyToneD878CodeplugEncoder.EncodeTalkGroups' remarks): a
            // CLEAR bit means used here, the opposite of every other "*Used" bitmap in this format.
            if (IsBitSet(used, i)) continue;
            if (!dump.HasRegion($"TalkGroupList[{i / 1000}]")) break;

            var record = TalkGroupRecordCodec.Decode(dump.GetTalkGroupRecord(i));
            var name = record.Name.Trim();
            if (name.Length == 0) { skipped++; continue; }

            uint? dmrId = record.DmrId >= SyntheticDmrIdThreshold ? null : record.DmrId;
            var callType = record.IsGroupCall ? "Group" : "Private";

            long? existingId = null;
            string? existingNetwork = null;
            using (var find = db.CreateCommand())
            {
                find.Transaction = tx;
                if (dmrId is not null)
                {
                    find.CommandText = "SELECT Id, Network FROM Contacts WHERE DmrId = $dmr AND CallType = $ct;";
                    find.Parameters.AddWithValue("$dmr", dmrId.Value);
                }
                else
                {
                    find.CommandText = "SELECT Id, Network FROM Contacts WHERE Name = $name AND CallType = $ct;";
                    find.Parameters.AddWithValue("$name", name);
                }
                find.Parameters.AddWithValue("$ct", callType);
                using var reader = find.ExecuteReader();
                if (reader.Read())
                {
                    existingId = reader.GetInt64(0);
                    existingNetwork = reader.IsDBNull(1) ? null : reader.GetString(1);
                }
            }

            long id;
            if (existingId is { } eid)
            {
                id = eid;
                // A network-tagged contact's Name is kept fresh by TalkGroupNetworkImporter, which
                // deliberately never overwrites a manually-created contact (see its own remarks) -
                // the radio's cached copy could be staler than that. Only refresh Name for contacts
                // nothing else already manages the freshness of.
                if (existingNetwork is null)
                {
                    using var upd = db.CreateCommand();
                    upd.Transaction = tx;
                    upd.CommandText = "UPDATE Contacts SET Name = $name WHERE Id = $id;";
                    upd.Parameters.AddWithValue("$name", name);
                    upd.Parameters.AddWithValue("$id", id);
                    upd.ExecuteNonQuery();
                }
                matched++;
            }
            else
            {
                using var ins = db.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = "INSERT INTO Contacts (Name, CallType, DmrId, Network) VALUES ($name, $ct, $dmr, NULL); SELECT last_insert_rowid();";
                ins.Parameters.AddWithValue("$name", name);
                ins.Parameters.AddWithValue("$ct", callType);
                ins.Parameters.AddWithValue("$dmr", dmrId is null ? DBNull.Value : dmrId.Value);
                id = (long)ins.ExecuteScalar()!;
                created++;
                newLabels.Add(dmrId is null ? $"{name} ({callType})" : $"{name} (TG{dmrId}, {callType})");
            }
            map[i] = id;
        }

        categories.Add(new ImportCategoryResult("Contacts", matched, created, skipped, newLabels));
        return map;
    }

    // ---- Scan Lists (metadata pass - membership needs Channels to exist first) ----

    private static (Dictionary<int, long> Index, List<(long Id, ScanListRecord Record)> Decoded) ImportScanListsMetadata(
        SqliteConnection db, SqliteTransaction tx, DumpReader dump, List<ImportCategoryResult> categories)
    {
        var index = new Dictionary<int, long>();
        var decoded = new List<(long, ScanListRecord)>();
        if (!dump.HasRegion("ScanListsUsed")) return (index, decoded);

        var used = dump.GetRegion("ScanListsUsed").ToArray();
        int matched = 0, created = 0;
        var newLabels = new List<string>();

        for (var i = 0; i < 250; i++)
        {
            if (!IsBitSet(used, i) || !dump.HasRegion($"ScanList[{i + 1}]")) continue;

            var record = ScanListRecordCodec.Decode(dump.GetScanListRecord(i));
            var name = record.Name.Trim();
            if (name.Length == 0) continue;

            var revertModeText = record.RevertMode.ToString();

            using var find = db.CreateCommand();
            find.Transaction = tx;
            find.CommandText = "SELECT Id FROM ScanLists WHERE Name = $name;";
            find.Parameters.AddWithValue("$name", name);
            var existing = find.ExecuteScalar();

            long id;
            if (existing is long existingId)
            {
                id = existingId;
                using var upd = db.CreateCommand();
                upd.Transaction = tx;
                upd.CommandText = """
                    UPDATE ScanLists SET LookBackTimeA = $a, LookBackTimeB = $b, DropoutDelayTime = $d,
                           DwellTime = $w, RevertMode = $r WHERE Id = $id;
                    """;
                upd.Parameters.AddWithValue("$a", record.LookBackTimeA);
                upd.Parameters.AddWithValue("$b", record.LookBackTimeB);
                upd.Parameters.AddWithValue("$d", record.DropoutDelayTime);
                upd.Parameters.AddWithValue("$w", record.DwellTime);
                upd.Parameters.AddWithValue("$r", revertModeText);
                upd.Parameters.AddWithValue("$id", id);
                upd.ExecuteNonQuery();
                matched++;
            }
            else
            {
                using var ins = db.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT INTO ScanLists (Name, LookBackTimeA, LookBackTimeB, DropoutDelayTime, DwellTime, RevertMode)
                    VALUES ($name, $a, $b, $d, $w, $r); SELECT last_insert_rowid();
                    """;
                ins.Parameters.AddWithValue("$name", name);
                ins.Parameters.AddWithValue("$a", record.LookBackTimeA);
                ins.Parameters.AddWithValue("$b", record.LookBackTimeB);
                ins.Parameters.AddWithValue("$d", record.DropoutDelayTime);
                ins.Parameters.AddWithValue("$w", record.DwellTime);
                ins.Parameters.AddWithValue("$r", revertModeText);
                id = (long)ins.ExecuteScalar()!;
                created++;
                newLabels.Add(name);
            }
            index[i] = id;
            decoded.Add((id, record));
        }

        categories.Add(new ImportCategoryResult("Scan Lists", matched, created, 0, newLabels));
        return (index, decoded);
    }

    // ---- Group Lists (metadata pass - no "used" bitmap exists, see IsAllOnes' call site) ----

    private static (Dictionary<int, long> Index, List<(long Id, GroupListRecord Record)> Decoded) ImportGroupListsMetadata(
        SqliteConnection db, SqliteTransaction tx, DumpReader dump, List<ImportCategoryResult> categories)
    {
        var index = new Dictionary<int, long>();
        var decoded = new List<(long, GroupListRecord)>();
        int matched = 0, created = 0;
        var newLabels = new List<string>();

        for (var i = 0; i < 250; i++)
        {
            if (!dump.HasRegion($"GroupList[{i + 1}]")) continue;
            var raw = dump.GetGroupListRecord(i);
            // No GroupListsUsed bitmap exists in this format (confirmed - EncodeGroupLists only
            // ever writes rows that exist in the DB). A real encoded record always has at least one
            // 0x00 byte from AsciiFieldCodec's null-padding (names are virtually never exactly 16
            // characters), so an entirely-0xFF record reliably means erased/never-written.
            if (IsAllOnes(raw)) continue;

            var record = GroupListRecordCodec.Decode(raw);
            var name = record.Name.Trim();
            if (name.Length == 0) continue;

            using var find = db.CreateCommand();
            find.Transaction = tx;
            find.CommandText = "SELECT Id FROM GroupLists WHERE Name = $name;";
            find.Parameters.AddWithValue("$name", name);
            var existing = find.ExecuteScalar();

            long id;
            if (existing is long existingId)
            {
                id = existingId;
                matched++;
            }
            else
            {
                using var ins = db.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = "INSERT INTO GroupLists (Name) VALUES ($name); SELECT last_insert_rowid();";
                ins.Parameters.AddWithValue("$name", name);
                id = (long)ins.ExecuteScalar()!;
                created++;
                newLabels.Add(name);
            }
            index[i] = id;
            decoded.Add((id, record));
        }

        categories.Add(new ImportCategoryResult("Group Lists", matched, created, 0, newLabels));
        return (index, decoded);
    }

    // ---- Channels ----

    private static Dictionary<uint, long> ImportChannels(SqliteConnection db, SqliteTransaction tx, DumpReader dump,
        Dictionary<int, long> contactIndex, Dictionary<int, long> radioIdIndex,
        Dictionary<int, long> scanListIndex, Dictionary<int, long> groupListIndex,
        List<ImportCategoryResult> categories, List<string> warnings)
    {
        var channelIdByFlatIndex = new Dictionary<uint, long>();
        int matched = 0, created = 0;
        var newLabels = new List<string>();

        for (var bank = 0; bank < 32; bank++)
        {
            if (!dump.HasRegion($"ChannelBank[{bank}]")) continue;
            var channelsInBank = AnyToneD878CodeplugEncoder.ChannelBankChannelsLength(bank) / 64;
            var bankRegion = dump.GetRegion($"ChannelBank[{bank}]");

            for (var slot = 0; slot < channelsInBank; slot++)
            {
                var flatIndex = (uint)(bank * AnyToneD878CodeplugEncoder.ChannelsPerBank + slot);
                var record = ChannelRecordCodec.Decode(dump.GetChannelRecord(flatIndex));

                // No ChannelsUsed bitmap exists in this format either. An erased/blank slot's BCD
                // frequency bytes (0xFF) decode via BcdCodec to a huge nonsense value (each 0xFF
                // byte contributes 165 per digit pair, not 0 - see BcdCodec's own remarks),
                // reliably outside any real amateur/PMR/commercial frequency range.
                if (record.RxFrequencyHz < 1_000_000 || record.RxFrequencyHz > 1_300_000_000) continue;

                var channelNumber = (int)flatIndex + 1;
                // Channels 4001/4002 ("4000 channels + 2 VFO channels" per the memory map's own
                // remarks) are the radio's live VFO A/B tuned-frequency pseudo-slots, not real
                // CPS-editable channels - they always have a plausible frequency (whatever's
                // currently tuned) and would otherwise get imported as spurious, constantly-
                // churning "channels" on every read. Confirmed on real hardware 2026-07-22.
                if (channelNumber > 4000) continue;
                var mode = record.IsDigital ? "Digital" : "Analog";
                var bandwidth = record.Is25kHz ? "25 kHz" : "12.5 kHz";
                var power = record.PowerLevel switch { 0 => "Low", 1 => "Mid", 3 => "Turbo", _ => "High" };

                // TX Color Code lives in a separate table (see AnyToneD878CodeplugEncoder's remarks
                // on TxColorCodeTableOffset) and the DB schema has no separate column for it - the
                // encoder always writes it equal to ColorCode. Flag it, don't silently drop the
                // information, if a radio's actual TX CC has since diverged (e.g. set via CPS) -
                // this exact kind of silent divergence was the whole MMDVM-silence root cause.
                var txColorCode = bankRegion[AnyToneD878CodeplugEncoder.TxColorCodeTableOffset + slot * 64 + 3];
                if (txColorCode != record.ColorCode)
                    warnings.Add($"Channel '{record.Name.Trim()}' (channel {channelNumber}) has TX Color Code {txColorCode} but RX/display Color Code {record.ColorCode} - only one is stored, imported as {record.ColorCode}.");

                long? contactId = contactIndex.TryGetValue((int)record.ContactIndex, out var cid) ? cid : null;
                long? radioIdId = radioIdIndex.TryGetValue(record.RadioIdIndex, out var rid) ? rid : null;
                long? scanListId = record.ScanListIndex is { } sli && scanListIndex.TryGetValue(sli, out var sid) ? sid : null;
                long? groupListId = record.GroupListIndex is { } gli && groupListIndex.TryGetValue(gli, out var gid) ? gid : null;

                using var find = db.CreateCommand();
                find.Transaction = tx;
                find.CommandText = "SELECT Id FROM Channels WHERE ChannelNumber = $num;";
                find.Parameters.AddWithValue("$num", channelNumber);
                var existing = find.ExecuteScalar();

                long id;
                if (existing is long existingId)
                {
                    id = existingId;
                    using var upd = db.CreateCommand();
                    upd.Transaction = tx;
                    upd.CommandText = """
                        UPDATE Channels SET Name = $name, Mode = $mode, RxFrequencyHz = $rx, TxFrequencyHz = $tx,
                               Bandwidth = $bw, Power = $pow, ColorCode = $cc, TimeSlot = $ts,
                               ContactId = $contact, RadioIdId = $radioId, ScanListId = $scanList, GroupListId = $groupList
                        WHERE Id = $id;
                        """;
                    AddChannelParameters(upd, record, channelNumber, mode, bandwidth, power, contactId, radioIdId, scanListId, groupListId);
                    upd.Parameters.AddWithValue("$id", id);
                    upd.ExecuteNonQuery();
                    matched++;
                }
                else
                {
                    using var ins = db.CreateCommand();
                    ins.Transaction = tx;
                    ins.CommandText = """
                        INSERT INTO Channels (ChannelNumber, Name, Mode, RxFrequencyHz, TxFrequencyHz, Bandwidth, Power,
                               ColorCode, TimeSlot, ContactId, RadioIdId, ScanListId, GroupListId)
                        VALUES ($num, $name, $mode, $rx, $tx, $bw, $pow, $cc, $ts, $contact, $radioId, $scanList, $groupList);
                        SELECT last_insert_rowid();
                        """;
                    AddChannelParameters(ins, record, channelNumber, mode, bandwidth, power, contactId, radioIdId, scanListId, groupListId);
                    id = (long)ins.ExecuteScalar()!;
                    created++;
                    newLabels.Add($"{channelNumber}: {record.Name.Trim()}");
                }
                channelIdByFlatIndex[flatIndex] = id;
            }
        }

        categories.Add(new ImportCategoryResult("Channels", matched, created, 0, newLabels));
        return channelIdByFlatIndex;
    }

    private static void AddChannelParameters(SqliteCommand cmd, ChannelRecord record, int channelNumber, string mode,
        string bandwidth, string power, long? contactId, long? radioIdId, long? scanListId, long? groupListId)
    {
        cmd.Parameters.AddWithValue("$num", channelNumber);
        cmd.Parameters.AddWithValue("$name", record.Name.Trim());
        cmd.Parameters.AddWithValue("$mode", mode);
        cmd.Parameters.AddWithValue("$rx", record.RxFrequencyHz);
        cmd.Parameters.AddWithValue("$tx", record.TxFrequencyHz);
        cmd.Parameters.AddWithValue("$bw", bandwidth);
        cmd.Parameters.AddWithValue("$pow", power);
        cmd.Parameters.AddWithValue("$cc", record.ColorCode);
        cmd.Parameters.AddWithValue("$ts", record.TimeSlot);
        cmd.Parameters.AddWithValue("$contact", (object?)contactId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$radioId", (object?)radioIdId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$scanList", (object?)scanListId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$groupList", (object?)groupListId ?? DBNull.Value);
    }

    // ---- Zones ----

    private static void ImportZones(SqliteConnection db, SqliteTransaction tx, DumpReader dump,
        Dictionary<uint, long> channelIdByFlatIndex, List<ImportCategoryResult> categories, List<string> warnings)
    {
        if (!dump.HasRegion("ZonesUsed") || !dump.HasRegion("ZoneNames")) return;
        var used = dump.GetRegion("ZonesUsed").ToArray();
        var namesRegion = dump.GetRegion("ZoneNames");
        int matched = 0, created = 0;
        var newLabels = new List<string>();

        for (var i = 0; i < 250; i++)
        {
            if (!IsBitSet(used, i)) continue;

            var name = AsciiFieldCodec.Decode(namesRegion.Slice(i * 32, 32)).Trim();
            if (name.Length == 0) continue;

            var members = ZoneRecordCodec.Decode(dump.GetZoneRecord(i));

            using var find = db.CreateCommand();
            find.Transaction = tx;
            find.CommandText = "SELECT Id FROM Zones WHERE Name = $name;";
            find.Parameters.AddWithValue("$name", name);
            var existing = find.ExecuteScalar();

            long zoneId;
            if (existing is long existingId)
            {
                zoneId = existingId;
                using (var upd = db.CreateCommand())
                {
                    upd.Transaction = tx;
                    upd.CommandText = "UPDATE Zones SET IsActive = 1 WHERE Id = $id;";
                    upd.Parameters.AddWithValue("$id", zoneId);
                    upd.ExecuteNonQuery();
                }
                using (var del = db.CreateCommand())
                {
                    del.Transaction = tx;
                    del.CommandText = "DELETE FROM ZoneChannels WHERE ZoneId = $id;";
                    del.Parameters.AddWithValue("$id", zoneId);
                    del.ExecuteNonQuery();
                }
                matched++;
            }
            else
            {
                using var ins = db.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = "INSERT INTO Zones (Name, IsActive) VALUES ($name, 1); SELECT last_insert_rowid();";
                ins.Parameters.AddWithValue("$name", name);
                zoneId = (long)ins.ExecuteScalar()!;
                created++;
                newLabels.Add(name);
            }

            var position = 0;
            foreach (var flatIndex in members)
            {
                if (!channelIdByFlatIndex.TryGetValue(flatIndex, out var channelId))
                {
                    warnings.Add($"Zone '{name}' references channel index {flatIndex}, which wasn't imported (blank/out of range) - skipped.");
                    continue;
                }
                using var insMember = db.CreateCommand();
                insMember.Transaction = tx;
                insMember.CommandText = "INSERT INTO ZoneChannels (ZoneId, ChannelId, Position) VALUES ($zone, $channel, $pos);";
                insMember.Parameters.AddWithValue("$zone", zoneId);
                insMember.Parameters.AddWithValue("$channel", channelId);
                insMember.Parameters.AddWithValue("$pos", position++);
                insMember.ExecuteNonQuery();
            }
        }

        categories.Add(new ImportCategoryResult("Zones", matched, created, 0, newLabels));
    }

    // ---- Scan List / Group List membership (second pass, now that Channels/Contacts exist) ----

    private static void ImportScanListMembership(SqliteConnection db, SqliteTransaction tx,
        List<(long Id, ScanListRecord Record)> decoded, Dictionary<uint, long> channelIdByFlatIndex, List<string> warnings)
    {
        foreach (var (scanListId, record) in decoded)
        {
            long? priority1 = record.PriorityChannel1Index is { } p1 && channelIdByFlatIndex.TryGetValue(p1, out var c1) ? c1 : null;
            long? priority2 = record.PriorityChannel2Index is { } p2 && channelIdByFlatIndex.TryGetValue(p2, out var c2) ? c2 : null;

            using (var upd = db.CreateCommand())
            {
                upd.Transaction = tx;
                upd.CommandText = "UPDATE ScanLists SET PriorityChannel1Id = $p1, PriorityChannel2Id = $p2 WHERE Id = $id;";
                upd.Parameters.AddWithValue("$p1", (object?)priority1 ?? DBNull.Value);
                upd.Parameters.AddWithValue("$p2", (object?)priority2 ?? DBNull.Value);
                upd.Parameters.AddWithValue("$id", scanListId);
                upd.ExecuteNonQuery();
            }
            using (var del = db.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM ScanListChannels WHERE ScanListId = $id;";
                del.Parameters.AddWithValue("$id", scanListId);
                del.ExecuteNonQuery();
            }

            var position = 0;
            foreach (var flatIndex in record.MemberChannelIndices)
            {
                if (!channelIdByFlatIndex.TryGetValue(flatIndex, out var channelId))
                {
                    warnings.Add($"Scan List '{record.Name.Trim()}' references channel index {flatIndex}, which wasn't imported - skipped.");
                    continue;
                }
                using var ins = db.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = "INSERT INTO ScanListChannels (ScanListId, ChannelId, Position) VALUES ($sl, $channel, $pos);";
                ins.Parameters.AddWithValue("$sl", scanListId);
                ins.Parameters.AddWithValue("$channel", channelId);
                ins.Parameters.AddWithValue("$pos", position++);
                ins.ExecuteNonQuery();
            }
        }
    }

    private static void ImportGroupListMembership(SqliteConnection db, SqliteTransaction tx,
        List<(long Id, GroupListRecord Record)> decoded, Dictionary<int, long> contactIndex, List<string> warnings)
    {
        foreach (var (groupListId, record) in decoded)
        {
            using (var del = db.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM GroupListContacts WHERE GroupListId = $id;";
                del.Parameters.AddWithValue("$id", groupListId);
                del.ExecuteNonQuery();
            }

            var position = 0;
            foreach (var memberIndex in record.MemberContactIndices)
            {
                if (!contactIndex.TryGetValue((int)memberIndex, out var contactId))
                {
                    warnings.Add($"Group List '{record.Name.Trim()}' references contact index {memberIndex}, which wasn't imported - skipped.");
                    continue;
                }
                using var ins = db.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = "INSERT INTO GroupListContacts (GroupListId, ContactId, Position) VALUES ($gl, $contact, $pos);";
                ins.Parameters.AddWithValue("$gl", groupListId);
                ins.Parameters.AddWithValue("$contact", contactId);
                ins.Parameters.AddWithValue("$pos", position++);
                ins.ExecuteNonQuery();
            }
        }
    }

    // ---- Roaming ----

    private static void ImportRoaming(SqliteConnection db, SqliteTransaction tx, DumpReader dump,
        List<ImportCategoryResult> categories, List<string> warnings)
    {
        if (!dump.HasRegion("RoamingChannelsUsed") || !dump.HasRegion("RoamingZonesUsed")) return;

        // RoamingChannelRecord doesn't carry the original Channels.Id - it's always a mirror of a
        // real channel (see AnyToneD878CodeplugEncoder.EncodeRoaming's own query), so match back to
        // whatever was just imported by physical identity instead.
        var channelByIdentity = new Dictionary<(long Rx, long Tx, byte Cc, byte Slot), long>();
        using (var cmd = db.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT Id, RxFrequencyHz, TxFrequencyHz, ColorCode, TimeSlot FROM Channels;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var key = (reader.GetInt64(1), reader.GetInt64(2),
                    reader.IsDBNull(3) ? (byte)1 : (byte)reader.GetInt32(3),
                    reader.IsDBNull(4) ? (byte)1 : (byte)reader.GetInt32(4));
                channelByIdentity.TryAdd(key, reader.GetInt64(0));
            }
        }

        var channelsUsed = dump.GetRegion("RoamingChannelsUsed").ToArray();
        var roamingChannelIndex = new Dictionary<int, long>();
        for (var i = 0; i < 250; i++)
        {
            if (!IsBitSet(channelsUsed, i)) continue;
            var record = RoamingChannelRecordCodec.Decode(dump.GetRoamingChannelRecord(i));
            // Roaming Slot is stored 0/1 (see EncodeRoaming's encode side) - normalize to the
            // Channels.TimeSlot convention (1/2) before matching.
            var slot = (byte)(record.Slot == 1 ? 2 : 1);
            var key = (record.RxFrequencyHz, record.TxFrequencyHz, record.ColorCode, slot);
            if (channelByIdentity.TryGetValue(key, out var channelId))
                roamingChannelIndex[i] = channelId;
            else
                warnings.Add($"Roaming channel '{record.Name.Trim()}' doesn't match any imported channel - skipped.");
        }

        var zonesUsed = dump.GetRegion("RoamingZonesUsed").ToArray();
        int matched = 0, created = 0;
        var newLabels = new List<string>();

        for (var i = 0; i < 64; i++)
        {
            if (!IsBitSet(zonesUsed, i)) continue;
            var record = RoamingZoneRecordCodec.Decode(dump.GetRoamingZoneRecord(i));
            var name = record.Name.Trim();
            if (name.Length == 0) continue;

            using var find = db.CreateCommand();
            find.Transaction = tx;
            find.CommandText = "SELECT Id FROM RoamingZones WHERE Name = $name;";
            find.Parameters.AddWithValue("$name", name);
            var existing = find.ExecuteScalar();

            long zoneId;
            if (existing is long existingId)
            {
                zoneId = existingId;
                matched++;
            }
            else
            {
                using var ins = db.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = "INSERT INTO RoamingZones (Name) VALUES ($name); SELECT last_insert_rowid();";
                ins.Parameters.AddWithValue("$name", name);
                zoneId = (long)ins.ExecuteScalar()!;
                created++;
                newLabels.Add(name);
            }

            using (var del = db.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM RoamingZoneChannels WHERE RoamingZoneId = $id;";
                del.Parameters.AddWithValue("$id", zoneId);
                del.ExecuteNonQuery();
            }

            var position = 0;
            foreach (var roamingChannelIdx in record.MemberRoamingChannelIndices)
            {
                if (!roamingChannelIndex.TryGetValue(roamingChannelIdx, out var channelId))
                {
                    warnings.Add($"Roaming Zone '{name}' references a roaming channel that wasn't matched - skipped.");
                    continue;
                }
                using var insMember = db.CreateCommand();
                insMember.Transaction = tx;
                insMember.CommandText = "INSERT INTO RoamingZoneChannels (RoamingZoneId, ChannelId, Position) VALUES ($zone, $channel, $pos);";
                insMember.Parameters.AddWithValue("$zone", zoneId);
                insMember.Parameters.AddWithValue("$channel", channelId);
                insMember.Parameters.AddWithValue("$pos", position++);
                insMember.ExecuteNonQuery();
            }
        }

        categories.Add(new ImportCategoryResult("Roaming Zones", matched, created, 0, newLabels));
    }
}
