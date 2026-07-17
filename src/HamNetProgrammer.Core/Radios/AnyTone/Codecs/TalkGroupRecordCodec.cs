namespace HamNetProgrammer.Core.Radios.AnyTone.Codecs;

public sealed record TalkGroupRecord(string Name, uint DmrId, bool IsGroupCall = true);

/// <summary>
/// One talkgroup/contact record (100 bytes). Byte layout confirmed empirically against the real
/// device dump - the doc's own column notation was ambiguous/wrong here (it implied a 4-byte ID
/// immediately after a 16-byte name; the real layout is CallType(1) + Name(35, ASCII, null-padded)
/// + Id(3-byte BCD at offset 36) + CallAlert(1) + reserved(60)). Cross-checked against two
/// independently identifiable real talkgroups (2348 "SOARC", 2350 "United K") which both decoded
/// to their exact known numeric ID.
/// </summary>
public static class TalkGroupRecordCodec
{
    public const int RecordLength = 100;
    private const int NameLength = 35;
    private const int IdOffset = 36;
    private const int IdLength = 3;

    public static byte[] Encode(TalkGroupRecord tg)
    {
        var bytes = new byte[RecordLength];
        bytes[0] = (byte)(tg.IsGroupCall ? 0x01 : 0x00);
        AsciiFieldCodec.Encode(tg.Name, NameLength).CopyTo(bytes, 1);
        BcdCodec.Encode(tg.DmrId, IdLength).CopyTo(bytes, IdOffset);
        return bytes;
    }

    public static TalkGroupRecord Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != RecordLength)
            throw new ArgumentException($"Talkgroup record must be {RecordLength} bytes.", nameof(bytes));

        var isGroupCall = bytes[0] == 0x01;
        var name = AsciiFieldCodec.Decode(bytes.Slice(1, NameLength));
        var dmrId = (uint)BcdCodec.Decode(bytes.Slice(IdOffset, IdLength));
        return new TalkGroupRecord(name, dmrId, isGroupCall);
    }
}
