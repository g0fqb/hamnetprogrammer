using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HamNetProgrammer.Desktop.Utils;

/// Small reusable single-line text prompt built from a ContentDialog, since WinUI3 has no
/// built-in equivalent to a simple InputBox.
public static class TextInputDialog
{
    public static async Task<string?> ShowAsync(XamlRoot xamlRoot, string title, string label, string initialValue)
    {
        var textBox = new TextBox
        {
            Text = initialValue,
            PlaceholderText = label,
        };
        textBox.SelectAll();

        var dialog = new ContentDialog
        {
            Title = title,
            Content = textBox,
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary ? textBox.Text : null;
    }
}
