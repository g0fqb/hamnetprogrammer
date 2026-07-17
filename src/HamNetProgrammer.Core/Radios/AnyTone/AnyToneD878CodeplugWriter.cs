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
    private const int ChunkSize = 16;

    public static void Write(AnyToneD878Transport radio, IReadOnlyList<EncodedRegion> regions, Action<EncodedRegion, int, int, long, long>? onProgress = null)
    {
        var totalBytes = regions.Sum(r => (long)r.Data.Length);
        var writtenBytes = 0L;

        for (var i = 0; i < regions.Count; i++)
        {
            var region = regions[i];
            for (var offset = 0; offset < region.Data.Length; offset += ChunkSize)
            {
                radio.WriteMemory(region.Address + (uint)offset, region.Data.AsSpan(offset, ChunkSize));
                writtenBytes += ChunkSize;
            }
            onProgress?.Invoke(region, i + 1, regions.Count, writtenBytes, totalBytes);
        }
    }
}
