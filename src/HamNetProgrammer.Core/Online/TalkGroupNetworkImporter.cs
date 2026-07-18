using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace HamNetProgrammer.Core.Online;

public sealed record TalkGroupImportResult(string Network, int Added, int Updated, int Unchanged, IReadOnlyList<string> Warnings);

/// <summary>
/// Imports talkgroup lists from public DMR network APIs into Contacts, tagged with which network
/// they came from. Deliberately does NOT try to merge/dedupe the same DmrId across networks (e.g.
/// Brandmeister's TG91 and TGIF's TG91 stay as two separate contacts) - talkgroup meaning genuinely
/// varies by network, so silently treating them as the same contact would be a false equivalence.
/// If importing would otherwise collide with an existing differently-tagged contact's exact name
/// (Contacts has a UNIQUE(Name, CallType) constraint predating this feature, not touched here to
/// avoid a table-rebuild migration on live user data), the new contact's name gets the network
/// appended in parentheses to disambiguate rather than fail the import.
///
/// Three sources chosen for having a real, working, unauthenticated endpoint (confirmed directly,
/// not assumed from documentation): Brandmeister's own public API needs no key despite some other
/// operations requiring one; TGIF publishes a plain CSV; FreeDMR's mirror needs an ordinary browser
/// User-Agent header (blocks the default HttpClient one) but is otherwise open.
/// </summary>
public static class TalkGroupNetworkImporter
{
    private const string BrandmeisterUrl = "https://api.brandmeister.network/v2/talkgroup";
    private const string TgifUrl = "https://api.tgif.network/dmr/talkgroups/csv";
    private const string FreeDmrUrl = "http://downloads.freedmr.uk/downloads/talkgroup_ids.json";
    private const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";

