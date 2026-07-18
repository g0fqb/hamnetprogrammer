using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HamNetProgrammer.Desktop.Utils;

/// A button showing the currently-picked channel's name that opens ChannelPickerDialog on click -
/// used wherever a single-channel reference needs picking from the full list (scan list priority
/// channels, APRS report channel) rather than a scrollable combo.
public static class ChannelButtonPicker
{
    public sealed class Picker
    {
        public required FrameworkElement Container { get; init; }
        public required Func<long?> GetSelectedId { get; init; }
    }

    public static Picker Build(
        XamlRoot xamlRoot, List<ChannelPickerDialog.ChannelPickerRow> allChannels, long? initialId, string pickerTitle)
    {
        var selected = allChannels.FirstOrDefault(c => c.ChannelId == initialId);
        var button = new Button
        {
            Content = selected?.Name ?? "(none)",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };

        button.Click += async (_, _) =>
        {
            var picked = await ChannelPickerDialog.ShowAsync(xamlRoot, allChannels, pickerTitle, "Select", allowNone: true);
            selected = picked;
            button.Content = picked?.Name ?? "(none)";
        };

        return new Picker { Container = button, GetSelectedId = () => selected?.ChannelId };
    }
}
