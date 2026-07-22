using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace HamNetProgrammer.Core.Radios.AnyTone;

public sealed record RegionDumpResult(MemoryRegion Region, long FileOffset, bool Succeeded, string? Error);

/// <summary>Reads every region in <see cref="AnyToneD878MemoryMap"/> and writes it to a raw binary file plus a CSV manifest.</summary>
public static class AnyToneD878MemoryDumper
{
    private const int MaxChunkLength = 0xFF;

    /// <param name="onRegionStarted">(region, index, total, bytesDoneSoFar, totalBytes) - called
    /// once per region, after it finishes. Report progress by BYTES, not just region index/total:
    /// the "six-block supercluster" raw-fill regions added 2026-07-22 are large (up to ~260,000
    /// bytes each) and sit at the end of the region list, so a region-COUNT-based progress bar
    /// crawls through the last few percent for a disproportionate amount of real time - confirmed
    /// directly, this looked like a hang on real hardware even though it was still progressing
    /// normally.</param>
    public static IReadOnlyList<RegionDumpResult> Dump(
        AnyToneD878Transport radio,
        string binaryOutputPath,
        string manifestOutputPath,
        Action<MemoryRegion, int, int, long, long>? onRegionStarted = null)
    {
        var regions = AnyToneD878MemoryMap.GetBaselineRegions();
        var results = new List<RegionDumpResult>(regions.Count);
        var totalBytes = regions.Sum(r => (long)r.Length);
        var bytesDone = 0L;

        using var output = new FileStream(binaryOutputPath, FileMode.Create, FileAccess.Write);

        for (var i = 0; i < regions.Count; i++)
        {
            var region = regions[i];
            var fileOffset = output.Position;

            try
            {
                var remaining = region.Length;
                var address = region.Address;
                while (remaining > 0)
                {
                    var chunkLength = (byte)Math.Min(MaxChunkLength, remaining);
                    var data = radio.ReadMemory(address, chunkLength);
                    output.Write(data, 0, data.Length);
                    address += chunkLength;
                    remaining -= chunkLength;
                }
                results.Add(new RegionDumpResult(region, fileOffset, true, null));
            }
            catch (Exception ex)
            {
                // Pad the file so later offsets in the manifest stay meaningful, and keep going -
                // one unreadable region shouldn't abort the whole baseline dump.
                output.Position = fileOffset + region.Length;
                output.SetLength(Math.Max(output.Length, output.Position));
                results.Add(new RegionDumpResult(region, fileOffset, false, ex.Message));
            }

            bytesDone += region.Length;
            onRegionStarted?.Invoke(region, i + 1, regions.Count, bytesDone, totalBytes);
        }

        WriteManifest(manifestOutputPath, results);
        return results;
    }

    private static void WriteManifest(string path, IReadOnlyList<RegionDumpResult> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Name,Address,Length,FileOffset,Succeeded,Error");
        foreach (var r in results)
        {
            var error = r.Error?.Replace(',', ';') ?? "";
            sb.AppendLine($"{r.Region.Name},0x{r.Region.Address:x8},{r.Region.Length},{r.FileOffset},{r.Succeeded},{error}");
        }
        File.WriteAllText(path, sb.ToString());
    }
}
