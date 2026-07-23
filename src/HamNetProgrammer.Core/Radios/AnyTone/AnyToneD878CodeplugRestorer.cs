namespace HamNetProgrammer.Core.Radios.AnyTone;

public sealed record RestoredRegion(string Name, uint Address, int Length, bool Skipped, string? SkipReason);

/// <summary>
/// Undoes a specific write-codeplug session by writing the same regions it touched back to their
/// pre-write values from that session's baseline dump - the mirror image of
/// <see cref="AnyToneD878CodeplugWriter"/>, not a generic "restore any dump" tool.
///
/// Deliberately scoped this way rather than replaying a full baseline dump wholesale: several of
/// this radio's regions (ScanList/RoamingChannels/PrefabSms entries, and the ZoneAChannel/
/// ZoneBChannel pair that caused a real corruption incident) are sparse records inside mostly-
/// undocumented 256KB flash erase blocks. A dump only ever captured the named sub-regions, never
/// the gaps between them, so blindly writing "the whole block from the dump" would re-erase the
/// block and leave the undocumented gap bytes blank - repeating the original bug. Restoring only
/// the exact regions the write actually touched, using the same live-read-modify-write splice for
/// the shared block, never depends on knowing what's in the gap at all.
///
/// The three shared-block regions are NOT restored inside <see cref="RestorePlainRegions"/> - see
/// AnyToneD878CodeplugWriter's remarks on WriteSharedBlockRegionIsolated for why (a real-hardware-
/// confirmed unreliable-when-bundled, reliable-when-isolated firmware commit behavior). Build each
/// with the BuildXxx methods here and write it via
/// AnyToneD878CodeplugWriter.WriteSharedBlockRegionIsolated in its own session, exactly like a
/// fresh write does.
/// </summary>
public static class AnyToneD878CodeplugRestorer
{
    public const string ZoneChannelDefaultsRegionName = "ZoneChannelDefaults (read-modify-write)";
    public const string RoamingBlockRegionName = "RoamingBlock (read-modify-write)";
    public const string GeneralUsedBitmapsBlockRegionName = "GeneralUsedBitmapsBlock (read-modify-write)";
    private const int MaxReadChunkSize = 0xFF;

    // Every named sub-region AnyToneD878MemoryMap knows about within the shared 0x02500000-
    // 0x02501900 block, with its offset relative to the block start. Broader than just the two
    // (PowerOnAndOptionalSettings, AprsGeneralSettings) this restorer originally covered - that gap
    // meant a restore left AprsSendingText/GpsTemplateText/MoreOptionalSettings/AnalogAprsList
    // un-reverted, confirmed the hard way on real hardware (2026-07-19: colours and scan behaviour
    // stayed wrong after a restore that "succeeded").
    private static readonly (string Name, int Offset)[] ZoneChannelDefaultsSubRegions =
    [
        ("PowerOnAndOptionalSettings", 0x0000),
        ("ZoneAChannel", AnyToneD878CodeplugEncoder.ZoneAChannelOffset),
        ("ZoneBChannel", AnyToneD878CodeplugEncoder.ZoneBChannelOffset),
        ("PowerOnSettings", 0x0600),
        ("AprsGeneralSettings", 0x1000),
        ("AprsSendingText", 0x1200),
        ("GpsTemplateText", 0x1280),
        ("MoreOptionalSettings", 0x1400),
        ("AnalogAprsList", 0x1800),
    ];

    // Every dump region name that lives inside one of the three shared read-modify-write blocks -
    // these must never be written as standalone plain regions (that's exactly the 2026-07-17
    // lock-screen incident), only ever spliced into their block via the BuildRestoredXxx methods
    // above. A raw full dump lists every one of these individually by name (it doesn't know they
    // share an erase block), so restoring a WHOLE dump wholesale - as opposed to just a write
    // session's touched subset - needs to exclude them explicitly. See PlainRegionsForFullRestore.
    private static readonly string[] SharedBlockSubRegionNames =
        ZoneChannelDefaultsSubRegions.Select(r => r.Name)
            .Concat(["RoamingChannels", "RoamingChannelsUsed", "RoamingZonesUsed", "RoamingZones"])
            .Concat(["ZonesUsed", "RadioIdListUsed", "ScanListsUsed"])
            .ToArray();

