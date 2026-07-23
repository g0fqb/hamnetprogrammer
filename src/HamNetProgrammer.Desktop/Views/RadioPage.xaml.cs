using System.IO.Ports;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using HamNetProgrammer.Desktop;
using HamNetProgrammer.Core.Data;
using HamNetProgrammer.Core.Diagnostics;
using HamNetProgrammer.Core.Online;
using HamNetProgrammer.Core.Planning;
using HamNetProgrammer.Core.Radios.AnyTone;
using HamNetProgrammer.Core.Radios.AnyTone.CallsignDb;
using HamNetProgrammer.Desktop.Utils;

namespace HamNetProgrammer.Desktop.Views;

public sealed partial class RadioPage : Page
{
    private static readonly SolidColorBrush NotConnectedBrush = new(Microsoft.UI.ColorHelper.FromArgb(255, 0x5a, 0x65, 0x70));
    private static readonly SolidColorBrush ConnectedBrush = new(Microsoft.UI.ColorHelper.FromArgb(255, 0x4c, 0xaf, 0x50));
    private static readonly SolidColorBrush SettlingBrush = new(Microsoft.UI.ColorHelper.FromArgb(255, 0xff, 0xb7, 0x4d));
    private static readonly SolidColorBrush ErrorBrush = new(Microsoft.UI.ColorHelper.FromArgb(255, 0xe9, 0x45, 0x60));
    private static readonly SolidColorBrush NeutralStatusBrush = new(Microsoft.UI.ColorHelper.FromArgb(255, 0x88, 0x96, 0xa0));
    private static readonly SolidColorBrush SuccessStatusBrush = new(Microsoft.UI.ColorHelper.FromArgb(255, 0x6b, 0xb0, 0xb8));

    private readonly DispatcherQueue _uiQueue;
    private bool _operationInProgress;
    private bool _radioSettling;
    // Whether a radio has actually answered on the currently-selected port (Test Connection,
    // Auto-Detect, or a prior write/backup/sample run's own identify step all set this) - NOT the
    // same as a port merely being selected. RefreshPorts() auto-selects whatever COM port happens
    // to exist at launch (which may not even be this radio, or may be unplugged/unpowered), so
    // gating on port-selection alone would still show Write/Backup/Contribute as ready before
    // anything has actually confirmed a radio is there.
    private bool _connected;
    // The most recent "Connected: ..." label, so WatchRadioSettleAsync can restore the exact same
    // text once a radio reappears after resetting, without every caller having to pass it in.
    private string? _lastConnectedLabel;
    private string? _lastDiagnosticsSession;

    public RadioPage()
    {
        this.InitializeComponent();
        // Without this, Frame.Navigate constructs a brand new RadioPage every time this nav item
        // is clicked, silently resetting the port selection (and Auto-Detect Radio's result) back
        // to whatever RefreshPorts() picks by default - confirmed the hard way when a write went
        // to the wrong port after navigating away and back. Caching the instance means the
        // constructor (and RefreshPorts) only runs once per app session.
        this.NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Enabled;
        _uiQueue = DispatcherQueue.GetForCurrentThread();
        RefreshPorts();
        // Write/Backup/Contribute default to IsEnabled="True" in XAML - without this call they'd
        // show as ready on first launch, before anything has confirmed a radio is connected.
        UpdateButtonStates();
    }

    private void OnRefreshClicked(object sender, RoutedEventArgs e) => RefreshPorts();

    private void RefreshPorts()
    {
        // Belt-and-braces alongside NavigationCacheMode above: even an explicit manual refresh
        // shouldn't discard a still-valid port selection.
        var previouslySelected = PortComboBox.SelectedItem as string;
        // SerialPort.GetPortNames() returns registry enumeration order, not numeric order (e.g.
        // COM1, COM2, COM13, COM14, ..., COM7, COM19, COM12) - genuinely confusing on a machine
        // with many ports, and the one wanted can end up scrolled off-screen. Sort by the numeric
        // suffix instead of the raw string (which would put COM13 before COM2).
        var ports = SerialPort.GetPortNames()
            .OrderBy(p => int.TryParse(p.AsSpan(3), out var n) ? n : int.MaxValue)
            .ToArray();
        PortComboBox.ItemsSource = ports;
        if (previouslySelected is not null && ports.Contains(previouslySelected))
            PortComboBox.SelectedItem = previouslySelected;
        else if (ports.Length > 0)
            PortComboBox.SelectedIndex = 0;
    }

