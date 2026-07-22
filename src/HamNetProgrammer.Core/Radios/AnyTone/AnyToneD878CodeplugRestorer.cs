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

    public static bool HasZoneChannelDefaults(IReadOnlyList<(string Name, uint Address, int Length)> writtenRegions) =>
        writtenRegions.Any(r => r.Name == ZoneChannelDefaultsRegionName);

    public static bool HasRoamingBlock(IReadOnlyList<(string Name, uint Address, int Length)> writtenRegions) =>
        writtenRegions.Any(r => r.Name == RoamingBlockRegionName);

    public static bool HasGeneralUsedBitmapsBlock(IReadOnlyList<(string Name, uint Address, int Length)> writtenRegions) =>
        writtenRegions.Any(r => r.Name == GeneralUsedBitmapsBlockRegionName);

    /// <summary>Restores everything EXCEPT the three shared-block regions, in the given
    /// already-open session - safe to bundle since none of these share a flash erase block with
    /// anything else in this list.</summary>
    public static List<RestoredRegion> RestorePlainRegions(
        AnyToneD878Transport radio,
        DumpReader baseline,
        IReadOnlyList<(string Name, uint Address, int Length)> writtenRegions,
        Action<RestoredRegion, int, int>? onProgress = null)
    {
        var specialNames = new[] { ZoneChannelDefaultsRegionName, RoamingBlockRegionName, GeneralUsedBitmapsBlockRegionName };
        var plain = writtenRegions.Where(r => !specialNames.Contains(r.Name)).ToList();
        var results = new List<RestoredRegion>();
        var done = 0;

        foreach (var (name, address, length) in plain)
        {
            done++;
            if (!baseline.HasRegion(name))
            {
                // TalkGroupOffsets (0x04340000) is deliberately excluded from baseline dumps - see
                // AnyToneD878MemoryMap's remarks. It's write-support scratch data with no
                // established "current state" semantics, not user-facing codeplug content, so
                // there's nothing to restore it to and skipping it is safe.
                var result = new RestoredRegion(name, address, length, true, "No baseline data for this region (never dumped).");
                results.Add(result);
                onProgress?.Invoke(result, done, plain.Count);
                continue;
            }

            var data = baseline.GetRegion(name)[..length].ToArray();
            WriteRegion(radio, address, data);
            var ok = new RestoredRegion(name, address, length, false, null);
            results.Add(ok);
            onProgress?.Invoke(ok, done, plain.Count);
        }

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
        for (var offset = 0; offset < data.Length; offset += 16)
            radio.WriteMemory(address + (uint)offset, data.AsSpan(offset, 16));
    }
}
