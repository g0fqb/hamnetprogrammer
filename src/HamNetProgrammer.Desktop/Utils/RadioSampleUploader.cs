using System.Net.Http;
using System.Net.Http.Headers;

namespace HamNetProgrammer.Desktop.Utils;

/// <summary>
/// Uploads a read-only radio memory-map sample to the shared Ham Net Global backend, which
/// records a small metadata row and relays the dump on as an email attachment - see
/// RadioSamplesHandler on the backend. Mirrors DiagnosticsUploader; kept separate rather than
/// generalized since the two have different recipients/purposes on the backend side.
/// </summary>
public static class RadioSampleUploader
{
    private const string Endpoint = "https://api-production-8765.up.railway.app/v1/radio-samples/upload";

    public static async Task UploadAsync(string zipPath, string model, string deviceId, string toolVersion, string? contactEmail, string? notes)
    {
        using var http = new HttpClient();
        using var form = new MultipartFormDataContent
        {
            { new StringContent(model), "model" },
            { new StringContent(deviceId), "device_id" },
            { new StringContent(toolVersion), "tool_version" },
            { new StringContent(notes ?? ""), "notes" },
            { new StringContent(contactEmail ?? ""), "contact_email" },
        };

        var bytes = await File.ReadAllBytesAsync(zipPath);
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        form.Add(fileContent, "report", Path.GetFileName(zipPath));

        using var response = await http.PostAsync(Endpoint, form);
        response.EnsureSuccessStatusCode();
    }
}
