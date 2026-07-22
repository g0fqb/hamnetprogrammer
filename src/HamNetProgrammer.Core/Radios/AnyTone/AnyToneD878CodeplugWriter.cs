using System.IO.Ports;
using System.Threading;

namespace HamNetProgrammer.Core.Radios.AnyTone;

/// <summary>Outcome of <see cref="AnyToneD878CodeplugWriter.WriteAndVerifySharedBlock"/> - whether
/// the block read back byte-identical to what was written, and how many attempts it took.
/// <paramref name="Verified"/> false is the expected, handled outcome of a real but rare firmware
/// commit failure (see that method's remarks), not necessarily a bug - the caller should surface
/// <paramref name="Error"/> to the user for manual recovery rather than treat it as unexpected.</summary>
public sealed record SharedBlockWriteResult(string RegionName, uint Address, int Length, bool Verified, int Attempts, string? Error);

/// <summary>
/// Writes a full set of encoded regions to the radio. Caller is responsible for
/// StartProgrammingSession()/EndProgrammingSession() around <see cref="WriteSafeRegions"/> - the
/// actual flash commit only happens at END (see AnyToneD878Transport.WriteMemory), and after that
/// the radio drops off USB and re-enumerates ~10-15s later.
///
/// The three shared-erase-block regions (<see cref="BuildZoneChannelDefaultsRegion"/>,
/// <see cref="BuildRoamingBlockRegion"/>, <see cref="BuildGeneralUsedBitmapsBlockRegion"/>) are
/// deliberately NOT written inside that same session - see their remarks and
/// WriteSharedBlockRegionIsolated for why. Each should be written via
/// <see cref="WriteAndVerifySharedBlock"/>, which owns its own Start/End cycles and port
/// re-enumeration waits end to end - the UI/CLI layer just calls it once per shared block.
/// </summary>
public static class AnyToneD878CodeplugWriter
{
    private const int WriteChunkSize = 16;
    private const int MaxReadChunkSize = 0xFF;

    /// <summary>How many times to retry a shared-block write before giving up and reporting
    /// failure - see <see cref="WriteAndVerifySharedBlock"/>'s remarks for why this is needed even
    /// for a fully isolated single-block session.</summary>
    public const int VerifyMaxAttempts = 3;

