namespace HamNetProgrammer.Core.Radios.AnyTone.Codecs;

public sealed record RadioIdRecord(uint DmrId, string Callsign);

/// <summary>
/// One radio ID record (32 bytes): DmrId(4-byte BCD) + reserved(1) + Callsign(26 ASCII,
/// null-terminated) + reserved(1). Confirmed against the real device dump, which showed a real
/// 7-digit UK-format DMR ID (234xxxx) alongside "G0FQB" - matching the doc's own worked example
/// of the same leading-reserved-byte-then-callsign pattern ("\x00DL9CAT...").
/// </summary>
public static class RadioIdRecordCodec
{
    public const int RecordLength = 32;
    private const int CallsignOffset = 5;
    private const int CallsignLength = 26;

    public static byte[] Encode(RadioIdRecord id)
    {
        var bytes = new byte[RecordLength];
        BcdCodec.Encode(id.DmrId, 4).CopyTo(bytes, 0);
        AsciiFieldCodec.Encode(id.Callsign, CallsignLength).CopyTo(bytes, CallsignOffset);
        return bytes;
    }

    public static RadioIdRecord Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != RecordLength)
            throw new ArgumentException($"Radio ID record must be {RecordLength} bytes.", nameof(bytes));

        var dmrId = (uint)BcdCodec.Decode(bytes[..4]);
        var callsign = AsciiFieldCodec.Decode(bytes.Slice(CallsignOffset, CallsignLength));
        return new RadioIdRecord(dmrId, callsign);
    }
}