    public const string GroupListsBlockRegionName = "GroupListsBlock (read-modify-write)";

    // All 250 GroupList[N] records (512 bytes each, AnyToneD878MemoryMap) sit at 0x02980000 +
    // n*512 - entirely inside ONE 256KB-aligned erase block (0x02980000-0x029C0000), with roughly
    // half the block left over as undocumented reserved space. This was never given the
    // read-modify-write treatment the other three shared blocks got, because the bug it causes only
    // shows up when plain regions get split across MULTIPLE separate session commits - which never
    // happened before Restore Radio Memory's batched restore (2026-07-23) existed. A normal Write
    // Codeplug bundles GroupLists into its one single unbatched commit and has never shown this
    // symptom.
    //
    // Confirmed on real hardware 2026-07-23: a full Restore Radio Memory run came back with
    // GroupList[1]-[8] wrong and GroupList[9]-[250] correct, with every other one of 711 regions
    // matching exactly. That split is the signature of this exact bug class - GroupList[1]-[8] and
    // GroupList[9]-[250] landed in two different batches (byte-budget-based batching has no
    // awareness of erase-block boundaries), and the later batch's commit re-erased the whole shared
    // block, wiping out the earlier batch's contribution as a side effect - same root cause as the
    // GeneralUsedBitmapsBlock/ZoneChannelDefaults contiguity incident, just a different block.
    public static readonly uint GroupListsBlockAddress = 0x02980000;
    public static readonly int GroupListsBlockLength = 0x40000;

    private static readonly (string Name, int Offset)[] GroupListsSubRegions =
        Enumerable.Range(1, 250).Select(n => ($"GroupList[{n}]", (n - 1) * 512)).ToArray();

    public static EncodedRegion BuildRestoredGroupListsBlock(AnyToneD878Transport radio, DumpReader baseline) =>
        BuildRestoredSplicedBlock(radio, baseline, GroupListsBlockRegionName, GroupListsBlockAddress, GroupListsBlockLength, GroupListsSubRegions);

    public static bool DumpHasGroupListsBlockData(DumpReader dump) =>
        GroupListsSubRegions.Any(r => dump.HasRegion(r.Name) && dump.RegionSucceeded(r.Name));

    // The four shared blocks' own address ranges - used to exclude ANY region overlapping one of
    // them, not just the specifically-named sub-regions above. A full dump's gap-filling
    // "RawFill_0x..." regions (added at dump time to capture every byte, including undocumented
    // gaps between named sub-regions) don't appear in SharedBlockSubRegionNames at all, since that
    // list only knows the named sub-regions - but several RawFill segments live INSIDE these same
    // blocks (e.g. the 12-byte gap between ZoneAChannel and ZoneBChannel). Restoring those as
    // standalone plain writes is exactly the class of bug the name-only filter was meant to
    // prevent (real hardware corruption, 2026-07-17/07-19 incidents) - confirmed again 2026-07-23,
    // when one such RawFill segment (12 bytes, not even 16-byte aligned) reached WriteRegion and
    // crashed a restore mid-session. Range-based exclusion catches all of these regardless of name.
    private static readonly (uint Address, int Length)[] SharedBlockRanges =
    [
        (AnyToneD878CodeplugEncoder.RoamingBlockAddress, AnyToneD878CodeplugEncoder.RoamingBlockLength),
        (AnyToneD878CodeplugEncoder.GeneralUsedBitmapsBlockAddress, AnyToneD878CodeplugEncoder.GeneralUsedBitmapsBlockLength),
        (AnyToneD878CodeplugEncoder.ZoneChannelDefaultsBlockAddress, AnyToneD878CodeplugEncoder.ZoneChannelDefaultsBlockLength),
        (GroupListsBlockAddress, GroupListsBlockLength),
    ];

    internal static bool OverlapsAnySharedBlock(uint address, int length) =>
        SharedBlockRanges.Any(b => address < b.Address + (uint)b.Length && address + (uint)length > b.Address);

