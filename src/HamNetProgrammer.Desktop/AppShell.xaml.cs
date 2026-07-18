using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using HamNetProgrammer.Desktop.Utils;
using HamNetProgrammer.Desktop.Views;
using Windows.System;

namespace HamNetProgrammer.Desktop;

public class AppShell : Window
{
    private static readonly SolidColorBrush NotConnectedBrush = new(Microsoft.UI.ColorHelper.FromArgb(255, 0x5a, 0x65, 0x70));
    private static readonly SolidColorBrush ConnectedBrush = new(Microsoft.UI.ColorHelper.FromArgb(255, 0x4c, 0xaf, 0x50));
    private static readonly SolidColorBrush SettlingBrush = new(Microsoft.UI.ColorHelper.FromArgb(255, 0xff, 0xb7, 0x4d));

    private readonly Frame _contentFrame;
    private readonly NavigationView _navView;
    private readonly Grid _rootGrid;
    private readonly Ellipse _connectionDot;
    private readonly TextBlock _connectionStatusText;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _uiQueue;

    /// <summary>The single running AppShell instance, so any page can reach the global connection
    /// indicator without a full navigation/DI service just for this. Set once in the constructor -
    /// this app only ever creates one AppShell (see App.xaml.cs). Named ActiveInstance rather than
    /// Current to avoid silently shadowing WinUI's own Window.Current.</summary>
    public static AppShell? ActiveInstance { get; private set; }

    public Frame ContentFrame => _contentFrame;

    public AppShell()
    {
        ActiveInstance = this;
        Title = "HamNetProgrammer";
        this.AppWindow.Resize(new Windows.Graphics.SizeInt32(1200, 800));
        AppIcon.Apply(this);

        _uiQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        _rootGrid = new Grid
        {
            RowDefinitions = { new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }, new RowDefinition { Height = GridLength.Auto } }
        };

