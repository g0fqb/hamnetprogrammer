using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using HamNetProgrammer.Core.Diagnostics;

namespace HamNetProgrammer.Desktop.Utils;

/// <summary>
/// Shows a write-risk disclaimer scaled to how much confidence exists in the target radio's
/// memory map (see RadioRiskCatalog). Validated/Moderate models get a checkbox; High/Unknown
/// models require typing a confirmation phrase, since a checkbox is too easy to click past for
/// a warning that means "recovery is not guaranteed".
/// </summary>
public static class RiskDisclaimerDialog
{
    private const string ConfirmPhrase = "I UNDERSTAND THE RISK";

    public static async Task<bool> ShowAsync(XamlRoot xamlRoot, RadioRiskProfile profile)
    {
        var warnBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0xe9, 0x45, 0x60));
        var mutedBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x88, 0x96, 0xa0));

        var panel = new StackPanel { Spacing = 10, MaxWidth = 440 };

        panel.Children.Add(new TextBlock
        {
            Text = $"{profile.ModelLabel} - {TierLabel(profile.Tier)}",
            FontWeight = FontWeights.SemiBold,
            Foreground = profile.Tier is RadioRiskTier.High or RadioRiskTier.Unknown ? warnBrush : mutedBrush,
        });

        panel.Children.Add(new TextBlock
        {
            Text = profile.Explanation,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
        });

        panel.Children.Add(new TextBlock
        {
            Text = "Before continuing: back up your codeplug using the official AnyTone CPS or RT Systems " +
                   "software as well. This tool takes its own automatic backup, but an official-software " +
                   "backup is an independent recovery path if anything goes wrong that this tool's own " +
                   "restore can't fix.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = warnBrush,
            Margin = new Thickness(0, 4, 0, 0),
        });

        panel.Children.Add(new TextBlock
        {
            Text = "A diagnostic session (audit log + memory backup) will also be saved automatically for this write, " +
                   "and you can send it to admin@hamsoft.co.uk afterwards if anything looks wrong.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = mutedBrush,
        });

        bool requireTyping = profile.Tier is RadioRiskTier.High or RadioRiskTier.Unknown;
        CheckBox? ackCheckBox = null;
        TextBox? ackTextBox = null;

        if (requireTyping)
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"Type \"{ConfirmPhrase}\" to continue:",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Margin = new Thickness(0, 6, 0, 0),
            });
            ackTextBox = new TextBox { PlaceholderText = ConfirmPhrase };
            panel.Children.Add(ackTextBox);
        }
        else
        {
            ackCheckBox = new CheckBox { Content = "I understand this writes to the radio's flash memory and want to continue." };
            panel.Children.Add(ackCheckBox);
        }

        var dialog = new ContentDialog
        {
            Title = $"{Glyphs.IconAlert}  Confirm Write",
            Content = panel,
            PrimaryButtonText = "Write",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = xamlRoot,
        };

        dialog.PrimaryButtonClick += (_, e) =>
        {
            var acknowledged = requireTyping
                ? string.Equals(ackTextBox!.Text.Trim(), ConfirmPhrase, StringComparison.OrdinalIgnoreCase)
                : ackCheckBox!.IsChecked == true;
            if (!acknowledged) e.Cancel = true;
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private static string TierLabel(RadioRiskTier tier) => tier switch
    {
        RadioRiskTier.Validated => "validated",
        RadioRiskTier.Moderate => "moderate risk",
        RadioRiskTier.High => "high risk - unverified",
        RadioRiskTier.Unknown => "unknown device",
        _ => tier.ToString(),
    };
}