    /// <summary>Every region in a full dump that should be restored as a standalone plain write
    /// when restoring the WHOLE dump (as opposed to just what one write session touched) - i.e.
    /// everything the dump captured successfully, except anything overlapping the shared blocks
    /// above (restored only via their Build* splice methods, in their own isolated sessions), the
    /// large per-bank regions (ChannelBank[N]/TalkGroupList[N]), which need their own isolated
    /// verified session same as a fresh write (see AnyToneD878CodeplugWriter's remarks on why those
    /// can't be bundled reliably), and RawFill_* gap-filler regions.
    ///
    /// RawFill exclusion added 2026-07-23 after RawFill_0x02480240 (261,568 bytes of completely
    /// undocumented flash, captured only so a dump is byte-complete for forensic purposes) failed
    /// verification 3/3 times even written as a single unsplit commit - an untested size (between
    /// the 100,000 bytes already proven safe and the ~558,000 already proven unsafe) in a region
    /// this project has no actual understanding of. Restoring undocumented padding bytes has no
    /// user-facing value (it's not a zone, channel, contact, or setting anyone configured) and has
    /// now caused real corruption twice trying anyway - not worth the risk. A dump still CAPTURES
    /// RawFill for backup completeness; restore just leaves it as whatever's already on the
    /// radio, same principle already applied to the shared blocks' own undocumented gaps.</summary>
    public static IReadOnlyList<(string Name, uint Address, int Length)> PlainRegionsForFullRestore(DumpReader dump) =>
        dump.RegionNames
            .Where(dump.RegionSucceeded)
            .Where(name => !SharedBlockSubRegionNames.Contains(name))
            .Where(name => !name.StartsWith("ChannelBank[", StringComparison.Ordinal) && !name.StartsWith("TalkGroupList[", StringComparison.Ordinal))
            .Where(name => !name.StartsWith("RawFill_", StringComparison.Ordinal))
            .Select(name => (Name: name, Address: dump.GetRegionAddress(name), Length: dump.GetRegion(name).Length))
            .Where(r => !OverlapsAnySharedBlock(r.Address, r.Length))
            .ToList();

    // The Has*(writtenRegions) overloads below check for the synthetic composite names
    // (e.g. "ZoneChannelDefaults (read-modify-write)") that only ever appear in a write/restore
    // session's audit log - a raw dump never contains them, only the individual sub-regions. These
    // Dump-prefixed variants check the dump directly instead, for a whole-dump restore.
    public static bool DumpHasZoneChannelDefaultsData(DumpReader dump) =>
        ZoneChannelDefaultsSubRegions.Any(r => dump.HasRegion(r.Name) && dump.RegionSucceeded(r.Name));

    public static bool DumpHasRoamingBlockData(DumpReader dump) =>
        new[] { "RoamingChannels", "RoamingChannelsUsed", "RoamingZonesUsed", "RoamingZones" }
            .Any(name => dump.HasRegion(name) && dump.RegionSucceeded(name));

    public static bool DumpHasGeneralUsedBitmapsBlockData(DumpReader dump) =>
        new[] { "ZonesUsed", "RadioIdListUsed", "ScanListsUsed" }
            .Any(name => dump.HasRegion(name) && dump.RegionSucceeded(name));

    public static bool HasZoneChannelDefaults(IReadOnlyList<(string Name, uint Address, int Length)> writtenRegions) =>
        writtenRegions.Any(r => r.Name == ZoneChannelDefaultsRegionName);

    public static bool HasRoamingBlock(IReadOnlyList<(string Name, uint Address, int Length)> writtenRegions) =>
        writtenRegions.Any(r => r.Name == RoamingBlockRegionName);

    public static bool HasGeneralUsedBitmapsBlock(IReadOnlyList<(string Name, uint Address, int Length)> writtenRegions) =>
        writtenRegions.Any(r => r.Name == GeneralUsedBitmapsBlockRegionName);

    // 100,000 bytes - the largest single-session commit already demonstrated safe on real hardware
    // (TalkGroupList's actual writes, confirmed working across many sessions including 2026-07-23's
    // full restore). Not a guessed value; deliberately matched to proven-safe evidence rather than
    // picked arbitrarily, since guessing wrong here means silently losing data again.
    public const int PlainRegionMaxBytesPerSession = 100_000;

