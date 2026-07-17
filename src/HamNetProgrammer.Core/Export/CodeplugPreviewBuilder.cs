using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace HamNetProgrammer.Core.Export;

/// <summary>Builds a self-contained, read-only HTML preview of the codeplug database (zones, scan/group lists, roaming matrix).</summary>
public static class CodeplugPreviewBuilder
{
    private const string ResourceName = "HamNetProgrammer.Core.Export.Resources.PreviewTemplate.html";
    private const string Placeholder = "/*__CODEPLUG_JSON__*/";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = null };

    public static void BuildToFile(SqliteConnection db, string outputPath)
    {
        var dto = CodeplugJsonExporter.Export(db);
        var json = JsonSerializer.Serialize(dto, JsonOptions).Replace("</script>", "<\\/script>");

        var template = LoadTemplate();
        if (!template.Contains(Placeholder))
            throw new InvalidOperationException($"Preview template is missing the '{Placeholder}' placeholder.");

        var html = template.Replace(Placeholder, json);
        File.WriteAllText(outputPath, html);
    }

    private static string LoadTemplate()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
