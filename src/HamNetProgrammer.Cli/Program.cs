using System.IO.Ports;
using HamNetProgrammer.Core.Radios.AnyTone;

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