    /// <summary>Restores everything EXCEPT the three shared-block regions, bundling several small
    /// regions into one committed-and-verified session at a time (up to
    /// <paramref name="maxBytesPerSession"/> total), while every INDIVIDUAL region's own bytes
    /// always go in exactly one uninterrupted commit, never split across sessions regardless of its
    /// size.
    ///
    /// TWO root causes found here on real hardware, both 2026-07-23, same day:
    ///
    /// 1. The original version of this method bundled ALL plain regions into ONE session with ONE
    ///    final commit and no verification at all - fine for a normal Write Codeplug (its
    ///    plain-region total is typically well under 200KB, since it only includes regions the
    ///    current codeplug actually touches), but a full "Restore Radio Memory" also restores every
    ///    RawFill gap-filler region a dump captured (multiple 250KB+ blobs of otherwise-undocumented
    ///    flash), pushing the bundled total past 1.3MB - squarely in the territory already proven
    ///    unsafe by WriteRegionChunkedAndVerify's remarks (too much in one uncommitted session gets
    ///    silently ACKed and discarded, no error, only visible as a mismatch on a later independent
    ///    read-back). A real restore hit exactly this - ZoneNames, RadioIdList (contacts),
    ///    HotKeyBlock, and several RawFill regions all came back different despite a "successful"
    ///    run, which is what a scrambled talkgroup table with correct-looking zones/channels
    ///    actually looks like from this bug.
    ///
    /// 2. The first fix attempt then tried splitting any single region bigger than the budget
    ///    across multiple committed chunks (mirroring ChannelBank/TalkGroupList's chunking). That's
    ///    NOT the same thing as what's proven safe elsewhere - Zones (128,000 bytes) failed
    ///    verification the one time this got tried, despite writing correctly as ONE unsplit commit
    ///    in every prior session ever observed, including a normal Write Codeplug. Almost certainly
    ///    the same family of bug as the erase-block-disturb issues elsewhere in this class: a
    ///    second commit into the same erase block wipes out what the first one just wrote. So a
    ///    region bigger than the whole budget just gets its own solo session instead of sharing one
    ///    with anything else - written whole, never chunked - even if that single commit ends up
    ///    bigger than <paramref name="maxBytesPerSession"/> itself.</summary>
    public static List<RestoredRegion> RestorePlainRegions(
        string portName,
        DumpReader baseline,
        IReadOnlyList<(string Name, uint Address, int Length)> writtenRegions,
        int maxBytesPerSession,
        Action<RestoredRegion, int, int>? onProgress = null,
        Action<string>? onLog = null)
    {
        var specialNames = new[] { ZoneChannelDefaultsRegionName, RoamingBlockRegionName, GeneralUsedBitmapsBlockRegionName };
        var plain = writtenRegions.Where(r => !specialNames.Contains(r.Name)).ToList();
        var results = new List<RestoredRegion>();
        var done = 0;

        var batch = new List<(string Name, uint Address, byte[] Data)>();
        var batchBytes = 0;

        void FlushBatch()
        {
            if (batch.Count == 0) return;
            var thisBatch = batch;
            var totalBytes = thisBatch.Sum(b => b.Data.Length);

            for (var attempt = 1; attempt <= AnyToneD878CodeplugWriter.VerifyMaxAttempts; attempt++)
            {
                onLog?.Invoke($"Writing a batch of {thisBatch.Count} plain region(s) ({totalBytes:N0} bytes total, attempt {attempt}/{AnyToneD878CodeplugWriter.VerifyMaxAttempts})...");
                AnyToneD878CodeplugWriter.WaitForPortDropThenReturn(portName);
                using (var radio = new AnyToneD878Transport(portName))
                {
                    radio.Open();
                    radio.StartProgrammingSession();
                    radio.ReadDeviceId();
                    foreach (var (_, address, data) in thisBatch)
                        WriteRegion(radio, address, data);
                    radio.EndProgrammingSession();
                }

                onLog?.Invoke("Batch committed. Verifying with a fresh read-only session...");
                AnyToneD878CodeplugWriter.WaitForPortDropThenReturn(portName);
                var mismatches = new List<string>();
                using (var verifyRadio = new AnyToneD878Transport(portName))
                {
                    verifyRadio.Open();
                    verifyRadio.StartProgrammingSession();
                    verifyRadio.ReadDeviceId();
                    foreach (var (name, address, data) in thisBatch)
                    {
                        var readBack = AnyToneD878CodeplugWriter.ReadRange(verifyRadio, address, data.Length);
                        if (!readBack.AsSpan().SequenceEqual(data))
                            mismatches.Add(name);
                    }
                    verifyRadio.EndProgrammingSession();
                }

                if (mismatches.Count == 0)
                {
                    onLog?.Invoke($"Batch verified correct on attempt {attempt}.");
                    batch = [];
                    batchBytes = 0;
                    return;
                }

                onLog?.Invoke($"Batch did NOT verify on attempt {attempt}/{AnyToneD878CodeplugWriter.VerifyMaxAttempts} - {mismatches.Count} region(s) wrong ({string.Join(", ", mismatches)}) - retrying with the same bytes.");
            }

            throw new InvalidOperationException(
                $"A batch of plain regions ({string.Join(", ", thisBatch.Select(b => b.Name))}) did not verify correctly after {AnyToneD878CodeplugWriter.VerifyMaxAttempts} attempts. The radio may need manual recovery - take a fresh backup and check before further writes.");
        }

        foreach (var (name, address, length) in plain)
        {
            if (!baseline.HasRegion(name))
            {
                // TalkGroupOffsets (0x04340000) is deliberately excluded from baseline dumps - see
                // AnyToneD878MemoryMap's remarks. It's write-support scratch data with no
                // established "current state" semantics, not user-facing codeplug content, so
                // there's nothing to restore it to and skipping it is safe.
                done++;
                var result = new RestoredRegion(name, address, length, true, "No baseline data for this region (never dumped).");
                results.Add(result);
                onProgress?.Invoke(result, done, plain.Count);
                continue;
            }

            var data = baseline.GetRegion(name)[..length].ToArray();

            // A region's own bytes are NEVER split across multiple session commits, no matter how
            // large - confirmed 2026-07-23 that this is unsafe, not just untested: Zones (128,000
            // bytes, bigger than the 100,000-byte budget) failed verification the one time this
            // method tried splitting it across two commits, despite Zones having written correctly
            // as ONE unsplit commit in every single prior session (including the very same run's
            // earlier attempt, and every normal Write Codeplug ever observed). Most likely the same
            // family of bug as the erase-block-disturb issues elsewhere in this class: a second
            // commit into the same erase block wipes out what the first commit just wrote. If a
            // region alone is bigger than the batch budget, it just gets its own solo session
            // instead of sharing one with anything else - written whole, never chunked.
            if (batchBytes > 0 && batchBytes + data.Length > maxBytesPerSession)
                FlushBatch();

            batch.Add((name, address, data));
            batchBytes += data.Length;
            done++;
            var pending = new RestoredRegion(name, address, length, false, null);
            results.Add(pending);
            onProgress?.Invoke(pending, done, plain.Count);
        }

        FlushBatch();
        return results;
    }

