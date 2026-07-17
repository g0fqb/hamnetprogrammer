namespace HamNetProgrammer.Core.Radios.AnyTone.Codecs;

/// <summary>
/// One channel record (64 bytes). Byte layout confirmed empirically against the real device dump
/// (not just the reverse-engineered doc's ambiguous column notation) - see project notes. Scoped
/// to digital simplex channels, which is everything the zone/scan-list/roaming builders produce;
/// analog and repeater-offset fields are implemented per the doc's stated semantics but were not
/// independently cross-checked against real repeater data.
/// </summary>
public sealed record ChannelRecord(
    long RxFrequencyHz,
    long TxFrequencyHz,
    bool Is25kHz,
    byte PowerLevel, // 0=Low, 1=Mid, 2=High, 3=Turbo
    bool IsDigital,
    byte ColorCode,
    byte TimeSlot, // 1 or 2
    uint ContactIndex, // 0-based index into the TalkGroupList
    byte RadioIdIndex, // 0-based index into the RadioIdList
    byte? ScanListIndex, // 0-based index into ScanLists; null = none (0xFF)
    byte? GroupListIndex, // 0-based index into GroupLists; null = none (0xFF)
    string Name,
    double? CtcssEncodeHz = null,
    double? CtcssDecodeHz = null);

public static class ChannelRecordCodec
{
    public const int RecordLength = 64;

    public static byte[] Encode(ChannelRecord ch)
    {
        var bytes = new byte[RecordLength];

        var offsetHz = ch.TxFrequencyHz - ch.RxFrequencyHz;
        var sign = offsetHz switch { > 0 => 0b01, < 0 => 0b10, _ => 0b00 };

        BcdCodec.Encode(ch.RxFrequencyHz / 10, 4).CopyTo(bytes, 0);
        BcdCodec.Encode(Math.Abs(offsetHz) / 10, 4).CopyTo(bytes, 4);

        var mm = (byte)((sign << 6) | (ch.Is25kHz ? 1 << 4 : 0) | ((ch.PowerLevel & 0b11) << 2) | (ch.IsDigital ? 0b01 : 0b00));
        bytes[8] = mm;

        byte ct = 0;
        if (ch.CtcssEncodeHz is not null) ct |= 0b01 << 2;
        if (ch.CtcssDecodeHz is not null) ct |= 0b01;
        bytes[9] = ct;

        bytes[10] = CtcssTones.IndexForTone(ch.CtcssEncodeHz);
        bytes[11] = CtcssTones.IndexForTone(ch.CtcssDecodeHz);
        // 12-15 DCS encode/decode: not modeled, left off (0x00 00 00 00)
        // 16-17 Custom CTCSS, 18-19 reserved: left 0

        WriteUInt32LE(bytes, 20, ch.ContactIndex);
        bytes[24] = ch.RadioIdIndex;
        bytes[25] = 0; // SQ: carrier squelch, PTT ID off
        bytes[26] = 0; // BL: no optional signal, no busy lock
        bytes[27] = ch.ScanListIndex ?? 0xFF;
        bytes[28] = ch.GroupListIndex ?? 0xFF;
        bytes[29] = 0; // 2Tone ID
        bytes[30] = 0; // 5Tone ID
        bytes[31] = 0; // DTMF ID

        bytes[32] = ch.ColorCode;
        bytes[33] = (byte)(ch.TimeSlot == 2 ? 0b1 : 0b0);
        bytes[34] = 0; // AES encryption off

        AsciiFieldCodec.Encode(ch.Name, 16).CopyTo(bytes, 35);

        bytes[51] = 0; // EX: not excluded from roaming
        bytes[52] = 0; // AR
        bytes[53] = 0; // AP
        bytes[54] = 0; // DP
        bytes[55] = 0; // DR
        bytes[56] = 0; // CO: no frequency correction
        bytes[57] = 0xFF; // EN: digital encryption off
        bytes[58] = 0; // KK

        return bytes;
    }

    public static ChannelRecord Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != RecordLength)
            throw new ArgumentException($"Channel record must be {RecordLength} bytes.", nameof(bytes));

        var rxHz = BcdCodec.Decode(bytes[..4]) * 10;
        var offsetHz = BcdCodec.Decode(bytes[4..8]) * 10;
        var mm = bytes[8];
        var sign = (mm >> 6) & 0b11;
        var txHz = sign switch { 0b01 => rxHz + offsetHz, 0b10 => rxHz - offsetHz, _ => rxHz };

        var is25kHz = ((mm >> 4) & 0b1) == 1;
        var power = (byte)((mm >> 2) & 0b11);
        var isDigital = (mm & 0b11) == 0b01;

        var ct = bytes[9];
        var ctcssEncodeMode = (ct >> 2) & 0b11;
        var ctcssDecodeMode = ct & 0b11;
        var ctcssEncode = ctcssEncodeMode == 1 ? CtcssTones.ToneForIndex(bytes[10]) : null;
        var ctcssDecode = ctcssDecodeMode == 1 ? CtcssTones.ToneForIndex(bytes[11]) : null;

        var contactIndex = ReadUInt32LE(bytes, 20);
        var radioIdIndex = bytes[24];
        var scanListIndex = bytes[27];
        var groupListIndex = bytes[28];
        var colorCode = bytes[32];
        var timeSlot = (byte)((bytes[33] & 0b1) == 1 ? 2 : 1);
        var name = AsciiFieldCodec.Decode(bytes[35..51]);

        return new ChannelRecord(
            rxHz, txHz, is25kHz, power, isDigital, colorCode, timeSlot,
            contactIndex, radioIdIndex,
            scanListIndex == 0xFF ? null : scanListIndex,
            groupListIndex == 0xFF ? null : groupListIndex,
            name, ctcssEncode, ctcssDecode);
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
