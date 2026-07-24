using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using HamNetProgrammer.Core.Data;
using HamNetProgrammer.Desktop.Utils;
using Microsoft.Data.Sqlite;
using Windows.System;

namespace HamNetProgrammer.Desktop.Views;

/// <summary>
/// Radio-wide settings (as opposed to per-channel/zone). Deliberately one page with headed
/// sections rather than a tab strip per settings category - the user asked for fewer tabs with
/// more content each, both to navigate better and to avoid mirroring the vendor CPS's many-sparse-
/// tabs layout too closely. GPS &amp; APRS is the first section; more (General/Display/Keys etc.)
/// are planned as additional headed sections on this same page.
///
/// Most of GPS/APRS IS written to the radio (see AnyToneD878CodeplugEncoder.EncodeRadioSettings) -
/// three fields are the exception (GPS Mode, APRS Sending Text, GPS Template Text), each labeled
/// "(not written to radio yet)" below: no independently-confirmed byte offset exists for them on
/// the D878UV specifically in either reference source checked, so they're saved to the database
/// but deliberately left unwritten rather than risk an unverified flash offset (see the past
/// incident in project notes for why that risk is taken seriously here).
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
    private NumberBox? _callsignSsidBox;
    private TextBox? _destCallsignBox;
    private NumberBox? _destSsidBox;
    private TextBox? _signalPathBox;
    private NumberBox? _autoTxIntervalBox;
    private ComboBox? _callTypeCombo;
    private ComboBox? _slotCombo;
    private CheckBox? _fixedLocationBox;
    private NumberBox? _latDegreeBox;
    private NumberBox? _latMinuteBox;
    private ComboBox? _latSignCombo;
    private NumberBox? _lonDegreeBox;
    private NumberBox? _lonMinuteBox;
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
            double? callsignSsid = reader.IsDBNull(4) ? null : reader.GetInt64(4);
            var destCallsign = reader.IsDBNull(5) ? "" : reader.GetString(5);
            double? destSsid = reader.IsDBNull(6) ? null : reader.GetInt64(6);
            var signalPath = reader.IsDBNull(7) ? "" : reader.GetString(7);
            double? autoTxInterval = reader.IsDBNull(8) ? null : reader.GetInt64(8);
            long? reportChannelId = reader.IsDBNull(9) ? null : reader.GetInt64(9);
            long? talkGroupId = reader.IsDBNull(10) ? null : reader.GetInt64(10);
            var callType = reader.IsDBNull(11) ? "Group" : reader.GetString(11);
            int? slot = reader.IsDBNull(12) ? null : reader.GetInt32(12);
            bool? fixedLocation = reader.IsDBNull(13) ? null : reader.GetInt64(13) != 0;
            double? latDegree = reader.IsDBNull(14) ? null : reader.GetInt64(14);
            double? latMinute = reader.IsDBNull(15) ? null : reader.GetInt64(15);
            var latSign = reader.IsDBNull(16) ? "N" : reader.GetString(16);
            double? lonDegree = reader.IsDBNull(17) ? null : reader.GetInt64(17);
            double? lonMinute = reader.IsDBNull(18) ? null : reader.GetInt64(18);
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
            _callsignSsidBox = RangeNumberBox(callsignSsid, 0, 15);
            _destCallsignBox = new TextBox { Text = destCallsign, PlaceholderText = "e.g. APAT81" };
            _destSsidBox = RangeNumberBox(destSsid, 0, 15);
            _signalPathBox = new TextBox { Text = signalPath, PlaceholderText = "e.g. WIDE1-1,WIDE2-1" };
            _autoTxIntervalBox = RangeNumberBox(autoTxInterval, 0, 65535);
            _callTypeCombo = new ComboBox { ItemsSource = CallTypeOptions, DisplayMemberPath = "Display", SelectedItem = CallTypeOptions.First(o => o.Value == callType) };
            _slotCombo = new ComboBox { ItemsSource = SlotOptions, DisplayMemberPath = "Display", SelectedItem = SlotOptions.First(o => o.Value == slot) };
            _fixedLocationBox = new CheckBox { Content = "Send a fixed position instead of live GPS", IsChecked = fixedLocation ?? false };
            _latDegreeBox = RangeNumberBox(latDegree, 0, 90, 90);
            _latMinuteBox = RangeNumberBox(latMinute, 0, 59, 90);
            _latSignCombo = new ComboBox { ItemsSource = new[] { "N", "S" }, SelectedItem = latSign };
            _lonDegreeBox = RangeNumberBox(lonDegree, 0, 180, 90);
            _lonMinuteBox = RangeNumberBox(lonMinute, 0, 59, 90);
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
        FormPanel.Children.Add(FormField.Row("GPS Mode (not written to radio yet)", _gpsModeCombo!,
            "Which satellite system(s) the GPS receiver uses. GPS+BDS gives better coverage in parts of Asia at the cost of slightly higher power use. " +
            "Saved here, but NOT currently written to the radio - no independently-confirmed byte offset for this field has been found yet, so it's left alone rather than risk writing to an unverified location."));

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
        FormPanel.Children.Add(FormField.Row("APRS Sending Text (not written to radio yet)", _sendingTextBox!,
            "Free text appended to your outgoing APRS report, e.g. a comment or status. Saved here, but NOT currently written to the radio - " +
            "no independently-confirmed byte offset for this field has been found yet, so it's left alone rather than risk writing to an unverified location."));
        FormPanel.Children.Add(FormField.Row("GPS Template Text (not written to radio yet)", _gpsTemplateBox!,
            "Text shown on the radio's display alongside a GPS fix. Saved here, but NOT currently written to the radio - " +
            "no independently-confirmed byte offset for this field has been found yet, so it's left alone rather than risk writing to an unverified location."));

        FormPanel.Children.Add(FormField.SectionHeader("Advanced"));
        FormPanel.Children.Add(new TextBlock
        {
            Text = "Everything above lives in one open SQLite database file, not a proprietary format - you're welcome to open it " +
                   "directly with a tool like DB Browser for SQLite if you want to see or query the raw tables. Editing it by hand " +
                   "isn't dangerous to the app itself, but it IS possible to put data into a shape this app or the AT-D878UV encoder " +
                   "doesn't expect (e.g. breaking a foreign-key link between a channel and its zone) - back up the file first if you " +
                   "plan to edit it directly, the same as you would before any bulk change.",
            FontSize = 12,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x88, 0x96, 0xa0)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });
        var openDbFolderButton = new Button { Content = "Open Database Folder", HorizontalAlignment = HorizontalAlignment.Left };
        openDbFolderButton.Click += async (_, _) =>
        {
            await Launcher.LaunchFolderPathAsync(Path.GetDirectoryName(AppPaths.CodeplugDbPath)!);
        };
        FormPanel.Children.Add(openDbFolderButton);
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
            cmd.Parameters.Add(new SqliteParameter("$callsignSsid", (object?)NumberBoxValueOrNull(_callsignSsidBox!) ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("$destCallsign", (object?)NullIfEmpty(_destCallsignBox!.Text) ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("$destSsid", (object?)NumberBoxValueOrNull(_destSsidBox!) ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("$signalPath", (object?)NullIfEmpty(_signalPathBox!.Text) ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("$autoTx", (object?)NumberBoxValueOrNull(_autoTxIntervalBox!) ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("$reportChannelId", (object?)_reportChannelPicker!.GetSelectedId() ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("$talkGroupId", (object?)_talkGroupPicker!.GetOrCreateId(db) ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("$callType", ((CallTypeOption)_callTypeCombo!.SelectedItem).Value));
            cmd.Parameters.Add(new SqliteParameter("$slot", (object?)((SlotOption)_slotCombo!.SelectedItem).Value ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("$fixedLocation", _fixedLocationBox!.IsChecked == true ? 1 : 0));
            cmd.Parameters.Add(new SqliteParameter("$latDeg", (object?)NumberBoxValueOrNull(_latDegreeBox!) ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("$latMin", (object?)NumberBoxValueOrNull(_latMinuteBox!) ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("$latSign", (string)_latSignCombo!.SelectedItem));
            cmd.Parameters.Add(new SqliteParameter("$lonDeg", (object?)NumberBoxValueOrNull(_lonDegreeBox!) ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("$lonMin", (object?)NumberBoxValueOrNull(_lonMinuteBox!) ?? DBNull.Value));
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

    // NumberBox, not a free TextBox with a placeholder hint - these are all genuinely range-bound
    // (an SSID is 0-15, a latitude minute is 0-59, etc.), and a placeholder-only hint like "0-15"
    // was easy to ignore; NumberBox rejects non-numeric input directly and SpinButtons make the
    // valid range visually obvious rather than something you find out by trial and error on write.
    private static NumberBox RangeNumberBox(double? value, double min, double max, double width = 120) => new()
    {
        Value = value ?? double.NaN,
        Minimum = min,
        Maximum = max,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        SmallChange = 1,
        PlaceholderText = "(none)",
        Width = width,
    };

    private static int? NumberBoxValueOrNull(NumberBox box) => double.IsNaN(box.Value) ? null : (int)box.Value;
}
