using Microsoft.UI.Xaml.Controls;
using HamNetProgrammer.Core.Data;
using HamNetProgrammer.Desktop.Utils;
using Microsoft.Data.Sqlite;

namespace HamNetProgrammer.Desktop.Views;

public sealed partial class ZonesPage : Page
{
    public sealed record ChannelRow(int Position, string Name, string FrequencyMHz, string TalkGroup, string ColorCodeSlot);

    public ZonesPage()
    {
        this.InitializeComponent();
        LoadZones();
    }

    private void LoadZones()
    {
        try
        {
            using var db = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT Name FROM Zones ORDER BY Name;";
            using var reader = cmd.ExecuteReader();

            var names = new List<string>();
            while (reader.Read())
                names.Add(reader.GetString(0));

            ZoneListView.ItemsSource = names;

            using var countCmd = db.CreateCommand();
            countCmd.CommandText = "SELECT COUNT(*) FROM Channels;";
            var totalChannels = Convert.ToInt32(countCmd.ExecuteScalar());
            SummaryText.Text = $"{names.Count} zones, {totalChannels} channels ({AppPaths.CodeplugDbPath})";

            if (names.Count > 0)
                ZoneListView.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            SummaryText.Text = $"Could not open codeplug database: {ex.Message}";
        }
    }

    private void OnZoneSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ZoneListView.SelectedItem is not string zoneName)
        {
            ChannelListView.ItemsSource = null;
            return;
        }

        try
        {
            using var db = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT zc.Position, c.Name, c.RxFrequencyHz, ct.Name AS TalkGroup, c.ColorCode, c.TimeSlot
                FROM Zones z
                JOIN ZoneChannels zc ON zc.ZoneId = z.Id
                JOIN Channels c ON c.Id = zc.ChannelId
                LEFT JOIN Contacts ct ON ct.Id = c.ContactId
                WHERE z.Name = $name
                ORDER BY zc.Position;
                """;
            cmd.Parameters.Add(new SqliteParameter("$name", zoneName));
            using var reader = cmd.ExecuteReader();

            var rows = new List<ChannelRow>();
            while (reader.Read())
            {
                var position = reader.GetInt32(0);
                var name = reader.GetString(1);
                var rxHz = reader.GetInt64(2);
                var talkGroup = reader.IsDBNull(3) ? "-" : reader.GetString(3);
                var colorCode = reader.IsDBNull(4) ? "-" : reader.GetInt32(4).ToString();
                var timeSlot = reader.IsDBNull(5) ? "-" : reader.GetInt32(5).ToString();

                rows.Add(new ChannelRow(position, name, (rxHz / 1_000_000.0).ToString("F5"), talkGroup, $"{colorCode}/{timeSlot}"));
            }

            ChannelListView.ItemsSource = rows;
        }
        catch (Exception ex)
        {
            SummaryText.Text = $"Could not load zone '{zoneName}': {ex.Message}";
        }
    }
}
