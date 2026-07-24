using System.Text.RegularExpressions;

namespace HamNetProgrammer.Core.Diagnostics;

/// <summary>The subset of a hotspot's MMDVMHost startup config actually useful for cross-checking
/// against a codeplug: what it's listening/transmitting on, what colour code it expects, and what
/// DMR ID it's configured to accept as "self". If a log has multiple restarts, this is the state
/// as of the LAST one (most recent config wins).</summary>
public sealed record MmdvmConfigSnapshot(
    uint? DmrId,
    uint? ColorCode,
    uint? RxFrequencyHz,
    uint? TxFrequencyHz,
    DateTime? Timestamp);

/// <summary>
/// Parses an MMDVMHost log file's startup configuration banner - not per-transmission activity
/// lines. Deliberately doesn't try to detect "colour code mismatch" from RF activity lines, because
/// there isn't one to detect: a colour-code or frequency mismatch is filtered by the modem firmware
/// before MMDVMHost ever sees the frame, so a real mismatch shows up as silence, not an error message
/// (see this project's own history with the MMDVM-silence saga - CC/frequency were the exact two
/// causes there too). Cross-checking the startup banner against the codeplug directly is the only
/// way to catch this from a log file.
///
/// Field format strings verified against g4klx/MMDVM-Host source (2026-07-24), not guessed:
/// MMDVM-Host.cpp's "DMR RF Parameters" section prints "    Id: %u" then "    Color Code: %u"; its
/// "Modem Parameters" section prints "    TX Frequency: %uHz (%uHz)" and "    RX Frequency: %uHz
/// (%uHz)" (base frequency, then frequency+offset in parens - this parser takes the base value).
/// Log.cpp's line prefix is "%c: %04u-%02u-%02u %02u:%02u:%02u.%03u " (level letter, colon, space,
/// timestamp, space) on every platform.
/// </summary>
public static class MmdvmLogParser
{
    // The trailing separator must be a SINGLE space, not \s+ - a greedy \s+ here would also eat an
    // indented field line's leading whitespace, making every line look like a section header. Bit
    // us during testing (verified with a synthetic log before trusting this against a real one).
    private static readonly Regex LogPrefix = new(
        @"^\S:\s+(?<ts>\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}\.\d{3}) ",
        RegexOptions.Compiled);

    private static readonly Regex IdField = new(@"^Id:\s*(\d+)$", RegexOptions.Compiled);
    private static readonly Regex ColorCodeField = new(@"^Color Code:\s*(\d+)$", RegexOptions.Compiled);
    private static readonly Regex TxFrequencyField = new(@"^TX Frequency:\s*(\d+)Hz", RegexOptions.Compiled);
    private static readonly Regex RxFrequencyField = new(@"^RX Frequency:\s*(\d+)Hz", RegexOptions.Compiled);

    public static MmdvmConfigSnapshot Parse(string logText)
    {
        uint? dmrId = null, colorCode = null, rxFrequencyHz = null, txFrequencyHz = null;
        DateTime? timestamp = null;
        string? currentSection = null;

        foreach (var rawLine in logText.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var prefixMatch = LogPrefix.Match(line);
            if (!prefixMatch.Success) continue;

            var rest = line[prefixMatch.Length..];
            if (rest.Length == 0) continue;

            // Section headers are un-indented; every field within a section is indented (4 spaces
            // in practice, but only whitespace-vs-not actually matters here).
            if (!char.IsWhiteSpace(rest[0]))
            {
                currentSection = rest.Trim();
                continue;
            }

            var field = rest.Trim();
            Match m;
            switch (currentSection)
            {
                case "DMR RF Parameters":
                    if ((m = IdField.Match(field)).Success)
                    {
                        dmrId = uint.Parse(m.Groups[1].Value);
                        timestamp = ParseTimestamp(prefixMatch.Groups["ts"].Value);
                    }
                    else if ((m = ColorCodeField.Match(field)).Success)
                    {
                        colorCode = uint.Parse(m.Groups[1].Value);
                        timestamp = ParseTimestamp(prefixMatch.Groups["ts"].Value);
                    }
                    break;

                case "Modem Parameters":
                    if ((m = TxFrequencyField.Match(field)).Success)
                        txFrequencyHz = uint.Parse(m.Groups[1].Value);
                    else if ((m = RxFrequencyField.Match(field)).Success)
                        rxFrequencyHz = uint.Parse(m.Groups[1].Value);
                    break;
            }
        }

        return new MmdvmConfigSnapshot(dmrId, colorCode, rxFrequencyHz, txFrequencyHz, timestamp);
    }

    private static DateTime? ParseTimestamp(string text) =>
        DateTime.TryParse(text, out var dt) ? dt : null;
}
