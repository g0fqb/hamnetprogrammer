using Microsoft.UI.Xaml;

namespace HamNetProgrammer.Desktop.Utils;

/// Applies the app icon to a window's title bar / taskbar entry, if one exists yet.
public static class AppIcon
{
    private static readonly string IconPath = Path.Combine(
        AppContext.BaseDirectory, "Assets", "Logo", "hnp_logo.ico");

    public static void Apply(Window window)
    {
        if (File.Exists(IconPath))
            window.AppWindow.SetIcon(IconPath);
    }
}
