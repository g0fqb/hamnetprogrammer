using Microsoft.UI.Xaml;

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
    // minimum of 2.5s (much shorter than PacketCluster's 9s - there's no network round trip to
    // hide here, just enough to avoid an instant flash on a fast machine).
    private static async Task FinishStartupAsync(SplashWindow splash, AppShell shell)
    {
        await Task.Delay(2500);

        MainWindow = shell;
        shell.Activate();
        shell.ShowMain();
        Log("AppShell activated");
        splash.Close();
    }
}
