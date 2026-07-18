using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Data.Sqlite;
using HamNetProgrammer.Core.Data;

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
        var modeBox = new TextBox { Text = GetStr(2) };
        var rxMhzBox = new TextBox { Text = (reader.GetInt64(3) / 1_000_000.0).ToString("F5") };
        var txMhzBox = new TextBox { Text = (reader.GetInt64(4) / 1_000_000.0).ToString("F5") };
        var bandwidthBox = new TextBox { Text = GetStr(5) };
        var powerBox = new TextBox { Text = GetStr(6) };
        var admitBox = new TextBox { Text = GetStr(7) };
        var colorCodeBox = new TextBox { Text = GetStr(8) };
        var timeSlotBox = new TextBox { Text = GetStr(9) };
        var toneModeBox = new TextBox { Text = GetStr(14) };
        var ctcssBox = new TextBox { Text = GetStr(15) };
        var rxCtcssBox = new TextBox { Text = GetStr(16) };
        var dcsBox = new TextBox { Text = GetStr(17) };
        var rxDcsBox = new TextBox { Text = GetStr(18) };
        var extraJson = reader.IsDBNull(19) ? null : reader.GetString(19);

        var contactId = GetLongOrNull(10);
        var radioIdId = GetLongOrNull(11);
        var scanListId = GetLongOrNull(12);
        var groupListId = GetLongOrNull(13);
        reader.Close();

        var talkGroupPicker = TalkGroupPicker.Build(db, contactId, "Search, or type a new talkgroup...");

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
        form.Children.Add(FormField.Row("Talk Group", talkGroupPicker.Container, "The DMR talkgroup this channel sends to and listens for. Type to search your existing talkgroups, or type a new one (e.g. \"TG12345 My New TG\") to create it. Only talkgroups already used somewhere in your codeplug show up here - this isn't the full worldwide list yet."));
        form.Children.Add(FormField.Row("Color Code", colorCodeBox, "DMR colour code (0-15). Must match your hotspot/repeater's colour code or you won't be heard and won't hear anyone."));
        form.Children.Add(FormField.Row("Repeater Slot", timeSlotBox, "DMR timeslot (1 or 2) this channel uses. Must match the talkgroup's slot on your hotspot/repeater."));
        form.Children.Add(FormField.Row("Tx Power", powerBox, "Transmit power level, e.g. Low / Mid / High / Turbo."));
        form.Children.Add(FormField.Row("Scan List", scanListCombo, "Which scan list this channel belongs to, used when the radio is scanning multiple channels."));
        form.Children.Add(FormField.Row("Group List", groupListCombo, "Which receive group list this channel uses - controls which talkgroups you can hear besides the one you're set to."));

        var advancedToggle = new CheckBox { Content = "Show advanced fields", Margin = new Thickness(0, 8, 0, 0) };
        form.Children.Add(advancedToggle);

        var advancedPanel = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0) };
        advancedPanel.Children.Add(FormField.Row("Channel Number", channelNumberBox, "The channel's position in the radio's overall channel list. Changing this can collide with another channel's number."));
        advancedPanel.Children.Add(FormField.Row("Mode", modeBox, "Digital (DMR) or Analog (FM) operating mode."));
        advancedPanel.Children.Add(FormField.Row("Bandwidth", bandwidthBox, "Analog channel bandwidth (12.5K narrow or 25K wide). Not used on digital channels."));
        advancedPanel.Children.Add(FormField.Row("Admit Criteria", admitBox, "Condition that must be met before the radio will transmit, e.g. Always, Channel Free, Color Code."));
        advancedPanel.Children.Add(FormField.Row("Radio ID", radioIdCombo, "Which of your programmed DMR IDs this channel transmits with, if you have more than one."));
        advancedPanel.Children.Add(FormField.Row("Tone Mode", toneModeBox, "Analog squelch tone type: Off, CTCSS, or DCS. Not used on digital channels."));
        advancedPanel.Children.Add(FormField.Row("CTCSS", ctcssBox, "Analog sub-audible tone frequency (Hz) sent/expected for squelch."));
        advancedPanel.Children.Add(FormField.Row("Rx CTCSS", rxCtcssBox, "Analog sub-audible tone frequency (Hz) required to open the squelch on receive, if different from CTCSS."));
        advancedPanel.Children.Add(FormField.Row("DCS", dcsBox, "Analog digital-coded squelch value used for transmit."));
        advancedPanel.Children.Add(FormField.Row("Rx DCS", rxDcsBox, "Analog digital-coded squelch value required on receive, if different from DCS."));

        var extraValues = string.IsNullOrWhiteSpace(extraJson)
            ? new Dictionary<string, string>()
            : JsonSerializer.Deserialize<Dictionary<string, string>>(extraJson) ?? new();
        var extraBoxes = new Dictionary<string, TextBox>();

        if (extraValues.Count > 0)
        {
            advancedPanel.Children.Add(new TextBlock
            {
                Text = "Other imported fields (not yet used when writing to the radio)",
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
        updateCmd.Parameters.Add(new SqliteParameter("$channelNumber", channelNumber));
        updateCmd.Parameters.Add(new SqliteParameter("$name", nameBox.Text));
        updateCmd.Parameters.Add(new SqliteParameter("$mode", (object?)NullIfEmpty(modeBox.Text) ?? DBNull.Value));
        updateCmd.Parameters.Add(new SqliteParameter("$rxHz", (long)Math.Round(rxMhz * 1_000_000)));
        updateCmd.Parameters.Add(new SqliteParameter("$txHz", (long)Math.Round(txMhz * 1_000_000)));
        updateCmd.Parameters.Add(new SqliteParameter("$bandwidth", (object?)NullIfEmpty(bandwidthBox.Text) ?? DBNull.Value));
        updateCmd.Parameters.Add(new SqliteParameter("$power", (object?)NullIfEmpty(powerBox.Text) ?? DBNull.Value));
        updateCmd.Parameters.Add(new SqliteParameter("$admit", (object?)NullIfEmpty(admitBox.Text) ?? DBNull.Value));
        updateCmd.Parameters.Add(new SqliteParameter("$colorCode", (object?)ParseIntOrNull(colorCodeBox.Text) ?? DBNull.Value));
        updateCmd.Parameters.Add(new SqliteParameter("$timeSlot", (object?)ParseIntOrNull(timeSlotBox.Text) ?? DBNull.Value));
        updateCmd.Parameters.Add(new SqliteParameter("$contactId", (object?)talkGroupPicker.GetOrCreateId(db) ?? DBNull.Value));
        updateCmd.Parameters.Add(new SqliteParameter("$radioIdId", (object?)(radioIdCombo.SelectedItem as PickerOption)?.Id ?? DBNull.Value));
        updateCmd.Parameters.Add(new SqliteParameter("$scanListId", (object?)(scanListCombo.SelectedItem as PickerOption)?.Id ?? DBNull.Value));
        updateCmd.Parameters.Add(new SqliteParameter("$groupListId", (object?)(groupListCombo.SelectedItem as PickerOption)?.Id ?? DBNull.Value));
        updateCmd.Parameters.Add(new SqliteParameter("$toneMode", (object?)NullIfEmpty(toneModeBox.Text) ?? DBNull.Value));
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
    private static int? ParseIntOrNull(string value) => int.TryParse(value, out var v) ? v : null;
    private static double? ParseDoubleOrNull(string value) => double.TryParse(value, out var v) ? v : null;
}
