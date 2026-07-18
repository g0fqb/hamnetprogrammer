namespace HamNetProgrammer.Core.Radios.AnyTone;

/// <summary>
/// Writes a full set of encoded regions to the radio within a single programming session.
/// Caller is responsible for StartProgrammingSession()/EndProgrammingSession() - the actual
/// flash commit only happens at END (see AnyToneD878Transport.WriteMemory), and after that the
/// radio drops off USB and re-enumerates ~10-15s later, so any post-write verification must
/// happen in a new session opened after waiting for the port to reappear.
/// </summary>
public static class AnyToneD878CodeplugWriter
{
    private const int WriteChunkSize = 16;
    private const int MaxReadChunkSize = 0xFF;

    public static void Write(AnyToneD878Transport radio, IReadOnlyList<EncodedRegion> regions, Action<EncodedRegion, int, int, long, long>? onProgress = null)
    {
        var allRegions = regions.ToList();

        // Number of zones actually being written this session, from the ZonesUsed bitmap (0-based
        // contiguous from index 0 - see AnyToneD878CodeplugEncoder.EncodeZones). Used below to keep
        // the "current zone" pointer bytes from ever going stale/out-of-range.
        var zoneCount = 0;
        var zonesUsedForCount = allRegions.FirstOrDefault(r => r.Name == "ZonesUsed");
        if (zonesUsedForCount is not null)
            foreach (var b in zonesUsedForCount.Data)
                zoneCount += System.Numerics.BitOperations.PopCount(b);

        // Zone A/B Channel defaults, and now the GPS/APRS RadioSettings fields, share a 256KB flash
        // erase block with settings sections this encoder doesn't otherwise touch (power-on
        // message, DTMF list, etc.) - writing any of them as standalone regions would erase the
        // whole block and silently wipe everything else sharing it. Read-modify-write the live
        // block instead, splicing in every region that belongs inside it. See
        // AnyToneD878CodeplugEncoder's remarks for the full story (this was found and fixed after
        // it happened on real hardware).
        var sharedBlockSubRegions = allRegions.Where(r => r.Name.StartsWith(AnyToneD878CodeplugEncoder.SharedBlockRegionPrefix)).ToList();
        if (allRegions.Any(r => r.Name == "Zones") || sharedBlockSubRegions.Count > 0)
        {
            allRegions.RemoveAll(r => sharedBlockSubRegions.Contains(r));
            allRegions.Add(BuildZoneChannelDefaultsRegion(radio, sharedBlockSubRegions, zoneCount));
        }

        // RoamingChannels/RoamingChannelsUsed/RoamingZonesUsed/RoamingZones share a different
        // 256KB erase block with a large undocumented tail - same failure mode as above, see
        // AnyToneD878CodeplugEncoder.RoamingBlockAddress's remarks. Pull these four out of the
        // direct-write list and splice them into a live read of the WHOLE containing block instead.
        var roamingSubRegions = allRegions.Where(r => AnyToneD878CodeplugEncoder.RoamingBlockRegionNames.Contains(r.Name)).ToList();
        if (roamingSubRegions.Count > 0)
        {
            allRegions.RemoveAll(r => roamingSubRegions.Contains(r));
            allRegions.Add(BuildSplicedBlockRegion(radio, "RoamingBlock (read-modify-write)",
                AnyToneD878CodeplugEncoder.RoamingBlockAddress, AnyToneD878CodeplugEncoder.RoamingBlockLength, roamingSubRegions));
        }

        // ZonesUsed/ScanListsUsed/RadioIdListUsed share a THIRD 256KB erase block
        // (0x024C0000-0x024FFFFF, alongside FiveTone/TwoTone/Alarm/Encryption/AutoRepeater data
        // this encoder never writes) - found the hard way on real hardware (2026-07-18): writing
        // these three as standalone regions, even within one session, does NOT merge safely -
        // each later write re-erased the block and silently wiped the earlier ones back to 0xFF,
        // contradicting the "erase happens once per session" assumption this project had been
        // operating on since the original 2026-07-17 incident. Only RadioIdListUsed (written last
        // in Build()'s call order) ever survived; ZonesUsed and ScanListsUsed did not, for every
        // write-codeplug run from 2026-07-17 onward until this fix. Same remedy as the other two
        // shared blocks: splice into one live-read/write-back instead of three standalone writes.
        var usedBitmapSubRegions = allRegions.Where(r => AnyToneD878CodeplugEncoder.GeneralUsedBitmapsBlockRegionNames.Contains(r.Name)).ToList();
        if (usedBitmapSubRegions.Count > 0)
        {
            allRegions.RemoveAll(r => usedBitmapSubRegions.Contains(r));
            allRegions.Add(BuildSplicedBlockRegion(radio, "GeneralUsedBitmapsBlock (read-modify-write)",
                AnyToneD878CodeplugEncoder.GeneralUsedBitmapsBlockAddress, AnyToneD878CodeplugEncoder.GeneralUsedBitmapsBlockLength, usedBitmapSubRegions));
        }

        var totalBytes = allRegions.Sum(r => (long)r.Data.Length);
        var writtenBytes = 0L;

        for (var i = 0; i < allRegions.Count; i++)
        {
            var region = allRegions[i];
            for (var offset = 0; offset < region.Data.Length; offset += WriteChunkSize)
            {
                radio.WriteMemory(region.Address + (uint)offset, region.Data.AsSpan(offset, WriteChunkSize));
                writtenBytes += WriteChunkSize;
            }
            onProgress?.Invoke(region, i + 1, allRegions.Count, writtenBytes, totalBytes);
        }
    }

