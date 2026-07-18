using System.IO.Ports;
using HamNetProgrammer.Core.Data;
using HamNetProgrammer.Core.Export;
using HamNetProgrammer.Core.Import;
using HamNetProgrammer.Core.Online;
using HamNetProgrammer.Core.Planning;
using HamNetProgrammer.Core.Radios.AnyTone;
using HamNetProgrammer.Core.Radios.TyT;

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

if (args.Length > 0 && args[0].Equals("encode", StringComparison.OrdinalIgnoreCase))
    return RunEncode(args.Skip(1).ToArray());

if (args.Length > 0 && args[0].Equals("write-codeplug", StringComparison.OrdinalIgnoreCase))
    return RunWriteCodeplug(args.Skip(1).ToArray());

if (args.Length > 0 && args[0].Equals("lookup-dmrid", StringComparison.OrdinalIgnoreCase))
    return await RunLookupDmrId(args.Skip(1).ToArray());

if (args.Length > 0 && args[0].Equals("tyt-identify", StringComparison.OrdinalIgnoreCase))
    return RunTytIdentify();

if (args.Length > 0 && args[0].Equals("tyt-dump", StringComparison.OrdinalIgnoreCase))
    return RunTytDump(args.Skip(1).ToArray());

var peekAddressArg = args.FirstOrDefault(a => a.StartsWith("peek=", StringComparison.OrdinalIgnoreCase));
var pokeArg = args.FirstOrDefault(a => a.StartsWith("poke=", StringComparison.OrdinalIgnoreCase));
var positional = args.Where(a =>
    !a.Equals("dump", StringComparison.OrdinalIgnoreCase) &&
    !a.StartsWith("peek=", StringComparison.OrdinalIgnoreCase) &&
    !a.StartsWith("poke=", StringComparison.OrdinalIgnoreCase)).ToArray();
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

    if (peekAddressArg is not null)
    {
        var addr = Convert.ToUInt32(peekAddressArg["peek=".Length..], 16);
        var data = radio.ReadMemory(addr, 64);
        Console.WriteLine($"  0x{addr:x8}: {Convert.ToHexString(data)}");
    }
    else if (pokeArg is not null)
    {
        var parts = pokeArg["poke=".Length..].Split(':');
        var addr = Convert.ToUInt32(parts[0], 16);
        var bytes = Convert.FromHexString(parts[1]);
        if (bytes.Length % 16 != 0) throw new InvalidOperationException("poke data must be a multiple of 16 bytes.");
        for (var i = 0; i < bytes.Length; i += 16)
            radio.WriteMemory(addr + (uint)i, bytes.AsSpan(i, 16));
        Console.WriteLine($"  Wrote {bytes.Length} bytes to 0x{addr:x8} (commits at END).");
    }
    else if (runDump)
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

static async Task<int> RunLookupDmrId(string[] lookupArgs)
{
    if (lookupArgs.Length < 1)
    {
        Console.WriteLine("Usage: HamNetProgrammer.Cli lookup-dmrid <callsign> [dbPath]");
        return 1;
    }

    var callsign = lookupArgs[0];
    var dbPath = lookupArgs.Length > 1
        ? lookupArgs[1]
        : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data", "codeplug.db"));
    var cachePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data", "radioid_users.csv"));

    if (!File.Exists(cachePath) || (DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath)).TotalDays > 7)
    {
        Console.WriteLine("Downloading radioid.net user database (~17MB, cached for 7 days)...");
        await RadioIdLookup.DownloadToCacheAsync(cachePath);
    }
    else
    {
        Console.WriteLine($"Using cached database ({cachePath}, last updated {File.GetLastWriteTimeUtc(cachePath):yyyy-MM-dd}).");
    }

    var result = RadioIdLookup.FindByCallsign(cachePath, callsign);
    if (result is null)
    {
        Console.WriteLine($"No entry found for callsign '{callsign}'.");
        return 1;
    }

    Console.WriteLine($"Found: {result.Callsign} - DMR ID {result.DmrId} ({result.Name}, {result.Country})");

    using var db = CodeplugDatabase.OpenOrCreate(dbPath);
    using var cmd = db.CreateCommand();
    cmd.CommandText = "UPDATE RadioIds SET DmrId = $dmrId WHERE Callsign = $callsign;";
    cmd.Parameters.AddWithValue("$dmrId", result.DmrId);
    cmd.Parameters.AddWithValue("$callsign", result.Callsign);
    var rowsUpdated = cmd.ExecuteNonQuery();
    Console.WriteLine(rowsUpdated > 0
        ? $"Updated {rowsUpdated} RadioIds row(s) in {dbPath}."
        : $"No matching RadioIds row for '{callsign}' in {dbPath} - nothing updated.");

    return 0;
}