    /// <summary>Everything except the three shared-block regions (see class remarks) - safe to
    /// write together in one session since none of these share a flash erase block with anything
    /// else in this list.</summary>
    public static void WriteSafeRegions(AnyToneD878Transport radio, IReadOnlyList<EncodedRegion> regions, Action<EncodedRegion, int, int, long, long>? onProgress = null)
    {
        var allRegions = regions
            .Where(r => !r.Name.StartsWith(AnyToneD878CodeplugEncoder.SharedBlockRegionPrefix))
            .Where(r => !AnyToneD878CodeplugEncoder.RoamingBlockRegionNames.Contains(r.Name))
            .Where(r => !AnyToneD878CodeplugEncoder.GeneralUsedBitmapsBlockRegionNames.Contains(r.Name))
            .ToList();

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

    /// <summary>True if this write plan touches any of the three shared-block regions at all -
    /// lets the caller skip the isolated-session dance entirely when there's nothing to do (e.g. a
    /// write with roaming/lists disabled and no zone changes).</summary>
    public static bool HasZoneChannelDefaults(IReadOnlyList<EncodedRegion> regions) =>
        regions.Any(r => r.Name == "Zones" || r.Name.StartsWith(AnyToneD878CodeplugEncoder.SharedBlockRegionPrefix));

    public static bool HasRoamingBlock(IReadOnlyList<EncodedRegion> regions) =>
        regions.Any(r => AnyToneD878CodeplugEncoder.RoamingBlockRegionNames.Contains(r.Name));

    public static bool HasGeneralUsedBitmapsBlock(IReadOnlyList<EncodedRegion> regions) =>
        regions.Any(r => AnyToneD878CodeplugEncoder.GeneralUsedBitmapsBlockRegionNames.Contains(r.Name));

    /// <summary>Writes ONE shared-block region (already built via one of the BuildXxx methods
    /// below) inside its own session: the caller must have just called StartProgrammingSession on
    /// a fresh session (after waiting for the port to reappear from whatever came before), and
    /// must call EndProgrammingSession + wait for re-enumeration immediately after this returns,
    /// before starting the next session.
    ///
    /// Why isolated, not bundled: writing multiple shared-block regions inside ONE session used to
    /// bundle all three together, immediately after each other, all committing together at the
    /// same final END, and that corrupted a block on real hardware. Isolating each into its own
    /// session helps but is NOT sufficient by itself - see WriteAndVerifySharedBlock's remarks for
    /// the actual root cause found on 2026-07-19 (a physical flash program/erase-disturb effect
    /// between ZoneChannelDefaults and its immediate neighbor GeneralUsedBitmapsBlock, fixed by
    /// write ORDER, not just isolation).</summary>
    public static void WriteSharedBlockRegionIsolated(AnyToneD878Transport radio, EncodedRegion region)
    {
        for (var offset = 0; offset < region.Data.Length; offset += WriteChunkSize)
            radio.WriteMemory(region.Address + (uint)offset, region.Data.AsSpan(offset, WriteChunkSize));
    }

    /// <summary>Builds, writes, and verifies ONE shared-block region entirely on its own - the
    /// caller doesn't need to manage sessions or port re-enumeration waits at all, just call this
    /// once per shared block between other operations.
    ///
    /// ROOT CAUSE, confirmed on real hardware 2026-07-19: ZoneChannelDefaultsBlockAddress
    /// (0x02500000) sits at exactly GeneralUsedBitmapsBlockAddress + GeneralUsedBitmapsBlockLength
    /// (0x024C0000 + 0x40000) - the two are physically contiguous erase blocks on the same flash
    /// die. Writing GeneralUsedBitmapsBlock was proven, via a controlled test that wrote ONLY that
    /// block five times in a row and never touched ZoneChannelDefaults' address at all, to corrupt
    /// an already-good ZoneChannelDefaults purely as a program/erase-disturb side effect. A
    /// separate control test (five isolated writes to RoamingBlock, at the unrelated address
    /// 0x01040000) never disturbed it. This means neither session isolation nor read-back
    /// verification of ZoneChannelDefaults ITSELF can fully protect it - if something writes its
    /// physical neighbor afterward, the disturb happens regardless. The actual fix is call ORDER:
    /// callers MUST write ZoneChannelDefaults LAST, after RoamingBlock and GeneralUsedBitmapsBlock,
    /// so nothing touches its neighbor again afterward (see RunWriteCodeplug in the CLI and
    /// OnWriteCodeplugClicked/OnRestoreClicked in the desktop app for the enforced order).
    ///
    /// The read-back verify-and-retry below is still worth keeping as defense in depth against
    /// other, less-understood failure modes (e.g. the original bundled-write corruption from
    /// 2026-07-17), but it is NOT a substitute for correct write order, and on 2026-07-19 it
    /// visibly failed to catch a real corruption once precisely because the disturb happened AFTER
    /// this method had already verified success and moved on to writing the next block.
    ///
    /// <paramref name="build"/> is called once, on the first attempt's session, to do the live
    /// read-modify-write splice (e.g. <see cref="BuildZoneChannelDefaultsRegion"/>).</summary>
    public static SharedBlockWriteResult WriteAndVerifySharedBlock(
        string portName,
        Func<AnyToneD878Transport, EncodedRegion> build,
        Action<string>? onLog = null)
    {
        EncodedRegion? region = null;

        for (var attempt = 1; attempt <= VerifyMaxAttempts; attempt++)
        {
            WaitForPortDropThenReturn(portName);

            using (var radio = new AnyToneD878Transport(portName))
            {
                radio.Open();
                radio.StartProgrammingSession();
                radio.ReadDeviceId();

                if (region is null)
                {
                    onLog?.Invoke("Reading live block and splicing changes...");
                    region = build(radio);
                }

                onLog?.Invoke($"Writing {region.Name} (attempt {attempt}/{VerifyMaxAttempts})...");
                WriteSharedBlockRegionIsolated(radio, region);
                radio.EndProgrammingSession();
            }

            WaitForPortDropThenReturn(portName);

            using (var verifyRadio = new AnyToneD878Transport(portName))
            {
                verifyRadio.Open();
                verifyRadio.StartProgrammingSession();
                verifyRadio.ReadDeviceId();
                var readBack = ReadRange(verifyRadio, region.Address, region.Data.Length);
                verifyRadio.EndProgrammingSession();

                if (readBack.AsSpan().SequenceEqual(region.Data))
                {
                    onLog?.Invoke($"{region.Name} verified correct on attempt {attempt}.");
                    return new SharedBlockWriteResult(region.Name, region.Address, region.Data.Length, true, attempt, null);
                }

                onLog?.Invoke($"{region.Name} did NOT verify on attempt {attempt}/{VerifyMaxAttempts} - retrying with the same bytes.");
            }
        }

        return new SharedBlockWriteResult(region!.Name, region.Address, region.Data.Length, false, VerifyMaxAttempts,
            $"{region.Name} did not verify correctly after {VerifyMaxAttempts} attempts. The radio may need manual recovery - take a fresh backup and check before further writes.");
    }

    /// <summary>Writes a large region across SEVERAL separately-committed sessions instead of one,
    /// then verifies the whole thing read back correctly afterward.
    ///
    /// Real hardware evidence (2026-07-19): writing TalkGroupList in one ~558KB session reported a
    /// completely clean commit (every WriteMemory chunk ACKed, END returned normally, port
    /// re-enumerated fine) but a fresh-session read-back afterward showed NOTHING had actually
    /// changed - not even the first record. This is the same failure class documented back on
    /// 2026-07-17 ("writing the full 10,000-slot/1MB region in one go was silently ACKed but
    /// discarded"; the fix then was writing only the used portion instead of the full buffer) but
    /// the used portion itself has now grown large enough (the talkgroup database has grown since)
    /// to independently hit whatever the real ceiling is. Splitting the SAME content across
    /// multiple smaller commits, each well under any previously-proven-safe size, works around
    /// this without needing to know the exact threshold.
    ///
    /// This is NOT one of the three shared-erase-block regions and has no known adjacent-block
    /// disturb risk (see WriteAndVerifySharedBlock's remarks for that separate problem) - splitting
    /// a large single-purpose region like this into sequential sub-writes is safe here.</summary>
    public static bool WriteRegionChunkedAndVerify(string portName, EncodedRegion region, int maxBytesPerSession, Action<string>? onLog = null)
    {
        var chunkSize = (maxBytesPerSession / WriteChunkSize) * WriteChunkSize; // keep 16-byte aligned
        var chunkCount = (region.Data.Length + chunkSize - 1) / chunkSize;
        var chunkNum = 0;

        for (var offset = 0; offset < region.Data.Length; offset += chunkSize)
        {
            chunkNum++;
            var length = Math.Min(chunkSize, region.Data.Length - offset);
            onLog?.Invoke($"Writing {region.Name} chunk {chunkNum}/{chunkCount} ({length:N0} bytes at offset {offset:N0})...");

            WaitForPortDropThenReturn(portName);
            using var radio = new AnyToneD878Transport(portName);
            radio.Open();
            radio.StartProgrammingSession();
            radio.ReadDeviceId();
            for (var i = 0; i < length; i += WriteChunkSize)
                radio.WriteMemory(region.Address + (uint)(offset + i), region.Data.AsSpan(offset + i, WriteChunkSize));
            radio.EndProgrammingSession();
        }

        onLog?.Invoke($"All {chunkCount} chunk(s) committed. Verifying with a fresh read-only session...");
        WaitForPortDropThenReturn(portName);
        using (var verifyRadio = new AnyToneD878Transport(portName))
        {
            verifyRadio.Open();
            verifyRadio.StartProgrammingSession();
            verifyRadio.ReadDeviceId();
            var readBack = ReadRange(verifyRadio, region.Address, region.Data.Length);
            verifyRadio.EndProgrammingSession();

            var matches = readBack.AsSpan().SequenceEqual(region.Data);
            onLog?.Invoke(matches ? $"{region.Name} verified correct." : $"{region.Name} did NOT verify correctly.");
            return matches;
        }
    }

    /// <summary>Covers six consecutive 256KB-aligned blocks (0x02480000-0x02600000) - the same
    /// range RT Systems was captured writing (via a real USB/serial trace, 2026-07-19) in one
    /// session, strictly ascending, without ever corrupting the radio. Our own tool has only ever
    /// isolated the two blocks it has actual data for within this range (GeneralUsedBitmapsBlock at
    /// 0x024C0000, ZoneChannelDefaults at 0x02500000) - this sweeps the whole proven-safe range
    /// instead, splicing those two in and leaving the other four blocks (0x02480000, 0x02540000,
    /// 0x02580000, 0x025C0000 - not otherwise documented by this project) completely untouched, in
    /// case matching RT Systems' actual write footprint (not just order) is what makes it reliable.</summary>
    public const uint WideSettingsSweepAddress = 0x02480000;
    public const int WideSettingsSweepLength = 0x180000;

    /// <summary>Reads the full sweep range live (must be called on a just-opened session) and
    /// splices in GeneralUsedBitmapsBlock and ZoneChannelDefaults at their real offsets within it -
    /// everything else in the range is carried through unchanged.</summary>
    public static EncodedRegion BuildWideSettingsSweepRegion(AnyToneD878Transport radio, IReadOnlyList<EncodedRegion> allRegions)
    {
        var buffer = ReadRange(radio, WideSettingsSweepAddress, WideSettingsSweepLength);

        var generalUsed = BuildGeneralUsedBitmapsBlockRegion(radio, allRegions);
        generalUsed.Data.CopyTo(buffer, (int)(generalUsed.Address - WideSettingsSweepAddress));

        var zoneDefaults = BuildZoneChannelDefaultsRegion(radio, allRegions);
        zoneDefaults.Data.CopyTo(buffer, (int)(zoneDefaults.Address - WideSettingsSweepAddress));

        return new EncodedRegion("WideSettingsSweep", WideSettingsSweepAddress, buffer);
    }

    /// <summary>Builds and writes the wide settings sweep entirely within ONE session (read, splice,
    /// and write all before ending - matching RT Systems' captured behavior exactly, unlike every
    /// other shared-block method in this class which isolates a single small region), then verifies
    /// with a fresh read-only session afterward.</summary>
    public static bool WriteWideSettingsSweepAndVerify(string portName, IReadOnlyList<EncodedRegion> allRegions, Action<string>? onLog = null)
    {
        WaitForPortDropThenReturn(portName);
        EncodedRegion region;
        using (var radio = new AnyToneD878Transport(portName))
        {
            radio.Open();
            radio.StartProgrammingSession();
            radio.ReadDeviceId();
            onLog?.Invoke("Reading full settings sweep range and splicing changes...");
            region = BuildWideSettingsSweepRegion(radio, allRegions);
            onLog?.Invoke($"Writing {region.Name} ({region.Data.Length:N0} bytes) in one session...");
            for (var offset = 0; offset < region.Data.Length; offset += WriteChunkSize)
                radio.WriteMemory(region.Address + (uint)offset, region.Data.AsSpan(offset, WriteChunkSize));
            radio.EndProgrammingSession();
        }

        onLog?.Invoke("Committed. Verifying with a fresh read-only session...");
        WaitForPortDropThenReturn(portName);
        using (var verifyRadio = new AnyToneD878Transport(portName))
        {
            verifyRadio.Open();
            verifyRadio.StartProgrammingSession();
            verifyRadio.ReadDeviceId();
            var readBack = ReadRange(verifyRadio, region.Address, region.Data.Length);
            verifyRadio.EndProgrammingSession();

            var matches = readBack.AsSpan().SequenceEqual(region.Data);
            onLog?.Invoke(matches ? $"{region.Name} verified correct." : $"{region.Name} did NOT verify correctly.");
            return matches;
        }
    }

    /// <summary>Waits for the given port to drop off USB (if it hasn't already) and reappear,
    /// then a short settle margin before it's safe to reopen - the same re-enumeration dance every
    /// session boundary on this radio needs, centralized here so callers don't duplicate it.</summary>
    private static void WaitForPortDropThenReturn(string portName, int timeoutSeconds = 45)
    {
        var deadline = DateTime.Now + TimeSpan.FromSeconds(timeoutSeconds);
        while (DateTime.Now < deadline && SerialPort.GetPortNames().Contains(portName)) Thread.Sleep(500);
        while (DateTime.Now < deadline && !SerialPort.GetPortNames().Contains(portName)) Thread.Sleep(500);
        if (!SerialPort.GetPortNames().Contains(portName))
            throw new InvalidOperationException($"Port {portName} did not re-enumerate within {timeoutSeconds}s.");
        Thread.Sleep(1500);
    }

    private static byte[] ReadRange(AnyToneD878Transport radio, uint address, int length)
    {
        var buffer = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var chunkLength = (byte)Math.Min(MaxReadChunkSize, length - offset);
            radio.ReadMemory(address + (uint)offset, chunkLength).CopyTo(buffer, offset);
            offset += chunkLength;
        }
        return buffer;
    }

