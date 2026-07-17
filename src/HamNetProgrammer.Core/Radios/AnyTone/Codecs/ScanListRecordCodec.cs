namespace HamNetProgrammer.Core.Radios.AnyTone.Codecs;

public sealed record ScanListRecord(string Name, IReadOnlyList<uint> MemberChannelIndices);

/// <summary>
/// One scan list record (144 bytes): priority channels (unused), look-back/dropout/dwell timing
/// (left at doc-implied defaults), revert mode, a 16-byte name, then up to 50 member channel
/// indices (2-byte little-endian, 0xFFFF = unused). Implemented per the doc's stated layout;
/// not independently cross-checked against real device data (the live radio currently has no
/// scan lists populated to validate against).
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
        bytes[0] = 0x00; // Priority Channel Select: off
        WriteUInt16LE(bytes, 1, Unused); // Priority Channel 1: off
        WriteUInt16LE(bytes, 3, Unused); // Priority Channel 2: off
        bytes[5] = 0x05;  // Look Back Time A: 0.5s (doc default)
        bytes[6] = 0x05;  // Look Back Time B: 0.5s
        bytes[7] = 0x01;  // Dropout Delay Time: 0.1s
        bytes[8] = 0x01;  // Dwell Time: 0.1s
        bytes[9] = 0x00;  // Revert Channel: Selected

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
        return new ScanListRecord(name, members);
    }

    private static void WriteUInt16LE(byte[] buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
    }

    private static ushort ReadUInt16LE(ReadOnlySpan<byte> buffer, int offset) =>
        (ushort)(buffer[offset] | (buffer[offset + 1] << 8));
}
