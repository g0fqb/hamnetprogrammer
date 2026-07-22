using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using HamNetProgrammer.Core.Data;
using HamNetProgrammer.Desktop.Utils;

namespace HamNetProgrammer.Desktop.Views;

/// <summary>
/// Runs CodeplugHealthCheck's small, high-confidence set of structural checks against the current
/// database and shows the results - a good first step after Read Codeplug, since a first read
/// against a radio with years of CPS/RT Systems history can easily surface real inconsistencies
/// (leftover duplicate channels, drifted lists) that are otherwise invisible without SQL.
/// </summary>
public sealed partial class HealthCheckPage : Page
{
    public HealthCheckPage()
    {
        this.InitializeComponent();
        this.NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Enabled;
    }

    private void OnRunClicked(object sender, RoutedEventArgs e)
    {
        RunButton.IsEnabled = false;
        ResultsText.Text = "Running...";

        try
        {
            using var db = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);
            var findings = CodeplugHealthCheck.Run(db);

            if (findings.Count == 0)
            {
                ResultsText.Text = "No issues found.";
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"{findings.Count} category(ies) with findings:");
            sb.AppendLine();
            foreach (var finding in findings)
            {
                sb.AppendLine($"{finding.Category}");
                sb.AppendLine($"  {finding.Summary}");
                foreach (var detail in finding.Details)
                    sb.AppendLine($"    - {detail}");
                sb.AppendLine();
            }
            ResultsText.Text = sb.ToString();
        }
        catch (Exception ex)
        {
            ResultsText.Text = $"Error: {ex.Message}";
        }
        finally
        {
            RunButton.IsEnabled = true;
        }
    }
}
