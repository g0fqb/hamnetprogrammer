namespace HamNetProgrammer.Core.Radios.AnyTone.Codecs;

/// <summary>
/// One Roaming Channel record (32 bytes), per anytone-flash-tools' at-d878uv_memory.md, byte
/// offsets confirmed against its real hardware dump sample ("Roam Channel 1" decodes correctly
/// at the documented offsets): RX/TX frequency (4-byte BCD, 10Hz resolution, same as
/// ChannelRecordCodec), color code, slot, then a 16-byte name. Bytes 26-31 are reserved/zero.
/// </summary>
public sealed record RoamingChannelRecord(long RxFrequencyHz, long TxFrequencyHz, byte ColorCode, byte Slot, string Name);

public static class RoamingChannelRecordCodec
{
    public const int RecordLength = 32;
    private const int NameOffset = 10;
    private const int NameLength = 16;

    public static byte[] Encode(RoamingChannelRecord ch)
    {
        var bytes = new byte[RecordLength];
        BcdCodec.Encode(ch.RxFrequencyHz / 10, 4).CopyTo(bytes, 0);
        BcdCodec.Encode(ch.TxFrequencyHz / 10, 4).CopyTo(bytes, 4);
        bytes[8] = ch.ColorCode;
        bytes[9] = ch.Slot;
        AsciiFieldCodec.Encode(ch.Name, NameLength).CopyTo(bytes, NameOffset);
        return bytes;
    }

    public static RoamingChannelRecord Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != RecordLength)
            throw new ArgumentException($"Roaming channel record must be {RecordLength} bytes.", nameof(bytes));

        var rxHz = BcdCodec.Decode(bytes[..4]) * 10;
        var txHz = BcdCodec.Decode(bytes[4..8]) * 10;
        var colorCode = bytes[8];
        var slot = bytes[9];
        var name = AsciiFieldCodec.Decode(bytes.Slice(NameOffset, NameLength));
        return new RoamingChannelRecord(rxHz, txHz, colorCode, slot, name);
    }
}
