using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Data.Sqlite;
using HamNetProgrammer.Core.Data;
using HamNetProgrammer.Core.Radios.AnyTone;

namespace HamNetProgrammer.Desktop.Utils;

/// <summary>
/// Full channel field editor. Covers every column in the Channels table (the fields the AT-D878UV
/// encoder actually consumes). RT Systems exports a lot more columns than that (2 Tone, 5 Tone,
/// APRS *, Encryption ID, etc.) - those aren't individually modeled in the schema since most are
/// legacy-analog/APRS-only concerns the encoder doesn't use yet, but the CSV importer already
/// preserves them verbatim in Channels.ExtraAttributesJson rather than dropping them. This dialog
/// surfaces that JSON as editable key/value rows so nothing imported is hidden or lost, without a
/// speculative schema migration for fields nothing writes to the radio yet.
/// </summary>
public static class ChannelEditDialog
{
    private sealed record PickerOption(long? Id, string Display);

    public static async Task<bool> ShowAsync(XamlRoot xamlRoot, long channelId)
    {
        using var db = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);

        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT ChannelNumber, Name, Mode, RxFrequencyHz, TxFrequencyHz, Bandwidth, Power, AdmitCriteria,
                   ColorCode, TimeSlot, ContactId, RadioIdId, ScanListId, GroupListId, ToneMode, CtcssHz, RxCtcssHz,
                   DcsCode, RxDcsCode, ExtraAttributesJson
            FROM Channels WHERE Id = $id;
            """;
        cmd.Parameters.Add(new SqliteParameter("$id", channelId));
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return false;

        string GetStr(int i) => reader.IsDBNull(i) ? "" : reader.GetValue(i).ToString() ?? "";
        long? GetLongOrNull(int i) => reader.IsDBNull(i) ? null : reader.GetInt64(i);

        var channelNumberBox = new TextBox { Text = GetStr(0) };
        var nameBox = new TextBox { Text = GetStr(1) };
        var modeCombo = FormField.ClosedCombo(["Digital", "Analog"], GetStr(2));
        var rxMhzBox = new TextBox { Text = (reader.GetInt64(3) / 1_000_000.0).ToString("F5") };
        var txMhzBox = new TextBox { Text = (reader.GetInt64(4) / 1_000_000.0).ToString("F5") };
        var bandwidthCombo = FormField.ClosedCombo(["12.5K", "25K"], GetStr(5));
        var powerCombo = FormField.ClosedCombo(["Low", "Mid", "High", "Turbo"], GetStr(6));
        var admitCombo = FormField.ClosedCombo(["(none)", "Always", "Channel Free", "Color Code", "CTCSS/DCS"], GetStr(7));
        var colorCodeCombo = FormField.ClosedCombo(Enumerable.Range(0, 16).Select(n => n.ToString()).ToList(), GetStr(8));
        var timeSlotCombo = FormField.ClosedCombo(["1", "2"], GetStr(9));
        var toneModeCombo = FormField.ClosedCombo(["Off", "CTCSS", "DCS"], GetStr(14));
        var ctcssToneOptions = CtcssTones.StandardTonesHz.Select(hz => hz.ToString("F1")).ToList();
        var ctcssBox = FormField.EditableCombo(ctcssToneOptions, GetStr(15));
        var rxCtcssBox = FormField.EditableCombo(ctcssToneOptions, GetStr(16));
        // Standard DCS code list (104 codes) - real data checked 2026-07-22: every channel in the
        // live database has DcsCode/RxDcsCode = "023" uniformly, almost certainly RT Systems' own
        // placeholder for an unused analog field on digital channels rather than meaningful
        // per-channel data - but it was still a completely unvalidated free TextBox before this,
        // accepting any string including alpha garbage with zero feedback.
        var dcsBox = FormField.EditableCombo(DcsCodes.StandardCodes, GetStr(17));
        var rxDcsBox = FormField.EditableCombo(DcsCodes.StandardCodes, GetStr(18));
        var extraJson = reader.IsDBNull(19) ? null : reader.GetString(19);

        var contactId = GetLongOrNull(10);
        var radioIdId = GetLongOrNull(11);
        var scanListId = GetLongOrNull(12);
        var groupListId = GetLongOrNull(13);
        reader.Close();

        var contactPicker = ContactPicker.Build(db, contactId);

        var radioIdCombo = BuildPickerCombo(db, "SELECT Id, Callsign FROM RadioIds ORDER BY Callsign;", radioIdId);
        var scanListCombo = BuildPickerCombo(db, "SELECT Id, Name FROM ScanLists ORDER BY Name;", scanListId);
        var groupListCombo = BuildPickerCombo(db, "SELECT Id, Name FROM GroupLists ORDER BY Name;", groupListId);

        // Basic = the fields a hotspot/DMR user actually touches day to day. Advanced = fields
        // that matter for analog/repeater channels or are rarely hand-edited (bandwidth, admit
        // criteria, radio ID, channel number, tone/CTCSS/DCS, plus whatever RT Systems columns
        // aren't individually modeled) - hidden by default per user request rather than dumping
        // ~30 fields on someone who only ever touches frequency/talkgroup/colour-code/slot.
        var form = new StackPanel { Spacing = 8 };
        form.Children.Add(FormField.Row("Name", nameBox, "The channel's display name on the radio (max 16 characters)."));
        form.Children.Add(FormField.Row("Rx Frequency (MHz)", rxMhzBox, "The frequency this channel listens on."));
        form.Children.Add(FormField.Row("Tx Frequency (MHz)", txMhzBox, "The frequency this channel transmits on. For a simplex hotspot this is usually the same as Rx."));
        form.Children.Add(FormField.Row("Contact", contactPicker.Container, "Group Call sends to a talkgroup - type to search your existing talkgroups, or type a new one (e.g. \"TG12345 My New TG\") to create it. Private Call rings one specific person - search their callsign or name (searches your own contacts plus radioid.net live)."));
        form.Children.Add(FormField.Row("Color Code", colorCodeCombo, "DMR colour code (0-15). Must match your hotspot/repeater's colour code or you won't be heard and won't hear anyone."));
        form.Children.Add(FormField.Row("Repeater Slot", timeSlotCombo, "DMR timeslot (1 or 2) this channel uses. Must match the talkgroup's slot on your hotspot/repeater."));
        form.Children.Add(FormField.Row("Tx Power", powerCombo, "Transmit power level."));
        // "Belongs to" was the wrong framing - this is a per-channel property (byte 27 of the
        // radio's own channel record: which list turns ON when you scan from THIS channel), not
        // membership in a list (that's ScanListChannels/GroupListContacts, managed on the Scan
        // Lists/Group Lists pages themselves - a different relationship entirely, real confusion
        // once pointed out since both are called "lists").
        form.Children.Add(FormField.Row("Scan List", scanListCombo, "Which scan list activates when you press Scan while on this channel. To edit which channels a scan list itself contains, use the Scan Lists page."));
        form.Children.Add(FormField.Row("Group List", groupListCombo, "Which receive group list filters incoming calls on this channel - controls which talkgroups you can hear besides the one you're set to. To edit which contacts a group list itself contains, use the Group Lists page."));

        var advancedToggle = new CheckBox { Content = "Show advanced fields", Margin = new Thickness(0, 8, 0, 0) };
        form.Children.Add(advancedToggle);

        var advancedPanel = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0) };
        advancedPanel.Children.Add(FormField.Row("Channel Number", channelNumberBox, "The channel's position in the radio's overall channel list. Changing this can collide with another channel's number."));
        advancedPanel.Children.Add(FormField.Row("Mode", modeCombo, "Digital (DMR) or Analog (FM) operating mode."));
        advancedPanel.Children.Add(FormField.Row("Bandwidth", bandwidthCombo, "Analog channel bandwidth (12.5K narrow or 25K wide). Not used on digital channels."));
        advancedPanel.Children.Add(FormField.Row("Admit Criteria", admitCombo, "Condition that must be met before the radio will transmit. Not yet written to the radio - recorded here for reference/future use."));
        advancedPanel.Children.Add(FormField.Row("Radio ID", radioIdCombo, "Which of your programmed DMR IDs this channel transmits with, if you have more than one."));
        advancedPanel.Children.Add(FormField.Row("Tone Mode", toneModeCombo, "Analog squelch tone type: Off, CTCSS, or DCS. Not used on digital channels, and not yet written to the radio - recorded here for reference/future use."));
        advancedPanel.Children.Add(FormField.Row("CTCSS", ctcssBox, "Analog sub-audible tone frequency (Hz) sent/expected for squelch - pick a standard tone or type a custom one. Not yet written to the radio - recorded here for reference/future use."));
        advancedPanel.Children.Add(FormField.Row("Rx CTCSS", rxCtcssBox, "Analog sub-audible tone frequency (Hz) required to open the squelch on receive, if different from CTCSS. Not yet written to the radio."));
        advancedPanel.Children.Add(FormField.Row("DCS", dcsBox, "Analog digital-coded squelch code used for transmit - pick a standard code or type a custom one. Not yet written to the radio."));
        advancedPanel.Children.Add(FormField.Row("Rx DCS", rxDcsBox, "Analog digital-coded squelch code required on receive, if different from DCS. Not yet written to the radio."));

        var extraValues = string.IsNullOrWhiteSpace(extraJson)
            ? new Dictionary<string, string>()
            : JsonSerializer.Deserialize<Dictionary<string, string>>(extraJson) ?? new();
        var extraBoxes = new Dictionary<string, TextBox>();

        if (extraValues.Count > 0)
        {
            // Deliberately NOT phrased "not written to the radio" - several fields directly above
            // this heading (Admit Criteria, Tone Mode, CTCSS, DCS) share that same status and
            // already say so individually; implying that's what sets this section apart would be
            // misleading. What actually distinguishes this section: these columns have no
            // dedicated field in the schema at all, just raw imported key/value pairs.
            advancedPanel.Children.Add(new TextBlock
            {
                Text = "Other imported fields (not individually modeled in this app yet)",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontSize = 12,
                Margin = new Thickness(0, 10, 0, 0),
            });
            foreach (var (key, value) in extraValues.OrderBy(kv => kv.Key))
            {
                var box = new TextBox { Text = value };
                extraBoxes[key] = box;
                advancedPanel.Children.Add(FormField.Row(key, box, null));
            }
        }

        form.Children.Add(advancedPanel);
        advancedToggle.Checked += (_, _) => advancedPanel.Visibility = Visibility.Visible;
        advancedToggle.Unchecked += (_, _) => advancedPanel.Visibility = Visibility.Collapsed;

        var scrollViewer = new ScrollViewer
        {
            Content = form,
            Height = 560,
            Width = 620,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        var dialog = new ContentDialog
        {
            Title = "Edit Channel",
            Content = scrollViewer,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return false;

        if (!int.TryParse(channelNumberBox.Text, out var channelNumber) ||
            !double.TryParse(rxMhzBox.Text, out var rxMhz) ||
            !double.TryParse(txMhzBox.Text, out var txMhz))
        {
            return false;
        }

        foreach (var (key, box) in extraBoxes)
            extraValues[key] = box.Text;
        var updatedExtraJson = extraValues.Count > 0 ? JsonSerializer.Serialize(extraValues) : null;

        using var updateCmd = db.CreateCommand();
        updateCmd.CommandText = """
            UPDATE Channels SET
                ChannelNumber = $channelNumber, Name = $name, Mode = $mode,
                RxFrequencyHz = $rxHz, TxFrequencyHz = $txHz, Bandwidth = $bandwidth, Power = $power,
                AdmitCriteria = $admit, ColorCode = $colorCode, TimeSlot = $timeSlot,
                ContactId = $contactId, RadioIdId = $radioIdId, ScanListId = $scanListId, GroupListId = $groupListId,
                ToneMode = $toneMode, CtcssHz = $ctcss, RxCtcssHz = $rxCtcss, DcsCode = $dcs, RxDcsCode = $rxDcs,
                ExtraAttributesJson = $extraJson
            WHERE Id = $id;
            """;
        var admitSelected = admitCombo.SelectedItem as string;
        updateCmd.Parameters.Add(new SqliteParameter("$channelNumber", channelNumber));
        updateCmd.Parameters.Add(new SqliteParameter("$name", nameBox.Text));
        updateCmd.Parameters.Add(new SqliteParameter("$mode", (object?)(modeCombo.SelectedItem as string) ?? DBNull.Value));
        updateCmd.Parameters.Add(new SqliteParameter("$rxHz", (long)Math.Round(rxMhz * 1_000_000)));
        updateCmd.Parameters.Add(new SqliteParameter("$txHz", (long)Math.Round(txMhz * 1_000_000)));
        updateCmd.Parameters.Add(new SqliteParameter("$bandwidth", (object?)(bandwidthCombo.SelectedItem as string) ?? DBNull.Value));
        updateCmd.Parameters.Add(new SqliteParameter("$power", (object?)(powerCombo.SelectedItem as string) ?? DBNull.Value));
        updateCmd.Parameters.Add(new SqliteParameter("$admit", (object?)(admitSelected == "(none)" ? null : admitSelected) ?? DBNull.Value));
        updateCmd.Parameters.Add(new SqliteParameter("$colorCode", (object?)ParseIntOrNull(colorCodeCombo.SelectedItem as string) ?? DBNull.Value));
        updateCmd.Parameters.Add(new SqliteParameter("$timeSlot", (object?)ParseIntOrNull(timeSlotCombo.SelectedItem as string) ?? DBNull.Value));
        updateCmd.Parameters.Add(new SqliteParameter("$contactId", (object?)contactPicker.GetOrCreateId(db) ?? DBNull.Value));
        updateCmd.Parameters.Add(new SqliteParameter("$radioIdId", (object?)(radioIdCombo.SelectedItem as PickerOption)?.Id ?? DBNull.Value));
        updateCmd.Parameters.Add(new SqliteParameter("$scanListId", (object?)(scanListCombo.SelectedItem as PickerOption)?.Id ?? DBNull.Value));
        updateCmd.Parameters.Add(new SqliteParameter("$groupListId", (object?)(groupListCombo.SelectedItem as PickerOption)?.Id ?? DBNull.Value));
        updateCmd.Parameters.Add(new SqliteParameter("$toneMode", (object?)(toneModeCombo.SelectedItem as string) ?? DBNull.Value));
        updateCmd.Parameters.Add(new SqliteParameter("$ctcss", (object?)ParseDoubleOrNull(ctcssBox.Text) ?? DBNull.Value));
        updateCmd.Parameters.Add(new SqliteParameter("$rxCtcss", (object?)ParseDoubleOrNull(rxCtcssBox.Text) ?? DBNull.Value));
        updateCmd.Parameters.Add(new SqliteParameter("$dcs", (object?)NullIfEmpty(dcsBox.Text) ?? DBNull.Value));
        updateCmd.Parameters.Add(new SqliteParameter("$rxDcs", (object?)NullIfEmpty(rxDcsBox.Text) ?? DBNull.Value));
        updateCmd.Parameters.Add(new SqliteParameter("$extraJson", (object?)updatedExtraJson ?? DBNull.Value));
        updateCmd.Parameters.Add(new SqliteParameter("$id", channelId));
        updateCmd.ExecuteNonQuery();

        return true;
    }

    private static ComboBox BuildPickerCombo(SqliteConnection db, string query, long? selectedId)
    {
        var options = new List<PickerOption> { new(null, "(none)") };
        using var cmd = db.CreateCommand();
        cmd.CommandText = query;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            options.Add(new PickerOption(reader.GetInt64(0), reader.GetString(1)));

        var combo = new ComboBox
        {
            ItemsSource = options,
            DisplayMemberPath = "Display",
            SelectedIndex = 0,
        };
        if (selectedId is { } id)
        {
            var match = options.FirstOrDefault(o => o.Id == id);
            if (match is not null) combo.SelectedItem = match;
        }
        return combo;
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static int? ParseIntOrNull(string? value) => int.TryParse(value, out var v) ? v : null;
    private static double? ParseDoubleOrNull(string? value) => double.TryParse(value, out var v) ? v : null;
}
