namespace HamNetProgrammer.Core.Radios.AnyTone;

/// <summary>
/// Standard CTCSS tone table. The AT-D878UV references a tone by its 1-based position in this
/// ascending list (0x01 -> 67.0 Hz, 0x0f -> 107.2 Hz - both confirmed against the memory layout
/// doc's worked examples). 0x33 (51) marks "Custom", which uses the channel's separate 2-byte
/// Custom CTCSS field instead of this table.
/// </summary>
public static class CtcssTones
{
    public const byte CustomIndex = 0x33;

    public static readonly double[] StandardTonesHz =
    [
        67.0, 69.3, 71.9, 74.4, 77.0, 79.7, 82.5, 85.4, 88.5, 91.5,
        94.8, 97.4, 100.0, 103.5, 107.2, 110.9, 114.8, 118.8, 123.0, 127.3,
        131.8, 136.5, 141.3, 146.2, 151.4, 156.7, 159.8, 162.2, 165.5, 167.9,
        171.3, 173.8, 177.3, 179.9, 183.5, 186.2, 189.9, 192.8, 196.6, 199.5,
        203.5, 206.5, 210.7, 218.1, 225.7, 229.1, 233.6, 241.8, 250.3, 254.1,
    ];

    public static byte IndexForTone(double? toneHz)
    {
        if (toneHz is null) return 0;
        for (var i = 0; i < StandardTonesHz.Length; i++)
            if (Math.Abs(StandardTonesHz[i] - toneHz.Value) < 0.05)
                return (byte)(i + 1);
        return CustomIndex;
    }

    public static double? ToneForIndex(byte index)
    {
        if (index == 0 || index == CustomIndex) return null;
        var i = index - 1;
        return i >= 0 && i < StandardTonesHz.Length ? StandardTonesHz[i] : null;
    }
}
