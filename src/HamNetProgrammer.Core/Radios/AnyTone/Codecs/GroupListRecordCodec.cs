namespace HamNetProgrammer.Core.Radios.AnyTone.Codecs;

/// <summary>
/// One Receive Group Call List record (512 bytes), per anytone-flash-tools' at-d878uv_memory.md:
/// up to 64 members as 4-byte little-endian 0-based indices into the TalkGroupList (0xFFFFFFFF =
/// unused), followed by a 16-byte ASCII name. Bytes 272-511 are reserved/zero. Not yet
/// independently cross-checked against real hardware the way ScanListRecordCodec was (see
/// AnyToneD878CodeplugEncoder's remarks) - implemented per the doc's stated layout.
/// </summary>
public sealed record GroupListRecord(string Name, IReadOnlyList<uint> MemberContactIndices);

public static class GroupListRecordCodec
{
    public const int RecordLength = 512;
    public const int MaxMembers = 64;
    private const uint Unused = 0xFFFFFFFF;
    private const int NameOffset = 256;
    private const int NameLength = 16;

    public static byte[] Encode(GroupListRecord record)
    {
        if (record.MemberContactIndices.Count > MaxMembers)
            throw new ArgumentException($"A group list can hold at most {MaxMembers} members, got {record.MemberContactIndices.Count}.", nameof(record));

        var bytes = new byte[RecordLength];
        for (var i = 0; i < MaxMembers; i++)
        {
            var value = i < record.MemberContactIndices.Count ? record.MemberContactIndices[i] : Unused;
            WriteUInt32LE(bytes, i * 4, value);
        }

        AsciiFieldCodec.Encode(record.Name, NameLength).CopyTo(bytes, NameOffset);
        return bytes;
    }

    public static GroupListRecord Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != RecordLength)
            throw new ArgumentException($"Group list record must be {RecordLength} bytes.", nameof(bytes));

        var members = new List<uint>();
        for (var i = 0; i < MaxMembers; i++)
        {
            var value = ReadUInt32LE(bytes, i * 4);
            if (value == Unused) break;
            members.Add(value);
        }

        var name = AsciiFieldCodec.Decode(bytes.Slice(NameOffset, NameLength));
        return new GroupListRecord(name, members);
    }

    private static void WriteUInt32LE(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }

    private static uint ReadUInt32LE(ReadOnlySpan<byte> buffer, int offset) =>
        (uint)(buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16) | (buffer[offset + 3] << 24));
}
