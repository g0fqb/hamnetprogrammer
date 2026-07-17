using System.Net.Http;

namespace HamNetProgrammer.Core.Online;

public sealed record RadioIdLookupResult(uint DmrId, string Callsign, string Name, string Country);

/// <summary>
/// Looks up a callsign's real DMR ID against radioid.net's public database
/// (https://www.radioid.net/static/user.csv - no auth required, ~17MB, RADIO_ID/CALLSIGN/...
/// columns). This is the fix for the CSV importer never actually capturing a numeric DMR ID for
/// RadioIds - RT Systems' "Radio ID" column is the callsign text, not a number.
/// </summary>
public static class RadioIdLookup
{
    private const string SourceUrl = "https://www.radioid.net/static/user.csv";

    public static async Task<string> DownloadToCacheAsync(string cachePath, HttpClient? httpClient = null)
    {
        var client = httpClient ?? new HttpClient();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            await using var stream = await client.GetStreamAsync(SourceUrl);
            await using var file = new FileStream(cachePath, FileMode.Create, FileAccess.Write);
            await stream.CopyToAsync(file);
            return cachePath;
        }
        finally
        {
            if (httpClient is null) client.Dispose();
        }
    }

    /// <summary>Looks up a single callsign. Streams the file rather than loading it all into memory (it's ~17MB/~300k rows).</summary>
    public static RadioIdLookupResult? FindByCallsign(string csvPath, string callsign)
    {
        using var reader = new StreamReader(csvPath);
        reader.ReadLine(); // header: RADIO_ID,CALLSIGN,FIRST_NAME,LAST_NAME,CITY,STATE,COUNTRY

        var target = callsign.Trim();
        while (reader.ReadLine() is { } line)
        {
            var fields = line.Split(',');
            if (fields.Length < 7) continue;
            if (!fields[1].Trim().Equals(target, StringComparison.OrdinalIgnoreCase)) continue;

            if (!uint.TryParse(fields[0].Trim(), out var dmrId)) continue;
            var name = $"{fields[2].Trim()} {fields[3].Trim()}".Trim();
            return new RadioIdLookupResult(dmrId, fields[1].Trim(), name, fields[6].Trim());
        }
        return null;
    }
}
