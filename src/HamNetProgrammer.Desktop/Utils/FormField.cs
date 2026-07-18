using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace HamNetProgrammer.Desktop.Utils;

/// Shared "label (?) | control" row builder used by every settings/edit form (Channel, Scan List,
/// Radio Settings) - keeps the tooltip-icon pattern (a plain TextBlock.Text with the PUA glyph
/// tofu-boxes; it needs a dedicated Run with FontFamily="Segoe Fluent Icons" - see
/// feedback_unicode_source_file_corruption memory notes) in exactly one place.
public static class FormField
{
    public static FrameworkElement Row(string label, FrameworkElement control, string? tooltip, double labelWidth = 160)
    {
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(labelWidth) },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
            },
        };
        var labelBlock = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
        labelBlock.Inlines.Add(new Run { Text = label });
        if (!string.IsNullOrEmpty(tooltip))
        {
            labelBlock.Inlines.Add(new Run { Text = "  " });
            labelBlock.Inlines.Add(new Run
            {
                Text = Glyphs.IconHelp,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"),
                FontSize = 12,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x6b, 0xb0, 0xb8)),
            });
            ToolTipService.SetToolTip(labelBlock, tooltip);
        }
        Grid.SetColumn(labelBlock, 0);
        Grid.SetColumn(control, 1);
        grid.Children.Add(labelBlock);
        grid.Children.Add(control);
        return grid;
    }

    public static TextBlock SectionHeader(string text) => new()
    {
        Text = text,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        FontSize = 14,
        Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0xe0, 0x92, 0x4a)),
        Margin = new Thickness(0, 14, 0, 2),
    };
}
