using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace HamNetProgrammer.Core.Online;

public sealed record TalkGroupImportResult(string Network, int Added, int Updated, int Unchanged, IReadOnlyList<string> Warnings);

/// <summary>
/// Imports talkgroup lists from public DMR network APIs into Contacts, deduped strictly by DmrId -
/// the radio's own TalkGroupList addresses a group call by number alone, with no concept of
/// "network" anywhere in the format (confirmed: TalkGroupRecordCodec only encodes Name+DmrId), and
/// which network a call actually reaches is entirely a function of the hotspot's own
/// configuration, outside the codeplug. Treating "Brandmeister's TG91" and "FreeDMR's TG91" as
/// different contacts would just waste a TalkGroupList slot on the radio for what is, at the
/// protocol level, an identical group call target. Whichever import claims a DmrId first keeps it;
/// a later import of the same number (from this or any other network, or a manually-created
/// contact) leaves it untouched rather than creating a duplicate. Network is recorded purely as
/// "where this contact's data came from" provenance, not a merge key - re-running the SAME
/// network's import later can still refresh a name it previously set, but never overrides a number
/// claimed by a different source.
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
            string? existingNetwork = null;
            using (var findCmd = db.CreateCommand())
            {
                findCmd.Transaction = transaction;
                findCmd.CommandText = "SELECT Id, Name, Network FROM Contacts WHERE DmrId = $dmrId AND CallType = 'Group' LIMIT 1;";
                findCmd.Parameters.AddWithValue("$dmrId", dmrId);
                using var reader = findCmd.ExecuteReader();
                if (reader.Read())
                {
                    existingId = reader.GetInt64(0);
                    existingName = reader.GetString(1);
                    existingNetwork = reader.IsDBNull(2) ? null : reader.GetString(2);
                }
            }

            if (existingId is { } id)
            {
                // A different source (another network, or a manually-created contact) already
                // claimed this number - leave it alone rather than duplicate or override it.
                if (existingNetwork != network || existingName == name)
                {
                    unchanged++;
                    continue;
                }
                // Re-running THIS network's own import can still refresh a name it previously set.
                using var updateCmd = db.CreateCommand();
                updateCmd.Transaction = transaction;
                updateCmd.CommandText = "UPDATE Contacts SET Name = $name WHERE Id = $id;";
                updateCmd.Parameters.AddWithValue("$name", name);
                updateCmd.Parameters.AddWithValue("$id", id);
                updateCmd.ExecuteNonQuery();
                updated++;
                continue;
            }

            // Disambiguate against Contacts.UNIQUE(Name, CallType) for the rarer case of two
            // different DmrIds sharing an identical display name (confirmed against real data,
            // not a hypothetical - e.g. TGIF has several distinct "SV2SNH" entries). Appending the
            // DmrId is guaranteed unique here since we've just confirmed no row already has it.
            var insertName = NameExists(db, transaction, name) ? $"{name} (TG{dmrId})" : name;

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
