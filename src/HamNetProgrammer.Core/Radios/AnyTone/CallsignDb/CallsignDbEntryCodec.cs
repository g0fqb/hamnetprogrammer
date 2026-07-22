namespace HamNetProgrammer.Core.Radios.AnyTone.CallsignDb;

/// <summary>One individual operator, as sourced from radioid.net for the bulk Callsign Database
/// (distinct from Contacts/TalkGroupList - see CallsignDbEncoder's remarks).</summary>
public sealed record CallsignDbUser(uint DmrId, string Callsign, string Name, string City, string State, string Country);

/// <summary>
/// Encodes one Callsign Database entry - variable length, always Private Call (this database has
/// no group-call concept; it exists purely to resolve an unknown caller's DMR ID to a name).
/// Layout confirmed against two independent sources: reald/anytone-flash-tools' AT-D878UV memory
/// doc, and qdmr's hardware-tested D868UVCallsignDB::EntryElement/D878UV2CallsignDB (same format,
/// just different base addresses for the D878UV II Plus - see CallsignDbEncoder).
///
/// Header (6 bytes): CallType(1, always 0x00=Private) + DmrId(4, big-endian BCD) + flags(1, always
/// 0x00 - friend-list/ring-tone features this project doesn't set). Then Name/City/Callsign/State/
/// Country/Comment as ASCII, each truncated to its max length and always null-terminated - unlike
/// AsciiFieldCodec's fixed-length null-padded fields elsewhere in this project, these are packed
/// one after another with no padding, which is why the record's total size varies per entry.
/// </summary>
public static class CallsignDbEntryCodec
{
    public const int HeaderLength = 6;
    private const int NameMaxLength = 16;
    private const int CityMaxLength = 15;
    private const int CallMaxLength = 8;
    private const int StateMaxLength = 16;
    private const int CountryMaxLength = 16;

    public static int Size(CallsignDbUser user) =>
        HeaderLength
        + FieldLength(user.Name, NameMaxLength)
        + FieldLength(user.City, CityMaxLength)
        + FieldLength(user.Callsign, CallMaxLength)
        + FieldLength(user.State, StateMaxLength)
        + FieldLength(user.Country, CountryMaxLength)
        + 1; // comment: this project never sets one, so just the terminator

    public static byte[] Encode(CallsignDbUser user)
    {
        var bytes = new byte[Size(user)];
        bytes[0] = 0x00; // CallType.Private
        BcdCodec.Encode(user.DmrId, 4).CopyTo(bytes, 1);
        bytes[5] = 0x00; // friend flag off, ring tone off

        var offset = HeaderLength;
        offset = WriteField(bytes, offset, user.Name, NameMaxLength);
        offset = WriteField(bytes, offset, user.City, CityMaxLength);
        offset = WriteField(bytes, offset, user.Callsign, CallMaxLength);
        offset = WriteField(bytes, offset, user.State, StateMaxLength);
        WriteField(bytes, offset, user.Country, CountryMaxLength);
        // Final byte (comment terminator) is already 0x00 from array allocation.
        return bytes;
    }

    private static int FieldLength(string? value, int maxLength) =>
        Math.Min(maxLength, (value ?? "").Length) + 1;

    private static int WriteField(byte[] bytes, int offset, string? value, int maxLength)
    {
        var text = value ?? "";
        var truncated = text.Length > maxLength ? text[..maxLength] : text;
        var ascii = System.Text.Encoding.ASCII.GetBytes(truncated);
        ascii.CopyTo(bytes, offset);
        return offset + ascii.Length + 1; // +1 for the null terminator, already zero
    }
}
