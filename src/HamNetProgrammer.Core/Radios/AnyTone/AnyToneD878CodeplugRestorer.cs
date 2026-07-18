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
/// </summary>
public static class AnyToneD878CodeplugRestorer
{
    private const string ZoneChannelDefaultsRegionName = "ZoneChannelDefaults (read-modify-write)";
    private const string RoamingBlockRegionName = "RoamingBlock (read-modify-write)";
    private const int MaxReadChunkSize = 0xFF;

    public static List<RestoredRegion> Restore(
        AnyToneD878Transport radio,
        DumpReader baseline,
        IReadOnlyList<(string Name, uint Address, int Length)> writtenRegions,
        Action<RestoredRegion, int, int>? onProgress = null)
    {
        var results = new List<RestoredRegion>();
        var specialNames = new[] { ZoneChannelDefaultsRegionName, RoamingBlockRegionName };
        var plain = writtenRegions.Where(r => !specialNames.Contains(r.Name)).ToList();
        var total = plain.Count
            + (writtenRegions.Any(r => r.Name == ZoneChannelDefaultsRegionName) ? 1 : 0)
            + (writtenRegions.Any(r => r.Name == RoamingBlockRegionName) ? 1 : 0);
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
                onProgress?.Invoke(result, done, total);
                continue;
            }

            var data = baseline.GetRegion(name)[..length].ToArray();
            WriteRegion(radio, address, data);
            var ok = new RestoredRegion(name, address, length, false, null);
            results.Add(ok);
            onProgress?.Invoke(ok, done, total);
        }

        if (writtenRegions.Any(r => r.Name == ZoneChannelDefaultsRegionName))
        {
            done++;
            var result = RestoreZoneChannelDefaults(radio, baseline);
            results.Add(result);
            onProgress?.Invoke(result, done, total);
        }

        if (writtenRegions.Any(r => r.Name == RoamingBlockRegionName))
        {
            done++;
            var result = RestoreRoamingBlock(radio, baseline);
            results.Add(result);
            onProgress?.Invoke(result, done, total);
        }

        return results;
    }

    private static RestoredRegion RestoreZoneChannelDefaults(AnyToneD878Transport radio, DumpReader baseline)
    {
        var address = AnyToneD878CodeplugEncoder.ZoneChannelDefaultsBlockAddress;
        var length = AnyToneD878CodeplugEncoder.ZoneChannelDefaultsBlockLength;

        if (!baseline.HasRegion("ZoneAChannel") || !baseline.HasRegion("ZoneBChannel"))
            return new RestoredRegion(ZoneChannelDefaultsRegionName, address, length, true, "No baseline data for ZoneAChannel/ZoneBChannel.");

        // Same pattern as the write path: read the block's LIVE current content first (not the
        // baseline's stale copy of the whole block), so anything else sharing this erase block
        // that changed for a legitimate reason since the backup is preserved. Only the two known
        // sub-ranges get overwritten, with the pre-write baseline's bytes instead of new ones.
        var buffer = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var chunkLength = (byte)Math.Min(MaxReadChunkSize, length - offset);
            radio.ReadMemory(address + (uint)offset, chunkLength).CopyTo(buffer, offset);
            offset += chunkLength;
        }

        baseline.GetRegion("ZoneAChannel").CopyTo(buffer.AsSpan(AnyToneD878CodeplugEncoder.ZoneAChannelOffset));
        baseline.GetRegion("ZoneBChannel").CopyTo(buffer.AsSpan(AnyToneD878CodeplugEncoder.ZoneBChannelOffset));

        WriteRegion(radio, address, buffer);
        return new RestoredRegion(ZoneChannelDefaultsRegionName, address, length, false, null);
    }

    private static RestoredRegion RestoreRoamingBlock(AnyToneD878Transport radio, DumpReader baseline)
    {
        var address = AnyToneD878CodeplugEncoder.RoamingBlockAddress;
        var length = AnyToneD878CodeplugEncoder.RoamingBlockLength;

        var subRegions = new (string Name, int Offset)[]
        {
            ("RoamingChannels", AnyToneD878CodeplugEncoder.RoamingChannelsOffset),
            ("RoamingChannelsUsed", AnyToneD878CodeplugEncoder.RoamingChannelsUsedOffset),
            ("RoamingZonesUsed", AnyToneD878CodeplugEncoder.RoamingZonesUsedOffset),
            ("RoamingZones", AnyToneD878CodeplugEncoder.RoamingZonesOffset),
        };

        var missing = subRegions.Where(r => !baseline.HasRegion(r.Name)).Select(r => r.Name).ToList();
        if (missing.Count > 0)
            return new RestoredRegion(RoamingBlockRegionName, address, length, true, $"No baseline data for {string.Join(", ", missing)}.");

        // Same pattern as ZoneChannelDefaults: read the block's LIVE current content first, so
        // anything else sharing this erase block that changed for a legitimate reason since the
        // backup is preserved. Only the four known sub-ranges get overwritten.
        var buffer = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var chunkLength = (byte)Math.Min(MaxReadChunkSize, length - offset);
            radio.ReadMemory(address + (uint)offset, chunkLength).CopyTo(buffer, offset);
            offset += chunkLength;
        }

        foreach (var (name, relativeOffset) in subRegions)
            baseline.GetRegion(name).CopyTo(buffer.AsSpan(relativeOffset));

        WriteRegion(radio, address, buffer);
        return new RestoredRegion(RoamingBlockRegionName, address, length, false, null);
    }

    private static void WriteRegion(AnyToneD878Transport radio, uint address, byte[] data)
    {
        for (var offset = 0; offset < data.Length; offset += 16)
            radio.WriteMemory(address + (uint)offset, data.AsSpan(offset, 16));
    }
}
