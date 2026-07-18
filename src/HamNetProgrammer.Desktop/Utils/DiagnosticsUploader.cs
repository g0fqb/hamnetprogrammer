using System.Net.Http;
using System.Net.Http.Headers;

namespace HamNetProgrammer.Desktop.Utils;

/// <summary>
/// Uploads a diagnostic report zip to the shared Ham Net Global backend (the same Railway-hosted
/// Go service PacketCluster's desktop app talks to), which emails it on to admin@hamsoft.co.uk.
/// Reusing that backend rather than standing up a dedicated one for HamNetProgrammer - it already
/// has a proven email-sending path, and the intent is to share it across future tools too, not
/// just PacketCluster.
/// </summary>
public static class DiagnosticsUploader
{
    private const string Endpoint = "https://api-production-8765.up.railway.app/v1/diagnostics/report";

    public static async Task UploadAsync(string zipPath, string? deviceId, string toolVersion, string? message)
    {
        using var http = new HttpClient();
        using var form = new MultipartFormDataContent
        {
            { new StringContent("HamNetProgrammer"), "app" },
            { new StringContent(deviceId ?? ""), "device_id" },
            { new StringContent(toolVersion ?? ""), "tool_version" },
            { new StringContent(message ?? ""), "message" },
        };

        var bytes = await File.ReadAllBytesAsync(zipPath);
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        form.Add(fileContent, "report", Path.GetFileName(zipPath));

        using var response = await http.PostAsync(Endpoint, form);
        response.EnsureSuccessStatusCode();
    }
}
