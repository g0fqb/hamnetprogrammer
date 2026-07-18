using System.IO.Ports;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using HamNetProgrammer.Core.Data;
using HamNetProgrammer.Core.Diagnostics;
using HamNetProgrammer.Core.Radios.AnyTone;
using HamNetProgrammer.Desktop.Utils;

namespace HamNetProgrammer.Desktop.Views;

public sealed partial class RadioPage : Page
{
    private readonly DispatcherQueue _uiQueue;
    private bool _operationInProgress;
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

    private string? SelectedPort => PortComboBox.SelectedItem as string;

    private void Log(string message)
    {
        _uiQueue.TryEnqueue(() =>
        {
            LogText.Text += (LogText.Text.Length > 0 ? "\n" : "") + message;
            LogScrollViewer.ChangeView(null, LogScrollViewer.ScrollableHeight, null);
        });
    }

    private void SetBusy(bool busy, string status = "")
    {
        _uiQueue.TryEnqueue(() =>
        {
            _operationInProgress = busy;
            TestConnectionButton.IsEnabled = !busy;
            WriteCodeplugButton.IsEnabled = !busy;
            BackupButton.IsEnabled = !busy;
            RefreshButton.IsEnabled = !busy;
            AutoDetectButton.IsEnabled = !busy;
            SendReportButton.IsEnabled = !busy && _lastDiagnosticsSession is not null;
            RestoreButton.IsEnabled = !busy && _lastWriteSessionFolder is not null;
            OperationProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            OperationProgressBar.IsIndeterminate = busy;
            OperationStatusText.Text = status;
        });
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
                ConnectionStatusText.Text = $"Found radio on {result.Port} - {result.DeviceId}";
            });
            Log($"Radio found on {result.Port}.");
        }
        else
        {
            _uiQueue.TryEnqueue(() => ConnectionStatusText.Text = "No radio found on any free port.");
            Log("No radio found on any free port.");
        }

        SetBusy(false);
    }

    private async void OnTestConnectionClicked(object sender, RoutedEventArgs e)
    {
        if (_operationInProgress) return;
        if (SelectedPort is not { } port)
        {
            ConnectionStatusText.Text = "Select a port first.";
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
            _uiQueue.TryEnqueue(() => ConnectionStatusText.Text = $"Connected - {result.Message}");
        }
        else
        {
            Log($"Error: {result.Message}");
            _uiQueue.TryEnqueue(() => ConnectionStatusText.Text = $"Connection failed: {result.Message}");
        }

        SetBusy(false);
    }

    private async void OnWriteCodeplugClicked(object sender, RoutedEventArgs e)
    {
        if (_operationInProgress) return;
        if (SelectedPort is not { } port)
        {
            ConnectionStatusText.Text = "Select a port first.";
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
            ConnectionStatusText.Text = $"Could not identify device: {ex.Message}";
            SetBusy(false);
            return;
        }

        var profile = RadioRiskCatalog.Lookup(deviceId);
        Log($"Device identifier: {deviceId} ({profile.ModelLabel}, {profile.Tier} risk).");
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
                            if (index % 40 == 0 || index == total) Log($"  baseline [{index}/{total}] {region.Name}");
                        });
                    radio.EndProgrammingSession();
                    var failed = baselineResults.Count(r => !r.Succeeded);
                    auditLog.LogNote($"Baseline backup: {baselineResults.Count} regions, {failed} failed.");
                    Log($"Baseline backup complete ({baselineResults.Count} regions, {failed} failed).");
                }

                using var db = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);
                var regions = AnyToneD878CodeplugEncoder.Build(db);
                var totalBytes = regions.Sum(r => (long)r.Data.Length);
                Log($"Encoded {regions.Count} regions, {totalBytes:N0} bytes to write.");
                auditLog.LogNote($"Encoded {regions.Count} regions, {totalBytes} bytes.");

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
                    SetBusy(true, $"Writing... [{index}/{total}] {region.Name} ({written:N0}/{totalW:N0} bytes)");
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
                            if (index % 40 == 0 || index == total) Log($"  post-write [{index}/{total}] {region.Name}");
                        });
                    afterRadio.EndProgrammingSession();
                    var failed = afterResults.Count(r => !r.Succeeded);
                    auditLog.LogNote($"Post-write backup: {afterResults.Count} regions, {failed} failed.");
                    Log($"Post-write backup complete ({afterResults.Count} regions, {failed} failed).");
                }
                else
                {
                    Log("WARNING: radio did not re-enumerate within 45s. No post-write backup taken.");
                    auditLog.LogNote("WARNING: radio did not re-enumerate within 45s after write - no post-write backup taken.");
                }

                auditLog.End("success");
                return (Success: true, Message: "Write complete.", Committed: committed);
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

        _uiQueue.TryEnqueue(() => ConnectionStatusText.Text = result.Success
            ? "Write complete - radio is re-enumerating."
            : $"Write failed: {result.Message}");

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
                ConnectionStatusText.Text = "Select a port first, or write a codeplug, before sending a report.";
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
                    var results = AnyToneD878MemoryDumper.Dump(radio,
                        Path.Combine(folder, "backup.bin"), Path.Combine(folder, "backup.manifest.csv"),
                        (region, index, total) =>
                        {
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
                ConnectionStatusText.Text = "Backup failed - report not sent.";
                return;
            }
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
            ConnectionStatusText.Text = "Diagnostic report sent - thank you.";
        }
        catch (Exception ex)
        {
            Log($"Failed to send diagnostic report: {ex.Message}");
            ConnectionStatusText.Text = $"Failed to send report: {ex.Message}";
        }

        SetBusy(false);
    }

    private async void OnRestoreClicked(object sender, RoutedEventArgs e)
    {
        if (_operationInProgress) return;
        if (_lastWriteSessionFolder is not { } sessionFolder || _lastWritePort is not { } port)
        {
            ConnectionStatusText.Text = "No committed write this session to restore.";
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
                radio.ReadDeviceId();

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

        _uiQueue.TryEnqueue(() => ConnectionStatusText.Text = result.Success
            ? result.Message
            : $"Restore failed: {result.Message}");

        SetBusy(false);
    }

    private async void OnBackupClicked(object sender, RoutedEventArgs e)
    {
        if (_operationInProgress) return;
        if (SelectedPort is not { } port)
        {
            ConnectionStatusText.Text = "Select a port first.";
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

                var results = AnyToneD878MemoryDumper.Dump(radio, binPath, manifestPath, (region, index, total) =>
                {
                    if (index % 20 == 0 || index == total)
                    {
                        Log($"  [{index}/{total}] {region.Name}");
                        SetBusy(true, $"Backing up... [{index}/{total}] {region.Name}");
                    }
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

        _uiQueue.TryEnqueue(() => ConnectionStatusText.Text = result.Message);
        SetBusy(false);
    }
}
