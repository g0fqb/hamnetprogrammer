using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using HamNetProgrammer.Core.Data;
using HamNetProgrammer.Desktop.Utils;
using Microsoft.Data.Sqlite;

namespace HamNetProgrammer.Desktop.Views;

/// <summary>
/// Radio-wide settings (as opposed to per-channel/zone). Deliberately one page with headed
/// sections rather than a tab strip per settings category - the user asked for fewer tabs with
/// more content each, both to navigate better and to avoid mirroring the vendor CPS's many-sparse-
/// tabs layout too closely. GPS &amp; APRS is the first section; more (General/Display/Keys etc.)
/// are planned as additional headed sections on this same page.
///
/// Database-only for now: no "Write to Radio" path yet. The AT-D878UV's documented byte layout for
/// this region has real ambiguity in places and shares a flash erase block with data implicated in
/// a past incident (see project notes) - wiring this to the radio write path needs the exact
/// offsets hardware-verified first, the same way ScanListRecordCodec's layout was cross-checked
/// against two independent sources before being trusted.
/// </summary>
public sealed partial class RadioSettingsPage : Page
{
    private sealed record CallTypeOption(string Value, string Display);
    private sealed record SlotOption(int? Value, string Display);
    private sealed record GpsModeOption(string Value, string Display);
    private sealed record AprsReportTypeOption(string Value, string Display);

    private static readonly CallTypeOption[] CallTypeOptions =
    [
        new("Private", "Private Call"),
        new("Group", "Group Call"),
        new("All", "All Call"),
    ];

    private static readonly SlotOption[] SlotOptions =
    [
        new(null, "Channel Slot"),
        new(1, "Slot 1"),
        new(2, "Slot 2"),
    ];

    private static readonly GpsModeOption[] GpsModeOptions =
    [
        new("Gps", "GPS"),
        new("Bds", "BDS"),
        new("GpsAndBds", "GPS + BDS"),
    ];

    private static readonly AprsReportTypeOption[] AprsReportTypeOptions =
    [
        new("Off", "Off"),
        new("Analog", "Analog"),
        new("Digital", "Digital"),
    ];

    private ChannelButtonPicker.Picker? _reportChannelPicker;
    private TalkGroupPicker.Picker? _talkGroupPicker;
    private CheckBox? _gpsEnabledBox;
    private ComboBox? _gpsModeCombo;
    private ComboBox? _aprsReportTypeCombo;
    private TextBox? _callsignBox;
    private TextBox? _callsignSsidBox;
    private TextBox? _destCallsignBox;
    private TextBox? _destSsidBox;
    private TextBox? _signalPathBox;
    private TextBox? _autoTxIntervalBox;
    private ComboBox? _callTypeCombo;
    private ComboBox? _slotCombo;
    private CheckBox? _fixedLocationBox;
    private TextBox? _latDegreeBox;
    private TextBox? _latMinuteBox;
    private ComboBox? _latSignCombo;
    private TextBox? _lonDegreeBox;
    private TextBox? _lonMinuteBox;
    private ComboBox? _lonSignCombo;
    private TextBox? _sendingTextBox;
    private TextBox? _gpsTemplateBox;

    public RadioSettingsPage()
    {
        this.InitializeComponent();
        Load();
    }

