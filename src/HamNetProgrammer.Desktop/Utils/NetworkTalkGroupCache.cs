using HamNetProgrammer.Core.Online;

namespace HamNetProgrammer.Desktop.Utils;

/// <summary>Fetches the full Brandmeister/TGIF/FreeDMR talkgroup directory once per app session
/// and reuses it - shared by TalkGroupPicker and ContactsPage so both get live multi-network
/// search/browse without either re-fetching the ~5,700-entry list independently. Read-only: never
/// writes to the local database itself - see TalkGroupPicker's remarks on why browsing all three
/// networks is safe even though only FreeDMR gets synced to the radio by default.</summary>
public static class NetworkTalkGroupCache
{
    private static List<NetworkTalkGroup>? _cache;
    private static Task<List<NetworkTalkGroup>>? _fetchInFlight;

    public static Task<List<NetworkTalkGroup>> GetAsync()
    {
        if (_cache is { } cached) return Task.FromResult(cached);
        _fetchInFlight ??= FetchAndCacheAsync();
        return _fetchInFlight;

        static async Task<List<NetworkTalkGroup>> FetchAndCacheAsync()
        {
            try
            {
                var fetched = await TalkGroupNetworkImporter.FetchAsync(networkFilter: null);
                _cache = fetched;
                return fetched;
            }
            catch
            {
                // Offline, backend down, etc. - local search still works fine without this: fail
                // quietly and let a later call retry rather than surfacing a hard error over
                // what's meant to be an unobtrusive background enhancement.
                _fetchInFlight = null;
                return [];
            }
        }
    }
}
