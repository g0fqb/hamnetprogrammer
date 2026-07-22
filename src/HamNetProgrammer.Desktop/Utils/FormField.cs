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

    /// <summary>Closed-choice ComboBox for a field with a small, fixed valid set (e.g. Tx Power,
    /// Colour Code) - a free-text TextBox for these let users type anything, including garbage the
    /// encoder can't do anything sensible with (confirmed: an unrecognized Power value silently
    /// becomes "High" with no warning). Falls back to the first option if the stored value doesn't
    /// match anything in the list (e.g. stale/legacy imported data) rather than throwing - visible
    /// to the user as "did this reset?" instead of a crash.</summary>
    public static ComboBox ClosedCombo(IReadOnlyList<string> options, string? currentValue)
    {
        var combo = new ComboBox { ItemsSource = options, HorizontalAlignment = HorizontalAlignment.Stretch };
        var match = options.FirstOrDefault(o => string.Equals(o, currentValue, StringComparison.OrdinalIgnoreCase));
        combo.SelectedItem = match ?? options.FirstOrDefault();
        return combo;
    }

    /// <summary>Same idea but editable - a curated list of common values (e.g. standard CTCSS
    /// tones) offered as choices, while still allowing a genuinely custom value to be typed (some
    /// fields have a real "Custom" concept beyond their standard table). Read back via .Text, not
    /// .SelectedItem, since IsEditable lets the box hold text that isn't in ItemsSource at all.</summary>
    public static ComboBox EditableCombo(IReadOnlyList<string> options, string? currentValue) => new()
    {
        ItemsSource = options,
        IsEditable = true,
        Text = currentValue ?? "",
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };

    public static TextBlock SectionHeader(string text) => new()
    {
        Text = text,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        FontSize = 14,
        Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0xe0, 0x92, 0x4a)),
        Margin = new Thickness(0, 14, 0, 2),
    };
}
