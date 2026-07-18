namespace HamNetProgrammer.Core.Radios.AnyTone.Codecs;

/// <summary>Revert Channel modes - byte values confirmed independently by both anytone-flash-tools
/// and qdmr's reverse-engineering docs.</summary>
public enum ScanListRevertMode : byte
{
    Selected = 0x00,
    SelectedTalkback = 0x01,
    PriorityChannel1 = 0x02,
    PriorityChannel2 = 0x03,
    LastCalled = 0x04,
    LastUsed = 0x05,
    PriorityChannel1Talkback = 0x06,
    PriorityChannel2Talkback = 0x07,
}

public sealed record ScanListRecord(
    string Name,
    IReadOnlyList<uint> MemberChannelIndices,
    uint? PriorityChannel1Index = null,
    uint? PriorityChannel2Index = null,
    double LookBackTimeA = 0.5,
    double LookBackTimeB = 0.5,
    double DropoutDelayTime = 0.1,
    double DwellTime = 0.1,
    ScanListRevertMode RevertMode = ScanListRevertMode.Selected);

/// <summary>
/// One scan list record (144 bytes): priority channels, look-back/dropout/dwell timing, revert
/// mode, a 16-byte name, then up to 50 member channel indices (2-byte little-endian, 0xFFFF =
/// unused). Byte layout confirmed against anytone-flash-tools' and qdmr's independent
/// reverse-engineering docs (both agree): timing bytes are seconds*10 (range 0.1-5.0s, i.e.
/// 1-50 raw), Priority Channel Select is derived from which priority channels are set
/// (0=off, 1=PC1 only, 2=PC2 only, 3=both) rather than being an independent field.
/// </summary>
public static class ScanListRecordCodec
{
    public const int RecordLength = 144;
    public const int MaxMembers = 50;
    private const int NameOffset = 10;
    private const int NameLength = 16;
    private const int MembersOffset = 26;
    private const ushort Unused = 0xFFFF;

    public static byte[] Encode(ScanListRecord sl)
    {
        if (sl.MemberChannelIndices.Count > MaxMembers)
            throw new ArgumentException($"A scan list can hold at most {MaxMembers} channels, got {sl.MemberChannelIndices.Count}.", nameof(sl));

        var bytes = new byte[RecordLength];

        byte prioritySelect = (sl.PriorityChannel1Index, sl.PriorityChannel2Index) switch
        {
            (not null, not null) => 3,
            (null, not null) => 2,
            (not null, null) => 1,
            (null, null) => 0,
        };
        bytes[0] = prioritySelect;
        WriteUInt16LE(bytes, 1, (ushort)(sl.PriorityChannel1Index ?? Unused));
        WriteUInt16LE(bytes, 3, (ushort)(sl.PriorityChannel2Index ?? Unused));
        bytes[5] = SecondsToRaw(sl.LookBackTimeA);
        bytes[6] = SecondsToRaw(sl.LookBackTimeB);
        bytes[7] = SecondsToRaw(sl.DropoutDelayTime);
        bytes[8] = SecondsToRaw(sl.DwellTime);
        bytes[9] = (byte)sl.RevertMode;

        AsciiFieldCodec.Encode(sl.Name, NameLength).CopyTo(bytes, NameOffset);

        for (var i = 0; i < MaxMembers; i++)
        {
            var value = i < sl.MemberChannelIndices.Count ? (ushort)sl.MemberChannelIndices[i] : Unused;
            WriteUInt16LE(bytes, MembersOffset + i * 2, value);
        }

        return bytes;
    }

    public static ScanListRecord Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != RecordLength)
            throw new ArgumentException($"Scan list record must be {RecordLength} bytes.", nameof(bytes));

        var name = AsciiFieldCodec.Decode(bytes.Slice(NameOffset, NameLength));
        var members = new List<uint>();
        for (var i = 0; i < MaxMembers; i++)
        {
            var value = ReadUInt16LE(bytes, MembersOffset + i * 2);
            if (value == Unused) break;
            members.Add(value);
        }

        var priority1 = ReadUInt16LE(bytes, 1);
        var priority2 = ReadUInt16LE(bytes, 3);

        return new ScanListRecord(
            name,
            members,
            PriorityChannel1Index: priority1 == Unused ? null : priority1,
            PriorityChannel2Index: priority2 == Unused ? null : priority2,
            LookBackTimeA: RawToSeconds(bytes[5]),
            LookBackTimeB: RawToSeconds(bytes[6]),
            DropoutDelayTime: RawToSeconds(bytes[7]),
            DwellTime: RawToSeconds(bytes[8]),
            RevertMode: (ScanListRevertMode)bytes[9]);
    }

    private static byte SecondsToRaw(double seconds)
    {
        var raw = (int)Math.Round(Math.Clamp(seconds, 0.1, 5.0) * 10);
        return (byte)Math.Clamp(raw, 1, 50);
    }

    private static double RawToSeconds(byte raw) => raw / 10.0;

    private static void WriteUInt16LE(byte[] buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
    }

    private static ushort ReadUInt16LE(ReadOnlySpan<byte> buffer, int offset) =>
        (ushort)(buffer[offset] | (buffer[offset + 1] << 8));
}
