using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using HamNetProgrammer.Desktop.Utils;
using HamNetProgrammer.Desktop.Views;
using Windows.System;

namespace HamNetProgrammer.Desktop;

public class AppShell : Window
{
    private readonly Frame _contentFrame;
    private readonly NavigationView _navView;
    private readonly Grid _rootGrid;
    private readonly TextBlock _statusBarText;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _uiQueue;

    public Frame ContentFrame => _contentFrame;

    public AppShell()
    {
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
            CreateNavItem("Radio", Glyphs.IconRadio, "radio"),
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

        _statusBarText = new TextBlock
        {
            FontSize = 11,
            Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x88, 0x96, 0xa0)),
            FontFamily = new FontFamily("Consolas"),
            VerticalAlignment = VerticalAlignment.Center,
            Text = "Ready",
        };
        var statusBar = new Border
        {
            Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x1a, 0x23, 0x2c)),
            Padding = new Thickness(12, 4, 12, 4),
            Child = _statusBarText,
        };
        Grid.SetRow(statusBar, 1);

        Grid.SetRow(_navView, 0);
        _rootGrid.Children.Add(_navView);
        _rootGrid.Children.Add(statusBar);

        this.Content = _rootGrid;
    }

    public void SetStatus(string message) => _uiQueue.TryEnqueue(() => _statusBarText.Text = message);

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
            case "radio":
                _contentFrame.Navigate(typeof(RadioPage));
                break;
            case "help":
                _contentFrame.Navigate(typeof(HelpPage));
                break;
        }
    }
}
