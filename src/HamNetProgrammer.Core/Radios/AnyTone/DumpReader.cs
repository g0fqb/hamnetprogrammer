using System.Globalization;

namespace HamNetProgrammer.Core.Radios.AnyTone;

/// <summary>Reads a raw memory dump produced by <see cref="AnyToneD878MemoryDumper"/> via its manifest CSV.</summary>
public sealed class DumpReader
{
    private readonly byte[] _data;
    private readonly Dictionary<string, (uint Address, long FileOffset, int Length, bool Succeeded)> _regions = new();

    private DumpReader(byte[] data) => _data = data;

    public static DumpReader Load(string binaryPath, string manifestPath)
    {
        var reader = new DumpReader(File.ReadAllBytes(binaryPath));
        var lines = File.ReadAllLines(manifestPath);
        for (var i = 1; i < lines.Length; i++) // skip header
        {
            var fields = lines[i].Split(',');
            if (fields.Length < 5) continue;
            var name = fields[0];
            var address = Convert.ToUInt32(fields[1], 16);
            var length = int.Parse(fields[2], CultureInfo.InvariantCulture);
            var fileOffset = long.Parse(fields[3], CultureInfo.InvariantCulture);
            var succeeded = fields.Length > 4 && bool.TryParse(fields[4], out var s) && s;
            reader._regions[name] = (address, fileOffset, length, succeeded);
        }
        return reader;
    }

    public bool HasRegion(string name) => _regions.ContainsKey(name);

    /// <summary>False for a region whose read failed when this dump was taken (padded with zeros,
    /// not real data) - callers comparing two dumps should skip these rather than treat padding as
    /// a genuine mismatch or, worse, a genuine match.</summary>
    public bool RegionSucceeded(string name) => _regions.TryGetValue(name, out var r) && r.Succeeded;

    public IReadOnlyCollection<string> RegionNames => _regions.Keys;

    public uint GetRegionAddress(string name) => _regions[name].Address;

    public ReadOnlySpan<byte> GetRegion(string name)
    {
        var (_, offset, length, _) = _regions[name];
        return _data.AsSpan((int)offset, length);
    }

    /// <summary>Gets a channel's 64-byte record by its 0-based flat index (128 channels per bank, per confirmed layout).</summary>
    public ReadOnlySpan<byte> GetChannelRecord(uint flatIndex0Based)
    {
        var bank = flatIndex0Based / 128;
        var slot = flatIndex0Based % 128;
        var (_, offset, _, _) = _regions[$"ChannelBank[{bank}]"];
        return _data.AsSpan((int)offset + (int)slot * 64, 64);
    }

    // Banked, matching AnyToneD878CodeplugEncoder.EncodeTalkGroups' ContactsPerBank=1000 exactly - see
    // AnyToneD878MemoryMap's "TalkGroupList[{bank}]" remarks for why a flat offset used to be wrong
    // past index 999.
    public ReadOnlySpan<byte> GetTalkGroupRecord(int index0Based)
    {
        var bank = index0Based / 1000;
        var slot = index0Based % 1000;
        var (_, offset, _, _) = _regions[$"TalkGroupList[{bank}]"];
        return _data.AsSpan((int)offset + slot * 100, 100);
    }

    public ReadOnlySpan<byte> GetRadioIdRecord(int index0Based)
    {
        var (_, offset, _, _) = _regions["RadioIdList"];
        return _data.AsSpan((int)offset + index0Based * 32, 32);
    }

    public ReadOnlySpan<byte> GetZoneRecord(int index0Based)
    {
        var (_, offset, _, _) = _regions["Zones"];
        return _data.AsSpan((int)offset + index0Based * 512, 512);
    }

    /// <summary>Per-slot named region ("ScanList[N]", 1-based in the name) - already addressed
    /// individually by AnyToneD878MemoryMap/AnyToneD878CodeplugEncoder, so this is just a thin
    /// wrapper for symmetry with the other Get*Record accessors.</summary>
    public ReadOnlySpan<byte> GetScanListRecord(int index0Based) => GetRegion($"ScanList[{index0Based + 1}]");

    /// <summary>Per-slot named region ("GroupList[N]", 1-based in the name) - see GetScanListRecord's remarks.</summary>
    public ReadOnlySpan<byte> GetGroupListRecord(int index0Based) => GetRegion($"GroupList[{index0Based + 1}]");

    public ReadOnlySpan<byte> GetRoamingChannelRecord(int index0Based)
    {
        var (_, offset, _, _) = _regions["RoamingChannels"];
        return _data.AsSpan((int)offset + index0Based * 32, 32);
    }

    public ReadOnlySpan<byte> GetRoamingZoneRecord(int index0Based)
    {
        var (_, offset, _, _) = _regions["RoamingZones"];
        return _data.AsSpan((int)offset + index0Based * 128, 128);
    }
}
