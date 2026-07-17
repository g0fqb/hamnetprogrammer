using System.IO.Ports;
using HamNetProgrammer.Core.Radios.AnyTone;

var ports = SerialPort.GetPortNames();
if (ports.Length == 0)
{
    Console.WriteLine("No serial ports found. Plug in the radio's USB programming cable and try again.");
    return 1;
}

Console.WriteLine("Available serial ports:");
foreach (var p in ports) Console.WriteLine($"  {p}");

string portName;
if (args.Length > 0)
{
    portName = args[0];
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

    Console.WriteLine("Reading first channel record (0x00800000, 16 bytes, read-only)...");
    var sample = radio.ReadMemory(0x00800000);
    Console.WriteLine($"  {Convert.ToHexString(sample)}");

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