    /// <summary>Reads the block live (must be called on a just-opened, otherwise-untouched
    /// session) and splices in every known sub-region from the baseline. Write the result via
    /// AnyToneD878CodeplugWriter.WriteSharedBlockRegionIsolated, then End that session, before
    /// touching any other shared block.</summary>
    public static EncodedRegion BuildRestoredZoneChannelDefaults(AnyToneD878Transport radio, DumpReader baseline)
    {
        var address = AnyToneD878CodeplugEncoder.ZoneChannelDefaultsBlockAddress;
        var length = AnyToneD878CodeplugEncoder.ZoneChannelDefaultsBlockLength;

        var buffer = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var chunkLength = (byte)Math.Min(MaxReadChunkSize, length - offset);
            radio.ReadMemory(address + (uint)offset, chunkLength).CopyTo(buffer, offset);
            offset += chunkLength;
        }

        foreach (var (name, relativeOffset) in ZoneChannelDefaultsSubRegions)
        {
            if (baseline.HasRegion(name))
                baseline.GetRegion(name).CopyTo(buffer.AsSpan(relativeOffset));
        }

        return new EncodedRegion(ZoneChannelDefaultsRegionName, address, buffer);
    }

    public static EncodedRegion BuildRestoredRoamingBlock(AnyToneD878Transport radio, DumpReader baseline) =>
        BuildRestoredSplicedBlock(radio, baseline, RoamingBlockRegionName,
            AnyToneD878CodeplugEncoder.RoamingBlockAddress, AnyToneD878CodeplugEncoder.RoamingBlockLength,
            new (string, int)[]
            {
                ("RoamingChannels", AnyToneD878CodeplugEncoder.RoamingChannelsOffset),
                ("RoamingChannelsUsed", AnyToneD878CodeplugEncoder.RoamingChannelsUsedOffset),
                ("RoamingZonesUsed", AnyToneD878CodeplugEncoder.RoamingZonesUsedOffset),
                ("RoamingZones", AnyToneD878CodeplugEncoder.RoamingZonesOffset),
            });

    public static EncodedRegion BuildRestoredGeneralUsedBitmapsBlock(AnyToneD878Transport radio, DumpReader baseline) =>
        // Offsets relative to GeneralUsedBitmapsBlockAddress (0x024C0000): ZonesUsed sits at
        // 0x024c1300 (offset 0x1300), ScanListsUsed at 0x024c1340 (offset 0x1340),
        // RadioIdListUsed at 0x024c1320 (offset 0x1320).
        BuildRestoredSplicedBlock(radio, baseline, GeneralUsedBitmapsBlockRegionName,
            AnyToneD878CodeplugEncoder.GeneralUsedBitmapsBlockAddress, AnyToneD878CodeplugEncoder.GeneralUsedBitmapsBlockLength,
            new (string, int)[]
            {
                ("ZonesUsed", 0x1300),
                ("RadioIdListUsed", 0x1320),
                ("ScanListsUsed", 0x1340),
            });

    private static EncodedRegion BuildRestoredSplicedBlock(AnyToneD878Transport radio, DumpReader baseline,
        string regionName, uint address, int length, IReadOnlyList<(string Name, int Offset)> subRegions)
    {
        var buffer = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var chunkLength = (byte)Math.Min(MaxReadChunkSize, length - offset);
            radio.ReadMemory(address + (uint)offset, chunkLength).CopyTo(buffer, offset);
            offset += chunkLength;
        }

        foreach (var (name, relativeOffset) in subRegions)
        {
            if (baseline.HasRegion(name))
                baseline.GetRegion(name).CopyTo(buffer.AsSpan(relativeOffset));
        }

        return new EncodedRegion(regionName, address, buffer);
    }

    private static void WriteRegion(AnyToneD878Transport radio, uint address, byte[] data)
    {
        // The radio's WriteMemory only ever accepts exactly 16 bytes per call, so every region
        // reaching here MUST be 16-byte aligned - PlainRegionsForFullRestore's shared-block-range
        // exclusion (see OverlapsAnySharedBlock) is what guarantees that, by keeping the
        // non-aligned RawFill gap-fillers (e.g. the 12-byte gap between ZoneAChannel and
        // ZoneBChannel) out of the plain-region list entirely. If a non-aligned region ever gets
        // here anyway, fail loudly with a clear cause instead of a confusing
        // ArgumentOutOfRangeException from WriteMemory's fixed-16 span slicing (what happened
        // 2026-07-23, mid-restore, on a real radio) - or worse, silently sending a short/padded
        // write that corrupts whatever real data sits in the remainder of that 16-byte window.
        if (data.Length % 16 != 0)
            throw new InvalidOperationException(
                $"Region at 0x{address:x8} is {data.Length} bytes, not 16-byte aligned - it should have been excluded from the plain-region restore list (see PlainRegionsForFullRestore).");

        for (var offset = 0; offset < data.Length; offset += 16)
            radio.WriteMemory(address + (uint)offset, data.AsSpan(offset, 16));
    }
}
