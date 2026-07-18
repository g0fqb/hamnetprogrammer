using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HamNetProgrammer.Desktop.Utils;

/// Search-and-pick dialog for choosing a channel from the full list - type-to-filter over channel
/// number/name. Used by Zones' "Add Channel" flow and by Scan Lists' priority-channel pickers.
public static class ChannelPickerDialog
{
    public sealed record ChannelPickerRow(long ChannelId, int ChannelNumber, string Name, string FrequencyMHz);

    private static readonly ChannelPickerRow NoneRow = new(0, 0, "(none)", "");

    public static async Task<ChannelPickerRow?> ShowAsync(
        XamlRoot xamlRoot, IReadOnlyList<ChannelPickerRow> candidates, string title = "Add Channel",
        string confirmText = "Add", bool allowNone = false)
    {
        var items = allowNone ? new[] { NoneRow }.Concat(candidates).ToList() : candidates.ToList();

        var searchBox = new TextBox { PlaceholderText = "Search by name or channel number..." };
        var listView = new ListView
        {
            Height = 320,
            SelectionMode = ListViewSelectionMode.Single,
            ItemsSource = items,
            ItemTemplate = BuildItemTemplate(),
        };

        var panel = new StackPanel { Spacing = 8, Width = 420 };
        panel.Children.Add(searchBox);
        panel.Children.Add(listView);

        var dialog = new ContentDialog
        {
            Title = title,
            Content = panel,
            PrimaryButtonText = confirmText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
            XamlRoot = xamlRoot,
        };

        listView.SelectionChanged += (_, _) =>
            dialog.IsPrimaryButtonEnabled = listView.SelectedItem is not null;

        searchBox.TextChanged += (_, _) =>
        {
            var query = searchBox.Text.Trim();
            var filtered = string.IsNullOrEmpty(query)
                ? candidates
                : candidates.Where(c =>
                    c.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    c.ChannelNumber.ToString().Contains(query)).ToList();
            listView.ItemsSource = allowNone ? new[] { NoneRow }.Concat(filtered).ToList() : filtered.ToList();
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return null;
        var selected = listView.SelectedItem as ChannelPickerRow;
        return selected == NoneRow ? null : selected;
    }

    private static DataTemplate BuildItemTemplate()
    {
        const string xaml = """
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                          xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <Grid Padding="6,4">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="50" />
                        <ColumnDefinition Width="*" />
                        <ColumnDefinition Width="90" />
                    </Grid.ColumnDefinitions>
                    <TextBlock Grid.Column="0" Text="{Binding ChannelNumber}" FontFamily="Consolas" FontSize="12" />
                    <TextBlock Grid.Column="1" Text="{Binding Name}" FontSize="12" />
                    <TextBlock Grid.Column="2" Text="{Binding FrequencyMHz}" FontFamily="Consolas" FontSize="12" />
                </Grid>
            </DataTemplate>
            """;
        return (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(xaml);
    }
}
