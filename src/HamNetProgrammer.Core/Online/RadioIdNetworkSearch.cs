using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HamNetProgrammer.Core.Online;

public sealed record RadioIdSearchResult(long DmrId, string Callsign, string Name, string Country);

/// <summary>
/// Live search against the shared backend's individual-DMR-ID directory (radioid.net's ~309k rows,
/// held server-side only - see the backend's internal/radioids package). This is deliberately a
/// per-query search, not a bulk import like TalkGroupNetworkImporter: the row count is ~60x bigger
/// than talkgroups, so no client downloads or holds the whole thing locally. Used for adding
/// someone else as a Private Call contact - distinct from RadioIdLookup, which is a single-callsign
/// exact lookup for "what's MY DMR ID" (Radio IDs page).
/// </summary>
public static class RadioIdNetworkSearch
{
    private const string SearchUrl = "https://api-production-8765.up.railway.app/v1/radio-ids/search";

    private sealed record SearchResponse(
        [property: JsonPropertyName("results")] List<ResultEntry>? Results,
        [property: JsonPropertyName("count")] int Count);

    private sealed record ResultEntry(
        [property: JsonPropertyName("dmr_id")] long DmrId,
        [property: JsonPropertyName("callsign")] string Callsign,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("country")] string Country);

    /// <summary>Returns an empty list on any failure (network error, backend not ready yet, query
    /// too short) rather than throwing - this backs live type-ahead search, where a blip should
    /// just show no results for that keystroke, not surface an error to the user.</summary>
    public static async Task<IReadOnlyList<RadioIdSearchResult>> SearchAsync(string query, HttpClient? httpClient = null, CancellationToken cancellationToken = default)
    {
        if (query.Trim().Length < 2) return [];

        var client = httpClient ?? new HttpClient();
        try
        {
            var url = $"{SearchUrl}?q={Uri.EscapeDataString(query.Trim())}";
            using var response = await client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode) return [];

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var parsed = JsonSerializer.Deserialize<SearchResponse>(json);
            if (parsed?.Results is null) return [];

            return parsed.Results
                .Select(r => new RadioIdSearchResult(r.DmrId, r.Callsign, r.Name, r.Country))
                .ToList();
        }
        catch (Exception)
        {
            // Covers both a genuine failure and a debounce cancellation - either way the caller
            // just sees "no results for this keystroke", not an error.
            return [];
        }
        finally
        {
            if (httpClient is null) client.Dispose();
        }
    }
}
