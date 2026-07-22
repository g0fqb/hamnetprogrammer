namespace HamNetProgrammer.Core.Radios.AnyTone;

public sealed record DumpComparisonResult(int RegionsCompared, IReadOnlyList<string> MismatchedRegionNames, IReadOnlyList<string> SkippedRegionNames)
{
    public bool AllMatch => MismatchedRegionNames.Count == 0;
}

/// <summary>Compares two memory dumps region-by-region, matched by name - the general-purpose,
/// automated version of the "diff two full baselines" technique that found the ContactIdTable and
/// erase-block-disturb bugs by hand (see project history). Used to actually PROVE a radio matches
/// its pre-write baseline (e.g. after "nothing was recorded as written" or after a restore
/// completes) rather than just inferring it from the absence of a recorded write.
///
/// Regions present in one dump but not the other (an older baseline predating a memory-map
/// widening, most likely) are reported as skipped, not silently ignored or treated as mismatches -
/// there's nothing meaningful to compare them against. Regions whose read failed in either dump
/// (padded with zeros, not real data - see DumpReader.RegionSucceeded) are skipped the same way.</summary>
public static class DumpComparer
{
    // WorkModeZoneAIndex/BIndex (see AnyToneD878CodeplugEncoder's remarks) record which zone is
    // CURRENTLY selected on the radio's own front panel - real hardware confirmed, 2026-07-22: a
    // restore correctly reported "2 region(s) still differ" here after the radio's own live state
    // moved between the pre-write baseline and the post-restore verification dump, which looked
    // exactly like corruption (byte 0x1F of the ZoneChannelDefaults block, historically
    // password-flag-adjacent) until traced to this specific known-dynamic field. These two byte
    // addresses are the ONLY ones excluded - a genuine corruption anywhere else in the same region
    // (including the actual password flag, 7 bytes earlier) still gets caught normally.
    private static readonly HashSet<uint> KnownDynamicAddresses =
    [
        AnyToneD878CodeplugEncoder.ZoneChannelDefaultsBlockAddress + AnyToneD878CodeplugEncoder.WorkModeZoneAIndexOffset,
        AnyToneD878CodeplugEncoder.ZoneChannelDefaultsBlockAddress + AnyToneD878CodeplugEncoder.WorkModeZoneBIndexOffset,
    ];

    public static DumpComparisonResult Compare(DumpReader before, DumpReader after)
    {
        var mismatched = new List<string>();
        var skipped = new List<string>();
        var compared = 0;

        foreach (var name in before.RegionNames)
        {
            if (!after.HasRegion(name) || !before.RegionSucceeded(name) || !after.RegionSucceeded(name))
            {
                skipped.Add(name);
                continue;
            }

            var beforeData = before.GetRegion(name);
            var afterData = after.GetRegion(name);
            if (beforeData.Length != afterData.Length)
            {
                skipped.Add(name);
                continue;
            }

            compared++;

            var baseAddress = before.GetRegionAddress(name);
            for (var i = 0; i < beforeData.Length; i++)
            {
                if (beforeData[i] == afterData[i]) continue;
                if (KnownDynamicAddresses.Contains(baseAddress + (uint)i)) continue;
                mismatched.Add(name);
                break;
            }
        }

        return new DumpComparisonResult(compared, mismatched, skipped);
    }
}
