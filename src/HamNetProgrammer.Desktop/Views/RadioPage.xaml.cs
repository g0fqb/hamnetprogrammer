using System.IO.Ports;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using HamNetProgrammer.Core.Data;
using HamNetProgrammer.Core.Radios.AnyTone;
using HamNetProgrammer.Desktop.Utils;

namespace HamNetProgrammer.Desktop.Views;

public sealed partial class RadioPage : Page
{
    private readonly DispatcherQueue _uiQueue;
    private bool _operationInProgress;

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

        var confirm = new ContentDialog
        {
            Title = "Write Codeplug to Radio",
            Content = "This overwrites the radio's current codeplug with the contents of the local database. Continue?",
            PrimaryButtonText = "Write",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        SetBusy(true, "Encoding codeplug...");
        Log("Encoding codeplug from database...");

        var result = await Task.Run(() =>
        {
            try
            {
                using var db = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);
                var regions = AnyToneD878CodeplugEncoder.Build(db);
                var totalBytes = regions.Sum(r => (long)r.Data.Length);
                Log($"Encoded {regions.Count} regions, {totalBytes:N0} bytes to write.");

                using var radio = new AnyToneD878Transport(port);
                Log($"Opening {port}...");
                radio.Open();

                Log("Starting programming session (radio should show 'PC Mode')...");
                radio.StartProgrammingSession();
                var deviceId = radio.ReadDeviceId();
                Log($"Device identifier: {deviceId}");

                var started = DateTime.Now;
                AnyToneD878CodeplugWriter.Write(radio, regions, (region, index, total, written, totalW) =>
                {
                    var elapsed = DateTime.Now - started;
                    Log($"  [{index}/{total}] {region.Name} done - {written:N0}/{totalW:N0} bytes, {elapsed.TotalSeconds:F0}s elapsed");
                    SetBusy(true, $"Writing... [{index}/{total}] {region.Name} ({written:N0}/{totalW:N0} bytes)");
                });

                Log("Ending programming session (this commits the write - device will drop off USB and re-enumerate)...");
                radio.EndProgrammingSession();
                Log("Write complete.");
                return (Success: true, Message: "Write complete.");
            }
            catch (Exception ex)
            {
                Log($"Error: {ex.Message}");
                return (Success: false, Message: ex.Message);
            }
        });

        _uiQueue.TryEnqueue(() => ConnectionStatusText.Text = result.Success
            ? "Write complete - radio is re-enumerating."
            : $"Write failed: {result.Message}");

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
