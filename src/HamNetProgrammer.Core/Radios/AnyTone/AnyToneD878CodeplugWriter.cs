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

        // Zone A/B Channel defaults share a 256KB flash erase block with several settings sections
        // this encoder doesn't otherwise touch (power-on message, APRS, DTMF list, etc.) - writing
        // them as standalone regions would erase the whole block and silently wipe everything else
        // sharing it. Read-modify-write the live block instead. See AnyToneD878CodeplugEncoder's
        // remarks for the full story (this was found and fixed after it happened on real hardware).
        if (allRegions.Any(r => r.Name == "Zones"))
            allRegions.Add(BuildZoneChannelDefaultsRegion(radio));

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

    private static EncodedRegion BuildZoneChannelDefaultsRegion(AnyToneD878Transport radio)
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

        return new EncodedRegion("ZoneChannelDefaults (read-modify-write)", address, buffer);
    }
}
