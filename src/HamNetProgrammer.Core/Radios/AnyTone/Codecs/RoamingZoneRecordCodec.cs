namespace HamNetProgrammer.Core.Radios.AnyTone.Codecs;

/// <summary>
/// One Roaming Zone record (128 bytes), per anytone-flash-tools' at-d878uv_memory.md: up to 64
/// members as 1-byte 0-based indices into the Roaming Channels list (0xFF = unused/terminator -
/// the doc's real dump sample shows a single member followed immediately by 0xFF fill), then a
/// 16-byte name starting exactly at offset 64 (confirmed against the sample: "Roam Zone HH" lands
/// at 0x01043040, which is 0x40 = 64 bytes into a record starting at 0x01043000). Bytes 80-127
/// are reserved/zero.
/// </summary>
public sealed record RoamingZoneRecord(string Name, IReadOnlyList<byte> MemberRoamingChannelIndices);

public static class RoamingZoneRecordCodec
{
    public const int RecordLength = 128;
    public const int MaxMembers = 64;
    private const byte Unused = 0xFF;
    private const int NameOffset = 64;
    private const int NameLength = 16;

    public static byte[] Encode(RoamingZoneRecord record)
    {
        if (record.MemberRoamingChannelIndices.Count > MaxMembers)
            throw new ArgumentException($"A roaming zone can hold at most {MaxMembers} channels, got {record.MemberRoamingChannelIndices.Count}.", nameof(record));

        var bytes = new byte[RecordLength];
        for (var i = 0; i < MaxMembers; i++)
            bytes[i] = i < record.MemberRoamingChannelIndices.Count ? record.MemberRoamingChannelIndices[i] : Unused;

        AsciiFieldCodec.Encode(record.Name, NameLength).CopyTo(bytes, NameOffset);
        return bytes;
    }

    public static RoamingZoneRecord Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != RecordLength)
            throw new ArgumentException($"Roaming zone record must be {RecordLength} bytes.", nameof(bytes));

        var members = new List<byte>();
        for (var i = 0; i < MaxMembers; i++)
        {
            if (bytes[i] == Unused) break;
            members.Add(bytes[i]);
        }

        var name = AsciiFieldCodec.Decode(bytes.Slice(NameOffset, NameLength));
        return new RoamingZoneRecord(name, members);
    }
}
