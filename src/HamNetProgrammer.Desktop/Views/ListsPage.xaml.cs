using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using HamNetProgrammer.Core.Data;
using HamNetProgrammer.Desktop.Utils;
using Microsoft.Data.Sqlite;

namespace HamNetProgrammer.Desktop.Views;

/// <summary>
/// Own page for the three "does this feature reach the radio at all" toggles (Scan Lists, Group
/// Lists, Roaming Zones) plus the existing zone-sync setting - previously a single checkbox buried
/// at the bottom of Radio Settings, which undersold how much these three features matter: someone
/// who doesn't use roaming, for instance, should be able to say so explicitly rather than have it
/// silently written anyway because nothing ever told the radio not to.
///
/// "Disabled" here means the encoder skips that feature's regions (including its own "Used"
/// bitmap) entirely on the next write - not merely "stop auto-syncing membership from zones",
/// which is what SyncListsWithZones alone used to (and still does) control.
/// </summary>
public sealed partial class ListsPage : Page
{
    private CheckBox? _scanListsEnabledBox;
    private CheckBox? _groupListsEnabledBox;
    private CheckBox? _roamingEnabledBox;
    private CheckBox? _syncListsWithZonesBox;

    public ListsPage()
    {
        this.InitializeComponent();
        Load();
    }

    private void Load()
    {
        try
        {
            using var db = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT ScanListsEnabled, GroupListsEnabled, RoamingEnabled, SyncListsWithZones
                FROM RadioSettings WHERE Id = 1;
                """;
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                SummaryText.Text = "No RadioSettings row found.";
                return;
            }

            var scanListsEnabled = reader.GetInt64(0) != 0;
            var groupListsEnabled = reader.GetInt64(1) != 0;
            var roamingEnabled = reader.GetInt64(2) != 0;
            var syncListsWithZones = reader.GetInt64(3) != 0;
            reader.Close();

            _scanListsEnabledBox = new CheckBox { Content = "Write Scan Lists to the radio", IsChecked = scanListsEnabled };
            _groupListsEnabledBox = new CheckBox { Content = "Write Group Lists to the radio", IsChecked = groupListsEnabled };
            _roamingEnabledBox = new CheckBox { Content = "Write Roaming Zones to the radio", IsChecked = roamingEnabled };
            _syncListsWithZonesBox = new CheckBox { Content = "Keep Scan/Group/Roaming Lists in sync with Zones", IsChecked = syncListsWithZones };

            BuildForm();
            SummaryText.Text = $"({AppPaths.CodeplugDbPath})";
        }
        catch (Exception ex)
        {
            SummaryText.Text = $"Could not open codeplug database: {ex.Message}";
        }
    }

    private void BuildForm()
    {
        FormPanel.Children.Clear();

        FormPanel.Children.Add(FormField.SectionHeader("Scan Lists"));
        FormPanel.Children.Add(FormField.Row("Enabled", _scanListsEnabledBox!,
            "If off, no Scan List data (or its per-slot channel references) reaches the radio on the next write, regardless of what's configured here - whatever the radio already has for Scan Lists is left untouched."));

        FormPanel.Children.Add(FormField.SectionHeader("Group Lists"));
        FormPanel.Children.Add(FormField.Row("Enabled", _groupListsEnabledBox!,
            "If off, no Group List data (or its per-channel references) reaches the radio on the next write - whatever the radio already has for Group Lists is left untouched."));

        FormPanel.Children.Add(FormField.SectionHeader("Roaming Zones"));
        FormPanel.Children.Add(FormField.Row("Enabled", _roamingEnabledBox!,
            "If off, no Roaming Zone/Channel data reaches the radio on the next write - whatever the radio already has for roaming is left untouched. Turn this off if you don't use roaming between hotspots."));

        FormPanel.Children.Add(FormField.SectionHeader("Automation"));
        FormPanel.Children.Add(FormField.Row("Sync Lists", _syncListsWithZonesBox!,
            "Before every Write Codeplug (for whichever of the above are enabled), regenerate each " +
            "zone's Scan List/Group List membership and talkgroup Roaming Zones from the zones' " +
            "current channels. Turn off if you've manually customized list membership and don't " +
            "want it overwritten."));
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            using var db = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                UPDATE RadioSettings SET
                    ScanListsEnabled = $scanListsEnabled,
                    GroupListsEnabled = $groupListsEnabled,
                    RoamingEnabled = $roamingEnabled,
                    SyncListsWithZones = $syncListsWithZones
                WHERE Id = 1;
                """;
            cmd.Parameters.Add(new SqliteParameter("$scanListsEnabled", _scanListsEnabledBox!.IsChecked == true ? 1 : 0));
            cmd.Parameters.Add(new SqliteParameter("$groupListsEnabled", _groupListsEnabledBox!.IsChecked == true ? 1 : 0));
            cmd.Parameters.Add(new SqliteParameter("$roamingEnabled", _roamingEnabledBox!.IsChecked == true ? 1 : 0));
            cmd.Parameters.Add(new SqliteParameter("$syncListsWithZones", _syncListsWithZonesBox!.IsChecked == true ? 1 : 0));
            cmd.ExecuteNonQuery();

            StatusText.Text = "Saved.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not save: {ex.Message}";
        }
    }
}
