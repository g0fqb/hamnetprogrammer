using Microsoft.UI.Xaml;
using HamNetProgrammer.Core.Data;
using HamNetProgrammer.Core.Online;
using HamNetProgrammer.Desktop.Utils;

namespace HamNetProgrammer.Desktop;

public partial class App : Application
{
    public static Window? MainWindow { get; private set; }
    private static readonly string LogPath = Path.Combine(
        AppContext.BaseDirectory, "HamNetProgrammer_crash.txt");

    public App()
    {
        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        Log("App() constructor started");
        try
        {
            InitializeComponent();
            Log("InitializeComponent() completed");
        }
        catch (Exception ex)
        {
            Log($"InitializeComponent() FAILED:\n{ex}");
        }
        Log("App() constructor completed");
    }

    public static void LogPublic(string message) => Log(message);

    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(LogPath, $"[{DateTime.Now}] {message}\n");
        }
        catch
        {
            // Logging must never throw - an exception here would escape across the native
            // WinUI activation boundary as a stowed exception and crash the process with no
            // diagnostics (see PacketCluster's App.xaml.cs, same reasoning).
        }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        Log($"WinUI UnhandledException:\n{e.Exception}\n");
    }

    private void OnDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        Log($"AppDomain UnhandledException:\n{e.ExceptionObject}\n");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Log("OnLaunched started");
        try
        {
            var splash = new SplashWindow();
            splash.Activate();
            Log("SplashWindow activated");

            var shell = new AppShell();
            Log("AppShell created");

            _ = FinishStartupAsync(splash, shell);
        }
        catch (Exception ex)
        {
            Log($"OnLaunched Exception:\n{ex}\n");
            throw;
        }
    }

    // No login/session to restore - this is a fully local, offline tool. Splash shows for a
    // minimum of 2.5s, during which the talkgroup list refreshes from the shared backend (see
    // RefreshTalkgroupsAsync) - started here rather than awaited, so a slow/absent connection
    // delays nothing; it just finishes in the background after the main window is already up.
    private static async Task FinishStartupAsync(SplashWindow splash, AppShell shell)
    {
        var minDelay = Task.Delay(2500);
        _ = RefreshTalkgroupsAsync();
        await minDelay;

        MainWindow = shell;
        shell.Activate();
        shell.ShowMain();
        Log("AppShell activated");
        splash.Close();
    }

    // Talkgroups (Brandmeister/TGIF/FreeDMR via the shared backend) used to need a manual "Import
    // Talkgroups" button - confusing, since it looked like it should prompt for a CSV rather than
    // just pulling from Railway. Refreshing automatically on every launch removes that button
    // entirely; individual DMR IDs (radioid.net, ~309k rows) are deliberately NOT synced this way -
    // that dataset stays search-only server-side, see RadioIdNetworkSearch's remarks.
    private static async Task RefreshTalkgroupsAsync()
    {
        try
        {
            using var db = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);
            var result = await TalkGroupNetworkImporter.ImportAsync(db);
            Log($"Startup talkgroup refresh: {result.Added} added, {result.Updated} updated, {result.Unchanged} unchanged, {result.Warnings.Count} warnings.");
        }
        catch (Exception ex)
        {
            Log($"Startup talkgroup refresh failed (non-fatal, using existing local data): {ex.Message}");
        }
    }
}
