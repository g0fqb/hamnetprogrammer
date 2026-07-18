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
    public static string IconScanLists => FromCodePoint(0xE721);
    public static string IconGroupLists => FromCodePoint(0xE77B);
    public static string IconRoaming => FromCodePoint(0xE774);
    public static string IconAdd => FromCodePoint(0xE710);
    public static string IconRemove => FromCodePoint(0xE738);
    public static string IconUp => FromCodePoint(0xE74A);
    public static string IconDown => FromCodePoint(0xE74B);
    public static string IconRename => FromCodePoint(0xE70F);
    public static string IconRefresh => FromCodePoint(0xE72C);
    public static string IconWrite => FromCodePoint(0xE898);
    public static string IconSave => FromCodePoint(0xE74E);
    public static string IconSettings => FromCodePoint(0xE713);
    public static string IconMail => FromCodePoint(0xE715);
    public static string IconAlert => FromCodePoint(0xE783);

    public static string Heart => FromCodePoint(0x2665);
    public static string Antenna => FromCodePoint(0x1F4E1);
}
