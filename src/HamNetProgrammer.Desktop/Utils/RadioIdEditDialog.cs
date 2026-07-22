using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using HamNetProgrammer.Core.Online;

namespace HamNetProgrammer.Desktop.Utils;

/// <summary>
/// Create/edit a single RadioIds row (Callsign + DMR ID). Until now the only way a row got into
/// this table was reading it back off a real radio's existing programming - this is the "set one
/// up from scratch" path. "Look Up" reuses RadioIdLookup against the same 7-day-cached
/// radioid.net user CSV the CLI's lookup-dmrid command already uses, so the user doesn't have to
/// know their own DMR ID by heart.
/// </summary>
public static class RadioIdEditDialog
{
    public sealed record Result(string Callsign, uint? DmrId);

    public static async Task<Result?> ShowAsync(XamlRoot xamlRoot, string initialCallsign = "", uint? initialDmrId = null)
    {
        var callsignBox = new TextBox { Text = initialCallsign, PlaceholderText = "e.g. G0FQB" };
        var dmrIdBox = new TextBox { Text = initialDmrId?.ToString() ?? "", PlaceholderText = "e.g. 2343621" };
        var lookupButton = new Button { Content = "Look Up from radioid.net" };
        var statusText = new TextBlock { FontSize = 12, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x6b, 0xb0, 0xb8)), TextWrapping = TextWrapping.Wrap };

        // Vertical, not horizontal - a horizontal row here squeezed "Look Up from radioid.net"
        // past the dialog's right edge (the Grid column only leaves ~260px after the label),
        // clipping the button text against the dialog chrome instead of wrapping or resizing it.
        var dmrIdRow = new StackPanel { Orientation = Orientation.Vertical, Spacing = 6 };
        dmrIdRow.Children.Add(dmrIdBox);
        lookupButton.HorizontalAlignment = HorizontalAlignment.Left;
        dmrIdRow.Children.Add(lookupButton);

        var form = new StackPanel { Spacing = 8, Width = 420 };
        form.Children.Add(FormField.Row("Callsign", callsignBox, "Your amateur radio callsign, exactly as registered with your DMR ID provider (e.g. radioid.net)."));
        form.Children.Add(FormField.Row("DMR ID", dmrIdRow, "Your numeric DMR radio ID. Look it up automatically from your callsign, or enter it directly if you already know it."));
        form.Children.Add(statusText);

        var dialog = new ContentDialog
        {
            Title = string.IsNullOrEmpty(initialCallsign) ? "New Radio ID" : "Edit Radio ID",
            Content = form,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
        };

        lookupButton.Click += async (_, _) =>
        {
            var callsign = callsignBox.Text.Trim();
            if (callsign.Length == 0)
            {
                statusText.Text = "Enter a callsign first.";
                return;
            }

            lookupButton.IsEnabled = false;
            statusText.Text = "Looking up...";
            try
            {
                if (!File.Exists(AppPaths.RadioIdCachePath) ||
                    (DateTime.UtcNow - File.GetLastWriteTimeUtc(AppPaths.RadioIdCachePath)).TotalDays > 7)
                {
                    statusText.Text = "Downloading radioid.net user database (~17MB, cached for 7 days)...";
                    await RadioIdLookup.DownloadToCacheAsync(AppPaths.RadioIdCachePath);
                }

                var result = await Task.Run(() => RadioIdLookup.FindByCallsign(AppPaths.RadioIdCachePath, callsign));
                if (result is null)
                {
                    statusText.Text = $"No entry found for callsign '{callsign}' on radioid.net.";
                }
                else
                {
                    dmrIdBox.Text = result.DmrId.ToString();
                    statusText.Text = $"Found: DMR ID {result.DmrId} ({result.Name}, {result.Country}).";
                }
            }
            catch (Exception ex)
            {
                statusText.Text = $"Look up failed: {ex.Message}";
            }
            finally
            {
                lookupButton.IsEnabled = true;
            }
        };

        var dialogResult = await dialog.ShowAsync();
        if (dialogResult != ContentDialogResult.Primary) return null;

        var finalCallsign = callsignBox.Text.Trim();
        if (finalCallsign.Length == 0) return null;

        uint? finalDmrId = uint.TryParse(dmrIdBox.Text.Trim(), out var parsed) ? parsed : null;
        return new Result(finalCallsign, finalDmrId);
    }
}
