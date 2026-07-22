using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Data.Sqlite;
using HamNetProgrammer.Core.Data;

namespace HamNetProgrammer.Desktop.Utils;

/// <summary>
/// Creates a brand-new channel from a talkgroup, the way CPS/RT Systems actually work: pick (or
/// type a new) talkgroup, and the channel's frequency/mode/colour-code/slot/etc. are inherited
/// from the zone's existing channels (every channel in a hotspot zone shares the same frequency in
/// this codeplug's model - only the talkgroup differs) rather than asking the user to re-enter
/// values that are already established. Complements ChannelPickerDialog's "add an existing unused
/// channel" flow rather than replacing it - both are useful, this one just matches how a zone is
/// actually built up day to day.
/// </summary>
public static class NewChannelDialog
{
    public static async Task<bool> ShowAsync(XamlRoot xamlRoot, SqliteConnection db, long zoneId, string zoneName)
    {
        // Pattern channel: any existing member of this zone, used as the template for every field
        // except Name/ChannelNumber/ContactId. Falls back to sane digital-hotspot defaults if the
        // zone is still empty (nothing to copy a pattern from yet).
        using var patternCmd = db.CreateCommand();
        patternCmd.CommandText = """
            SELECT c.Mode, c.RxFrequencyHz, c.TxFrequencyHz, c.Bandwidth, c.Power, c.AdmitCriteria,
                   c.ColorCode, c.TimeSlot, c.RadioIdId, c.ScanListId, c.GroupListId,
                   c.ToneMode, c.CtcssHz, c.RxCtcssHz, c.DcsCode, c.RxDcsCode
            FROM ZoneChannels zc
            JOIN Channels c ON c.Id = zc.ChannelId
            WHERE zc.ZoneId = $zoneId
            ORDER BY zc.Position
            LIMIT 1;
            """;
        patternCmd.Parameters.Add(new SqliteParameter("$zoneId", zoneId));
        using var patternReader = patternCmd.ExecuteReader();

        string mode = "Digital", bandwidth = "12.5K", power = "Low", admitCriteria = "Color Code", toneMode = "";
        double? rxMhz = null, txMhz = null, ctcss = null, rxCtcss = null;
        int colorCode = 1, timeSlot = 1;
        long? radioIdId = null, scanListId = null, groupListId = null;
        string dcs = "", rxDcs = "";

        if (patternReader.Read())
        {
            mode = patternReader.GetString(0);
            rxMhz = patternReader.GetInt64(1) / 1_000_000.0;
            txMhz = patternReader.GetInt64(2) / 1_000_000.0;
            bandwidth = patternReader.IsDBNull(3) ? bandwidth : patternReader.GetString(3);
            power = patternReader.IsDBNull(4) ? power : patternReader.GetString(4);
            admitCriteria = patternReader.IsDBNull(5) ? admitCriteria : patternReader.GetString(5);
            colorCode = patternReader.IsDBNull(6) ? colorCode : patternReader.GetInt32(6);
            timeSlot = patternReader.IsDBNull(7) ? timeSlot : patternReader.GetInt32(7);
            radioIdId = patternReader.IsDBNull(8) ? null : patternReader.GetInt64(8);
            scanListId = patternReader.IsDBNull(9) ? null : patternReader.GetInt64(9);
            groupListId = patternReader.IsDBNull(10) ? null : patternReader.GetInt64(10);
            toneMode = patternReader.IsDBNull(11) ? toneMode : patternReader.GetString(11);
            ctcss = patternReader.IsDBNull(12) ? null : patternReader.GetDouble(12);
            rxCtcss = patternReader.IsDBNull(13) ? null : patternReader.GetDouble(13);
            dcs = patternReader.IsDBNull(14) ? dcs : patternReader.GetString(14);
            rxDcs = patternReader.IsDBNull(15) ? rxDcs : patternReader.GetString(15);
        }
        patternReader.Close();

        // A zone maps to one hotspot, which is configured for exactly one DMR network at a time
        // (confirmed by the user, who runs several MMDVMs, each on a different network) - so a new
        // channel here should default the talkgroup search to whatever network this zone's other
        // channels are already using, not "All networks" every time.
        string? preferredNetwork = null;
        using (var networkCmd = db.CreateCommand())
        {
            networkCmd.CommandText = """
                SELECT ct.Network FROM ZoneChannels zc
                JOIN Channels c ON c.Id = zc.ChannelId
                JOIN Contacts ct ON ct.Id = c.ContactId
                WHERE zc.ZoneId = $zoneId AND ct.Network IS NOT NULL
                GROUP BY ct.Network ORDER BY COUNT(*) DESC LIMIT 1;
                """;
            networkCmd.Parameters.Add(new SqliteParameter("$zoneId", zoneId));
            preferredNetwork = networkCmd.ExecuteScalar() as string;
        }

        var hasPattern = rxMhz is not null;
        var contactPicker = ContactPicker.Build(db, null, preferredNetwork);
        var nameBox = new TextBox { MaxLength = 16 };
        var rxMhzBox = new TextBox { Text = rxMhz?.ToString("F5") ?? "" };
        var txMhzBox = new TextBox { Text = txMhz?.ToString("F5") ?? "" };
        var colorCodeCombo = FormField.ClosedCombo(Enumerable.Range(0, 16).Select(n => n.ToString()).ToList(), colorCode.ToString());
        var timeSlotCombo = FormField.ClosedCombo(["1", "2"], timeSlot.ToString());
        var powerCombo = FormField.ClosedCombo(["Low", "Mid", "High", "Turbo"], power);

        // Auto-suggest a channel name from the zone + contact as soon as one is picked, but leave
        // it fully editable - there's no reliable way to reconstruct this codeplug's established
        // short-suffix naming convention (e.g. "TG2350 United K" -> "Home-UK") mechanically, so
        // this is a starting point, not a final answer.
        contactPicker.OnDisplayChosen = display =>
        {
            if (string.IsNullOrEmpty(nameBox.Text))
                nameBox.Text = SuggestChannelName(zoneName, display);
        };

        var form = new StackPanel { Spacing = 10 };
        form.Children.Add(FormField.Row("Contact", contactPicker.Container,
            "Group Call for a talkgroup (type to search, or type a new one to create it), or Private Call for a specific person (search callsign/name)."));
        form.Children.Add(FormField.Row("Name", nameBox, "The channel's display name on the radio (max 16 characters)."));
        form.Children.Add(FormField.Row("Rx Frequency (MHz)", rxMhzBox,
            hasPattern ? "Inherited from this zone's other channels." : "This zone has no channels yet, so there's no frequency to inherit - enter one."));
        form.Children.Add(FormField.Row("Tx Frequency (MHz)", txMhzBox, "For a simplex hotspot this is usually the same as Rx."));
        form.Children.Add(FormField.Row("Color Code", colorCodeCombo, "DMR colour code (0-15). Inherited from this zone's other channels."));
        form.Children.Add(FormField.Row("Repeater Slot", timeSlotCombo, "DMR timeslot (1 or 2). Inherited from this zone's other channels."));
        form.Children.Add(FormField.Row("Tx Power", powerCombo, "Transmit power level. Inherited from this zone's other channels."));

        var scrollViewer = new ScrollViewer
        {
            Content = form,
            Height = 420,
            Width = 520,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        var dialog = new ContentDialog
        {
            Title = "New Channel from Talk Group",
            Content = scrollViewer,
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return false;

        if (!double.TryParse(rxMhzBox.Text, out var finalRxMhz) || !double.TryParse(txMhzBox.Text, out var finalTxMhz))
            return false;

        var contactId = contactPicker.GetOrCreateId(db);
        var name = string.IsNullOrWhiteSpace(nameBox.Text) ? "New Channel" : nameBox.Text.Trim();

        using var tx = db.BeginTransaction();

        long channelId;
        using (var insertCmd = db.CreateCommand())
        {
            insertCmd.Transaction = tx;
            insertCmd.CommandText = """
                INSERT INTO Channels
                    (ChannelNumber, Name, Mode, RxFrequencyHz, TxFrequencyHz, Bandwidth, Power, AdmitCriteria,
                     ColorCode, TimeSlot, ContactId, RadioIdId, ScanListId, GroupListId, ToneMode, CtcssHz, RxCtcssHz,
                     DcsCode, RxDcsCode)
                VALUES
                    ((SELECT COALESCE(MAX(ChannelNumber), 0) + 1 FROM Channels), $name, $mode, $rxHz, $txHz, $bandwidth,
                     $power, $admit, $colorCode, $timeSlot, $contactId, $radioIdId, $scanListId, $groupListId,
                     $toneMode, $ctcss, $rxCtcss, $dcs, $rxDcs);
                SELECT last_insert_rowid();
                """;
            insertCmd.Parameters.Add(new SqliteParameter("$name", name));
            insertCmd.Parameters.Add(new SqliteParameter("$mode", mode));
            insertCmd.Parameters.Add(new SqliteParameter("$rxHz", (long)Math.Round(finalRxMhz * 1_000_000)));
            insertCmd.Parameters.Add(new SqliteParameter("$txHz", (long)Math.Round(finalTxMhz * 1_000_000)));
            insertCmd.Parameters.Add(new SqliteParameter("$bandwidth", (object?)NullIfEmpty(bandwidth) ?? DBNull.Value));
            insertCmd.Parameters.Add(new SqliteParameter("$power", (object?)(powerCombo.SelectedItem as string) ?? DBNull.Value));
            insertCmd.Parameters.Add(new SqliteParameter("$admit", (object?)NullIfEmpty(admitCriteria) ?? DBNull.Value));
            insertCmd.Parameters.Add(new SqliteParameter("$colorCode", ParseIntOrNull(colorCodeCombo.SelectedItem as string) ?? colorCode));
            insertCmd.Parameters.Add(new SqliteParameter("$timeSlot", ParseIntOrNull(timeSlotCombo.SelectedItem as string) ?? timeSlot));
            insertCmd.Parameters.Add(new SqliteParameter("$contactId", (object?)contactId ?? DBNull.Value));
            insertCmd.Parameters.Add(new SqliteParameter("$radioIdId", (object?)radioIdId ?? DBNull.Value));
            insertCmd.Parameters.Add(new SqliteParameter("$scanListId", (object?)scanListId ?? DBNull.Value));
            insertCmd.Parameters.Add(new SqliteParameter("$groupListId", (object?)groupListId ?? DBNull.Value));
            insertCmd.Parameters.Add(new SqliteParameter("$toneMode", (object?)NullIfEmpty(toneMode) ?? DBNull.Value));
            insertCmd.Parameters.Add(new SqliteParameter("$ctcss", (object?)ctcss ?? DBNull.Value));
            insertCmd.Parameters.Add(new SqliteParameter("$rxCtcss", (object?)rxCtcss ?? DBNull.Value));
            insertCmd.Parameters.Add(new SqliteParameter("$dcs", (object?)NullIfEmpty(dcs) ?? DBNull.Value));
            insertCmd.Parameters.Add(new SqliteParameter("$rxDcs", (object?)NullIfEmpty(rxDcs) ?? DBNull.Value));
            channelId = (long)insertCmd.ExecuteScalar()!;
        }

        using (var membershipCmd = db.CreateCommand())
        {
            membershipCmd.Transaction = tx;
            membershipCmd.CommandText = """
                INSERT INTO ZoneChannels (ZoneId, ChannelId, Position)
                VALUES ($zoneId, $channelId, (SELECT COALESCE(MAX(Position) + 1, 0) FROM ZoneChannels WHERE ZoneId = $zoneId));
                """;
            membershipCmd.Parameters.Add(new SqliteParameter("$zoneId", zoneId));
            membershipCmd.Parameters.Add(new SqliteParameter("$channelId", channelId));
            membershipCmd.ExecuteNonQuery();
        }

        tx.Commit();
        return true;
    }

    private static string SuggestChannelName(string zoneName, string talkGroupDisplay)
    {
        // Strip a leading "TG12345 " prefix and/or trailing " (TG12345)"/" [Network]" tags (added
        // to the picker's display text so the DMR ID and network are always visible - see
        // TalkGroupPicker), then squeeze what's left into a short suffix - a starting point the
        // user can edit, not an attempt to reproduce this codeplug's existing hand-chosen
        // abbreviations (e.g. "SOARC", "UK") exactly.
        var text = System.Text.RegularExpressions.Regex.Replace(talkGroupDisplay, @"^TG\d+\s*", "");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s*\[[^\]]+\]$", "");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s*\(TG\d+\)$", "");
        var suffix = text.Replace(" ", "");
        var name = $"{zoneName}-{suffix}";
        return name.Length > 16 ? name[..16] : name;
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static int? ParseIntOrNull(string? value) => int.TryParse(value, out var v) ? v : null;
}
