using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace HamNetProgrammer.Core.Online;

public sealed record AppVersionInfo(
    [property: JsonPropertyName("latest_version")] string LatestVersion,
    [property: JsonPropertyName("download_url")] string? DownloadUrl);

/// <summary>
/// Powers a "please update" nag banner, not a forced update - mirrors PacketCluster's own
/// AppShell.xaml.cs pattern exactly. The shared backend's /v1/version endpoint is used by every
/// app in the Ham Net Global stable; ?app=hamnetprogrammer namespaces which env vars it reads
/// (HAMNETPROGRAMMER_LATEST_APP_VERSION/HAMNETPROGRAMMER_APP_DOWNLOAD_URL), defaulting to
/// "0.0.0" (never nags) until those are actually set - safe to call unconditionally.
/// </summary>
public static class AppVersionCheck
{
    private const string VersionUrl = "https://api-production-8765.up.railway.app/v1/version?app=hamnetprogrammer";

    public static async Task<AppVersionInfo?> GetLatestAsync(HttpClient? httpClient = null)
    {
        var client = httpClient ?? new HttpClient();
        try
        {
            return await client.GetFromJsonAsync<AppVersionInfo>(VersionUrl);
        }
        catch
        {
            return null;
        }
    }
}