    // A previously-confirmed connection doesn't carry over to a newly (or differently) selected
    // port - only Test Connection/Auto-Detect/a successful operation on THIS port can confirm one.
    private void OnPortSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_connected) return;
        SetConnectionStatus(false, "Not connected.");
    }

    private string? SelectedPort => PortComboBox.SelectedItem as string;

    private void Log(string message)
    {
        _uiQueue.TryEnqueue(() =>
        {
            LogText.Text += (LogText.Text.Length > 0 ? "\n" : "") + message;
            LogScrollViewer.ChangeView(null, LogScrollViewer.ScrollableHeight, null);
        });
    }

    /// <summary>Enables/disables every button that opens a session on the radio. Gated on
    /// _operationInProgress (an operation is actively running) and _radioSettling (the radio just
    /// ended a session - even a read-only one like Test Connection - and is mid drop-off/
    /// re-enumerate, so a new session opened right now would fail) for all of them. Write/Backup/
    /// Contribute additionally require _connected - on first launch (or after switching ports)
    /// nothing has confirmed a radio is actually there yet, so they start disabled until Test
    /// Connection or Auto-Detect succeeds, rather than looking "ready" when they're not. Test
    /// Connection, Auto-Detect, and RefreshButton stay exempt from the _connected requirement
    /// since they're how a connection gets confirmed in the first place.</summary>
    private void UpdateButtonStates()
    {
        _uiQueue.TryEnqueue(() =>
        {
            var enabled = !_operationInProgress && !_radioSettling;
            TestConnectionButton.IsEnabled = enabled;
            AutoDetectButton.IsEnabled = enabled;
            WriteCodeplugButton.IsEnabled = enabled && _connected;
            BackupButton.IsEnabled = enabled && _connected;
            RestoreRadioMemoryButton.IsEnabled = enabled && _connected;
            ReadCodeplugButton.IsEnabled = enabled && _connected;
            SyncReferenceDataButton.IsEnabled = enabled && _connected;
            ContributeSampleButton.IsEnabled = enabled && _connected;
            SendReportButton.IsEnabled = enabled && _lastDiagnosticsSession is not null;
            // Not gated on _lastWriteSessionFolder (this app session's own last write) - Restore
            // now offers every past write's backup via RestorePointPicker, so it should be
            // available whenever a radio is connected, same as Write/Backup/Sync.
            RestoreButton.IsEnabled = enabled && _connected;
            RefreshButton.IsEnabled = !_operationInProgress;
        });
    }

    private void SetBusy(bool busy, string status = "")
    {
        _operationInProgress = busy;
        UpdateButtonStates();
        _uiQueue.TryEnqueue(() =>
        {
            // Only touch the progress bar/status text when a new operation is STARTING - when
            // busy goes false, whatever SetProgress/SetComplete last wrote stays on screen rather
            // than being blanked, which was the actual complaint: a fast operation could finish
            // and clear its own status before anyone had a chance to read it.
            if (busy)
            {
                OperationProgressBar.IsIndeterminate = true;
                OperationStatusText.Text = status;
                OperationStatusText.Foreground = NeutralStatusBrush;
            }
        });
    }

    /// <summary>Sets the connection indicator (dot + text) both on this page and in the app
    /// shell's global status bar - the shell's copy is what stays visible after navigating away
    /// from Radio, since connection state is meaningful app-wide (which radio, of possibly
    /// several a user owns, is currently connected and on which port).</summary>
    private void SetConnectionStatus(bool connected, string text)
    {
        _connected = connected;
        if (connected) _lastConnectedLabel = text;
        UpdateButtonStates();
        _uiQueue.TryEnqueue(() =>
        {
            ConnectionDot.Fill = connected ? ConnectedBrush : NotConnectedBrush;
            ConnectionStatusText.Text = text;
        });
        AppShell.ActiveInstance?.SetConnectionStatus(connected, text);
    }

    /// <summary>A radio has been confirmed, but the session that confirmed it just ended, so it's
    /// mid drop-off/re-enumerate right now - genuinely neither "connected" nor "not connected".
    /// Purely visual (amber, not green/grey) and deliberately doesn't touch _connected: a radio
    /// resetting is not the same as a radio never having been confirmed, and buttons are already
    /// disabled via _radioSettling regardless of this distinction.</summary>
    private void SetSettlingStatus(string text)
    {
        _uiQueue.TryEnqueue(() =>
        {
            ConnectionDot.Fill = SettlingBrush;
            ConnectionStatusText.Text = text;
        });
        AppShell.ActiveInstance?.SetSettlingStatus(text);
    }

    private static string DescribeDevice(string deviceId, string port) =>
        $"Connected: {RadioRiskCatalog.Lookup(deviceId).ModelLabel} ({deviceId}) on {port}";

    /// <summary>Switches the progress bar to determinate and reports real progress - used from
    /// callbacks that already know a current/total (bytes written, regions dumped, etc.).</summary>
    private void SetProgress(long current, long total, string status)
    {
        _uiQueue.TryEnqueue(() =>
        {
            OperationProgressBar.IsIndeterminate = false;
            OperationProgressBar.Maximum = Math.Max(total, 1);
            OperationProgressBar.Value = Math.Min(current, OperationProgressBar.Maximum);
            OperationStatusText.Text = status;
            OperationStatusText.Foreground = NeutralStatusBrush;
        });
    }

    /// <summary>Shows/hides the "radio will restart several times, this is expected" banner -
    /// only used around Write Codeplug, the one operation that legitimately drops the radio off
    /// USB and re-enumerates it repeatedly (once per isolated channel-bank/shared-block session).
    /// Without this, someone unfamiliar with the tool watching the radio reboot itself 8+ times in
    /// a row could easily read that as something going wrong.</summary>
    private void SetRebootNoticeVisible(bool visible) =>
        _uiQueue.TryEnqueue(() => RebootNoticeBanner.Visibility = visible ? Visibility.Visible : Visibility.Collapsed);

    /// <summary>Reports a finished operation's outcome persistently (fills the progress bar,
    /// colors the status text) so a fast operation doesn't complete without any visible trace.</summary>
    private void SetComplete(bool success, string message)
    {
        _uiQueue.TryEnqueue(() =>
        {
            OperationProgressBar.IsIndeterminate = false;
            OperationProgressBar.Value = OperationProgressBar.Maximum;
            OperationStatusText.Text = (success ? "Done - " : "Failed - ") + message;
            OperationStatusText.Foreground = success ? SuccessStatusBrush : ErrorBrush;
        });
    }

    /// <summary>Ending ANY programming session - not just one that committed a write - causes the
    /// radio to drop off USB and re-enumerate (confirmed directly: Test Connection alone triggers
    /// it). Nothing previously waited for that to finish before the next click could open a new
    /// session, so a Write Codeplug immediately after Test Connection would fail. This disables
    /// every radio-opening button until the port is confirmed to have come back, running in the
    /// background rather than blocking the click that triggered it - it's a background settle
    /// watch, not an in-line wait.</summary>
    /// <param name="reason">Fills the blank in "Radio is resetting {reason} - waiting for it to
    /// come back..." - callers should describe what just happened (e.g. "after being detected",
    /// "after the write") so this doesn't read as a stale/generic "after that session" regardless
    /// of context, which was confusing right after something as simple as Auto-Detect or Test
    /// Connection.</param>
    private async Task WatchRadioSettleAsync(string port, string reason = "after that check")
    {
        _radioSettling = true;
        UpdateButtonStates();
        SetProgress(0, 1, $"Radio is resetting {reason} - waiting for it to come back...");
        Log($"Watching {port} for the radio to finish resetting before allowing another session...");
        // The radio just confirmed itself moments ago, but that session ending means it's
        // currently mid drop-off/re-enumerate at the USB level - showing green "Connected" through
        // this window would be actively wrong, not just imprecise (confirmed directly: it used to
        // sit there claiming "Connected" for the entire ~10-25s reset).
        SetSettlingStatus($"Resetting {port} - waiting for it to come back...");

        var backAgain = await Task.Run(() => WaitForPortToReturn(port, TimeSpan.FromSeconds(30)));

        _radioSettling = false;
        UpdateButtonStates();

        if (backAgain)
        {
            Log("Radio is back and ready for another session.");
            SetComplete(true, "Radio successfully detected and ready.");
            SetConnectionStatus(true, _lastConnectedLabel ?? DescribeDevice("unknown", port));
        }
        else
        {
            Log($"WARNING: {port} did not come back within 30s after the last session.");
            SetConnectionStatus(false, "Not connected - radio did not come back after last session.");
            SetComplete(false, "Radio did not come back - check the connection and try again.");
        }
    }

    private async void OnAutoDetectClicked(object sender, RoutedEventArgs e)
    {
        if (_operationInProgress) return;

        var ports = SerialPort.GetPortNames();
        if (ports.Length == 0)
        {
            Log("No serial ports found.");
            return;
        }

        SetBusy(true, "Auto-detecting radio...");
        Log($"Probing {ports.Length} port(s) for the AT-D878UV's handshake...");

        var found = await Task.Run(() =>
        {
            foreach (var port in ports)
            {
                Log($"  Trying {port}...");
                try
                {
                    using var radio = new AnyToneD878Transport(port);
                    radio.Open();
                    try
                    {
                        radio.StartProgrammingSession();
                        var id = radio.ReadDeviceId();
                        radio.EndProgrammingSession();
                        Log($"  {port}: responded as '{id}'.");
                        return (Port: port, DeviceId: id);
                    }
                    finally
                    {
                        radio.Close();
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    Log($"  {port}: in use by another program, skipped.");
                }
                catch (Exception ex)
                {
                    Log($"  {port}: no response ({ex.GetType().Name}).");
                }
            }
            return ((string Port, string DeviceId)?)null;
        });

        if (found is { } result)
        {
            _uiQueue.TryEnqueue(() =>
            {
                var items = (IReadOnlyList<string>)PortComboBox.ItemsSource;
                var index = items.ToList().IndexOf(result.Port);
                if (index >= 0) PortComboBox.SelectedIndex = index;
            });
            SetConnectionStatus(true, DescribeDevice(result.DeviceId, result.Port));
            Log($"Radio found on {result.Port}.");
            SetBusy(false);
            await WatchRadioSettleAsync(result.Port, "after being detected");
        }
        else
        {
            // Deliberately more specific than a bare "Not connected." here - that's the same text
            // shown before any attempt was ever made, so a failed attempt would otherwise look
            // visually identical to never having tried, on the one indicator most likely to
            // actually be watched.
            SetConnectionStatus(false, "Not connected - no radio found on any port.");
            SetComplete(false, "No radio found on any free port.");
            Log("No radio found on any free port.");
            SetBusy(false);
        }
    }

    private async void OnTestConnectionClicked(object sender, RoutedEventArgs e)
    {
        if (_operationInProgress) return;
        if (SelectedPort is not { } port)
        {
            SetComplete(false, "Select a port first.");
            return;
        }

        SetBusy(true, "Testing connection...");
        Log($"Opening {port}...");

        var result = await Task.Run(() =>
        {
            using var radio = new AnyToneD878Transport(port);
            try
            {
                radio.Open();
                radio.StartProgrammingSession();
                var id = radio.ReadDeviceId();
                radio.EndProgrammingSession();
                return (Success: true, Message: id);
            }
            catch (Exception ex)
            {
                return (Success: false, Message: ex.Message);
            }
        });

        if (result.Success)
        {
            Log($"Device identifier: {result.Message}");
            Log("Connection OK.");
            SetConnectionStatus(true, DescribeDevice(result.Message, port));
            SetBusy(false);
            await WatchRadioSettleAsync(port, "after being detected");
        }
        else
        {
            Log($"Error: {result.Message}");
            // Same reasoning as Auto-Detect's failure path - "Not connected." alone is identical
            // to the pre-attempt idle text, so a failed test would show no visible change on the
            // one indicator most likely to actually be watched.
            SetConnectionStatus(false, $"Not connected - no radio responded on {port}.");
            SetComplete(false, $"Connection failed: {result.Message}");
            SetBusy(false);
        }
    }

    private async void OnWriteCodeplugClicked(object sender, RoutedEventArgs e)
    {
        if (_operationInProgress) return;
        if (SelectedPort is not { } port)
        {
            SetComplete(false, "Select a port first.");
            return;
        }

        // Hard block, not just a warning - writes an empty/near-empty database over a radio's real
        // configuration, silently, with no error at the radio end (unlike the erase-block/lock-
        // screen failure mode, this "succeeds" perfectly, it just erases everything). Found via a
        // real near-miss on a fresh install where Write Codeplug got clicked before Read Codeplug
        // or a CSV import had ever populated the database. Checked before ConfirmContactsFreshEnoughAsync
        // and before ever touching the radio, since there's no reason to open a session at all if
        // this is going to refuse anyway.
        using (var emptyCheckDb = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath))
        using (var zoneCountCmd = emptyCheckDb.CreateCommand())
        {
            zoneCountCmd.CommandText = "SELECT COUNT(*) FROM Zones;";
            var totalZones = Convert.ToInt32(zoneCountCmd.ExecuteScalar());
            if (totalZones == 0)
            {
                Log("Write Codeplug refused: the local database has no zones at all.");
                SetComplete(false, "Your local codeplug database is empty (no zones) - writing this would erase everything currently on the radio. Use Read Codeplug from the radio, or import a CSV, before writing.");
                return;
            }
        }

        if (!await ConfirmContactsFreshEnoughAsync())
        {
            Log("Write cancelled - contacts not synced.");
            return;
        }

        SetBusy(true, "Identifying radio...");
        Log($"Opening {port} to identify the radio...");

        // One continuous session now covers identify through the final commit, instead of three
        // separate sessions (identify, baseline backup, write) each ending and restarting. Ending
        // ANY session costs a real reboot/re-enumerate (~10-25s) - confirmed directly on real
        // hardware that the old three-session shape cost four full reboots for one Write Codeplug
        // run (three unnecessary, plus the one actually unavoidable for the commit itself). The
        // radio just idles on its "PC Write" screen while this session is held open across the
        // disclaimer dialog wait below - nothing in either reference source, or this project's own
        // hardware testing, suggests an open-but-idle session times out or causes harm.
        (AnyToneD878Transport Radio, string DeviceId) identified;
        try
        {
            identified = await Task.Run(() =>
            {
                var r = new AnyToneD878Transport(port);
                r.Open();
                r.StartProgrammingSession();
                var id = r.ReadDeviceId();
                return (r, id);
            });
        }
        catch (Exception ex)
        {
            Log($"Error identifying device: {ex.Message}");
            SetConnectionStatus(false, "Not connected.");
            SetComplete(false, $"Could not identify device: {ex.Message}");
            SetBusy(false);
            return;
        }

        var radio = identified.Radio;
        var deviceId = identified.DeviceId;
        var profile = RadioRiskCatalog.Lookup(deviceId);
        Log($"Device identifier: {deviceId} ({profile.ModelLabel}, {profile.Tier} risk).");
        SetConnectionStatus(true, DescribeDevice(deviceId, port));
        SetBusy(false);

        // Hard block, not just a stronger disclaimer - Write is destructive (unlike Read, which
        // was already gated this way). A Moderate/High/Unknown model's erase-block layout isn't
        // confirmed, so a bad write here could land exactly like the D878UV lock-screen incident
        // with no known recovery path. Same wording pattern as Read Codeplug's existing refusal.
        if (profile.Tier != RadioRiskTier.Validated)
        {
            Log($"Write Codeplug refused: {profile.ModelLabel} is {profile.Tier} risk, not hardware-verified.");
            Log("Releasing the radio from programming mode...");
            try
            {
                await Task.Run(() =>
                {
                    radio.EndProgrammingSession();
                    radio.Close();
                });
            }
            catch (Exception ex)
            {
                Log($"Error releasing radio (it may still show 'PC Write' until power-cycled): {ex.Message}");
            }
            SetComplete(false, $"{profile.ModelLabel} isn't a hardware-verified model for Write Codeplug - writing to it could corrupt settings in ways this tool doesn't know how to detect or undo, with no guaranteed recovery. Use \"Contribute a Memory Sample\" instead so this model can be properly validated before writing is ever enabled for it.");
            return;
        }

        if (!await RiskDisclaimerDialog.ShowAsync(this.XamlRoot, profile))
        {
            Log("Write cancelled - releasing the radio from programming mode...");
            try
            {
                await Task.Run(() =>
                {
                    radio.EndProgrammingSession();
                    radio.Close();
                });
            }
            catch (Exception ex)
            {
                Log($"Error releasing radio (it may still show 'PC Write' until power-cycled): {ex.Message}");
            }
            return;
        }

        SetRebootNoticeVisible(true);

        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var safeDeviceId = string.Join("_", deviceId.Split(Path.GetInvalidFileNameChars()));
        var sessionFolder = Path.Combine(AppPaths.DiagnosticsDirectory, $"{stamp}_{safeDeviceId}");
        Directory.CreateDirectory(sessionFolder);
        _lastDiagnosticsSession = sessionFolder;
        var toolVersion = typeof(RadioPage).Assembly.GetName().Version?.ToString() ?? "dev";

        SetBusy(true, "Taking pre-write baseline backup...");
        Log("Taking pre-write baseline backup (for diagnostics if anything goes wrong)...");

        var result = await Task.Run(() =>
        {
            using var auditLog = WriteSessionAuditLog.Start(
                Path.Combine(sessionFolder, "audit.jsonl"), "write-codeplug", port, deviceId, toolVersion);
            var committed = false;

            try
            {
                auditLog.LogNote("Starting pre-write baseline backup.");
                var baselineResults = AnyToneD878MemoryDumper.Dump(radio,
                    Path.Combine(sessionFolder, "baseline_before.bin"),
                    Path.Combine(sessionFolder, "baseline_before.manifest.csv"),
                    (region, index, total, bytesDone, totalBytes) =>
                    {
                        SetProgress(bytesDone, totalBytes, $"Pre-write backup... [{index}/{total}] {region.Name} ({bytesDone:N0}/{totalBytes:N0} bytes)");
                        if (index % 40 == 0 || index == total) Log($"  baseline [{index}/{total}] {region.Name}");
                    });
                var baselineFailed = baselineResults.Count(r => !r.Succeeded);
                auditLog.LogNote($"Baseline backup: {baselineResults.Count} regions, {baselineFailed} failed.");
                Log($"Baseline backup complete ({baselineResults.Count} regions, {baselineFailed} failed). Same session, no reboot needed here.");

                using var db = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);

                using (var syncCheckCmd = db.CreateCommand())
                {
                    syncCheckCmd.CommandText = "SELECT SyncListsWithZones FROM RadioSettings WHERE Id = 1;";
                    if (Convert.ToInt64(syncCheckCmd.ExecuteScalar() ?? 1L) != 0)
                    {
                        Log("Syncing Scan/Group/Roaming Lists with current zone membership...");
                        var scanResult = ZoneScanListBuilder.BuildFromZones(db);
                        var groupResult = ZoneGroupListBuilder.BuildFromZones(db);
                        var roamResult = TalkGroupRoamingZoneBuilder.BuildFromZones(db);
                        Log($"  Scan Lists: {scanResult.ZonesProcessed} zones, {scanResult.ChannelsLinked} channels linked.");
                        Log($"  Group Lists: {groupResult.ZonesProcessed} zones, {groupResult.ChannelsLinked} channels linked.");
                        Log($"  Roaming Zones: {roamResult.RoamingZonesProcessed} talkgroups, {roamResult.ChannelsLinked} channels linked.");
                        foreach (var warning in scanResult.Warnings.Concat(groupResult.Warnings).Concat(roamResult.Warnings))
                            Log($"  Warning: {warning}");
                        auditLog.LogNote($"Synced lists with zones: {scanResult.ZonesProcessed} scan, {groupResult.ZonesProcessed} group, {roamResult.RoamingZonesProcessed} roaming.");
                    }
                    else
                    {
                        Log("List sync with zones is turned off - Scan/Group/Roaming Lists written as-is.");
                        auditLog.LogNote("List sync with zones is off.");
                    }
                }

                using (var zoneCountCmd = db.CreateCommand())
                {
                    zoneCountCmd.CommandText = "SELECT COUNT(*), SUM(IsActive) FROM Zones;";
                    using var zoneCountReader = zoneCountCmd.ExecuteReader();
                    zoneCountReader.Read();
                    var totalZones = zoneCountReader.GetInt32(0);
                    var activeZones = zoneCountReader.IsDBNull(1) ? 0 : zoneCountReader.GetInt32(1);
                    Log($"{activeZones}/{totalZones} zones active - only active zones are written.");
                    auditLog.LogNote($"{activeZones}/{totalZones} zones active.");
                }

                var encodeWarnings = new List<string>();
                var regions = AnyToneD878CodeplugEncoder.Build(db, encodeWarnings);
                var totalBytes = regions.Sum(r => (long)r.Data.Length);
                Log($"Encoded {regions.Count} regions, {totalBytes:N0} bytes to write.");
                auditLog.LogNote($"Encoded {regions.Count} regions, {totalBytes} bytes.");
                foreach (var warning in encodeWarnings)
                {
                    Log($"  Warning: {warning}");
                    auditLog.LogNote($"Warning: {warning}");
                }

                // ChannelBank[N] regions (channel table + TX Color Code table combined, see
                // AnyToneD878CodeplugEncoder.EncodeChannels' remarks) each get their own committed,
                // read-back-verified session - kept OUT of the bundled write below both because
                // including them was found to overflow that session's reliability threshold
                // (2026-07-21) and because splitting a bank's two halves across separate sessions
                // would risk a same-erase-block disturb between them (see the encoder's remarks).
                var channelBankRegions = regions.Where(r => r.Name.StartsWith("ChannelBank[")).OrderBy(r => r.Name).ToList();
                var bundledRegions = regions.Where(r => !r.Name.StartsWith("ChannelBank[")).ToList();
                var sharedBlockFailures = new List<string>();

                // Every session end is a real radio restart (see RebootNoticeBanner) - counting
                // them up front and threading "restart N of totalSessions" through each SetBusy
                // call turns "why does this keep rebooting" into visible, expected progress instead
                // of several unexplained resets in a row.
                var totalSessions = 1 + channelBankRegions.Count +
                    (AnyToneD878CodeplugWriter.HasRoamingBlock(regions) ? 1 : 0) +
                    (AnyToneD878CodeplugWriter.HasGeneralUsedBitmapsBlock(regions) ? 1 : 0) +
                    (AnyToneD878CodeplugWriter.HasZoneChannelDefaults(regions) ? 1 : 0);
                var sessionStep = 0;

                // Reuses the SAME connection identify/baseline-backup already opened above -
                // matching this method's own documented intent ("one continuous session now covers
                // identify through the final commit"). Opening a SECOND AnyToneD878Transport on the
                // same port here, without ever closing the first, was a real bug: Windows denies
                // the second Open() with "Access to the path 'COMn' is denied" since the original
                // `radio` handle is still live (found on real hardware 2026-07-22) - and even if it
                // hadn't been, closing and reopening here would cost an entirely unnecessary extra
                // reboot on top of the ones this write already needs.
                {
                    sessionStep++;
                    var started = DateTime.Now;
                    AnyToneD878CodeplugWriter.WriteSafeRegions(radio, bundledRegions, (region, index, total, written, totalW) =>
                    {
                        var elapsed = DateTime.Now - started;
                        Log($"  [{index}/{total}] {region.Name} done - {written:N0}/{totalW:N0} bytes, {elapsed.TotalSeconds:F0}s elapsed");
                        auditLog.LogRegion(region.Name, region.Address, region.Data.Length, "written");
                        SetProgress(written, totalW, $"Restart {sessionStep}/{totalSessions} - writing {region.Name} ({written:N0}/{totalW:N0} bytes)");
                    });

                    Log("Ending programming session (this commits the write - device will drop off USB and re-enumerate)...");
                    radio.EndProgrammingSession();
                    radio.Close();
                    committed = true;
                    auditLog.LogNote(WriteSessionAuditLog.CommitConfirmedMessage);
                }

                foreach (var bankRegion in channelBankRegions)
                {
                    sessionStep++;
                    SetBusy(true, $"Restart {sessionStep}/{totalSessions} - writing and verifying {bankRegion.Name}...");
                    const int maxBytesPerSession = 20_000; // each bank is at most ~16,384 bytes - one chunk per bank
                    var verified = AnyToneD878CodeplugWriter.WriteRegionChunkedAndVerify(port, bankRegion, maxBytesPerSession, msg =>
                    {
                        Log($"  {msg}");
                        auditLog.LogNote(msg);
                    });
                    if (verified)
                        auditLog.LogRegion(bankRegion.Name, bankRegion.Address, bankRegion.Data.Length, "written and verified");
                    else
                        sharedBlockFailures.Add($"{bankRegion.Name} did not verify correctly.");
                }

                // The three shared-erase-block regions below each get their OWN isolated session
                // (own Start, own End, own re-enumerate wait), built/written/verified/retried end to
                // end by WriteAndVerifySharedBlock. ZoneChannelDefaults MUST be written LAST - proven
                // on real hardware (2026-07-19) that GeneralUsedBitmapsBlock's address
                // (0x024C0000) + its length lands at EXACTLY ZoneChannelDefaultsBlockAddress
                // (0x02500000): they're physically contiguous erase blocks on the same flash die, and
                // writing GeneralUsedBitmapsBlock corrupts an already-good, untouched
                // ZoneChannelDefaults as a program/erase-disturb side effect - session isolation and
                // write-verify-retry on ZoneChannelDefaults itself can't help if something else writes
                // its neighbor afterward. RoamingBlock is at a different address entirely and was
                // confirmed NOT to cause this (5 isolated write cycles, no disturb), so its position
                // relative to the other two doesn't matter.
                WriteSharedBlockAndCheck("RoamingBlock", AnyToneD878CodeplugWriter.HasRoamingBlock(regions), r =>
                    AnyToneD878CodeplugWriter.BuildRoamingBlockRegion(r, regions));
                WriteSharedBlockAndCheck("GeneralUsedBitmapsBlock", AnyToneD878CodeplugWriter.HasGeneralUsedBitmapsBlock(regions), r =>
                    AnyToneD878CodeplugWriter.BuildGeneralUsedBitmapsBlockRegion(r, regions));
                WriteSharedBlockAndCheck("ZoneChannelDefaults", AnyToneD878CodeplugWriter.HasZoneChannelDefaults(regions), r =>
                    AnyToneD878CodeplugWriter.BuildZoneChannelDefaultsRegion(r, regions));

                void WriteSharedBlockAndCheck(string label, bool needed, Func<AnyToneD878Transport, EncodedRegion> build)
                {
                    if (!needed) return;

                    sessionStep++;
                    SetBusy(true, $"Restart {sessionStep}/{totalSessions} - writing and verifying {label}...");
                    var writeResult = AnyToneD878CodeplugWriter.WriteAndVerifySharedBlock(port, build, msg =>
                    {
                        Log($"  [{label}] {msg}");
                        auditLog.LogNote($"{label}: {msg}");
                    });

                    if (writeResult.Verified)
                    {
                        auditLog.LogRegion(label, writeResult.Address, writeResult.Length, $"written and verified (attempt {writeResult.Attempts})");
                    }
                    else
                    {
                        sharedBlockFailures.Add(writeResult.Error ?? $"{label} failed to verify.");
                        auditLog.LogNote($"FAILED: {writeResult.Error}");
                    }
                }

                if (sharedBlockFailures.Count > 0)
                {
                    Log("WARNING: one or more shared blocks did not verify correctly even after automatic retries:");
                    foreach (var f in sharedBlockFailures) Log($"  {f}");
                    Log("Do not assume the radio is in a good state - check it before further writes.");
                }

                Log("Write complete.");
                Log("Waiting for the radio to re-enumerate for a post-write verification backup...");
                SetBusy(true, "Waiting for radio to re-enumerate...");
                if (WaitForPortToReturn(port, TimeSpan.FromSeconds(45)))
                {
                    Log("Radio is back - taking post-write backup...");
                    using var afterRadio = new AnyToneD878Transport(port);
                    afterRadio.Open();
                    afterRadio.StartProgrammingSession();
                    afterRadio.ReadDeviceId();
                    var afterResults = AnyToneD878MemoryDumper.Dump(afterRadio,
                        Path.Combine(sessionFolder, "baseline_after.bin"),
                        Path.Combine(sessionFolder, "baseline_after.manifest.csv"),
                        (region, index, total, bytesDone, totalBytes) =>
                        {
                            SetProgress(bytesDone, totalBytes, $"Post-write backup... [{index}/{total}] {region.Name} ({bytesDone:N0}/{totalBytes:N0} bytes)");
                            if (index % 40 == 0 || index == total) Log($"  post-write [{index}/{total}] {region.Name}");
                        });
                    afterRadio.EndProgrammingSession();
                    var failed = afterResults.Count(r => !r.Succeeded);
                    auditLog.LogNote($"Post-write backup: {afterResults.Count} regions, {failed} failed.");
                    Log($"Post-write backup complete ({afterResults.Count} regions, {failed} failed).");
                    SetConnectionStatus(true, DescribeDevice(deviceId, port));
                }
                else
                {
                    Log("WARNING: radio did not re-enumerate within 45s. No post-write backup taken.");
                    auditLog.LogNote("WARNING: radio did not re-enumerate within 45s after write - no post-write backup taken.");
                    SetConnectionStatus(false, "Not connected - radio did not re-enumerate.");
                }

                if (sharedBlockFailures.Count > 0)
                {
                    auditLog.End("failed", string.Join(" | ", sharedBlockFailures));
                    return (Success: false,
                        Message: $"Write mostly completed ({regions.Count} regions, {totalBytes:N0} bytes) but {sharedBlockFailures.Count} shared block(s) failed to verify even after retries - check the radio before writing again. Use Send Diagnostic Report to have this investigated.",
                        Committed: committed);
                }

                auditLog.End("success");
                return (Success: true, Message: $"Write complete - {regions.Count} regions, {totalBytes:N0} bytes.", Committed: committed);
            }
            catch (Exception ex)
            {
                Log($"Error: {ex.Message}");
                if (committed)
                    Log("The write had already committed to the radio before this error - use Restore Previous Codeplug if anything looks wrong.");
                auditLog.End("failed", ex.Message);
                return (Success: false, Message: ex.Message, Committed: committed);
            }
        });

        SetRebootNoticeVisible(false);
        SetComplete(result.Success, result.Message);
        SetBusy(false);
    }

    /// <summary>Routine writes stopped including the talkgroup list (it's large, changes rarely -
    /// see AnyToneD878CodeplugEncoder's remarks on Build's includeTalkGroups parameter), so a
    /// channel referencing a contact added/removed/reordered since the last "Sync Reference Data"
    /// run may point at something the radio's copy of the list doesn't actually have at that
    /// position - the exact shape of the old TG1-fallback bug, just from a different cause. This
    /// checks a cheap fingerprint before writing and asks rather than writing something wrong
    /// silently.</summary>
    private async Task<bool> ConfirmContactsFreshEnoughAsync()
    {
        (long Count, long MaxId) current, lastSynced;
        try
        {
            using var db = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);
            current = AnyToneD878CodeplugEncoder.GetContactIndexFingerprint(db);
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT LastSyncedContactCount, LastSyncedMaxContactId FROM RadioSettings WHERE Id = 1;";
            using var reader = cmd.ExecuteReader();
            reader.Read();
            lastSynced = (reader.GetInt64(0), reader.GetInt64(1));
        }
        catch (Exception ex)
        {
            Log($"Could not check contact sync status (continuing anyway): {ex.Message}");
            return true;
        }

        if (current == lastSynced) return true;

        var dialog = new ContentDialog
        {
            Title = $"{Glyphs.IconAlert}  Contacts Not Synced",
            Content = "Your talkgroups/contacts have changed since the last time you ran " +
                      "\"Sync Reference Data\" (Radio page). This write won't touch the radio's " +
                      "talkgroup list, so any channel using a contact added or removed since then " +
                      "may show the wrong (or no) name until you sync. Write anyway, or sync first?",
            PrimaryButtonText = "Write Anyway",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>Polls for the port to disappear (the USB drop after EndProgrammingSession) and
    /// then reappear, rather than assuming a fixed delay - observed re-enumeration time varies
    /// roughly 10-25s across write cycles.</summary>
    private static bool WaitForPortToReturn(string port, TimeSpan timeout)
    {
        var deadline = DateTime.Now + timeout;
        while (DateTime.Now < deadline && SerialPort.GetPortNames().Contains(port))
            Thread.Sleep(500);
        while (DateTime.Now < deadline)
        {
            if (SerialPort.GetPortNames().Contains(port)) return true;
            Thread.Sleep(500);
        }
        return SerialPort.GetPortNames().Contains(port);
    }

    private async void OnSendReportClicked(object sender, RoutedEventArgs e)
    {
        if (_operationInProgress) return;

        var sessionFolder = _lastDiagnosticsSession;

        if (sessionFolder is null || !Directory.Exists(sessionFolder))
        {
            if (SelectedPort is not { } port)
            {
                SetComplete(false, "Select a port first, or write a codeplug, before sending a report.");
                return;
            }

            var confirm = new ContentDialog
            {
                Title = "Send Diagnostic Report",
                Content = "No write session has run yet. Take a fresh backup from the radio now to include in the report?",
                PrimaryButtonText = "Backup and Send",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot,
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

            SetBusy(true, "Backing up radio memory for report...");
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var folder = Path.Combine(AppPaths.DiagnosticsDirectory, $"{stamp}_manual-report");
            Directory.CreateDirectory(folder);
            var toolVersion = typeof(RadioPage).Assembly.GetName().Version?.ToString() ?? "dev";

            var backupOk = await Task.Run(() =>
            {
                using var auditLog = WriteSessionAuditLog.Start(
                    Path.Combine(folder, "audit.jsonl"), "manual-report", port, null, toolVersion);
                try
                {
                    using var radio = new AnyToneD878Transport(port);
                    radio.Open();
                    radio.StartProgrammingSession();
                    var deviceId = radio.ReadDeviceId();
                    auditLog.LogNote($"Device identifier: {deviceId}");
                    SetConnectionStatus(true, DescribeDevice(deviceId, port));
                    var results = AnyToneD878MemoryDumper.Dump(radio,
                        Path.Combine(folder, "backup.bin"), Path.Combine(folder, "backup.manifest.csv"),
                        (region, index, total, bytesDone, totalBytes) =>
                        {
                            SetProgress(bytesDone, totalBytes, $"Backing up... [{index}/{total}] {region.Name} ({bytesDone:N0}/{totalBytes:N0} bytes)");
                            if (index % 40 == 0 || index == total) Log($"  [{index}/{total}] {region.Name}");
                        });
                    radio.EndProgrammingSession();
                    var failed = results.Count(r => !r.Succeeded);
                    auditLog.LogNote($"Backup: {results.Count} regions, {failed} failed.");
                    auditLog.End("success");
                    return true;
                }
                catch (Exception ex)
                {
                    Log($"Error: {ex.Message}");
                    auditLog.End("failed", ex.Message);
                    return false;
                }
            });

            SetBusy(false);
            if (!backupOk)
            {
                SetComplete(false, "Backup failed - report not sent.");
                return;
            }
            // Fire-and-forget: nothing else in this flow needs the radio again (the rest is
            // packaging/upload), so there's no reason to make sending the report wait on it -
            // it just needs to disable the radio buttons in the background until settled.
            _ = WatchRadioSettleAsync(port, "after the backup");
            sessionFolder = folder;
            _lastDiagnosticsSession = folder;
        }

        var messageDialog = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 100,
            PlaceholderText = "What happened? (optional, but helpful)",
        };
        var confirmSend = new ContentDialog
        {
            Title = "Send Diagnostic Report",
            Content = messageDialog,
            PrimaryButtonText = "Send",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };
        if (await confirmSend.ShowAsync() != ContentDialogResult.Primary) return;

        SetBusy(true, "Sending diagnostic report...");
        Log("Packaging and uploading diagnostic report...");

        try
        {
            var zipPath = DiagnosticPackager.CreateZip(sessionFolder);
            var deviceIdForReport = Path.GetFileName(sessionFolder);
            var toolVersion = typeof(RadioPage).Assembly.GetName().Version?.ToString() ?? "dev";
            await DiagnosticsUploader.UploadAsync(zipPath, deviceIdForReport, toolVersion, messageDialog.Text);
            Log("Diagnostic report sent.");
            SetComplete(true, "Diagnostic report sent - thank you.");
        }
        catch (Exception ex)
        {
            Log($"Failed to send diagnostic report: {ex.Message}");
            SetComplete(false, $"Failed to send report: {ex.Message}");
        }

        SetBusy(false);
    }

    private async void OnRestoreClicked(object sender, RoutedEventArgs e)
    {
        if (_operationInProgress) return;
        if (SelectedPort is not { } port)
        {
            SetComplete(false, "Select a port first.");
            return;
        }

        // Every past Write Codeplug run left its own pre-write baseline on disk (see
        // RestorePointPicker's remarks) - not just the most recent one from this app session, so
        // this can go back further than "undo my last action" (e.g. a week, if that backup is
        // still on disk). Restores to whichever port is selected NOW, which may not be the same
        // port name the original write used.
        var candidates = RestorePointPicker.Scan(AppPaths.DiagnosticsDirectory);
        if (candidates.Count == 0)
        {
            SetComplete(false, "No Write Codeplug run has been backed up yet - Backup Radio Memory alone doesn't create a restore point (it doesn't write anything, so there's nothing to undo). Run Write Codeplug at least once first.");
            return;
        }

        var picked = await RestorePointPicker.ShowAsync(this.XamlRoot, candidates);
        if (picked is null) return;
        var sessionFolder = picked.FolderPath;

        var confirm = new ContentDialog
        {
            Title = "Restore Codeplug",
            Content = $"This writes back exactly what the {picked.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm} " +
                      "Write Codeplug run overwrote, using the automatic backup taken just before it - " +
                      "undoing that write. It does not touch anything that write didn't touch. Continue?",
            PrimaryButtonText = "Restore",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        SetBusy(true, "Restoring previous codeplug...");
        Log($"Restoring from the pre-write baseline backup in {sessionFolder}...");

        var result = await Task.Run(() =>
        {
            using var auditLog = WriteSessionAuditLog.Start(
                Path.Combine(sessionFolder, "restore_audit.jsonl"), "restore-codeplug", port, null,
                typeof(RadioPage).Assembly.GetName().Version?.ToString() ?? "dev");
            try
            {
                var writtenRegions = WriteSessionAuditLog.ReadWrittenRegions(Path.Combine(sessionFolder, "audit.jsonl"));
                if (writtenRegions.Count == 0)
                {
                    // "Nothing recorded as written" is an inference the radio is still fine, not
                    // proof - found the hard way (2026-07-22) when a write failed before any
                    // WriteMemory call: the honest thing to do is actually check, the same way that
                    // failure's radio state was verified (a fresh dump, byte-diffed against the
                    // pre-write baseline), rather than just reporting silence.
                    Log("Nothing was recorded as written in that session - taking a fresh dump to verify the radio still matches its pre-write baseline...");
                    auditLog.LogNote("Nothing recorded as written - verifying against baseline instead of restoring.");

                    var baselineForVerify = DumpReader.Load(
                        Path.Combine(sessionFolder, "baseline_before.bin"),
                        Path.Combine(sessionFolder, "baseline_before.manifest.csv"));

                    using (var verifyRadio = new AnyToneD878Transport(port))
                    {
                        Log($"Opening {port}...");
                        verifyRadio.Open();
                        verifyRadio.StartProgrammingSession();
                        var verifyDeviceId = verifyRadio.ReadDeviceId();
                        SetConnectionStatus(true, DescribeDevice(verifyDeviceId, port));

                        var verifyResults = AnyToneD878MemoryDumper.Dump(verifyRadio,
                            Path.Combine(sessionFolder, "verify_now.bin"),
                            Path.Combine(sessionFolder, "verify_now.manifest.csv"),
                            (region, index, total, bytesDone, totalBytes) =>
                            {
                                SetProgress(bytesDone, totalBytes, $"Verifying... [{index}/{total}] {region.Name} ({bytesDone:N0}/{totalBytes:N0} bytes)");
                                if (index % 40 == 0 || index == total) Log($"  verify [{index}/{total}] {region.Name}");
                            });
                        verifyRadio.EndProgrammingSession();

                        var verifyFailed = verifyResults.Count(r => !r.Succeeded);
                        auditLog.LogNote($"Verification dump: {verifyResults.Count} regions, {verifyFailed} failed.");
                    }

                    var freshDump = DumpReader.Load(
                        Path.Combine(sessionFolder, "verify_now.bin"),
                        Path.Combine(sessionFolder, "verify_now.manifest.csv"));
                    var comparison = DumpComparer.Compare(baselineForVerify, freshDump);

                    if (comparison.AllMatch)
                    {
                        Log($"Verified: {comparison.RegionsCompared} region(s) match the pre-write baseline exactly. The radio is unchanged.");
                        auditLog.End("success", $"Verified match: {comparison.RegionsCompared} regions, 0 mismatches.");
                        return (Success: true, Message: $"Verified - radio matches its pre-write baseline exactly ({comparison.RegionsCompared} regions compared).");
                    }

                    Log($"WARNING: {comparison.MismatchedRegionNames.Count} region(s) DIFFER from the pre-write baseline despite nothing being recorded as written:");
                    foreach (var name in comparison.MismatchedRegionNames) Log($"  MISMATCH: {name}");
                    auditLog.End("failed", $"{comparison.MismatchedRegionNames.Count} region(s) differ from baseline.");
                    return (Success: false,
                        Message: $"WARNING - {comparison.MismatchedRegionNames.Count} region(s) differ from the pre-write baseline even though nothing was recorded as written. Do not assume the radio is in a good state.");
                }

                var baseline = DumpReader.Load(
                    Path.Combine(sessionFolder, "baseline_before.bin"),
                    Path.Combine(sessionFolder, "baseline_before.manifest.csv"));

                List<RestoredRegion> restored;
                using (var radio = new AnyToneD878Transport(port))
                {
                    Log($"Opening {port}...");
                    radio.Open();
                    radio.StartProgrammingSession();
                    var restoreDeviceId = radio.ReadDeviceId();
                    SetConnectionStatus(true, DescribeDevice(restoreDeviceId, port));

                    restored = AnyToneD878CodeplugRestorer.RestorePlainRegions(radio, baseline, writtenRegions, (region, index, total) =>
                    {
                        if (region.Skipped)
                        {
                            Log($"  [{index}/{total}] {region.Name}: skipped - {region.SkipReason}");
                            auditLog.LogRegion(region.Name, region.Address, region.Length, "skipped", region.SkipReason);
                        }
                        else
                        {
                            Log($"  [{index}/{total}] {region.Name}: restored");
                            auditLog.LogRegion(region.Name, region.Address, region.Length, "restored");
                        }
                        SetProgress(index, total, $"Restoring... [{index}/{total}] {region.Name}");
                    });

                    Log("Ending programming session (this commits the restore)...");
                    radio.EndProgrammingSession();
                    radio.Close();
                }

                // Same isolated-session-plus-verify treatment as a fresh write. ZoneChannelDefaults
                // MUST be restored LAST - proven on real hardware (2026-07-19) that
                // GeneralUsedBitmapsBlock is physically contiguous with ZoneChannelDefaults on the
                // same flash die (0x024C0000 + its length == 0x02500000 exactly) and writing it
                // disturbs/corrupts an already-good, untouched ZoneChannelDefaults - session
                // isolation and verify-retry on ZoneChannelDefaults itself can't help if something
                // else writes its neighbor afterward. RoamingBlock is at a different address and
                // confirmed NOT to cause this.
                var sharedBlockFailures = new List<string>();
                RestoreSharedBlockAndCheck("RoamingBlock", AnyToneD878CodeplugRestorer.HasRoamingBlock(writtenRegions),
                    r => AnyToneD878CodeplugRestorer.BuildRestoredRoamingBlock(r, baseline));
                RestoreSharedBlockAndCheck("GeneralUsedBitmapsBlock", AnyToneD878CodeplugRestorer.HasGeneralUsedBitmapsBlock(writtenRegions),
                    r => AnyToneD878CodeplugRestorer.BuildRestoredGeneralUsedBitmapsBlock(r, baseline));
                RestoreSharedBlockAndCheck("ZoneChannelDefaults", AnyToneD878CodeplugRestorer.HasZoneChannelDefaults(writtenRegions),
                    r => AnyToneD878CodeplugRestorer.BuildRestoredZoneChannelDefaults(r, baseline));

                void RestoreSharedBlockAndCheck(string label, bool needed, Func<AnyToneD878Transport, EncodedRegion> build)
                {
                    if (!needed) return;

                    SetBusy(true, $"Restoring and verifying {label}...");
                    var writeResult = AnyToneD878CodeplugWriter.WriteAndVerifySharedBlock(port, build, msg =>
                    {
                        Log($"  [{label}] {msg}");
                        auditLog.LogNote($"{label}: {msg}");
                    });

                    if (writeResult.Verified)
                    {
                        auditLog.LogRegion(label, writeResult.Address, writeResult.Length, $"restored and verified (attempt {writeResult.Attempts})");
                        restored.Add(new RestoredRegion(label, writeResult.Address, writeResult.Length, false, null));
                    }
                    else
                    {
                        sharedBlockFailures.Add(writeResult.Error ?? $"{label} failed to verify.");
                        auditLog.LogNote($"FAILED: {writeResult.Error}");
                    }
                }

                if (sharedBlockFailures.Count > 0)
                {
                    Log("WARNING: one or more shared blocks did not restore correctly even after automatic retries:");
                    foreach (var f in sharedBlockFailures) Log($"  {f}");
                    Log("Do not assume the radio is in a good state - check it before further writes.");
                }

                var skipped = restored.Count(r => r.Skipped);
                Log($"Restore complete: {restored.Count - skipped} region(s) restored, {skipped} skipped.");
                auditLog.LogNote(WriteSessionAuditLog.CommitConfirmedMessage);

                // Restore's whole point is to bring the radio back to exactly the pre-write
                // baseline - so that baseline is also the correctness check: comparing the fresh
                // post-restore dump against it proves the restore actually worked, rather than
                // just reporting that the write-back commands completed without error (which the
                // 2026-07-19/20/21 erase-block-disturb incidents proved is not the same thing).
                var restoreVerified = false;
                DumpComparisonResult? restoreComparison = null;
                if (WaitForPortToReturn(port, TimeSpan.FromSeconds(45)))
                {
                    Log("Radio is back - taking post-restore verification backup...");
                    using var afterRadio = new AnyToneD878Transport(port);
                    afterRadio.Open();
                    afterRadio.StartProgrammingSession();
                    afterRadio.ReadDeviceId();
                    var afterResults = AnyToneD878MemoryDumper.Dump(afterRadio,
                        Path.Combine(sessionFolder, "baseline_after_restore.bin"),
                        Path.Combine(sessionFolder, "baseline_after_restore.manifest.csv"),
                        (region, index, total, bytesDone, totalBytes) =>
                        {
                            SetProgress(bytesDone, totalBytes, $"Post-restore backup... [{index}/{total}] {region.Name} ({bytesDone:N0}/{totalBytes:N0} bytes)");
                            if (index % 40 == 0 || index == total) Log($"  post-restore [{index}/{total}] {region.Name}");
                        });
                    afterRadio.EndProgrammingSession();
                    var failed = afterResults.Count(r => !r.Succeeded);
                    auditLog.LogNote($"Post-restore backup: {afterResults.Count} regions, {failed} failed.");

                    var beforeBaseline = DumpReader.Load(
                        Path.Combine(sessionFolder, "baseline_before.bin"),
                        Path.Combine(sessionFolder, "baseline_before.manifest.csv"));
                    var afterRestoreDump = DumpReader.Load(
                        Path.Combine(sessionFolder, "baseline_after_restore.bin"),
                        Path.Combine(sessionFolder, "baseline_after_restore.manifest.csv"));
                    restoreComparison = DumpComparer.Compare(beforeBaseline, afterRestoreDump);
                    restoreVerified = restoreComparison.AllMatch;

                    if (restoreVerified)
                    {
                        Log($"Verified: all {restoreComparison.RegionsCompared} compared region(s) now match the pre-write baseline exactly.");
                    }
                    else
                    {
                        Log($"WARNING: {restoreComparison.MismatchedRegionNames.Count} region(s) still differ from the pre-write baseline after restore:");
                        foreach (var name in restoreComparison.MismatchedRegionNames) Log($"  MISMATCH: {name}");
                    }
                    auditLog.LogNote($"Post-restore verification: {restoreComparison.RegionsCompared} compared, {restoreComparison.MismatchedRegionNames.Count} mismatch(es).");
                }
                else
                {
                    Log("WARNING: radio did not re-enumerate within 45s after restore.");
                    auditLog.LogNote("WARNING: radio did not re-enumerate within 45s after restore.");
                }

                if (sharedBlockFailures.Count > 0)
                {
                    auditLog.End("failed", string.Join(" | ", sharedBlockFailures));
                    return (Success: false,
                        Message: $"Restore mostly completed ({restored.Count - skipped} region(s)) but {sharedBlockFailures.Count} shared block(s) failed to verify even after retries - check the radio before writing again. Use Send Diagnostic Report to have this investigated.");
                }

                if (!restoreVerified)
                {
                    var mismatchCount = restoreComparison?.MismatchedRegionNames.Count ?? 0;
                    auditLog.End("failed", $"{mismatchCount} region(s) still differ from baseline after restore.");
                    return (Success: false,
                        Message: mismatchCount > 0
                            ? $"Restore completed but {mismatchCount} region(s) still differ from the pre-write baseline - do not assume the radio matches. Use Send Diagnostic Report to have this investigated."
                            : "Restore completed but could not be verified (radio didn't re-enumerate in time) - check it before assuming it matches the pre-write baseline.");
                }

                auditLog.End("success");
                return (Success: true, Message: $"Restore complete and verified: {restored.Count - skipped} region(s) restored, radio now matches its pre-write baseline.");
            }
            catch (Exception ex)
            {
                Log($"Error: {ex.Message}");
                auditLog.End("failed", ex.Message);
                return (Success: false, Message: ex.Message);
            }
        });

        // Was never set anywhere in this method - Send Diagnostic Report stayed disabled after
        // every restore, success or failure, so a failed restore's "see the log for details" had
        // no way to actually get that log to anyone without the user manually copying text out of
        // a panel most people don't know is there. Set unconditionally (not just on failure) so
        // it's available either way, matching what Write Codeplug already does.
        _lastDiagnosticsSession = sessionFolder;
        SetComplete(result.Success, result.Message);
        SetBusy(false);
    }

    /// <summary>Writes an ENTIRE arbitrary full memory dump (from Backup Radio Memory, or any past
    /// diagnostics-folder dump) back to the radio - the actual counterpart Backup Radio Memory was
    /// missing (Restore Previous Codeplug only ever undoes one specific Write Codeplug session's
    /// own automatic baseline, it can't consume a manual backup at all). Reuses the exact same
    /// tiered writer machinery Write Codeplug/Restore Previous Codeplug already use (plain bundled
    /// session, isolated-verified per-bank sessions for ChannelBank[]/TalkGroupList[], and the
    /// three shared-block regions last in their required order) - just driven by "every region the
    /// chosen dump captured" instead of "what one write session's audit log says it touched".</summary>
    private async void OnRestoreRadioMemoryClicked(object sender, RoutedEventArgs e)
    {
        if (_operationInProgress) return;
        if (SelectedPort is not { } port)
        {
            SetComplete(false, "Select a port first.");
            return;
        }

        string binPath, manifestPath;
        var candidates = MemoryDumpPicker.Scan(AppPaths.DumpsDirectory, AppPaths.DiagnosticsDirectory);
        if (candidates.Count > 0)
        {
            var picked = await MemoryDumpPicker.ShowAsync(this.XamlRoot, candidates);
            if (picked is null) return;
            binPath = picked.BinPath;
            manifestPath = picked.ManifestPath;
        }
        else
        {
            Log("No memory dumps found yet (Backup Radio Memory or any diagnostics session) - browsing for a file instead...");
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".bin");
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder;
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;

            binPath = file.Path;
            manifestPath = Path.Combine(Path.GetDirectoryName(binPath)!, Path.GetFileNameWithoutExtension(binPath) + ".manifest.csv");
            if (!File.Exists(manifestPath))
            {
                SetComplete(false, $"No matching .manifest.csv found next to {Path.GetFileName(binPath)} - pick a .bin file produced by Backup Radio Memory or another HamNetProgrammer dump.");
                return;
            }
        }

        DumpReader dump;
        try
        {
            dump = DumpReader.Load(binPath, manifestPath);
        }
        catch (Exception ex)
        {
            SetComplete(false, $"Could not read that dump: {ex.Message}");
            return;
        }

        SetBusy(true, "Identifying radio...");
        Log($"Opening {port} to identify the radio...");

        (AnyToneD878Transport Radio, string DeviceId) identified;
        try
        {
            identified = await Task.Run(() =>
            {
                var r = new AnyToneD878Transport(port);
                r.Open();
                r.StartProgrammingSession();
                var id = r.ReadDeviceId();
                return (r, id);
            });
        }
        catch (Exception ex)
        {
            Log($"Error identifying device: {ex.Message}");
            SetConnectionStatus(false, "Not connected.");
            SetComplete(false, $"Could not identify device: {ex.Message}");
            SetBusy(false);
            return;
        }

        var radio = identified.Radio;
        var deviceId = identified.DeviceId;
        var profile = RadioRiskCatalog.Lookup(deviceId);
        Log($"Device identifier: {deviceId} ({profile.ModelLabel}, {profile.Tier} risk).");
        SetConnectionStatus(true, DescribeDevice(deviceId, port));
        SetBusy(false);

        // Same hard block as Write Codeplug - restoring an arbitrary full dump onto an unvalidated
        // model is at least as risky (blind whole-region replacement, no per-field sanity checks at
        // all), so it gets no weaker a gate.
        if (profile.Tier != RadioRiskTier.Validated)
        {
            Log($"Restore Radio Memory refused: {profile.ModelLabel} is {profile.Tier} risk, not hardware-verified.");
            try
            {
                await Task.Run(() => { radio.EndProgrammingSession(); radio.Close(); });
            }
            catch (Exception ex)
            {
                Log($"Error releasing radio: {ex.Message}");
            }
            SetComplete(false, $"{profile.ModelLabel} isn't a hardware-verified model for Restore Radio Memory. Use \"Contribute a Memory Sample\" instead so this model can be properly validated first.");
            return;
        }

        var regionCount = dump.RegionNames.Count(dump.RegionSucceeded);
        var ackBox = new TextBox { PlaceholderText = "OVERWRITE RADIO" };
        var panel = new StackPanel { Spacing = 10, MaxWidth = 460 };
        panel.Children.Add(new TextBlock
        {
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0xe9, 0x45, 0x60)),
            TextWrapping = TextWrapping.Wrap,
            Text = $"This overwrites the radio's ENTIRE current configuration with the {regionCount}-region dump in {Path.GetFileName(binPath)}. Everything currently on the radio that isn't captured in this file will be lost.",
        });
        panel.Children.Add(new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Text = "A full backup of the radio's CURRENT state will be taken automatically first, before anything is written, as a safety net.",
        });
        panel.Children.Add(new TextBlock { FontSize = 12, Text = "Type \"OVERWRITE RADIO\" to continue:" });
        panel.Children.Add(ackBox);

        var confirmDialog = new ContentDialog
        {
            Title = $"{Glyphs.IconAlert}  Restore Radio Memory",
            Content = panel,
            PrimaryButtonText = "Restore",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot,
        };
        confirmDialog.PrimaryButtonClick += (_, args) =>
        {
            if (!string.Equals(ackBox.Text.Trim(), "OVERWRITE RADIO", StringComparison.OrdinalIgnoreCase))
                args.Cancel = true;
        };

        if (await confirmDialog.ShowAsync() != ContentDialogResult.Primary)
        {
            Log("Restore Radio Memory cancelled - releasing the radio...");
            try
            {
                await Task.Run(() => { radio.EndProgrammingSession(); radio.Close(); });
            }
            catch (Exception ex)
            {
                Log($"Error releasing radio: {ex.Message}");
            }
            return;
        }

        SetRebootNoticeVisible(true);

        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var safeDeviceId = string.Join("_", deviceId.Split(Path.GetInvalidFileNameChars()));
        var sessionFolder = Path.Combine(AppPaths.DiagnosticsDirectory, $"{stamp}_restoremem_{safeDeviceId}");
        Directory.CreateDirectory(sessionFolder);
        _lastDiagnosticsSession = sessionFolder;
        var toolVersion = typeof(RadioPage).Assembly.GetName().Version?.ToString() ?? "dev";

        var result = await Task.Run(() =>
        {
            using var auditLog = WriteSessionAuditLog.Start(
                Path.Combine(sessionFolder, "audit.jsonl"), "restore-radio-memory", port, deviceId, toolVersion);
            var committed = false;

            try
            {
                Log("Taking a safety backup of the radio's current state before restoring...");
                auditLog.LogNote("Taking pre-restore safety backup.");
                var safetyResults = AnyToneD878MemoryDumper.Dump(radio,
                    Path.Combine(sessionFolder, "pre_restore_backup.bin"),
                    Path.Combine(sessionFolder, "pre_restore_backup.manifest.csv"),
                    (region, index, total, bytesDone, totalBytes) =>
                    {
                        SetProgress(bytesDone, totalBytes, $"Safety backup before restoring... [{index}/{total}] {region.Name}");
                        if (index % 40 == 0 || index == total) Log($"  safety backup [{index}/{total}] {region.Name}");
                    });
                var safetyFailed = safetyResults.Count(r => !r.Succeeded);
                Log($"Safety backup complete ({safetyResults.Count} regions, {safetyFailed} failed). Same session, no reboot needed yet.");
                auditLog.LogNote($"Safety backup: {safetyResults.Count} regions, {safetyFailed} failed.");

                var plainRegions = AnyToneD878CodeplugRestorer.PlainRegionsForFullRestore(dump);
                Log($"Restoring {plainRegions.Count} plain region(s) in this session...");
                var restored = AnyToneD878CodeplugRestorer.RestorePlainRegions(radio, dump, plainRegions, (region, index, total) =>
                {
                    if (region.Skipped)
                    {
                        Log($"  [{index}/{total}] {region.Name}: skipped - {region.SkipReason}");
                        auditLog.LogRegion(region.Name, region.Address, region.Length, "skipped", region.SkipReason);
                    }
                    else
                    {
                        auditLog.LogRegion(region.Name, region.Address, region.Length, "written");
                    }
                    SetProgress(index, total, $"Restoring... [{index}/{total}] {region.Name}");
                });

                Log("Ending programming session (this commits the plain-region restore)...");
                radio.EndProgrammingSession();
                radio.Close();
                committed = true;
                auditLog.LogNote(WriteSessionAuditLog.CommitConfirmedMessage);

                var sharedBlockFailures = new List<string>();

                var bankFailures = new List<string>();
                foreach (var bankName in dump.RegionNames.Where(n => n.StartsWith("ChannelBank[", StringComparison.Ordinal)).OrderBy(n => n))
                {
                    if (!dump.RegionSucceeded(bankName))
                    {
                        Log($"  Skipping {bankName} - not captured successfully in this dump.");
                        continue;
                    }
                    SetBusy(true, $"Restoring and verifying {bankName}...");
                    var region = new EncodedRegion(bankName, dump.GetRegionAddress(bankName), dump.GetRegion(bankName).ToArray());
                    var verified = AnyToneD878CodeplugWriter.WriteRegionChunkedAndVerify(port, region, 20_000, msg =>
                    {
                        Log($"  {msg}");
                        auditLog.LogNote(msg);
                    });
                    if (verified)
                        auditLog.LogRegion(bankName, region.Address, region.Data.Length, "written and verified");
                    else
                        bankFailures.Add($"{bankName} did not verify correctly.");
                }

                foreach (var bankName in dump.RegionNames.Where(n => n.StartsWith("TalkGroupList[", StringComparison.Ordinal)).OrderBy(n => n))
                {
                    if (!dump.RegionSucceeded(bankName))
                    {
                        Log($"  Skipping {bankName} - not captured successfully in this dump.");
                        continue;
                    }
                    SetBusy(true, $"Restoring and verifying {bankName}...");
                    var region = new EncodedRegion(bankName, dump.GetRegionAddress(bankName), dump.GetRegion(bankName).ToArray());
                    var verified = AnyToneD878CodeplugWriter.WriteRegionChunkedAndVerify(port, region, 200_000, msg =>
                    {
                        Log($"  {msg}");
                        auditLog.LogNote(msg);
                    });
                    if (verified)
                        auditLog.LogRegion(bankName, region.Address, region.Data.Length, "written and verified");
                    else
                        bankFailures.Add($"{bankName} did not verify correctly.");
                }

                if (bankFailures.Count > 0)
                    sharedBlockFailures.AddRange(bankFailures);

                // Required order - ZoneChannelDefaults MUST be restored LAST, same reason as
                // Write Codeplug/Restore Previous Codeplug (GeneralUsedBitmapsBlock is physically
                // contiguous with it on the same flash die and disturbs it as a side effect if
                // written afterward).
                WriteSharedBlockAndCheck("RoamingBlock", AnyToneD878CodeplugRestorer.DumpHasRoamingBlockData(dump),
                    r => AnyToneD878CodeplugRestorer.BuildRestoredRoamingBlock(r, dump));
                WriteSharedBlockAndCheck("GeneralUsedBitmapsBlock", AnyToneD878CodeplugRestorer.DumpHasGeneralUsedBitmapsBlockData(dump),
                    r => AnyToneD878CodeplugRestorer.BuildRestoredGeneralUsedBitmapsBlock(r, dump));
                WriteSharedBlockAndCheck("ZoneChannelDefaults", AnyToneD878CodeplugRestorer.DumpHasZoneChannelDefaultsData(dump),
                    r => AnyToneD878CodeplugRestorer.BuildRestoredZoneChannelDefaults(r, dump));

                void WriteSharedBlockAndCheck(string label, bool needed, Func<AnyToneD878Transport, EncodedRegion> build)
                {
                    if (!needed) return;
                    SetBusy(true, $"Restoring and verifying {label}...");
                    var writeResult = AnyToneD878CodeplugWriter.WriteAndVerifySharedBlock(port, build, msg =>
                    {
                        Log($"  [{label}] {msg}");
                        auditLog.LogNote($"{label}: {msg}");
                    });
                    if (writeResult.Verified)
                        auditLog.LogRegion(label, writeResult.Address, writeResult.Length, $"written and verified (attempt {writeResult.Attempts})");
                    else
                    {
                        sharedBlockFailures.Add(writeResult.Error ?? $"{label} failed to verify.");
                        auditLog.LogNote($"FAILED: {writeResult.Error}");
                    }
                }

                Log("Restore complete. Waiting for the radio to re-enumerate for a verification pass...");
                SetBusy(true, "Waiting for radio to re-enumerate...");

                var verified2 = false;
                DumpComparisonResult? comparison = null;
                if (WaitForPortToReturn(port, TimeSpan.FromSeconds(45)))
                {
                    Log("Radio is back - taking verification backup...");
                    using var afterRadio = new AnyToneD878Transport(port);
                    afterRadio.Open();
                    afterRadio.StartProgrammingSession();
                    afterRadio.ReadDeviceId();
                    var afterResults = AnyToneD878MemoryDumper.Dump(afterRadio,
                        Path.Combine(sessionFolder, "post_restore.bin"),
                        Path.Combine(sessionFolder, "post_restore.manifest.csv"),
                        (region, index, total, bytesDone, totalBytes) =>
                        {
                            SetProgress(bytesDone, totalBytes, $"Verifying... [{index}/{total}] {region.Name}");
                            if (index % 40 == 0 || index == total) Log($"  verify [{index}/{total}] {region.Name}");
                        });
                    afterRadio.EndProgrammingSession();
                    var failed = afterResults.Count(r => !r.Succeeded);
                    auditLog.LogNote($"Post-restore backup: {afterResults.Count} regions, {failed} failed.");

                    var afterDump = DumpReader.Load(
                        Path.Combine(sessionFolder, "post_restore.bin"),
                        Path.Combine(sessionFolder, "post_restore.manifest.csv"));
                    comparison = DumpComparer.Compare(dump, afterDump);
                    verified2 = comparison.AllMatch;

                    if (verified2)
                        Log($"Verified: all {comparison.RegionsCompared} compared region(s) now match the chosen dump exactly.");
                    else
                    {
                        Log($"WARNING: {comparison.MismatchedRegionNames.Count} region(s) differ from the chosen dump:");
                        foreach (var name in comparison.MismatchedRegionNames) Log($"  MISMATCH: {name}");
                    }
                    auditLog.LogNote($"Post-restore verification: {comparison.RegionsCompared} compared, {comparison.MismatchedRegionNames.Count} mismatch(es).");
                    SetConnectionStatus(true, DescribeDevice(deviceId, port));
                }
                else
                {
                    Log("WARNING: radio did not re-enumerate within 45s after restore.");
                    auditLog.LogNote("WARNING: radio did not re-enumerate within 45s after restore.");
                    SetConnectionStatus(false, "Not connected - radio did not re-enumerate.");
                }

                if (sharedBlockFailures.Count > 0)
                {
                    Log("WARNING: one or more regions did not verify correctly even after automatic retries:");
                    foreach (var f in sharedBlockFailures) Log($"  {f}");
                    auditLog.End("failed", string.Join(" | ", sharedBlockFailures));
                    return (Success: false,
                        Message: $"Restore mostly completed but {sharedBlockFailures.Count} region(s) failed to verify - a pre-restore safety backup was taken (pre_restore_backup.bin in this session's diagnostics folder). Use Send Diagnostic Report to have this investigated.");
                }

                if (!verified2)
                {
                    var mismatchCount = comparison?.MismatchedRegionNames.Count ?? 0;
                    auditLog.End("failed", $"{mismatchCount} region(s) differ from the chosen dump after restore.");
                    return (Success: false,
                        Message: mismatchCount > 0
                            ? $"Restore completed but {mismatchCount} region(s) still differ from the chosen dump - do not assume the radio matches it. A pre-restore safety backup was taken if you need to back out. Use Send Diagnostic Report to have this investigated."
                            : "Restore completed but could not be verified (radio didn't re-enumerate in time).");
                }

                auditLog.End("success");
                return (Success: true, Message: $"Restore complete and verified: radio now matches {Path.GetFileName(binPath)}.");
            }
            catch (Exception ex)
            {
                Log($"Error: {ex.Message}");
                if (committed)
                    Log("The restore had already partly committed before this error - a pre-restore safety backup was taken (pre_restore_backup.bin in this session's diagnostics folder).");
                auditLog.End("failed", ex.Message);
                return (Success: false, Message: ex.Message);
            }
        });

        SetRebootNoticeVisible(false);
        SetComplete(result.Success, result.Message);
        SetBusy(false);
    }

    private async void OnBackupClicked(object sender, RoutedEventArgs e)
    {
        if (_operationInProgress) return;
        if (SelectedPort is not { } port)
        {
            SetComplete(false, "Select a port first.");
            return;
        }

        SetBusy(true, "Backing up radio memory...");

        var result = await Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DumpsDirectory);
                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var binPath = Path.Combine(AppPaths.DumpsDirectory, $"d878uv_{stamp}.bin");
                var manifestPath = Path.Combine(AppPaths.DumpsDirectory, $"d878uv_{stamp}.manifest.csv");

                using var radio = new AnyToneD878Transport(port);
                Log($"Opening {port}...");
                radio.Open();
                radio.StartProgrammingSession();
                var deviceId = radio.ReadDeviceId();
                Log($"Device identifier: {deviceId}");
                SetConnectionStatus(true, DescribeDevice(deviceId, port));

                var results = AnyToneD878MemoryDumper.Dump(radio, binPath, manifestPath, (region, index, total, bytesDone, totalBytes) =>
                {
                    SetProgress(bytesDone, totalBytes, $"Backing up... [{index}/{total}] {region.Name} ({bytesDone:N0}/{totalBytes:N0} bytes)");
                    if (index % 20 == 0 || index == total)
                        Log($"  [{index}/{total}] {region.Name}");
                });

                radio.EndProgrammingSession();

                var failed = results.Count(r => !r.Succeeded);
                Log($"Backup complete: {results.Count} regions, {failed} failed. Saved to {binPath}");
                return (Success: true, Message: $"Backup saved: {Path.GetFileName(binPath)} ({failed} region(s) failed)");
            }
            catch (Exception ex)
            {
                Log($"Error: {ex.Message}");
                return (Success: false, Message: ex.Message);
            }
        });

        SetBusy(false);
        if (result.Success)
        {
            SetComplete(true, result.Message);
            await WatchRadioSettleAsync(port, "after the backup");
        }
        else
        {
            SetComplete(false, result.Message);
        }
    }

    /// <summary>Reads the radio (same safe operation as Backup) then decodes that dump back into
    /// the SQLite database via AnyToneD878CodeplugImporter - a good first action for a user who
    /// already has a working radio and wants its configuration as a starting point instead of
    /// building one from scratch. Gated on RadioRiskTier.Validated (hard block, not just a
    /// disclaimer like Write's) - reads are safe on the radio regardless of model, but importing an
    /// unverified model's mis-decoded bytes into the shared database fails invisibly rather than
    /// loudly, unlike a bad write.</summary>
    private async void OnReadCodeplugClicked(object sender, RoutedEventArgs e)
    {
        if (_operationInProgress) return;
        if (SelectedPort is not { } port)
        {
            SetComplete(false, "Select a port first.");
            return;
        }

        SetBusy(true, "Identifying radio...");
        Log($"Opening {port} to identify the radio...");

        string deviceId;
        try
        {
            deviceId = await Task.Run(() =>
            {
                using var radio = new AnyToneD878Transport(port);
                radio.Open();
                try
                {
                    radio.StartProgrammingSession();
                    var id = radio.ReadDeviceId();
                    radio.EndProgrammingSession();
                    return id;
                }
                finally
                {
                    radio.Close();
                }
            });
        }
        catch (Exception ex)
        {
            Log($"Error identifying device: {ex.Message}");
            SetConnectionStatus(false, "Not connected.");
            SetComplete(false, $"Could not identify device: {ex.Message}");
            SetBusy(false);
            return;
        }

        var profile = RadioRiskCatalog.Lookup(deviceId);
        SetConnectionStatus(true, DescribeDevice(deviceId, port));
        SetBusy(false);
        var settleTask = WatchRadioSettleAsync(port, "after being detected");

        if (profile.Tier != RadioRiskTier.Validated)
        {
            Log($"Read Codeplug refused: {profile.ModelLabel} is {profile.Tier} risk, not hardware-verified.");
            await settleTask;
            SetComplete(false, $"{profile.ModelLabel} isn't a hardware-verified model for Read Codeplug - decoding it could silently import wrong data. Use \"Contribute a Memory Sample\" instead, which only reads and never decodes.");
            return;
        }

        var confirm = new ContentDialog
        {
            Title = "Read Codeplug from Radio",
            Content = "This reads the radio (safe, same as Backup), then updates this database: " +
                      "new zones/channels/contacts/lists get added, and anything already here that " +
                      "matches by name or channel number gets its details and membership refreshed " +
                      "to match the radio. Nothing already in the database is deleted. Continue?",
            PrimaryButtonText = "Read",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot,
        };
        await settleTask;
        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            Log("Read Codeplug cancelled.");
            return;
        }

        SetBusy(true, "Reading radio memory...");
        Log($"Opening {port} to read the radio...");

        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var safeDeviceId = string.Join("_", deviceId.Split(Path.GetInvalidFileNameChars()));
        var sessionFolder = Path.Combine(AppPaths.DiagnosticsDirectory, $"{stamp}_read_{safeDeviceId}");
        Directory.CreateDirectory(sessionFolder);
        _lastDiagnosticsSession = sessionFolder;

        var result = await Task.Run(() =>
        {
            try
            {
                var binPath = Path.Combine(sessionFolder, "read.bin");
                var manifestPath = Path.Combine(sessionFolder, "read.manifest.csv");

                using var radio = new AnyToneD878Transport(port);
                radio.Open();
                radio.StartProgrammingSession();
                radio.ReadDeviceId();

                var dumpResults = AnyToneD878MemoryDumper.Dump(radio, binPath, manifestPath, (region, index, total, bytesDone, totalBytes) =>
                {
                    SetProgress(bytesDone, totalBytes, $"Reading... [{index}/{total}] {region.Name} ({bytesDone:N0}/{totalBytes:N0} bytes)");
                    if (index % 40 == 0 || index == total) Log($"  [{index}/{total}] {region.Name}");
                });
                radio.EndProgrammingSession();

                var failed = dumpResults.Count(r => !r.Succeeded);
                Log($"Read complete: {dumpResults.Count} regions, {failed} failed.");

                Log("Decoding and importing into the database...");
                var dump = DumpReader.Load(binPath, manifestPath);
                using var db = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);
                var importResult = AnyToneD878CodeplugImporter.Import(db, dump);

                foreach (var category in importResult.Categories)
                {
                    Log($"  {category.Category}: {category.Matched} matched, {category.New} new, {category.Skipped} skipped.");
                    foreach (var label in category.NewItemLabels)
                        Log($"    + {label}");
                }
                foreach (var warning in importResult.Warnings)
                    Log($"  Warning: {warning}");

                return (Success: true, Message: $"Read complete - {importResult.Categories.Sum(c => c.New)} new, {importResult.Categories.Sum(c => c.Matched)} matched.");
            }
            catch (Exception ex)
            {
                Log($"Error: {ex.Message}");
                return (Success: false, Message: ex.Message);
            }
        });

        SetBusy(false);
        SetComplete(result.Success, result.Message);
        if (result.Success)
            await WatchRadioSettleAsync(port, "after the read");
    }

    /// <summary>Writes the talkgroup list plus the bulk individual-callsign database (radioid.net,
    /// ~309k rows) - deliberately separate from Write Codeplug (see AnyToneD878CodeplugEncoder's
    /// remarks on includeTalkGroups): both change rarely and take real time/bytes to upload, so
    /// bundling them into every small channel edit was the actual problem being solved here. The
    /// callsign database region was read-verified empty on real hardware before this was built -
    /// nothing pre-existing is at risk, but it's genuinely new/untested territory for this project,
    /// hence the explicit confirmation and no automatic pre-write backup (the region is ~800MB of
    /// reserved address space in total; a full baseline dump of it was never feasible - same
    /// pre-existing gap the talkgroup list write has always had).</summary>
    private async void OnSyncReferenceDataClicked(object sender, RoutedEventArgs e)
    {
        if (_operationInProgress) return;
        if (SelectedPort is not { } port)
        {
            SetComplete(false, "Select a port first.");
            return;
        }

        SetBusy(true, "Identifying radio...");
        Log($"Opening {port} to identify the radio...");

        string deviceId;
        try
        {
            deviceId = await Task.Run(() =>
            {
                using var radio = new AnyToneD878Transport(port);
                radio.Open();
                try
                {
                    radio.StartProgrammingSession();
                    var id = radio.ReadDeviceId();
                    radio.EndProgrammingSession();
                    return id;
                }
                finally
                {
                    radio.Close();
                }
            });
        }
        catch (Exception ex)
        {
            Log($"Error identifying device: {ex.Message}");
            SetConnectionStatus(false, "Not connected.");
            SetComplete(false, $"Could not identify device: {ex.Message}");
            SetBusy(false);
            return;
        }

        var profile = RadioRiskCatalog.Lookup(deviceId);
        SetConnectionStatus(true, DescribeDevice(deviceId, port));
        SetBusy(false);

        var settleTask = WatchRadioSettleAsync(port, "after being detected");

        var confirm = new ContentDialog
        {
            Title = $"{Glyphs.IconAlert}  Sync Reference Data",
            Content = "This writes your talkgroup list and the full radioid.net individual-" +
                      "callsign database to the radio - likely tens of megabytes and several " +
                      "minutes, far more than a normal Write Codeplug. There's no automatic " +
                      "backup for the callsign database region beforehand, the way there is for " +
                      "a codeplug write. Only run this with the radio staying connected and " +
                      "powered for the whole duration.",
            PrimaryButtonText = "Sync",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            Log("Sync cancelled.");
            return;
        }

        await settleTask;
        SetRebootNoticeVisible(true);

        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var safeDeviceId = string.Join("_", deviceId.Split(Path.GetInvalidFileNameChars()));
        var sessionFolder = Path.Combine(AppPaths.DiagnosticsDirectory, $"{stamp}_sync_{safeDeviceId}");
        Directory.CreateDirectory(sessionFolder);
        _lastDiagnosticsSession = sessionFolder;
        var toolVersion = typeof(RadioPage).Assembly.GetName().Version?.ToString() ?? "dev";

        SetBusy(true, "Preparing reference data...");
        Log("Encoding talkgroup list...");

        List<EncodedRegion> regions;
        (long Count, long MaxId) fingerprint;
        try
        {
            regions = await Task.Run(async () =>
            {
                var warnings = new List<string>();
                using var db = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);
                var talkGroupRegions = AnyToneD878CodeplugEncoder.BuildTalkGroupsOnly(db, warnings);
                Log($"  {talkGroupRegions.Count} talkgroup region(s) encoded.");

                if (!File.Exists(AppPaths.RadioIdCachePath) ||
                    (DateTime.UtcNow - File.GetLastWriteTimeUtc(AppPaths.RadioIdCachePath)).TotalDays > 7)
                {
                    Log("Downloading radioid.net user database (~17MB, cached for 7 days)...");
                    SetBusy(true, "Downloading radioid.net database...");
                    await RadioIdLookup.DownloadToCacheAsync(AppPaths.RadioIdCachePath);
                }

                Log("Reading and encoding the callsign database (this can take a moment)...");
                SetBusy(true, "Encoding callsign database...");
                var users = RadioIdLookup.ReadAll(AppPaths.RadioIdCachePath);
                var callsignDbUsers = users
                    .Select(u => new CallsignDbUser(u.DmrId, u.Callsign, u.Name, u.City, u.State, u.Country))
                    .ToList();
                var callsignDbRegions = CallsignDbEncoder.Build(callsignDbUsers, warnings);
                Log($"  {callsignDbUsers.Count:N0} individuals, {callsignDbRegions.Count} region(s) encoded.");

                foreach (var warning in warnings) Log($"  Warning: {warning}");

                var allRegions = talkGroupRegions.Concat(callsignDbRegions).ToList();
                return allRegions;
            });
            using var fingerprintDb = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);
            fingerprint = AnyToneD878CodeplugEncoder.GetContactIndexFingerprint(fingerprintDb);
        }
        catch (Exception ex)
        {
            Log($"Error preparing reference data: {ex.Message}");
            SetRebootNoticeVisible(false);
            SetComplete(false, $"Could not prepare reference data: {ex.Message}");
            SetBusy(false);
            return;
        }

        var totalBytes = regions.Sum(r => (long)r.Data.Length);
        Log($"Writing {regions.Count} regions, {totalBytes:N0} bytes to {port}...");
        SetBusy(true, "Writing reference data...");

        // TalkGroupList[N] bank regions (see AnyToneD878CodeplugEncoder's remarks on bank-based
        // addressing - real hardware corruption 2026-07-19 traced to treating this list as one
        // flat array) each get their own committed session and read-back verification, rather than
        // being bundled with everything else. The other talkgroup regions (small, a few tens of KB)
        // and the Callsign Database regions (already real-hardware-validated as one bundled write)
        // keep the existing bundled-session path.
        var talkGroupBankRegions = regions.Where(r => r.Name.StartsWith("TalkGroupList[")).OrderBy(r => r.Name).ToList();
        var bundledRegions = regions.Where(r => !r.Name.StartsWith("TalkGroupList[")).ToList();
        var totalSessions = 1 + talkGroupBankRegions.Count;
        var sessionStep = 0;

        var result = await Task.Run(() =>
        {
            using var auditLog = WriteSessionAuditLog.Start(
                Path.Combine(sessionFolder, "audit.jsonl"), "sync-reference-data", port, deviceId, toolVersion);
            var committed = false;
            try
            {
                using (var radio = new AnyToneD878Transport(port))
                {
                    radio.Open();
                    radio.StartProgrammingSession();
                    radio.ReadDeviceId();

                    sessionStep++;
                    var started = DateTime.Now;
                    AnyToneD878CodeplugWriter.WriteSafeRegions(radio, bundledRegions, (region, index, total, written, totalW) =>
                    {
                        var elapsed = DateTime.Now - started;
                        if (index % 10 == 0 || index == total)
                            Log($"  [{index}/{total}] {region.Name} - {written:N0}/{totalW:N0} bytes, {elapsed.TotalSeconds:F0}s elapsed");
                        SetProgress(written, totalW, $"Restart {sessionStep}/{totalSessions} - writing reference data... [{index}/{total}] ({written:N0}/{totalW:N0} bytes, {elapsed.TotalMinutes:F1}m elapsed)");
                        auditLog.LogRegion(region.Name, region.Address, region.Data.Length, "written");
                    });

                    Log("Ending programming session (radio will drop off USB and re-enumerate)...");
                    radio.EndProgrammingSession();
                }
                committed = true;
                auditLog.LogNote(WriteSessionAuditLog.CommitConfirmedMessage);

                var bankFailures = new List<string>();
                foreach (var bankRegion in talkGroupBankRegions)
                {
                    sessionStep++;
                    SetBusy(true, $"Restart {sessionStep}/{totalSessions} - writing and verifying {bankRegion.Name}...");
                    const int maxBytesPerSession = 200_000; // each bank is at most 100,000 bytes - one chunk per bank
                    var verified = AnyToneD878CodeplugWriter.WriteRegionChunkedAndVerify(port, bankRegion, maxBytesPerSession, msg =>
                    {
                        Log($"  {msg}");
                        auditLog.LogNote(msg);
                    });
                    if (verified)
                        auditLog.LogRegion(bankRegion.Name, bankRegion.Address, bankRegion.Data.Length, "written and verified");
                    else
                        bankFailures.Add(bankRegion.Name);
                }

                if (bankFailures.Count > 0)
                {
                    var message = $"{bankFailures.Count} talkgroup bank(s) did not verify correctly: {string.Join(", ", bankFailures)}.";
                    auditLog.End("failed", message);
                    return (Success: false, Message: message);
                }

                auditLog.End("success");
                return (Success: true, Message: $"Sync complete - {regions.Count} regions, {totalBytes:N0} bytes.");
            }
            catch (Exception ex)
            {
                Log($"Error: {ex.Message}");
                if (committed)
                    Log("The write had already committed to the radio before this error.");
                auditLog.End("failed", ex.Message);
                return (Success: false, Message: ex.Message);
            }
        });

        if (result.Success)
        {
            using var db = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);
            using var cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE RadioSettings SET LastSyncedContactCount = $count, LastSyncedMaxContactId = $maxId WHERE Id = 1;";
            cmd.Parameters.AddWithValue("$count", fingerprint.Count);
            cmd.Parameters.AddWithValue("$maxId", fingerprint.MaxId);
            cmd.ExecuteNonQuery();
        }

        SetRebootNoticeVisible(false);
        SetComplete(result.Success, result.Message);
        SetBusy(false);
        if (result.Success) await WatchRadioSettleAsync(port, "after the sync");
    }

    /// <summary>Captures the same read-only region dump used for Backup/pre-write baselines and
    /// uploads it to the shared backend as a research sample - never issues a WriteMemory call.
    /// Most useful against a radio we have no hardware access to validate against (D868UV/D578UV/
    /// D890UV etc.): reading the AT-D878UV's known region list against unknown firmware is a cheap
    /// way to see what does and doesn't line up, without needing that model's layout up front.</summary>
    private async void OnContributeSampleClicked(object sender, RoutedEventArgs e)
    {
        if (_operationInProgress) return;
        if (SelectedPort is not { } port)
        {
            SetComplete(false, "Select a port first.");
            return;
        }

        SetBusy(true, "Identifying radio...");
        Log($"Opening {port} to identify the radio...");

        string deviceId;
        try
        {
            deviceId = await Task.Run(() =>
            {
                using var radio = new AnyToneD878Transport(port);
                radio.Open();
                try
                {
                    radio.StartProgrammingSession();
                    var id = radio.ReadDeviceId();
                    radio.EndProgrammingSession();
                    return id;
                }
                finally
                {
                    radio.Close();
                }
            });
        }
        catch (Exception ex)
        {
            Log($"Error identifying device: {ex.Message}");
            SetConnectionStatus(false, "Not connected.");
            SetComplete(false, $"Could not identify device: {ex.Message}");
            SetBusy(false);
            return;
        }

        var profile = RadioRiskCatalog.Lookup(deviceId);
        SetConnectionStatus(true, DescribeDevice(deviceId, port));
        SetBusy(false);

        // Same fix as Write Codeplug: the identify session above already ended, so the radio is
        // mid re-enumerate right now - start waiting for it in the background while the confirm
        // dialog is up, rather than reopening the port with no wait right after it closes.
        var settleTask = WatchRadioSettleAsync(port, "after being detected");

        var notesBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 70,
            PlaceholderText = "Anything worth knowing - firmware version, what you were doing when this radio behaved oddly, etc. (optional)",
        };
        var contactBox = new TextBox { PlaceholderText = "Contact email, if you're OK being followed up with (optional)" };
        var panel = new StackPanel { Spacing = 10, MaxWidth = 440 };
        panel.Children.Add(new TextBlock
        {
            Text = $"Detected: {profile.ModelLabel} ({deviceId})",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Text = "This only reads memory - the same read-only operation Backup uses - and never sends a write command, so it cannot change your radio's configuration. The raw dump gets uploaded to help extend support to this model. We haven't tested this against your specific radio before, so if anything about the read looks wrong (errors, timeouts) it will just be logged, not retried destructively.",
        });
        panel.Children.Add(notesBox);
        panel.Children.Add(contactBox);

        var confirm = new ContentDialog
        {
            Title = "Contribute Memory Sample",
            Content = panel,
            PrimaryButtonText = "Read and Upload",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        await settleTask;

        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var safeDeviceId = string.Join("_", deviceId.Split(Path.GetInvalidFileNameChars()));
        var sessionFolder = Path.Combine(AppPaths.DiagnosticsDirectory, $"{stamp}_sample_{safeDeviceId}");
        Directory.CreateDirectory(sessionFolder);
        var toolVersion = typeof(RadioPage).Assembly.GetName().Version?.ToString() ?? "dev";

        SetBusy(true, "Reading radio memory for sample...");
        Log($"Reading known regions from {profile.ModelLabel} ({deviceId})...");

        var readResult = await Task.Run(() =>
        {
            try
            {
                using var radio = new AnyToneD878Transport(port);
                radio.Open();
                radio.StartProgrammingSession();
                radio.ReadDeviceId();
                var results = AnyToneD878MemoryDumper.Dump(radio,
                    Path.Combine(sessionFolder, "sample.bin"), Path.Combine(sessionFolder, "sample.manifest.csv"),
                    (region, index, total, bytesDone, totalBytes) =>
                    {
                        SetProgress(bytesDone, totalBytes, $"Reading... [{index}/{total}] {region.Name} ({bytesDone:N0}/{totalBytes:N0} bytes)");
                        if (index % 40 == 0 || index == total) Log($"  [{index}/{total}] {region.Name}");
                    });
                radio.EndProgrammingSession();
                var failed = results.Count(r => !r.Succeeded);
                Log($"Read complete: {results.Count} regions, {failed} failed to respond (expected - this model's layout is unverified).");
                return (Success: true, Message: (string?)null);
            }
            catch (Exception ex)
            {
                Log($"Error: {ex.Message}");
                return (Success: false, Message: ex.Message);
            }
        });

        SetBusy(false);
        if (!readResult.Success)
        {
            SetComplete(false, $"Read failed: {readResult.Message}");
            await WatchRadioSettleAsync(port, "after the read attempt");
            return;
        }

        // Upload doesn't touch the radio at all - do it before the settle-wait, not after, the
        // same order Backup already uses. Waiting for the radio first was a real bug: the settle
        // watch posts its own "Radio ready" completion message, which looked like the operation
        // had finished and buried the real upload-completion message that came after it.
        SetBusy(true, "Uploading sample...");
        Log("Packaging and uploading memory sample...");
        try
        {
            var zipPath = DiagnosticPackager.CreateZip(sessionFolder);
            await RadioSampleUploader.UploadAsync(zipPath, profile.ModelLabel, deviceId, toolVersion, contactBox.Text, notesBox.Text);
            Log("Memory sample uploaded - thank you.");
            SetComplete(true, "Memory sample uploaded - thank you.");

            var thanksDialog = new ContentDialog
            {
                Title = "Thank You",
                Content = "Your memory-map sample has been uploaded. It'll be reviewed, and used to help extend real support to this radio model in due course.",
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot,
            };
            await thanksDialog.ShowAsync();
        }
        catch (Exception ex)
        {
            Log($"Failed to upload sample: {ex.Message}");
            SetComplete(false, $"Failed to upload sample: {ex.Message}");
        }
        SetBusy(false);
        await WatchRadioSettleAsync(port, "after the read");
    }
}
