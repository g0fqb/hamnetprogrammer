using System.Text;

namespace HamNetProgrammer.Core.Radios.AnyTone;

/// <summary>Fixed-length, null-padded ASCII text field, as used throughout the AT-D878UV's memory layout.</summary>
public static class AsciiFieldCodec
{
    public static byte[] Encode(string text, int length)
    {
        var bytes = new byte[length];
        var encoded = Encoding.ASCII.GetBytes(text ?? string.Empty);
        var copyLength = Math.Min(encoded.Length, length);
        Array.Copy(encoded, bytes, copyLength);
        return bytes;
    }

    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        var nullIndex = bytes.IndexOf((byte)0);
        var slice = nullIndex >= 0 ? bytes[..nullIndex] : bytes;
        return Encoding.ASCII.GetString(slice);
    }
}
