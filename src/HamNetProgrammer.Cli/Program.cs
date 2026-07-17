using System.IO.Ports;
using HamNetProgrammer.Core.Data;
using HamNetProgrammer.Core.Export;
using HamNetProgrammer.Core.Import;
using HamNetProgrammer.Core.Planning;
using HamNetProgrammer.Core.Radios.AnyTone;

if (args.Length > 0 && args[0].Equals("import", StringComparison.OrdinalIgnoreCase))
    return RunImport(args.Skip(1).ToArray());

if (args.Length > 0 && args[0].Equals("query", StringComparison.OrdinalIgnoreCase))
    return RunQuery(args.Skip(1).ToArray());

if (args.Length > 0 && args[0].Equals("build-scanlists", StringComparison.OrdinalIgnoreCase))
    return RunBuildScanLists(args.Skip(1).ToArray());

if (args.Length > 0 && args[0].Equals("build-grouplists", StringComparison.OrdinalIgnoreCase))
    return RunBuildGroupLists(args.Skip(1).ToArray());

if (args.Length > 0 && args[0].Equals("build-roaming", StringComparison.OrdinalIgnoreCase))
    return RunBuildRoaming(args.Skip(1).ToArray());

if (args.Length > 0 && args[0].Equals("export-json", StringComparison.OrdinalIgnoreCase))
    return RunExportJson(args.Skip(1).ToArray());

if (args.Length > 0 && args[0].Equals("preview", StringComparison.OrdinalIgnoreCase))
    return RunPreview(args.Skip(1).ToArray());

if (args.Length > 0 && args[0].Equals("validate-codecs", StringComparison.OrdinalIgnoreCase))
    return RunValidateCodecs(args.Skip(1).ToArray());

var positional = args.Where(a => !a.Equals("dump", StringComparison.OrdinalIgnoreCase)).ToArray();
var runDump = args.Any(a => a.Equals("dump", StringComparison.OrdinalIgnoreCase));

var ports = SerialPort.GetPortNames();
if (ports.Length == 0)
{
    Console.WriteLine("No serial ports found. Plug in the radio's USB programming cable and try again.");
    return 1;
}

Console.WriteLine("Available serial ports:");
foreach (var p in ports) Console.WriteLine($"  {p}");

string portName;
if (positional.Length > 0)
{
    portName = positional[0];
}
else if (ports.Length == 1)
{
    portName = ports[0];
    Console.WriteLine($"Using the only available port: {portName}");
}
else
{
    Console.Write("Enter the COM port to use (e.g. COM5): ");
    portName = Console.ReadLine()?.Trim() ?? string.Empty;
}

if (string.IsNullOrWhiteSpace(portName))
{
    Console.WriteLine("No port selected.");
    return 1;
}

using var radio = new AnyToneD878Transport(portName);

try
{
    Console.WriteLine($"Opening {portName}...");
    radio.Open();

    Console.WriteLine("Starting programming session (radio should show 'PC Mode')...");
    radio.StartProgrammingSession();

    var id = radio.ReadDeviceId();
    Console.WriteLine($"Device identifier: {id}");

    if (runDump)
    {
        RunFullDump(radio);
    }
    else
    {
        Console.WriteLine("Reading first channel record (0x00800000, 16 bytes, read-only)...");
        var sample = radio.ReadMemory(0x00800000);
        Console.WriteLine($"  {Convert.ToHexString(sample)}");
    }

    Console.WriteLine("Ending programming session...");
    radio.EndProgrammingSession();
    Console.WriteLine("Done. Radio has returned to normal operation.");
    return 0;
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
    return 1;
}

