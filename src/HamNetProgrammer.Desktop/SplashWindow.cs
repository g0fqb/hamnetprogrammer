using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using HamNetProgrammer.Desktop.Utils;
using Windows.Graphics;

namespace HamNetProgrammer.Desktop;

/// Brief branded splash window shown while the app starts up.
public sealed class SplashWindow : Window
{
    private const int Width = 480;
    private const int Height = 380;

    private const string FeaturesText =
        "\U0001F4E1  Direct Radio Read/Write      \U0001F4CB  SQLite Codeplug      " +
        "\U0001F4CB  Zones & Scan Lists Auto-Built      \U0001F4E1  Roaming from Talkgroups      " +
        "\U0001F50D  Open, Not a Black Box      ";

    private Storyboard? _marqueeStoryboard;

    public SplashWindow()
    {
        Title = "HamNetProgrammer";
        AppWindow.Resize(new SizeInt32(Width, Height));
        AppIcon.Apply(this);

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(true, false);
        }

        CenterOnScreen();
        Content = BuildContent();
    }

    private void CenterOnScreen()
    {
        var displayArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var x = (displayArea.WorkArea.Width - Width) / 2;
        var y = (displayArea.WorkArea.Height - Height) / 2;
        AppWindow.Move(new PointInt32(x, y));
    }

    private UIElement BuildContent()
    {
        var root = new Grid
        {
            Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x10, 0x16, 0x1d)),
            RowDefinitions =
            {
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
            }
        };

        var mark = new TextBlock
        {
            Text = "\U0001F4E1",
            FontSize = 72,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 24, 0, 8),
        };
        Grid.SetRow(mark, 0);

        var title = new TextBlock
        {
            Text = "HamNetProgrammer",
            FontSize = 22,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0xe0, 0x92, 0x4a)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
        };
        Grid.SetRow(title, 1);

        var progress = new ProgressRing
        {
            IsActive = true,
            Width = 24,
            Height = 24,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0xe0, 0x92, 0x4a)),
            Margin = new Thickness(0, 12, 0, 8),
        };
        Grid.SetRow(progress, 2);

        var marquee = BuildMarquee();
        Grid.SetRow(marquee, 3);

        var credit = new TextBlock
        {
            Text = "An open codeplug tool for the AnyTone AT-D878UV",
            FontSize = 12,
            Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x88, 0x96, 0xa0)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 20),
        };
        Grid.SetRow(credit, 4);

        root.Children.Add(mark);
        root.Children.Add(title);
        root.Children.Add(progress);
        root.Children.Add(marquee);
        root.Children.Add(credit);
        return root;
    }

    private FrameworkElement BuildMarquee()
    {
        var clipHost = new Grid
        {
            Height = 22,
            Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, Width, 22) },
        };

        var transform = new TranslateTransform();
        var text = new TextBlock
        {
            Text = FeaturesText,
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x6b, 0xb0, 0xb8)),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            TextWrapping = TextWrapping.NoWrap,
            RenderTransform = transform,
        };
        clipHost.Children.Add(text);

        var animation = new DoubleAnimation
        {
            From = Width,
            To = -1200,
            Duration = new Duration(TimeSpan.FromSeconds(8)),
            RepeatBehavior = RepeatBehavior.Forever,
        };
        Storyboard.SetTarget(animation, transform);
        Storyboard.SetTargetProperty(animation, "X");

        _marqueeStoryboard = new Storyboard();
        _marqueeStoryboard.Children.Add(animation);
        _marqueeStoryboard.Begin();

        return clipHost;
    }
}