    private void Load()
    {
        try
        {
            using var db = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT GpsEnabled, GpsMode, AprsReportType, AprsCallsign, AprsCallsignSsid, AprsDestCallsign,
                       AprsDestSsid, AprsSignalPath, AprsAutoTxIntervalSeconds, AprsReportChannelId, AprsTalkGroupId,
                       AprsCallType, AprsSlot, AprsFixedLocationBeacon, AprsLatitudeDegree, AprsLatitudeMinute,
                       AprsLatitudeSign, AprsLongitudeDegree, AprsLongitudeMinute, AprsLongitudeSign,
                       AprsSendingText, GpsTemplateText
                FROM RadioSettings WHERE Id = 1;
                """;
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                SummaryText.Text = "No RadioSettings row found.";
                return;
            }

            bool? gpsEnabled = reader.IsDBNull(0) ? null : reader.GetInt64(0) != 0;
            var gpsMode = reader.IsDBNull(1) ? "Gps" : reader.GetString(1);
            var aprsReportType = reader.IsDBNull(2) ? "Off" : reader.GetString(2);
            var callsign = reader.IsDBNull(3) ? "" : reader.GetString(3);
            var callsignSsid = reader.IsDBNull(4) ? "" : reader.GetInt64(4).ToString();
            var destCallsign = reader.IsDBNull(5) ? "" : reader.GetString(5);
            var destSsid = reader.IsDBNull(6) ? "" : reader.GetInt64(6).ToString();
            var signalPath = reader.IsDBNull(7) ? "" : reader.GetString(7);
            var autoTxInterval = reader.IsDBNull(8) ? "" : reader.GetInt64(8).ToString();
            long? reportChannelId = reader.IsDBNull(9) ? null : reader.GetInt64(9);
            long? talkGroupId = reader.IsDBNull(10) ? null : reader.GetInt64(10);
            var callType = reader.IsDBNull(11) ? "Group" : reader.GetString(11);
            int? slot = reader.IsDBNull(12) ? null : reader.GetInt32(12);
            bool? fixedLocation = reader.IsDBNull(13) ? null : reader.GetInt64(13) != 0;
            var latDegree = reader.IsDBNull(14) ? "" : reader.GetInt64(14).ToString();
            var latMinute = reader.IsDBNull(15) ? "" : reader.GetInt64(15).ToString();
            var latSign = reader.IsDBNull(16) ? "N" : reader.GetString(16);
            var lonDegree = reader.IsDBNull(17) ? "" : reader.GetInt64(17).ToString();
            var lonMinute = reader.IsDBNull(18) ? "" : reader.GetInt64(18).ToString();
            var lonSign = reader.IsDBNull(19) ? "E" : reader.GetString(19);
            var sendingText = reader.IsDBNull(20) ? "" : reader.GetString(20);
            var gpsTemplateText = reader.IsDBNull(21) ? "" : reader.GetString(21);
            reader.Close();

            var allChannels = ChannelQueries.GetAllChannels(db);
            _reportChannelPicker = ChannelButtonPicker.Build(this.XamlRoot, allChannels, reportChannelId, "APRS Report Channel");
            _talkGroupPicker = TalkGroupPicker.Build(db, talkGroupId, "Search, or type a new talkgroup...");

            _gpsEnabledBox = new CheckBox { Content = "GPS enabled", IsChecked = gpsEnabled ?? false };
            _gpsModeCombo = new ComboBox { ItemsSource = GpsModeOptions, DisplayMemberPath = "Display", SelectedItem = GpsModeOptions.First(o => o.Value == gpsMode) };
            _aprsReportTypeCombo = new ComboBox { ItemsSource = AprsReportTypeOptions, DisplayMemberPath = "Display", SelectedItem = AprsReportTypeOptions.First(o => o.Value == aprsReportType) };
            _callsignBox = new TextBox { Text = callsign, PlaceholderText = "e.g. G0FQB" };
            _callsignSsidBox = new TextBox { Text = callsignSsid, PlaceholderText = "0-15" };
            _destCallsignBox = new TextBox { Text = destCallsign, PlaceholderText = "e.g. APAT81" };
            _destSsidBox = new TextBox { Text = destSsid, PlaceholderText = "0-15" };
            _signalPathBox = new TextBox { Text = signalPath, PlaceholderText = "e.g. WIDE1-1,WIDE2-1" };
            _autoTxIntervalBox = new TextBox { Text = autoTxInterval, PlaceholderText = "seconds, 0 = off" };
            _callTypeCombo = new ComboBox { ItemsSource = CallTypeOptions, DisplayMemberPath = "Display", SelectedItem = CallTypeOptions.First(o => o.Value == callType) };
            _slotCombo = new ComboBox { ItemsSource = SlotOptions, DisplayMemberPath = "Display", SelectedItem = SlotOptions.First(o => o.Value == slot) };
            _fixedLocationBox = new CheckBox { Content = "Send a fixed position instead of live GPS", IsChecked = fixedLocation ?? false };
            _latDegreeBox = new TextBox { Text = latDegree, PlaceholderText = "deg" };
            _latMinuteBox = new TextBox { Text = latMinute, PlaceholderText = "min" };
            _latSignCombo = new ComboBox { ItemsSource = new[] { "N", "S" }, SelectedItem = latSign };
            _lonDegreeBox = new TextBox { Text = lonDegree, PlaceholderText = "deg" };
            _lonMinuteBox = new TextBox { Text = lonMinute, PlaceholderText = "min" };
            _lonSignCombo = new ComboBox { ItemsSource = new[] { "E", "W" }, SelectedItem = lonSign };
            _sendingTextBox = new TextBox { Text = sendingText, PlaceholderText = "Free text appended to your APRS report", MaxLength = 60 };
            _gpsTemplateBox = new TextBox { Text = gpsTemplateText, PlaceholderText = "Template shown alongside GPS fixes", MaxLength = 32 };

            BuildForm();
            SummaryText.Text = $"({AppPaths.CodeplugDbPath})";
        }
        catch (Exception ex)
        {
            SummaryText.Text = $"Could not open codeplug database: {ex.Message}";
        }
    }

    private void BuildForm()
    {
        FormPanel.Children.Clear();

        FormPanel.Children.Add(FormField.SectionHeader("GPS"));
        FormPanel.Children.Add(FormField.Row("GPS", _gpsEnabledBox!, "Turns the radio's built-in GPS receiver on or off."));
        FormPanel.Children.Add(FormField.Row("GPS Mode", _gpsModeCombo!, "Which satellite system(s) the GPS receiver uses. GPS+BDS gives better coverage in parts of Asia at the cost of slightly higher power use."));

        FormPanel.Children.Add(FormField.SectionHeader("APRS"));
        FormPanel.Children.Add(FormField.Row("Report Type", _aprsReportTypeCombo!, "Off disables APRS reporting. Digital sends your position over a DMR talkgroup (the common choice for a hotspot). Analog transmits real APRS packets on an FM frequency."));
        FormPanel.Children.Add(FormField.Row("Your Callsign", _callsignBox!, "Your callsign as it appears in APRS reports (max 6 characters)."));
        FormPanel.Children.Add(FormField.Row("Callsign SSID", _callsignSsidBox!, "SSID suffix for your callsign (0-15), e.g. -7 for a handheld."));
        FormPanel.Children.Add(FormField.Row("Destination Callsign", _destCallsignBox!, "The APRS destination address, used for routing/decoding hints (e.g. a radio-model code like APAT81). Leave as your CPS default unless you know you need to change it."));
        FormPanel.Children.Add(FormField.Row("Destination SSID", _destSsidBox!, "SSID suffix for the destination callsign (0-15)."));
        FormPanel.Children.Add(FormField.Row("Signal Path", _signalPathBox!, "APRS digipeater path, e.g. \"WIDE1-1,WIDE2-1\" for analog RF APRS. Not used for digital (talkgroup) reporting."));
        FormPanel.Children.Add(FormField.Row("Auto TX Interval", _autoTxIntervalBox!, "How often the radio automatically sends an APRS position report, in seconds. 0 turns automatic reporting off."));
        FormPanel.Children.Add(FormField.Row("Report Channel", _reportChannelPicker!.Container, "Which channel APRS reports are sent on. (none) uses whichever channel you're currently on."));
        FormPanel.Children.Add(FormField.Row("Talk Group", _talkGroupPicker!.Container, "The DMR talkgroup that carries your digital APRS reports."));
        FormPanel.Children.Add(FormField.Row("Call Type", _callTypeCombo!, "Whether the APRS report is sent as a group call, private call, or all call."));
        FormPanel.Children.Add(FormField.Row("Slot", _slotCombo!, "Which DMR timeslot carries the APRS report. \"Channel Slot\" uses whatever slot the report channel is already set to."));

        FormPanel.Children.Add(FormField.SectionHeader("Fixed Location"));
        FormPanel.Children.Add(FormField.Row("Fixed Position", _fixedLocationBox!, "When on, the radio always reports the position below instead of your live GPS fix - useful for a base station or hotspot with no GPS view."));
        var latRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        latRow.Children.Add(_latDegreeBox!);
        latRow.Children.Add(_latMinuteBox!);
        latRow.Children.Add(_latSignCombo!);
        FormPanel.Children.Add(FormField.Row("Latitude", latRow, "Degrees, minutes, and N/S for the fixed position."));
        var lonRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        lonRow.Children.Add(_lonDegreeBox!);
        lonRow.Children.Add(_lonMinuteBox!);
        lonRow.Children.Add(_lonSignCombo!);
        FormPanel.Children.Add(FormField.Row("Longitude", lonRow, "Degrees, minutes, and E/W for the fixed position."));

        FormPanel.Children.Add(FormField.SectionHeader("Text"));
        FormPanel.Children.Add(FormField.Row("APRS Sending Text", _sendingTextBox!, "Free text appended to your outgoing APRS report, e.g. a comment or status."));
        FormPanel.Children.Add(FormField.Row("GPS Template Text", _gpsTemplateBox!, "Text shown on the radio's display alongside a GPS fix."));
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            using var db = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                UPDATE RadioSettings SET
                    GpsEnabled = $gpsEnabled, GpsMode = $gpsMode, AprsReportType = $aprsReportType,
                    AprsCallsign = $callsign, AprsCallsignSsid = $callsignSsid, AprsDestCallsign = $destCallsign,
                    AprsDestSsid = $destSsid, AprsSignalPath = $signalPath, AprsAutoTxIntervalSeconds = $autoTx,
                    AprsReportChannelId = $reportChannelId, AprsTalkGroupId = $talkGroupId, AprsCallType = $callType,
                    AprsSlot = $slot, AprsFixedLocationBeacon = $fixedLocation, AprsLatitudeDegree = $latDeg,
                    AprsLatitudeMinute = $latMin, AprsLatitudeSign = $latSign, AprsLongitudeDegree = $lonDeg,
                    AprsLongitudeMinute = $lonMin, AprsLongitudeSign = $lonSign, AprsSendingText = $sendingText,
                    GpsTemplateText = $gpsTemplate
                WHERE Id = 1;
                """;
            cmd.Parameters.Add(new SqliteParameter("$gpsEnabled", _gpsEnabledBox!.IsChecked == true ? 1 : 0));
            cmd.Parameters.Add(new SqliteParameter("$gpsMode", ((GpsModeOption)_gpsModeCombo!.SelectedItem).Value));
            cmd.Parameters.Add(new SqliteParameter("$aprsReportType", ((AprsReportTypeOption)_aprsReportTypeCombo!.SelectedItem).Value));
            cmd.Parameters.Add(new SqliteParameter("$callsign", (object?)NullIfEmpty(_callsignBox!.Text) ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("$callsignSsid", (object?)ParseIntOrNull(_callsignSsidBox!.Text) ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("$destCallsign", (object?)NullIfEmpty(_destCallsignBox!.Text) ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("$destSsid", (object?)ParseIntOrNull(_destSsidBox!.Text) ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("$signalPath", (object?)NullIfEmpty(_signalPathBox!.Text) ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("$autoTx", (object?)ParseIntOrNull(_autoTxIntervalBox!.Text) ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("$reportChannelId", (object?)_reportChannelPicker!.GetSelectedId() ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("$talkGroupId", (object?)_talkGroupPicker!.GetOrCreateId(db) ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("$callType", ((CallTypeOption)_callTypeCombo!.SelectedItem).Value));
            cmd.Parameters.Add(new SqliteParameter("$slot", (object?)((SlotOption)_slotCombo!.SelectedItem).Value ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("$fixedLocation", _fixedLocationBox!.IsChecked == true ? 1 : 0));
            cmd.Parameters.Add(new SqliteParameter("$latDeg", (object?)ParseIntOrNull(_latDegreeBox!.Text) ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("$latMin", (object?)ParseIntOrNull(_latMinuteBox!.Text) ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("$latSign", (string)_latSignCombo!.SelectedItem));
            cmd.Parameters.Add(new SqliteParameter("$lonDeg", (object?)ParseIntOrNull(_lonDegreeBox!.Text) ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("$lonMin", (object?)ParseIntOrNull(_lonMinuteBox!.Text) ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("$lonSign", (string)_lonSignCombo!.SelectedItem));
            cmd.Parameters.Add(new SqliteParameter("$sendingText", (object?)NullIfEmpty(_sendingTextBox!.Text) ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("$gpsTemplate", (object?)NullIfEmpty(_gpsTemplateBox!.Text) ?? DBNull.Value));
            cmd.ExecuteNonQuery();

            StatusText.Text = "Saved.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not save: {ex.Message}";
        }
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static int? ParseIntOrNull(string value) => int.TryParse(value, out var v) ? v : null;
}
