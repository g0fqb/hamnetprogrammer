namespace HamNetProgrammer.Desktop.Utils;

/// <summary>
/// Builds Unicode characters (icon glyphs, symbols) from their hex code point at runtime rather
/// than embedding them as literal characters in source files. Literal non-ASCII characters in
/// this project's .cs files have shown real double-encoding corruption during editing (bytes
/// silently mangled into different, wrong characters) - keeping source files pure ASCII and
/// building the actual characters here at runtime sidesteps that risk entirely.
/// </summary>
public static class Glyphs
{
    public static string FromCodePoint(int codePoint) => char.ConvertFromUtf32(codePoint);

    // Segoe Fluent Icons (PUA)
    public static string IconZones => FromCodePoint(0xE8A9);
    public static string IconRadio => FromCodePoint(0xE704);
    public static string IconHelp => FromCodePoint(0xE897);
    public static string IconCheck => FromCodePoint(0xE73E);
    public static string IconWarning => FromCodePoint(0xE7BA);

    public static string Heart => FromCodePoint(0x2665);
    public static string Antenna => FromCodePoint(0x1F4E1);
}