    private static EncodedRegion BuildZoneChannelDefaultsRegion(AnyToneD878Transport radio, IReadOnlyList<EncodedRegion> sharedBlockSubRegions, int zoneCount)
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

        // Every zone defaults to its first channel (position 0) for both VFO A and B.
        Array.Clear(buffer, AnyToneD878CodeplugEncoder.ZoneAChannelOffset, 512);
        Array.Clear(buffer, AnyToneD878CodeplugEncoder.ZoneBChannelOffset, 512);

        // Defense in depth, not the confirmed fix (see AnyToneD878CodeplugEncoder.
        // GeneralUsedBitmapsBlockAddress's remarks for that): if the live "current zone" pointer
        // for VFO A/B is stale from before this write shrank the zone count - or simply out of
        // range for any other reason - reset it to zone 0 rather than leave it pointing past the
        // end of the list. Observed once with the pointer sitting at exactly the last valid index
        // right before the AT-D878UV's zone-scroll rocker switch stopped responding; the actual
        // root cause traced to a different bug, but a stale out-of-range pointer here is never
        // correct regardless, so it's worth normalizing on every write.
        if (zoneCount > 0)
        {
            if (buffer[AnyToneD878CodeplugEncoder.WorkModeZoneAIndexOffset] >= zoneCount)
                buffer[AnyToneD878CodeplugEncoder.WorkModeZoneAIndexOffset] = 0;
            if (buffer[AnyToneD878CodeplugEncoder.WorkModeZoneBIndexOffset] >= zoneCount)
                buffer[AnyToneD878CodeplugEncoder.WorkModeZoneBIndexOffset] = 0;
        }

        // GPS/APRS RadioSettings fields, if the encoder produced any this run - see
        // AnyToneD878CodeplugEncoder.SharedBlockRegionPrefix's remarks.
        foreach (var region in sharedBlockSubRegions)
        {
            var relativeOffset = (int)(region.Address - address);
            region.Data.CopyTo(buffer, relativeOffset);
        }

        return new EncodedRegion("ZoneChannelDefaults (read-modify-write)", address, buffer);
    }

    /// <summary>Reads an entire shared erase block live from the radio, splices the given
    /// sub-regions into it at their correct relative offsets, and returns the merged whole-block
    /// region ready to write back in one piece - the general remedy for "these regions share a
    /// 256KB flash erase block and can't be written standalone or separately."</summary>
    private static EncodedRegion BuildSplicedBlockRegion(AnyToneD878Transport radio, string regionName, uint blockAddress, int blockLength, IReadOnlyList<EncodedRegion> subRegions)
    {
        var buffer = new byte[blockLength];

        var offset = 0;
        while (offset < blockLength)
        {
            var chunkLength = (byte)Math.Min(MaxReadChunkSize, blockLength - offset);
            radio.ReadMemory(blockAddress + (uint)offset, chunkLength).CopyTo(buffer, offset);
            offset += chunkLength;
        }

        foreach (var region in subRegions)
        {
            var relativeOffset = (int)(region.Address - blockAddress);
            region.Data.CopyTo(buffer, relativeOffset);
        }

        return new EncodedRegion(regionName, blockAddress, buffer);
    }
}