        _navView = new NavigationView
        {
            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
            IsPaneToggleButtonVisible = true,
            PaneDisplayMode = NavigationViewPaneDisplayMode.Left,
            IsSettingsVisible = false,
            OpenPaneLength = 200,
            Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x10, 0x16, 0x1d)),
        };
        _navView.Resources["NavigationViewDefaultPaneBackground"] = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x1a, 0x23, 0x2c));

        // Icon glyphs use \u escapes (Segoe Fluent Icons PUA codepoints) rather than literal
        // characters - literal Unicode in source files has shown real double-encoding corruption
        // risk in this environment (confirmed the hard way while writing this file).
        var menuItems = new List<NavigationViewItem>
        {
            CreateNavItem("Zones", Glyphs.IconZones, "zones"),
            CreateNavItem("Scan Lists", Glyphs.IconScanLists, "scanlists"),
            CreateNavItem("Group Lists", Glyphs.IconGroupLists, "grouplists"),
            CreateNavItem("Roaming", Glyphs.IconRoaming, "roaming"),
            CreateNavItem("Contacts", Glyphs.IconMail, "contacts"),
            CreateNavItem("Lists", Glyphs.IconCheck, "lists"),
            CreateNavItem("Radio", Glyphs.IconRadio, "radio"),
            CreateNavItem("Settings", Glyphs.IconSettings, "settings"),
            CreateNavItem("Help", Glyphs.IconHelp, "help"),
        };
        foreach (var item in menuItems)
            _navView.MenuItems.Add(item);

        _contentFrame = new Frame();

        var contentWrapper = new Grid();
        contentWrapper.Children.Add(_contentFrame);
        contentWrapper.Children.Add(BuildDonateButton());

        _navView.Content = contentWrapper;
        _navView.ItemInvoked += OnNavItemInvoked;

        // Shows which radio (if any) is currently connected and on which port - not a generic
        // "Ready"/idle placeholder (the previous text here was never actually updated by anything
        // in the app, which is exactly why it was confusing). Persists across page navigation
        // since it lives in the shell, not a page - RadioPage calls SetConnectionStatus whenever
        // it identifies a device. Useful given a user may have more than one radio to choose
        // between (this app only tracks one active connection at a time, matching RadioPage).
        _connectionDot = new Ellipse { Width = 8, Height = 8, Fill = NotConnectedBrush, VerticalAlignment = VerticalAlignment.Center };
        _connectionStatusText = new TextBlock
        {
            FontSize = 11,
            Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x88, 0x96, 0xa0)),
            FontFamily = new FontFamily("Consolas"),
            VerticalAlignment = VerticalAlignment.Center,
            Text = "Not connected.",
        };
        var statusBarContent = new Grid();
        var leftPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        leftPanel.Children.Add(_connectionDot);
        leftPanel.Children.Add(_connectionStatusText);
        statusBarContent.Children.Add(leftPanel);

        // Answers "am I running the build I think I am" without having to check file timestamps -
        // shows the app's own version plus when THIS running instance's assembly was actually
        // compiled, so a stale shortcut (or a rebuild you haven't relaunched into yet) is obvious
        // at a glance rather than something to guess at.
        var version = typeof(AppShell).Assembly.GetName().Version?.ToString(3) ?? "dev";
        var builtAt = System.IO.File.GetLastWriteTime(typeof(AppShell).Assembly.Location);
        var versionText = new TextBlock
        {
            FontSize = 11,
            Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x5a, 0x65, 0x70)),
            FontFamily = new FontFamily("Consolas"),
            HorizontalAlignment = HorizontalAlignment.Right,
            Text = $"v{version} - built {builtAt:yyyy-MM-dd HH:mm}",
        };
        statusBarContent.Children.Add(versionText);

        var statusBar = new Border
        {
            Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x1a, 0x23, 0x2c)),
            Padding = new Thickness(12, 4, 12, 4),
            Child = statusBarContent,
        };
        Grid.SetRow(statusBar, 1);

        Grid.SetRow(_navView, 0);
        _rootGrid.Children.Add(_navView);
        _rootGrid.Children.Add(statusBar);

        this.Content = _rootGrid;
    }

    public void SetConnectionStatus(bool connected, string text) => _uiQueue.TryEnqueue(() =>
    {
        _connectionDot.Fill = connected ? ConnectedBrush : NotConnectedBrush;
        _connectionStatusText.Text = text;
    });

    /// <summary>A radio was confirmed but is currently mid drop-off/re-enumerate after its session
    /// ended - neither the green "connected" nor grey "not connected" state is accurate here.</summary>
    public void SetSettlingStatus(string text) => _uiQueue.TryEnqueue(() =>
    {
        _connectionDot.Fill = SettlingBrush;
        _connectionStatusText.Text = text;
    });

    private static UIElement BuildDonateButton()
    {
        var btn = new Button
        {
            Content = Glyphs.Heart + " Support",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 10, 0),
            Padding = new Thickness(10, 4, 10, 4),
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0xff, 0xb7, 0x4d)),
            Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(40, 0xff, 0xb7, 0x4d)),
            BorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(100, 0xff, 0xb7, 0x4d)),
            CornerRadius = new CornerRadius(14),
            IsHitTestVisible = true,
        };
        btn.Click += async (_, _) =>
        {
            await Launcher.LaunchUriAsync(
                new Uri("https://www.paypal.com/donate?business=g0fqb1%40gmail.com&currency_code=GBP&item_name=HamNetProgrammer"));
        };
        return btn;
    }


    private NavigationViewItem CreateNavItem(string content, string glyph, string tag) => new()
    {
        Content = content,
        Tag = tag,
        Icon = new FontIcon { Glyph = glyph },
    };

    public void ShowMain()
    {
        _contentFrame.Navigate(typeof(ZonesPage));
    }

    private void OnNavItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is NavigationViewItem item && item.Tag is string tag)
            NavigateTo(tag);
    }

    private void NavigateTo(string tag)
    {
        switch (tag)
        {
            case "zones":
                _contentFrame.Navigate(typeof(ZonesPage));
                break;
            case "scanlists":
                _contentFrame.Navigate(typeof(ScanListsPage));
                break;
            case "grouplists":
                _contentFrame.Navigate(typeof(GroupListsPage));
                break;
            case "roaming":
                _contentFrame.Navigate(typeof(RoamingZonesPage));
                break;
            case "contacts":
                _contentFrame.Navigate(typeof(ContactsPage));
                break;
            case "lists":
                _contentFrame.Navigate(typeof(ListsPage));
                break;
            case "radio":
                _contentFrame.Navigate(typeof(RadioPage));
                break;
            case "settings":
                _contentFrame.Navigate(typeof(RadioSettingsPage));
                break;
            case "help":
                _contentFrame.Navigate(typeof(HelpPage));
                break;
        }
    }
}