    public static async Task<TalkGroupImportResult> ImportBrandmeisterAsync(SqliteConnection db, HttpClient? httpClient = null)
    {
        var client = httpClient ?? new HttpClient();
        try
        {
            var json = await client.GetStringAsync(BrandmeisterUrl);
            using var doc = JsonDocument.Parse(json);
            var entries = new List<(long DmrId, string Name)>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (!long.TryParse(prop.Name, out var dmrId)) continue;
                var name = prop.Value.GetString();
                if (string.IsNullOrWhiteSpace(name)) continue;
                entries.Add((dmrId, name.Trim()));
            }
            return ApplyImport(db, "Brandmeister", entries);
        }
        finally
        {
            if (httpClient is null) client.Dispose();
        }
    }

    public static async Task<TalkGroupImportResult> ImportTgifAsync(SqliteConnection db, HttpClient? httpClient = null)
    {
        var client = httpClient ?? new HttpClient();
        try
        {
            var csv = await client.GetStringAsync(TgifUrl);
            var entries = new List<(long DmrId, string Name)>();
            using var reader = new StringReader(csv);
            reader.ReadLine(); // header: TG Number,TG Name
            while (reader.ReadLine() is { } line)
            {
                var commaIndex = line.IndexOf(',');
                if (commaIndex <= 0) continue;
                var idPart = line[..commaIndex].Trim();
                var namePart = line[(commaIndex + 1)..].Trim();
                if (!long.TryParse(idPart, out var dmrId)) continue;
                if (string.IsNullOrWhiteSpace(namePart)) continue;
                entries.Add((dmrId, namePart));
            }
            return ApplyImport(db, "TGIF", entries);
        }
        finally
        {
            if (httpClient is null) client.Dispose();
        }
    }

    public static async Task<TalkGroupImportResult> ImportFreeDmrAsync(SqliteConnection db, HttpClient? httpClient = null)
    {
        var client = httpClient ?? new HttpClient();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, FreeDmrUrl);
            // The server blocks HttpClient's default User-Agent (or lack thereof) with a 403 -
            // confirmed directly, an ordinary browser UA is all it takes.
            request.Headers.UserAgent.ParseAdd(BrowserUserAgent);
            using var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(json);
            var entries = new List<(long DmrId, string Name)>();
            foreach (var item in doc.RootElement.GetProperty("results").EnumerateArray())
            {
                if (!item.TryGetProperty("tgid", out var idProp)) continue;
                var dmrId = idProp.GetInt64();
                var name = item.TryGetProperty("callsign", out var nameProp) ? nameProp.GetString() : null;
                if (string.IsNullOrWhiteSpace(name)) continue;
                entries.Add((dmrId, name.Trim()));
            }
            return ApplyImport(db, "FreeDMR", entries);
        }
        finally
        {
            if (httpClient is null) client.Dispose();
        }
    }

    private static TalkGroupImportResult ApplyImport(SqliteConnection db, string network, IReadOnlyList<(long DmrId, string Name)> entries)
    {
        var warnings = new List<string>();
        int added = 0, updated = 0, unchanged = 0;

        using var transaction = db.BeginTransaction();

        foreach (var (dmrId, name) in entries)
        {
            long? existingId = null;
            string? existingName = null;
            using (var findCmd = db.CreateCommand())
            {
                findCmd.Transaction = transaction;
                findCmd.CommandText = "SELECT Id, Name FROM Contacts WHERE DmrId = $dmrId AND Network = $network AND CallType = 'Group';";
                findCmd.Parameters.AddWithValue("$dmrId", dmrId);
                findCmd.Parameters.AddWithValue("$network", network);
                using var reader = findCmd.ExecuteReader();
                if (reader.Read())
                {
                    existingId = reader.GetInt64(0);
                    existingName = reader.GetString(1);
                }
            }

            if (existingId is { } id)
            {
                if (existingName == name)
                {
                    unchanged++;
                    continue;
                }
                using var updateCmd = db.CreateCommand();
                updateCmd.Transaction = transaction;
                updateCmd.CommandText = "UPDATE Contacts SET Name = $name WHERE Id = $id;";
                updateCmd.Parameters.AddWithValue("$name", name);
                updateCmd.Parameters.AddWithValue("$id", id);
                updateCmd.ExecuteNonQuery();
                updated++;
                continue;
            }

            // Disambiguate against Contacts.UNIQUE(Name, CallType) - not just across networks
            // (the expected case), but also within the SAME network's own list, since a handful of
            // real entries share an identical name under different DmrIds (confirmed against the
            // real TGIF/FreeDMR/Brandmeister data, not a hypothetical). Network alone isn't always
            // enough; falling back to including the DmrId (which IS guaranteed unique per network)
            // always resolves it.
            var insertName = NameExists(db, transaction, name) ? $"{name} ({network})" : name;
            if (NameExists(db, transaction, insertName))
                insertName = $"{name} ({network} TG{dmrId})";

            using (var insertCmd = db.CreateCommand())
            {
                insertCmd.Transaction = transaction;
                insertCmd.CommandText = "INSERT INTO Contacts (Name, CallType, DmrId, Network) VALUES ($name, 'Group', $dmrId, $network);";
                insertCmd.Parameters.AddWithValue("$name", insertName);
                insertCmd.Parameters.AddWithValue("$dmrId", dmrId);
                insertCmd.Parameters.AddWithValue("$network", network);
                try
                {
                    insertCmd.ExecuteNonQuery();
                    added++;
                }
                catch (SqliteException ex)
                {
                    warnings.Add($"Skipped TG{dmrId} '{insertName}': {ex.Message}");
                }
            }
        }

        transaction.Commit();
        return new TalkGroupImportResult(network, added, updated, unchanged, warnings);
    }

    private static bool NameExists(SqliteConnection db, SqliteTransaction transaction, string name)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT COUNT(*) FROM Contacts WHERE Name = $name AND CallType = 'Group';";
        cmd.Parameters.AddWithValue("$name", name);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }
}
