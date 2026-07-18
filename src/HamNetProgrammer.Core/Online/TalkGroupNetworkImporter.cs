using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace HamNetProgrammer.Core.Online;

public sealed record TalkGroupImportResult(int Added, int Updated, int Unchanged, IReadOnlyList<string> Warnings);

/// <summary>
/// Imports the DMR talkgroup list from HamNetProgrammer's shared backend (PacketCluster/Ham Net
/// Global on Railway), which itself merges Brandmeister, TGIF, and FreeDMR into one list deduped
/// by DmrId - see the backend's internal/talkgroups package. Every install reads the same
/// reference this way, rather than each client independently importing from and resolving
/// overlaps between the three networks itself.
///
/// Dedup is strictly by DmrId, not (DmrId, Network): the radio's own TalkGroupList addresses a
/// group call by number alone, with no concept of "network" anywhere in the format (confirmed:
/// TalkGroupRecordCodec only encodes Name+DmrId), and which network a call actually reaches is
/// entirely a function of the hotspot's own configuration, outside the codeplug. A DmrId already
/// present locally (imported before, or manually created) is left untouched rather than
/// duplicated; Network is recorded purely as "where this contact's data came from" provenance.
/// </summary>
public static class TalkGroupNetworkImporter
{
    private const string TalkgroupsUrl = "https://api-production-8765.up.railway.app/v1/talkgroups";

    private sealed record TalkgroupsResponse(
        [property: JsonPropertyName("talkgroups")] List<TalkgroupEntry> Talkgroups,
        [property: JsonPropertyName("count")] int Count);

    private sealed record TalkgroupEntry(
        [property: JsonPropertyName("dmr_id")] long DmrId,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("network")] string Network);

    public static async Task<TalkGroupImportResult> ImportAsync(SqliteConnection db, HttpClient? httpClient = null)
    {
        var client = httpClient ?? new HttpClient();
        try
        {
            using var response = await client.GetAsync(TalkgroupsUrl);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();

            var parsed = JsonSerializer.Deserialize<TalkgroupsResponse>(json)
                ?? throw new InvalidOperationException("Empty response from talkgroups endpoint.");

            return ApplyImport(db, parsed.Talkgroups);
        }
        finally
        {
            if (httpClient is null) client.Dispose();
        }
    }

    private static TalkGroupImportResult ApplyImport(SqliteConnection db, IReadOnlyList<TalkgroupEntry> entries)
    {
        var warnings = new List<string>();
        int added = 0, updated = 0, unchanged = 0;

        using var transaction = db.BeginTransaction();

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Name)) continue;
            var name = entry.Name.Trim();

            long? existingId = null;
            string? existingName = null;
            string? existingNetwork = null;
            using (var findCmd = db.CreateCommand())
            {
                findCmd.Transaction = transaction;
                findCmd.CommandText = "SELECT Id, Name, Network FROM Contacts WHERE DmrId = $dmrId AND CallType = 'Group' LIMIT 1;";
                findCmd.Parameters.AddWithValue("$dmrId", entry.DmrId);
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
                // A manually-created contact (Network NULL) already claims this number - leave it.
                // Otherwise this is just re-running the same import; refresh the name if it changed.
                if (existingNetwork is null || existingName == name)
                {
                    unchanged++;
                    continue;
                }
                // Disambiguate against Contacts.UNIQUE(Name, CallType) the same way INSERT does -
                // a refreshed name can collide with a different contact's existing name (e.g. the
                // upstream data renames this entry to something another row already has).
                var updateName = NameExists(db, transaction, name, excludingId: id) ? $"{name} (TG{entry.DmrId})" : name;

                using var updateCmd = db.CreateCommand();
                updateCmd.Transaction = transaction;
                updateCmd.CommandText = "UPDATE Contacts SET Name = $name, Network = $network WHERE Id = $id;";
                updateCmd.Parameters.AddWithValue("$name", updateName);
                updateCmd.Parameters.AddWithValue("$network", entry.Network);
                updateCmd.Parameters.AddWithValue("$id", id);
                try
                {
                    updateCmd.ExecuteNonQuery();
                    updated++;
                }
                catch (SqliteException ex)
                {
                    warnings.Add($"Could not update TG{entry.DmrId} '{updateName}': {ex.Message}");
                }
                continue;
            }

            // Disambiguate against Contacts.UNIQUE(Name, CallType) for the rare case of two
            // different DmrIds sharing an identical display name (confirmed against real data -
            // e.g. TGIF has several distinct "SV2SNH" entries).
            var insertName = NameExists(db, transaction, name) ? $"{name} (TG{entry.DmrId})" : name;

            using (var insertCmd = db.CreateCommand())
            {
                insertCmd.Transaction = transaction;
                insertCmd.CommandText = "INSERT INTO Contacts (Name, CallType, DmrId, Network) VALUES ($name, 'Group', $dmrId, $network);";
                insertCmd.Parameters.AddWithValue("$name", insertName);
                insertCmd.Parameters.AddWithValue("$dmrId", entry.DmrId);
                insertCmd.Parameters.AddWithValue("$network", entry.Network);
                try
                {
                    insertCmd.ExecuteNonQuery();
                    added++;
                }
                catch (SqliteException ex)
                {
                    warnings.Add($"Skipped TG{entry.DmrId} '{insertName}': {ex.Message}");
                }
            }
        }

        transaction.Commit();
        return new TalkGroupImportResult(added, updated, unchanged, warnings);
    }

    private static bool NameExists(SqliteConnection db, SqliteTransaction transaction, string name, long? excludingId = null)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = excludingId is null
            ? "SELECT COUNT(*) FROM Contacts WHERE Name = $name AND CallType = 'Group';"
            : "SELECT COUNT(*) FROM Contacts WHERE Name = $name AND CallType = 'Group' AND Id != $excludingId;";
        cmd.Parameters.AddWithValue("$name", name);
        if (excludingId is { } id) cmd.Parameters.AddWithValue("$excludingId", id);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }
}