static int RunWriteCodeplug(string[] writeArgs)
{
    if (writeArgs.Length < 1)
    {
        Console.WriteLine("Usage: HamNetProgrammer.Cli write-codeplug <port> [dbPath]");
        return 1;
    }

    var portName = writeArgs[0];
    var dbPath = writeArgs.Length > 1
        ? writeArgs[1]
        : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data", "codeplug.db"));

    using var db = CodeplugDatabase.OpenOrCreate(dbPath);
    var regions = AnyToneD878CodeplugEncoder.Build(db);
    var totalBytes = regions.Sum(r => (long)r.Data.Length);
    Console.WriteLine($"Encoded {regions.Count} regions, {totalBytes:N0} bytes to write.");

    using var radio = new AnyToneD878Transport(portName);
    try
    {
        Console.WriteLine($"Opening {portName}...");
        radio.Open();

        Console.WriteLine("Starting programming session (radio should show 'PC Mode')...");
        radio.StartProgrammingSession();
        Console.WriteLine($"Device identifier: {radio.ReadDeviceId()}");

        var started = DateTime.Now;
        AnyToneD878CodeplugWriter.Write(radio, regions, (region, index, total, written, totalW) =>
        {
            var elapsed = DateTime.Now - started;
            Console.WriteLine($"  [{index}/{total}] {region.Name} done - {written:N0}/{totalW:N0} bytes, {elapsed.TotalSeconds:F0}s elapsed");
        });

        Console.WriteLine("Ending programming session (this commits the write - device will drop off USB and re-enumerate in ~10-15s)...");
        radio.EndProgrammingSession();
        Console.WriteLine("Done.");
        return 0;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error: {ex.Message}");
        return 1;
    }
}

// Smoke test for TytDfuTransport against real hardware, before any of this is wired into the
// desktop UI. Requires the radio to already be in DFU/bootloader mode (button-and-power-on
// combo, model-specific) and, on Windows, bound to a WinUSB driver rather than its default one.
static int RunTytIdentify()
{
    if (!TytDfuTransport.AnyDeviceConnected())
    {
        Console.WriteLine($"No device found presenting the DFU interface (VID {TytDfuTransport.VendorId:x4}:PID {TytDfuTransport.ProductId:x4}).");
        Console.WriteLine("Put the radio into DFU/bootloader mode (button-and-power-on combo) and check Windows has it bound to a WinUSB driver (see Zadig), then try again.");
        return 1;
    }

    using var radio = new TytDfuTransport();
    try
    {
        Console.WriteLine("DFU device found - opening...");
        radio.Open();
        Console.WriteLine("Entering programming mode...");
        radio.EnterProgramMode();
        var id = radio.Identify();
        Console.WriteLine($"Device identifier: '{id}'");
        Console.WriteLine("Rebooting radio back to normal mode...");
        radio.Reboot();
        Console.WriteLine("Done.");
        return 0;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error: {ex.Message}");
        return 1;
    }
}

// Reads the MD-380/MD-390's single known codeplug segment (per qdmr's md390_codeplug.hh -
// there's no separate MD-380 class in qdmr at all, meaning the DR780/MD-380 and MD-390 are
// documented as sharing the identical layout) to a raw .bin file, for offline inspection against
// qdmr's documented byte layout - read-only, no write/erase involved. A DM-1701 or other
// TyT-family model would need different addresses (see dm1701_codeplug.hh's two-segment layout).
static int RunTytDump(string[] dumpArgs)
{
    var outputPath = dumpArgs.Length > 0
        ? dumpArgs[0]
        : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "dumps", $"tyt_{DateTime.Now:yyyyMMdd_HHmmss}.bin");
    outputPath = Path.GetFullPath(outputPath);
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

    (uint Start, uint End)[] segments = [(0x002000, 0x040000)];
    const int chunkSize = 1024;

    using var radio = new TytDfuTransport();
    try
    {
        Console.WriteLine("Opening DFU device...");
        radio.Open();
        radio.EnterProgramMode();
        var id = radio.Identify();
        Console.WriteLine($"Device identifier: '{id}'");

        using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        var buffer = new byte[chunkSize];
        foreach (var (start, end) in segments)
        {
            Console.WriteLine($"Reading 0x{start:x6}-0x{end:x6}...");
            for (var address = start; address < end; address += chunkSize)
            {
                radio.Read(address, buffer, chunkSize);
                output.Write(buffer, 0, chunkSize);
                if ((address - start) % (chunkSize * 64) == 0)
                    Console.WriteLine($"  0x{address:x6} ({(address - start) * 100 / (end - start)}%)");
            }
        }

        Console.WriteLine("Rebooting radio back to normal mode...");
        radio.Reboot();
        Console.WriteLine($"Done. Saved to {outputPath}");
        return 0;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error: {ex.Message}");
        return 1;
    }
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

static int RunEncode(string[] encodeArgs)
{
    var dbPath = encodeArgs.Length > 0
        ? encodeArgs[0]
        : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data", "codeplug.db"));
    var outputDir = encodeArgs.Length > 1
        ? encodeArgs[1]
        : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data", "encoded"));

    Directory.CreateDirectory(outputDir);

    using var db = CodeplugDatabase.OpenOrCreate(dbPath);
    var regions = AnyToneD878CodeplugEncoder.Build(db);

    var binaryPath = Path.Combine(outputDir, "codeplug_image.bin");
    var manifestPath = Path.Combine(outputDir, "codeplug_image.manifest.csv");

    using (var output = new FileStream(binaryPath, FileMode.Create, FileAccess.Write))
    using (var manifest = new StreamWriter(Path.Combine(outputDir, "codeplug_image.manifest.csv")))
    {
        manifest.WriteLine("Name,Address,Length,FileOffset");
        foreach (var region in regions.OrderBy(r => r.Address))
        {
            manifest.WriteLine($"{region.Name},0x{region.Address:x8},{region.Data.Length},{output.Position}");
            output.Write(region.Data, 0, region.Data.Length);
        }
    }

    var totalBytes = regions.Sum(r => (long)r.Data.Length);
    Console.WriteLine($"Encoded {regions.Count} regions, {totalBytes:N0} bytes.");
    Console.WriteLine($"Binary: {binaryPath}");
    Console.WriteLine($"Manifest: {manifestPath}");
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