static void RunFullDump(AnyToneD878Transport radio)
{
    var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
    var dumpDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "dumps");
    dumpDir = Path.GetFullPath(dumpDir);
    Directory.CreateDirectory(dumpDir);

    var binaryPath = Path.Combine(dumpDir, $"d878uv_{stamp}.bin");
    var manifestPath = Path.Combine(dumpDir, $"d878uv_{stamp}.manifest.csv");

    Console.WriteLine($"Dumping baseline memory regions to {binaryPath}");
    var failures = 0;
    var results = AnyToneD878MemoryDumper.Dump(radio, binaryPath, manifestPath, (region, index, total) =>
    {
        if (index == 1 || index % 25 == 0 || index == total)
            Console.WriteLine($"  [{index}/{total}] {region.Name} (0x{region.Address:x8}, {region.Length} bytes)");
    });

    foreach (var r in results)
    {
        if (!r.Succeeded)
        {
            failures++;
            Console.WriteLine($"  FAILED: {r.Region.Name} - {r.Error}");
        }
    }

    var totalBytes = results.Sum(r => (long)r.Region.Length);
    Console.WriteLine($"Dump complete: {results.Count} regions, {totalBytes:N0} bytes, {failures} failure(s).");
    Console.WriteLine($"Binary: {binaryPath}");
    Console.WriteLine($"Manifest: {manifestPath}");
}

static int RunImport(string[] importArgs)
{
    if (importArgs.Length < 1)
    {
        Console.WriteLine("Usage: HamNetProgrammer.Cli import <csvPath> [dbPath]");
        return 1;
    }

    var csvPath = importArgs[0];
    var dbPath = importArgs.Length > 1
        ? importArgs[1]
        : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data", "codeplug.db"));

    Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

    Console.WriteLine($"Importing {csvPath} into {dbPath}");
    using var db = CodeplugDatabase.OpenOrCreate(dbPath);
    var result = RtSystemsChannelCsvImporter.Import(csvPath, db);

    Console.WriteLine($"Channels imported: {result.ChannelsImported}");
    Console.WriteLine($"Zones created: {result.ZonesCreated}");
    Console.WriteLine($"Contacts (talkgroups) created: {result.ContactsCreated}");
    Console.WriteLine($"Radio IDs created: {result.RadioIdsCreated}");
    Console.WriteLine($"Scan lists created: {result.ScanListsCreated}");
    Console.WriteLine($"Group lists created: {result.GroupListsCreated}");

    if (result.Warnings.Count > 0)
    {
        Console.WriteLine($"Warnings ({result.Warnings.Count}):");
        foreach (var w in result.Warnings) Console.WriteLine($"  {w}");
    }

    return 0;
}

static int RunBuildScanLists(string[] buildArgs)
{
    var dbPath = buildArgs.Length > 0
        ? buildArgs[0]
        : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data", "codeplug.db"));

    using var db = CodeplugDatabase.OpenOrCreate(dbPath);
    var result = ZoneScanListBuilder.BuildFromZones(db);

    Console.WriteLine($"Zones processed: {result.ZonesProcessed}");
    Console.WriteLine($"Channels linked to a zone scan list: {result.ChannelsLinked}");
    if (result.Warnings.Count > 0)
    {
        Console.WriteLine($"Warnings ({result.Warnings.Count}):");
        foreach (var w in result.Warnings) Console.WriteLine($"  {w}");
    }

    return 0;
}

static int RunBuildGroupLists(string[] buildArgs)
{
    var dbPath = buildArgs.Length > 0
        ? buildArgs[0]
        : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data", "codeplug.db"));

    using var db = CodeplugDatabase.OpenOrCreate(dbPath);
    var result = ZoneGroupListBuilder.BuildFromZones(db);

    Console.WriteLine($"Zones processed: {result.ZonesProcessed}");
    Console.WriteLine($"Channels linked to a zone group list: {result.ChannelsLinked}");
    if (result.Warnings.Count > 0)
    {
        Console.WriteLine($"Warnings ({result.Warnings.Count}):");
        foreach (var w in result.Warnings) Console.WriteLine($"  {w}");
    }

    return 0;
}