    /// <summary>Reads the live block fresh (must be called on a just-opened session, before
    /// WriteSharedBlockRegionIsolated for this same region) and splices in the zone-defaults +
    /// GPS/APRS sub-regions from this write's plan.</summary>
    public static EncodedRegion BuildZoneChannelDefaultsRegion(AnyToneD878Transport radio, IReadOnlyList<EncodedRegion> allRegions)
    {
        var zoneCount = 0;
        var zonesUsedForCount = allRegions.FirstOrDefault(r => r.Name == "ZonesUsed");
        if (zonesUsedForCount is not null)
            foreach (var b in zonesUsedForCount.Data)
                zoneCount += System.Numerics.BitOperations.PopCount(b);

        var sharedBlockSubRegions = allRegions.Where(r => r.Name.StartsWith(AnyToneD878CodeplugEncoder.SharedBlockRegionPrefix)).ToList();

        var address = AnyToneD878CodeplugEncoder.ZoneChannelDefaultsBlockAddress;
        var length = AnyToneD878CodeplugEncoder.ZoneChannelDefaultsBlockLength;
        var buffer = ReadRange(radio, address, length);

        // Every zone defaults to its first channel (position 0) for both VFO A and B.
        Array.Clear(buffer, AnyToneD878CodeplugEncoder.ZoneAChannelOffset, 512);
        Array.Clear(buffer, AnyToneD878CodeplugEncoder.ZoneBChannelOffset, 512);

        // Defense in depth, not the confirmed fix (see AnyToneD878CodeplugEncoder.
        // GeneralUsedBitmapsBlockAddress's remarks for that): if the live "current zone" pointer
        // for VFO A/B is stale from before this write shrank the zone count - or simply out of
        // range for any other reason - reset it to zone 0 rather than leave it pointing past the
        // end of the list.
        if (zoneCount > 0)
        {
            if (buffer[AnyToneD878CodeplugEncoder.WorkModeZoneAIndexOffset] >= zoneCount)
                buffer[AnyToneD878CodeplugEncoder.WorkModeZoneAIndexOffset] = 0;
            if (buffer[AnyToneD878CodeplugEncoder.WorkModeZoneBIndexOffset] >= zoneCount)
                buffer[AnyToneD878CodeplugEncoder.WorkModeZoneBIndexOffset] = 0;
        }

        foreach (var region in sharedBlockSubRegions)
        {
            var relativeOffset = (int)(region.Address - address);
            region.Data.CopyTo(buffer, relativeOffset);
        }

        return new EncodedRegion("ZoneChannelDefaults (read-modify-write)", address, buffer);
    }

