using System.IO.Ports;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using HamNetProgrammer.Desktop;
using HamNetProgrammer.Core.Data;
using HamNetProgrammer.Core.Diagnostics;
using HamNetProgrammer.Core.Planning;
using HamNetProgrammer.Core.Radios.AnyTone;
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
    private string? _lastWriteSessionFolder;
    private string? _lastWritePort;

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
        var ports = SerialPort.GetPortNames();
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
            ContributeSampleButton.IsEnabled = enabled && _connected;
            SendReportButton.IsEnabled = enabled && _lastDiagnosticsSession is not null;
            RestoreButton.IsEnabled = enabled && _lastWriteSessionFolder is not null;
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
    private async Task WatchRadioSettleAsync(string port)
    {
        _radioSettling = true;
        UpdateButtonStates();
        SetProgress(0, 1, "Radio is resetting after that session - waiting for it to come back...");
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
            SetComplete(true, "Radio ready.");
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
            await WatchRadioSettleAsync(result.Port);
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
            await WatchRadioSettleAsync(port);
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
        Log($"Device identifier: {deviceId} ({profile.ModelLabel}, {profile.Tier} risk).");
        SetConnectionStatus(true, DescribeDevice(deviceId, port));
        SetBusy(false);

        if (!await RiskDisclaimerDialog.ShowAsync(this.XamlRoot, profile))
        {
            Log("Write cancelled.");
            return;
        }

        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var safeDeviceId = string.Join("_", deviceId.Split(Path.GetInvalidFileNameChars()));
        var sessionFolder = Path.Combine(AppPaths.DiagnosticsDirectory, $"{stamp}_{safeDeviceId}");
        Directory.CreateDirectory(sessionFolder);
        _lastDiagnosticsSession = sessionFolder;
        var toolVersion = typeof(RadioPage).Assembly.GetName().Version?.ToString() ?? "dev";

        SetBusy(true, "Encoding codeplug...");
        Log("Encoding codeplug from database...");

        var result = await Task.Run(() =>
        {
            using var auditLog = WriteSessionAuditLog.Start(
                Path.Combine(sessionFolder, "audit.jsonl"), "write-codeplug", port, deviceId, toolVersion);
            var committed = false;

            try
            {
                Log("Taking pre-write baseline backup (for diagnostics if anything goes wrong)...");
                auditLog.LogNote("Starting pre-write baseline backup.");
                using (var radio = new AnyToneD878Transport(port))
                {
                    radio.Open();
                    radio.StartProgrammingSession();
                    radio.ReadDeviceId();
                    var baselineResults = AnyToneD878MemoryDumper.Dump(radio,
                        Path.Combine(sessionFolder, "baseline_before.bin"),
                        Path.Combine(sessionFolder, "baseline_before.manifest.csv"),
                        (region, index, total) =>
                        {
                            SetProgress(index, total, $"Pre-write backup... [{index}/{total}] {region.Name}");
                            if (index % 40 == 0 || index == total) Log($"  baseline [{index}/{total}] {region.Name}");
                        });
                    radio.EndProgrammingSession();
                    var failed = baselineResults.Count(r => !r.Succeeded);
                    auditLog.LogNote($"Baseline backup: {baselineResults.Count} regions, {failed} failed.");
                    Log($"Baseline backup complete ({baselineResults.Count} regions, {failed} failed).");
                }

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

                using var writeRadio = new AnyToneD878Transport(port);
                Log($"Opening {port}...");
                writeRadio.Open();

                Log("Starting programming session (radio should show 'PC Mode')...");
                writeRadio.StartProgrammingSession();
                writeRadio.ReadDeviceId();

                var started = DateTime.Now;
                AnyToneD878CodeplugWriter.Write(writeRadio, regions, (region, index, total, written, totalW) =>
                {
                    var elapsed = DateTime.Now - started;
                    Log($"  [{index}/{total}] {region.Name} done - {written:N0}/{totalW:N0} bytes, {elapsed.TotalSeconds:F0}s elapsed");
                    auditLog.LogRegion(region.Name, region.Address, region.Data.Length, "written");
                    SetProgress(written, totalW, $"Writing... [{index}/{total}] {region.Name} ({written:N0}/{totalW:N0} bytes)");
                });

                Log("Ending programming session (this commits the write - device will drop off USB and re-enumerate)...");
                writeRadio.EndProgrammingSession();
                writeRadio.Close();
                Log("Write complete.");
                committed = true;
                auditLog.LogNote(WriteSessionAuditLog.CommitConfirmedMessage);

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
                        (region, index, total) =>
                        {
                            SetProgress(index, total, $"Post-write backup... [{index}/{total}] {region.Name}");
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

        if (result.Committed)
        {
            _lastWriteSessionFolder = sessionFolder;
            _lastWritePort = port;
        }

        SetComplete(result.Success, result.Message);
        SetBusy(false);
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
                        (region, index, total) =>
                        {
                            SetProgress(index, total, $"Backing up... [{index}/{total}] {region.Name}");
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
            _ = WatchRadioSettleAsync(port);
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
        if (_lastWriteSessionFolder is not { } sessionFolder || _lastWritePort is not { } port)
        {
            SetComplete(false, "No committed write this session to restore.");
            return;
        }

        var confirm = new ContentDialog
        {
            Title = "Restore Previous Codeplug",
            Content = "This writes back exactly what the last Write Codeplug run overwrote, using the " +
                      "automatic backup taken just before it - undoing that write. It does not touch " +
                      "anything that write didn't touch. Continue?",
            PrimaryButtonText = "Restore",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        SetBusy(true, "Restoring previous codeplug...");
        Log("Restoring from the pre-write baseline backup...");

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
                    Log("Nothing was recorded as written in that session - nothing to restore.");
                    auditLog.End("skipped", "No written regions recorded.");
                    return (Success: true, Message: "Nothing to restore.");
                }

                var baseline = DumpReader.Load(
                    Path.Combine(sessionFolder, "baseline_before.bin"),
                    Path.Combine(sessionFolder, "baseline_before.manifest.csv"));

                using var radio = new AnyToneD878Transport(port);
                Log($"Opening {port}...");
                radio.Open();
                radio.StartProgrammingSession();
                var restoreDeviceId = radio.ReadDeviceId();
                SetConnectionStatus(true, DescribeDevice(restoreDeviceId, port));

                var restored = AnyToneD878CodeplugRestorer.Restore(radio, baseline, writtenRegions, (region, index, total) =>
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
                var skipped = restored.Count(r => r.Skipped);
                Log($"Restore complete: {restored.Count - skipped} region(s) restored, {skipped} skipped.");
                auditLog.LogNote(WriteSessionAuditLog.CommitConfirmedMessage);

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
                        (region, index, total) =>
                        {
                            SetProgress(index, total, $"Post-restore backup... [{index}/{total}] {region.Name}");
                            if (index % 40 == 0 || index == total) Log($"  post-restore [{index}/{total}] {region.Name}");
                        });
                    afterRadio.EndProgrammingSession();
                    var failed = afterResults.Count(r => !r.Succeeded);
                    auditLog.LogNote($"Post-restore backup: {afterResults.Count} regions, {failed} failed.");
                }
                else
                {
                    Log("WARNING: radio did not re-enumerate within 45s after restore.");
                    auditLog.LogNote("WARNING: radio did not re-enumerate within 45s after restore.");
                }

                auditLog.End("success");
                return (Success: true, Message: $"Restore complete: {restored.Count - skipped} region(s) restored.");
            }
            catch (Exception ex)
            {
                Log($"Error: {ex.Message}");
                auditLog.End("failed", ex.Message);
                return (Success: false, Message: ex.Message);
            }
        });

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

                var results = AnyToneD878MemoryDumper.Dump(radio, binPath, manifestPath, (region, index, total) =>
                {
                    SetProgress(index, total, $"Backing up... [{index}/{total}] {region.Name}");
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
            await WatchRadioSettleAsync(port);
        }
        else
        {
            SetComplete(false, result.Message);
        }
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
                    (region, index, total) =>
                    {
                        SetProgress(index, total, $"Reading... [{index}/{total}] {region.Name}");
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
            await WatchRadioSettleAsync(port);
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
        await WatchRadioSettleAsync(port);
    }
}
