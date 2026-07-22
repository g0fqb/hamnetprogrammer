using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace HamNetProgrammer.Core.Online;

public sealed record TalkGroupImportResult(int Added, int Updated, int Unchanged, IReadOnlyList<string> Warnings);

/// <summary>One network's name for a DmrId, as published by the shared backend - public so
/// read-only consumers (e.g. TalkGroupAuditor) can compare against it without triggering an
/// actual import/write.</summary>
public sealed record NetworkTalkGroup(long DmrId, string Name, string Network);

/// <summary>
/// Imports the DMR talkgroup list from HamNetProgrammer's shared backend (PacketCluster/Ham Net
/// Global on Railway), which merges Brandmeister, TGIF, and FreeDMR - see the backend's
/// internal/talkgroups package. Every install reads the same reference this way, rather than each
/// client independently importing from and resolving overlaps between the three networks itself.
///
/// The same DmrId can legitimately appear more than once (once per network that has its own name
/// for it) - the backend no longer collapses these, since the display name is what a user actually
/// searches by, and picking an arbitrary "winning" network's name for a number forced anyone on a
/// different network to know that other network's title just to find their own TG. Existing-row
/// matching here is scoped to (DmrId, Network) accordingly: re-running the import refreshes each
/// network's own row in place, but a different network's entry for the same number becomes its own
/// row rather than overwriting it. A manually-created contact (Network NULL) never matches this
/// lookup at all, so it's never touched by any network's import.
///
/// Real-hardware incident, 2026-07-20: switching off the old DmrId-only dedup let all three
/// networks' rows coexist, which took the on-radio talkgroup list from ~3,000 to 5,721 entries -
/// enough to freeze the AT-D878UV's own talkgroup browser solid (required a battery pull to
/// recover), and every contact after the new rows got a different index than before, which is
/// what a channel's on-radio group-call reference actually is (see AnyToneD878CodeplugEncoder's
/// BuildContactIndex remarks) - explaining channels resolving to the wrong, unrelated talkgroup
/// after a sync. <c>networkFilter</c> defaults to the one network actually in use (FreeDMR) until
/// there's a real per-user network preference (the still-open "network-scoping redesign") - this
/// is a stopgap that keeps the import from re-exploding on every app launch, not the final design.
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

    public static async Task<TalkGroupImportResult> ImportAsync(SqliteConnection db, HttpClient? httpClient = null, string? networkFilter = "FreeDMR")
    {
        var fetched = await FetchAsync(httpClient, networkFilter);
        var entries = fetched.Select(f => new TalkgroupEntry(f.DmrId, f.Name, f.Network)).ToList();
        return ApplyImport(db, entries);
    }

    /// <summary>Read-only fetch from the shared backend - no database touched. Public so
    /// read-only consumers (e.g. a duplicate/mislabeled-talkgroup audit) can compare against the
    /// same source of truth ImportAsync itself uses, without writing anything.</summary>
    public static async Task<List<NetworkTalkGroup>> FetchAsync(HttpClient? httpClient = null, string? networkFilter = null)
    {
        var client = httpClient ?? new HttpClient();
        try
        {
            using var response = await client.GetAsync(TalkgroupsUrl);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();

            var parsed = JsonSerializer.Deserialize<TalkgroupsResponse>(json)
                ?? throw new InvalidOperationException("Empty response from talkgroups endpoint.");

            var entries = networkFilter is null
                ? parsed.Talkgroups
                : parsed.Talkgroups.Where(t => string.Equals(t.Network, networkFilter, StringComparison.OrdinalIgnoreCase)).ToList();

            return entries.Select(e => new NetworkTalkGroup(e.DmrId, e.Name, e.Network)).ToList();
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
            // Scoped to this entry's own network - a manually-created contact (Network NULL) can
            // never match, and a different network's row for the same DmrId is a separate entry,
            // not something this lookup should find or touch.
            using (var findCmd = db.CreateCommand())
            {
                findCmd.Transaction = transaction;
                findCmd.CommandText = "SELECT Id, Name FROM Contacts WHERE DmrId = $dmrId AND CallType = 'Group' AND Network = $network LIMIT 1;";
                findCmd.Parameters.AddWithValue("$dmrId", entry.DmrId);
                findCmd.Parameters.AddWithValue("$network", entry.Network);
                using var reader = findCmd.ExecuteReader();
                if (reader.Read())
                {
                    existingId = reader.GetInt64(0);
                    existingName = reader.GetString(1);
                }
            }

            if (existingId is { } id)
            {
                // Just re-running the same import; refresh the name if it changed.
                if (existingName == name)
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