static int RunBuildRoaming(string[] buildArgs)
{
    var dbPath = buildArgs.Length > 0
        ? buildArgs[0]
        : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data", "codeplug.db"));

    using var db = CodeplugDatabase.OpenOrCreate(dbPath);
    var result = TalkGroupRoamingZoneBuilder.BuildFromZones(db);

    Console.WriteLine($"Roaming zones created: {result.RoamingZonesProcessed}");
    Console.WriteLine($"Channels linked into a roaming zone: {result.ChannelsLinked}");
    if (result.Warnings.Count > 0)
    {
        Console.WriteLine($"Warnings ({result.Warnings.Count}):");
        foreach (var w in result.Warnings) Console.WriteLine($"  {w}");
    }

    return 0;
}

static int RunExportJson(string[] exportArgs)
{
    var dbPath = exportArgs.Length > 0
        ? exportArgs[0]
        : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data", "codeplug.db"));
    var outputPath = exportArgs.Length > 1
        ? exportArgs[1]
        : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data", "codeplug_export.json"));

    using var db = CodeplugDatabase.OpenOrCreate(dbPath);
    CodeplugJsonExporter.ExportToFile(db, outputPath);
    Console.WriteLine($"Exported to {outputPath}");
    return 0;
}

static int RunPreview(string[] previewArgs)
{
    var dbPath = previewArgs.Length > 0
        ? previewArgs[0]
        : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data", "codeplug.db"));
    var outputPath = previewArgs.Length > 1
        ? previewArgs[1]
        : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data", "codeplug_preview.html"));

    using var db = CodeplugDatabase.OpenOrCreate(dbPath);
    CodeplugPreviewBuilder.BuildToFile(db, outputPath);
    Console.WriteLine($"Preview written to {outputPath}");
    return 0;
}

static int RunValidateCodecs(string[] validateArgs)
{
    var binaryPath = validateArgs.Length > 0
        ? validateArgs[0]
        : FindLatestDump();
    var manifestPath = validateArgs.Length > 1
        ? validateArgs[1]
        : Path.ChangeExtension(binaryPath, null) + ".manifest.csv";

    if (binaryPath is null || !File.Exists(binaryPath))
    {
        Console.WriteLine("No dump found. Run 'dump' against the radio first, or pass a .bin path explicitly.");
        return 1;
    }

    Console.WriteLine($"Validating codecs against {binaryPath}");
    var dump = DumpReader.Load(binaryPath, manifestPath);
    var results = CodecValidator.Run(dump);

    var failed = 0;
    foreach (var r in results)
    {
        Console.WriteLine($"  [{(r.Passed ? "PASS" : "FAIL")}] {r.Name}: {r.Detail}");
        if (!r.Passed) failed++;
    }

    Console.WriteLine($"{results.Count - failed}/{results.Count} checks passed.");
    return failed == 0 ? 0 : 1;
}

static string? FindLatestDump()
{
    var dumpDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "dumps"));
    if (!Directory.Exists(dumpDir)) return null;
    return Directory.GetFiles(dumpDir, "*.bin").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
}

static int RunQuery(string[] queryArgs)
{
    if (queryArgs.Length < 1)
    {
        Console.WriteLine("Usage: HamNetProgrammer.Cli query <sql> [dbPath]");
        return 1;
    }

    var sql = queryArgs[0];
    var dbPath = queryArgs.Length > 1
        ? queryArgs[1]
        : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data", "codeplug.db"));

    using var db = CodeplugDatabase.OpenOrCreate(dbPath);
    using var cmd = db.CreateCommand();
    cmd.CommandText = sql;
    using var reader = cmd.ExecuteReader();

    var columnNames = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
    Console.WriteLine(string.Join(" | ", columnNames));
    while (reader.Read())
    {
        var values = Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "" : reader.GetValue(i).ToString());
        Console.WriteLine(string.Join(" | ", values));
    }

    return 0;
}
