using Microsoft.UI.Xaml.Controls;
using HamNetProgrammer.Core.Data;
using HamNetProgrammer.Desktop.Utils;
using Microsoft.Data.Sqlite;

namespace HamNetProgrammer.Desktop.Views;

public sealed partial class GroupListsPage : Page
{
    public sealed record MemberRow(int Position, string Name, string CallType, string DmrId);

    public GroupListsPage()
    {
        this.InitializeComponent();
        LoadGroupLists();
    }

    private void LoadGroupLists()
    {
        try
        {
            using var db = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT Name FROM GroupLists ORDER BY Name;";
            using var reader = cmd.ExecuteReader();

            var names = new List<string>();
            while (reader.Read())
                names.Add(reader.GetString(0));

            GroupListListView.ItemsSource = names;
            SummaryText.Text = $"{names.Count} group lists ({AppPaths.CodeplugDbPath})";

            if (names.Count > 0)
                GroupListListView.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            SummaryText.Text = $"Could not open codeplug database: {ex.Message}";
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GroupListListView.SelectedItem is not string name)
        {
            MemberListView.ItemsSource = null;
            return;
        }

        try
        {
            using var db = CodeplugDatabase.OpenOrCreate(AppPaths.CodeplugDbPath);
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT glc.Position, ct.Name, ct.CallType, ct.DmrId
                FROM GroupLists gl
                JOIN GroupListContacts glc ON glc.GroupListId = gl.Id
                JOIN Contacts ct ON ct.Id = glc.ContactId
                WHERE gl.Name = $name
                ORDER BY glc.Position;
                """;
            cmd.Parameters.Add(new SqliteParameter("$name", name));
            using var reader = cmd.ExecuteReader();

            var rows = new List<MemberRow>();
            while (reader.Read())
            {
                var position = reader.GetInt32(0);
                var contactName = reader.GetString(1);
                var callType = reader.GetString(2);
                var dmrId = reader.IsDBNull(3) ? "-" : reader.GetInt64(3).ToString();
                rows.Add(new MemberRow(position, contactName, callType, dmrId));
            }

            MemberListView.ItemsSource = rows;
        }
        catch (Exception ex)
        {
            SummaryText.Text = $"Could not load group list '{name}': {ex.Message}";
        }
    }
}