    public static EncodedRegion BuildRoamingBlockRegion(AnyToneD878Transport radio, IReadOnlyList<EncodedRegion> allRegions)
    {
        var roamingSubRegions = allRegions.Where(r => AnyToneD878CodeplugEncoder.RoamingBlockRegionNames.Contains(r.Name)).ToList();
        return BuildSplicedBlockRegion(radio, "RoamingBlock (read-modify-write)",
            AnyToneD878CodeplugEncoder.RoamingBlockAddress, AnyToneD878CodeplugEncoder.RoamingBlockLength, roamingSubRegions);
    }

    public static EncodedRegion BuildGeneralUsedBitmapsBlockRegion(AnyToneD878Transport radio, IReadOnlyList<EncodedRegion> allRegions)
    {
        var usedBitmapSubRegions = allRegions.Where(r => AnyToneD878CodeplugEncoder.GeneralUsedBitmapsBlockRegionNames.Contains(r.Name)).ToList();
        return BuildSplicedBlockRegion(radio, "GeneralUsedBitmapsBlock (read-modify-write)",
            AnyToneD878CodeplugEncoder.GeneralUsedBitmapsBlockAddress, AnyToneD878CodeplugEncoder.GeneralUsedBitmapsBlockLength, usedBitmapSubRegions);
    }

    /// <summary>Reads an entire shared erase block live from the radio, splices the given
    /// sub-regions into it at their correct relative offsets, and returns the merged whole-block
    /// region ready to write back in one piece - the general remedy for "these regions share a
    /// 256KB flash erase block and can't be written standalone or separately."</summary>
    private static EncodedRegion BuildSplicedBlockRegion(AnyToneD878Transport radio, string regionName, uint blockAddress, int blockLength, IReadOnlyList<EncodedRegion> subRegions)
    {
        var buffer = ReadRange(radio, blockAddress, blockLength);

        foreach (var region in subRegions)
        {
            var relativeOffset = (int)(region.Address - blockAddress);
            region.Data.CopyTo(buffer, relativeOffset);
        }

        return new EncodedRegion(regionName, blockAddress, buffer);
    }
}
