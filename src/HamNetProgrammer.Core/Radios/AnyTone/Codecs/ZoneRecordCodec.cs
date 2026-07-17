namespace HamNetProgrammer.Core.Radios.AnyTone.Codecs;

/// <summary>
/// A zone's channel membership list (512 bytes = up to 256 slots of 2-byte little-endian,
/// 0-based flat channel indices; 0xFFFF marks an unused slot). Indexing scheme confirmed against
/// the real device dump: index N decodes to the Nth channel record (128 channels per bank,
/// bank = N / 128), producing valid frequency/name data for every sampled index.
/// </summary>
public static class ZoneRecordCodec
{
    public const int RecordLength = 512;
    public const int MaxMembers = 250;
    private const ushort Unused = 0xFFFF;

    public static byte[] Encode(IReadOnlyList<uint> memberChannelIndices)
    {
        if (memberChannelIndices.Count > MaxMembers)
            throw new ArgumentException($"A zone can hold at most {MaxMembers} channels, got {memberChannelIndices.Count}.", nameof(memberChannelIndices));

        var bytes = new byte[RecordLength];
        for (var i = 0; i < RecordLength / 2; i++)
        {
            var value = i < memberChannelIndices.Count ? (ushort)memberChannelIndices[i] : Unused;
            bytes[i * 2] = (byte)value;
            bytes[i * 2 + 1] = (byte)(value >> 8);
        }
        return bytes;
    }

    public static List<uint> Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != RecordLength)
            throw new ArgumentException($"Zone record must be {RecordLength} bytes.", nameof(bytes));

        var members = new List<uint>();
        for (var i = 0; i < RecordLength / 2; i++)
        {
            var value = (ushort)(bytes[i * 2] | (bytes[i * 2 + 1] << 8));
            if (value == Unused) break;
            members.Add(value);
        }
        return members;
    }
}
