using Microsoft.Data.Sqlite;

namespace HamNetProgrammer.Core.Diagnostics;

public sealed record MmdvmMismatch(string Field, string HotspotExpects, string ChannelHas);

public sealed record MmdvmMatchedChannel(long ChannelId, string ChannelName, IReadOnlyList<MmdvmMismatch> Mismatches);

public sealed record MmdvmCrossCheckResult(
    bool FrequencyDataAvailable,
    IReadOnlyList<MmdvmMatchedChannel> MatchedChannels);

/// <summary>
/// Cross-references a parsed MMDVMHost config snapshot against the codeplug database's channels,
/// looking for the two failure modes that this project's own MMDVM-silence saga was actually caused
/// by: colour code and (less commonly, since most hotspots are pre-tuned) frequency/DMR ID mismatch
/// between what the hotspot expects and what a channel is programmed with. A mismatch here means the
/// radio will key up and the hotspot will show zero RF activity - no error, just silence - which is
/// exactly why this needs a direct database comparison rather than scanning the log for an error
/// message that was never going to appear.
/// </summary>
public static class MmdvmCrossCheck
{
    public static MmdvmCrossCheckResult Run(SqliteConnection db, MmdvmConfigSnapshot snapshot)
    {
        if (snapshot.RxFrequencyHz is null || snapshot.TxFrequencyHz is null)
            return new MmdvmCrossCheckResult(false, []);

        // The hotspot's RX is what the radio transmits on, and vice versa - so match a channel
        // whose TxFrequencyHz equals the hotspot's RX and whose RxFrequencyHz equals the hotspot's TX.
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT c.Id, c.Name, c.ColorCode, r.DmrId
            FROM Channels c
            LEFT JOIN RadioIds r ON r.Id = c.RadioIdId
            WHERE c.TxFrequencyHz = $hotspotRx AND c.RxFrequencyHz = $hotspotTx
            ORDER BY c.ChannelNumber;
            """;
        cmd.Parameters.AddWithValue("$hotspotRx", snapshot.RxFrequencyHz.Value);
        cmd.Parameters.AddWithValue("$hotspotTx", snapshot.TxFrequencyHz.Value);

        var matches = new List<MmdvmMatchedChannel>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var channelId = reader.GetInt64(0);
            var channelName = reader.GetString(1);
            uint? channelColorCode = reader.IsDBNull(2) ? null : (uint)reader.GetInt64(2);
            uint? channelDmrId = reader.IsDBNull(3) ? null : (uint)reader.GetInt64(3);

            var mismatches = new List<MmdvmMismatch>();

            if (snapshot.ColorCode.HasValue && channelColorCode.HasValue && snapshot.ColorCode != channelColorCode)
                mismatches.Add(new MmdvmMismatch("Colour Code", $"CC{snapshot.ColorCode}", $"CC{channelColorCode}"));

            if (snapshot.DmrId.HasValue && channelDmrId.HasValue && snapshot.DmrId != channelDmrId)
                mismatches.Add(new MmdvmMismatch("DMR ID", snapshot.DmrId.Value.ToString(), channelDmrId.Value.ToString()));

            matches.Add(new MmdvmMatchedChannel(channelId, channelName, mismatches));
        }

        return new MmdvmCrossCheckResult(true, matches);
    }

    /// <summary>Shared plain-text report, used by both the CLI and the desktop app's Health Check
    /// page so the two never drift into describing the same result differently.</summary>
    public static string Format(MmdvmConfigSnapshot snapshot, MmdvmCrossCheckResult result)
    {
        var lines = new List<string>();

        if (!result.FrequencyDataAvailable)
        {
            lines.Add("Could not find a modem RX/TX frequency in this log.");
            lines.Add("Make sure it's a full MMDVMHost log including its startup banner (\"Modem Parameters\"), not just a tail of recent activity.");
            return string.Join(Environment.NewLine, lines);
        }

        lines.Add($"Hotspot config" + (snapshot.Timestamp is { } ts ? $" (as of {ts:yyyy-MM-dd HH:mm:ss})" : "") + ":");
        lines.Add($"  RX {snapshot.RxFrequencyHz:N0} Hz / TX {snapshot.TxFrequencyHz:N0} Hz" +
                   (snapshot.ColorCode is { } cc ? $", Colour Code {cc}" : ", Colour Code not found in log") +
                   (snapshot.DmrId is { } id ? $", Id {id}" : ""));
        lines.Add("");

        if (result.MatchedChannels.Count == 0)
        {
            lines.Add($"No channel in the codeplug has RX {snapshot.TxFrequencyHz:N0} Hz / TX {snapshot.RxFrequencyHz:N0} Hz " +
                       "(a channel's TX must equal the hotspot's RX, and vice versa).");
            lines.Add("Check you picked the right zone/hotspot, or that the channel's frequencies haven't drifted from what's actually programmed into this hotspot.");
            return string.Join(Environment.NewLine, lines);
        }

        foreach (var channel in result.MatchedChannels)
        {
            if (channel.Mismatches.Count == 0)
            {
                lines.Add($"'{channel.ChannelName}': matches the hotspot config - no mismatch found.");
                continue;
            }

            lines.Add($"'{channel.ChannelName}': {channel.Mismatches.Count} mismatch(es):");
            foreach (var mismatch in channel.Mismatches)
                lines.Add($"  - {mismatch.Field}: hotspot expects {mismatch.HotspotExpects}, channel is set to {mismatch.ChannelHas}");
        }

        return string.Join(Environment.NewLine, lines);
    }
}
