namespace HamNetProgrammer.Core.Radios.AnyTone;

/// <summary>
/// BCD helper for the AT-D878UV's numeric fields (frequency, talkgroup/DMR IDs). Validated
/// against the real device dump: bytes are in natural digit order (byte 0 holds the most
/// significant digit pair), not reversed - e.g. 433.45000 MHz (4334500 * 10Hz) is stored as
/// 43 34 50 00, and talkgroup 2350 is stored as 00 23 50 (3-byte field).
/// </summary>
public static class BcdCodec
{
    public static byte[] Encode(long value, int byteCount)
    {
        var digits = value.ToString().PadLeft(byteCount * 2, '0');
        if (digits.Length > byteCount * 2)
            throw new ArgumentOutOfRangeException(nameof(value), $"{value} does not fit in {byteCount} BCD bytes.");

        var result = new byte[byteCount];
        for (var i = 0; i < byteCount; i++)
        {
            var hi = digits[i * 2] - '0';
            var lo = digits[i * 2 + 1] - '0';
            result[i] = (byte)((hi << 4) | lo);
        }
        return result;
    }

    public static long Decode(ReadOnlySpan<byte> bytes)
    {
        long value = 0;
        foreach (var b in bytes)
        {
            var hi = (b >> 4) & 0xF;
            var lo = b & 0xF;
            value = value * 100 + hi * 10 + lo;
        }
        return value;
    }
}
