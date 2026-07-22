using System.IO.Ports;
using System.Threading;
using HamNetProgrammer.Core.Data;
using HamNetProgrammer.Core.Diagnostics;
using HamNetProgrammer.Core.Export;
using HamNetProgrammer.Core.Import;
using HamNetProgrammer.Core.Online;
using HamNetProgrammer.Core.Planning;
using HamNetProgrammer.Core.Radios.AnyTone;
using HamNetProgrammer.Core.Radios.AnyTone.CallsignDb;
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

if (args.Length > 0 && args[0].Equals("read-codeplug", StringComparison.OrdinalIgnoreCase))
    return RunReadCodeplug(args.Skip(1).ToArray());

if (args.Length > 0 && args[0].Equals("lookup-dmrid", StringComparison.OrdinalIgnoreCase))
    return await RunLookupDmrId(args.Skip(1).ToArray());

if (args.Length > 0 && args[0].Equals("callsigndb-test", StringComparison.OrdinalIgnoreCase))
    return await RunCallsignDbTest(args.Skip(1).ToArray());

if (args.Length > 0 && args[0].Equals("fix-poweron-block", StringComparison.OrdinalIgnoreCase))
    return RunFixPowerOnBlock(args.Skip(1).ToArray());

if (args.Length > 0 && args[0].Equals("test-restore", StringComparison.OrdinalIgnoreCase))
    return RunTestRestore(args.Skip(1).ToArray());

if (args.Length > 0 && args[0].Equals("restore-block", StringComparison.OrdinalIgnoreCase))
    return RunRestoreBlock(args.Skip(1).ToArray());

if (args.Length > 0 && args[0].Equals("read-region", StringComparison.OrdinalIgnoreCase))
    return RunReadRegion(args.Skip(1).ToArray());

if (args.Length > 0 && args[0].Equals("stress-block", StringComparison.OrdinalIgnoreCase))
    return RunStressBlock(args.Skip(1).ToArray());

if (args.Length > 0 && args[0].Equals("sync-talkgroups", StringComparison.OrdinalIgnoreCase))
    return RunSyncTalkGroups(args.Skip(1).ToArray());

if (args.Length > 0 && args[0].Equals("write-wide-sweep", StringComparison.OrdinalIgnoreCase))
    return RunWriteWideSweep(args.Skip(1).ToArray());

if (args.Length > 0 && args[0].Equals("merge-duplicate-talkgroups", StringComparison.OrdinalIgnoreCase))
    return RunMergeDuplicateTalkGroups(args.Skip(1).ToArray());

if (args.Length > 0 && args[0].Equals("health-check", StringComparison.OrdinalIgnoreCase))
    return RunHealthCheck(args.Skip(1).ToArray());

if (args.Length > 0 && args[0].Equals("audit-talkgroups", StringComparison.OrdinalIgnoreCase))
    return await RunAuditTalkGroups(args.Skip(1).ToArray());

if (args.Length > 0 && args[0].Equals("compare-dumps", StringComparison.OrdinalIgnoreCase))
    return RunCompareDumps(args.Skip(1).ToArray());

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
    var results = AnyToneD878MemoryDumper.Dump(radio, binaryPath, manifestPath, (region, index, total, bytesDone, totalBytes) =>
    {
        if (index == 1 || index % 25 == 0 || index == total)
            Console.WriteLine($"  [{index}/{total}] {region.Name} (0x{region.Address:x8}, {region.Length} bytes) - {bytesDone:N0}/{totalBytes:N0} bytes");
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

// Builds the bulk Callsign Database write plan against the real cached radioid.net data (no
// hardware touched) and round-trip-decodes one known entry from the produced bytes to confirm the
// encoder is actually correct before it's ever tried on real, virgin flash territory - the same
// "decode real data, cross-check against a known value" discipline used to validate every other
// codec in this project, just done here instead of via validate-codecs since this format has no
// existing device dump to decode from (it was confirmed empty on real hardware).
static async Task<int> RunCallsignDbTest(string[] testArgs)
{
    var checkCallsign = testArgs.Length > 0 ? testArgs[0] : "G0FQB";
    var cachePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data", "radioid_users.csv"));

    if (!File.Exists(cachePath) || (DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath)).TotalDays > 7)
    {
        Console.WriteLine("Downloading radioid.net user database (~17MB, cached for 7 days)...");
        await RadioIdLookup.DownloadToCacheAsync(cachePath);
    }

    Console.WriteLine("Reading full radioid.net database...");
    var users = RadioIdLookup.ReadAll(cachePath);
    Console.WriteLine($"  {users.Count:N0} unique individuals, sorted ascending by DMR ID.");

    var callsignDbUsers = users
        .Select(u => new CallsignDbUser(u.DmrId, u.Callsign, u.Name, u.City, u.State, u.Country))
        .ToList();

    var warnings = new List<string>();
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var regions = CallsignDbEncoder.Build(callsignDbUsers, warnings);
    sw.Stop();

    var totalBytes = regions.Sum(r => (long)r.Data.Length);
    Console.WriteLine($"Encoded {regions.Count} regions, {totalBytes:N0} bytes, in {sw.Elapsed.TotalSeconds:F1}s.");
    foreach (var w in warnings) Console.WriteLine($"  Warning: {w}");

    var target = users.FirstOrDefault(u => u.Callsign.Equals(checkCallsign, StringComparison.OrdinalIgnoreCase));
    if (target is null)
    {
        Console.WriteLine($"Could not find '{checkCallsign}' in the source data to verify against.");
        return 1;
    }

    // Reconstruct the virtual (gapless) entries stream by concatenating every entry bank's real
    // bytes in order, then decode straight from the byte offset the algorithm assigned this user -
    // exactly what a real device does when it follows an index entry's offset field.
    var entryBankRegions = regions.Where(r => r.Name.StartsWith("CallsignDbEntries[")).ToList();
    var virtualStream = new byte[entryBankRegions.Sum(r => r.Data.Length)];
    var pos = 0;
    foreach (var r in entryBankRegions) { r.Data.CopyTo(virtualStream, pos); pos += r.Data.Length; }

    var sortedIndex = callsignDbUsers.FindIndex(u => u.DmrId == target.DmrId);
    long byteOffset = 0;
    for (var i = 0; i < sortedIndex; i++) byteOffset += CallsignDbEntryCodec.Size(callsignDbUsers[i]);

    var callType = virtualStream[byteOffset];
    var decodedId = BcdCodec.Decode(virtualStream.AsSpan((int)byteOffset + 1, 4));
    var nameOffset = (int)byteOffset + CallsignDbEntryCodec.HeaderLength;
    string ReadCString(ref int offset)
    {
        var start = offset;
        while (virtualStream[offset] != 0) offset++;
        var text = System.Text.Encoding.ASCII.GetString(virtualStream, start, offset - start);
        offset++; // skip terminator
        return text;
    }
    var decodedName = ReadCString(ref nameOffset);
    var decodedCity = ReadCString(ref nameOffset);
    var decodedCall = ReadCString(ref nameOffset);
    var decodedState = ReadCString(ref nameOffset);
    var decodedCountry = ReadCString(ref nameOffset);

    Console.WriteLine($"Decoded entry for '{checkCallsign}' at virtual offset {byteOffset}:");
    Console.WriteLine($"  CallType={callType} (expect 0=Private), DmrId={decodedId} (expect {target.DmrId})");
    Console.WriteLine($"  Name='{decodedName}' City='{decodedCity}' Call='{decodedCall}' State='{decodedState}' Country='{decodedCountry}'");
    Console.WriteLine($"  Source: Name='{target.Name}' City='{target.City}' Call='{target.Callsign}' State='{target.State}' Country='{target.Country}'");

    // Field lengths are truncated by design (see CallsignDbEntryCodec's max lengths) - compare
    // against the same truncation, not the raw source, so a long name doesn't read as a false
    // mismatch.
    static string Truncate(string value, int max) => value.Length > max ? value[..max] : value;
    var ok = callType == 0
        && decodedId == target.DmrId
        && decodedCall == Truncate(target.Callsign, 8)
        && decodedName == Truncate(target.Name, 16)
        && decodedCity == Truncate(target.City, 15)
        && decodedState == Truncate(target.State, 16)
        && decodedCountry == Truncate(target.Country, 16);
    Console.WriteLine(ok ? "MATCH - round trip confirmed correct." : "MISMATCH - something is wrong, do not write this to a radio.");
    return ok ? 0 : 1;
}

// Emergency, isolated recovery for the shared 0x02500000 erase block - the same block behind the
// original 2026-07-17 "stuck on Chinese password screen" incident. Splices back every named
// sub-region this project knows about within it (not just the two the built-in Restore path
// covers), in ONE read-modify-write, nothing else touched in the session - the same proven-safe
// pattern, just complete this time.
static int RunFixPowerOnBlock(string[] fixArgs)
{
    if (fixArgs.Length < 2)
    {
        Console.WriteLine("Usage: HamNetProgrammer.Cli fix-poweron-block <port> <diagnosticsSessionFolder>");
        return 1;
    }

    var portName = fixArgs[0];
    var sessionFolder = fixArgs[1];
    var baselinePath = Path.Combine(sessionFolder, "baseline_before.bin");
    var manifestPath = Path.Combine(sessionFolder, "baseline_before.manifest.csv");
    if (!File.Exists(baselinePath) || !File.Exists(manifestPath))
    {
        Console.WriteLine($"Could not find baseline_before.bin/.manifest.csv in {sessionFolder}");
        return 1;
    }

    var baseline = DumpReader.Load(baselinePath, manifestPath);

    // Every named region AnyToneD878MemoryMap knows about within the shared 0x02500000-
    // 0x02501900 block, with its offset relative to the block start.
    var subRegions = new (string Name, int Offset)[]
    {
        ("PowerOnAndOptionalSettings", 0x0000),
        ("ZoneAChannel", AnyToneD878CodeplugEncoder.ZoneAChannelOffset),
        ("ZoneBChannel", AnyToneD878CodeplugEncoder.ZoneBChannelOffset),
        ("PowerOnSettings", 0x0600),
        ("AprsGeneralSettings", 0x1000),
        ("AprsSendingText", 0x1200),
        ("GpsTemplateText", 0x1280),
        ("MoreOptionalSettings", 0x1400),
        ("AnalogAprsList", 0x1800),
    };

    var missing = subRegions.Where(r => !baseline.HasRegion(r.Name)).Select(r => r.Name).ToList();
    if (missing.Count > 0)
    {
        Console.WriteLine($"Baseline is missing: {string.Join(", ", missing)} - cannot safely proceed.");
        return 1;
    }

    const uint address = AnyToneD878CodeplugEncoder.ZoneChannelDefaultsBlockAddress;
    const int length = AnyToneD878CodeplugEncoder.ZoneChannelDefaultsBlockLength;

    using var radio = new AnyToneD878Transport(portName);
    try
    {
        Console.WriteLine($"Opening {portName}...");
        radio.Open();
        radio.StartProgrammingSession();
        var deviceId = radio.ReadDeviceId();
        Console.WriteLine($"Device identifier: {deviceId}");

        Console.WriteLine($"Reading live block at 0x{address:x8} ({length} bytes)...");
        var buffer = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var chunkLength = (byte)Math.Min(0xFF, length - offset);
            radio.ReadMemory(address + (uint)offset, chunkLength).CopyTo(buffer, offset);
            offset += chunkLength;
        }
        Console.WriteLine($"  Live PowerOnAndOptionalSettings[0..8] before splice: {Convert.ToHexString(buffer.AsSpan(0, 8))}");

        foreach (var (name, regionOffset) in subRegions)
        {
            baseline.GetRegion(name).CopyTo(buffer.AsSpan(regionOffset));
            Console.WriteLine($"  Spliced {name} at offset 0x{regionOffset:x4} ({baseline.GetRegion(name).Length} bytes).");
        }

        Console.WriteLine("Writing spliced block back (this is the only write in this session)...");
        for (var writeOffset = 0; writeOffset < length; writeOffset += 16)
            radio.WriteMemory(address + (uint)writeOffset, buffer.AsSpan(writeOffset, 16));

        Console.WriteLine("Ending programming session (commits - radio will drop off USB and re-enumerate)...");
        radio.EndProgrammingSession();
        radio.Close();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error: {ex.Message}");
        return 1;
    }

    Console.WriteLine("Waiting for the radio to re-enumerate...");
    var deadline = DateTime.Now + TimeSpan.FromSeconds(45);
    while (DateTime.Now < deadline && SerialPort.GetPortNames().Contains(portName))
        Thread.Sleep(500);
    while (DateTime.Now < deadline && !SerialPort.GetPortNames().Contains(portName))
        Thread.Sleep(500);
    if (!SerialPort.GetPortNames().Contains(portName))
    {
        Console.WriteLine("WARNING: port did not reappear within 45s.");
        return 1;
    }
    Thread.Sleep(1500); // let the port fully settle before reopening, same margin used elsewhere

    Console.WriteLine("Verifying with a fresh read-only session...");
    using var verifyRadio = new AnyToneD878Transport(portName);
    try
    {
        verifyRadio.Open();
        verifyRadio.StartProgrammingSession();
        var deviceId = verifyRadio.ReadDeviceId();
        var readBack = new byte[length];
        var off = 0;
        while (off < length)
        {
            var chunkLength = (byte)Math.Min(0xFF, length - off);
            verifyRadio.ReadMemory(address + (uint)off, chunkLength).CopyTo(readBack, off);
            off += chunkLength;
        }
        verifyRadio.EndProgrammingSession();
        verifyRadio.Close();

        Console.WriteLine($"Device identifier: {deviceId}");
        var allMatch = true;
        foreach (var (name, regionOffset) in subRegions)
        {
            var expected = baseline.GetRegion(name).ToArray();
            var actual = readBack.AsSpan(regionOffset, expected.Length);
            var matches = actual.SequenceEqual(expected);
            allMatch &= matches;
            Console.WriteLine($"  {name}: {(matches ? "MATCH" : "MISMATCH")}");
        }
        Console.WriteLine($"Password byte (PowerOnAndOptionalSettings offset 7): 0x{readBack[7]:x2}");
        Console.WriteLine(allMatch
            ? "ALL REGIONS MATCH the known-good baseline."
            : "At least one region did NOT match - do not assume this is fully fixed.");
        return allMatch ? 0 : 1;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Verification read failed: {ex.Message}");
        return 1;
    }
}

// Exercises AnyToneD878CodeplugRestorer's actual shared-block splice logic (the same code
// RadioPage's "Restore Previous Codeplug" button uses) against an existing baseline, WITHOUT
// requiring a fresh Write Codeplug first - restorer normally only ever runs after a same-session
// write auto-captures its own baseline, so this is the first way to test the 9-region-fix
// (2026-07-19) independently, on demand, against whatever's currently on the radio right now.
// Order matches every other write path in this project: RoamingBlock, GeneralUsedBitmapsBlock,
// then ZoneChannelDefaults strictly LAST (see AnyToneD878CodeplugEncoder's erase-block-disturb
// remarks - writing GeneralUsedBitmapsBlock after ZoneChannelDefaults is what corrupts it).
static int RunTestRestore(string[] restoreArgs)
{
    if (restoreArgs.Length < 2)
    {
        Console.WriteLine("Usage: HamNetProgrammer.Cli test-restore <port> <baselineSessionFolder>");
        return 1;
    }

    var portName = restoreArgs[0];
    var sessionFolder = restoreArgs[1];
    var baselinePath = Path.Combine(sessionFolder, "baseline_before.bin");
    var manifestPath = Path.Combine(sessionFolder, "baseline_before.manifest.csv");
    if (!File.Exists(baselinePath) || !File.Exists(manifestPath))
    {
        Console.WriteLine($"Could not find baseline_before.bin/.manifest.csv in {sessionFolder}");
        return 1;
    }

    var baseline = DumpReader.Load(baselinePath, manifestPath);

    var results = new List<SharedBlockWriteResult>();
    void RunOne(string label, Func<AnyToneD878Transport, EncodedRegion> build)
    {
        Console.WriteLine($"--- {label} ---");
        var result = AnyToneD878CodeplugWriter.WriteAndVerifySharedBlock(portName, build,
            msg => Console.WriteLine($"  {msg}"));
        results.Add(result);
    }

    RunOne("RoamingBlock", radio => AnyToneD878CodeplugRestorer.BuildRestoredRoamingBlock(radio, baseline));
    RunOne("GeneralUsedBitmapsBlock", radio => AnyToneD878CodeplugRestorer.BuildRestoredGeneralUsedBitmapsBlock(radio, baseline));
    RunOne("ZoneChannelDefaults", radio => AnyToneD878CodeplugRestorer.BuildRestoredZoneChannelDefaults(radio, baseline));

    var failed = results.Where(r => !r.Verified).ToList();
    if (failed.Count > 0)
    {
        Console.WriteLine("FAILED - one or more shared blocks did not verify correctly after retries:");
        foreach (var f in failed) Console.WriteLine($"  {f.RegionName}: {f.Error}");
        Console.WriteLine("Do not assume the radio is in a good state. Take a fresh backup and check before writing again.");
        return 1;
    }

    Console.WriteLine("All three shared blocks reported verified during write. Running an independent fresh-session confirmation read...");

    var address = AnyToneD878CodeplugEncoder.ZoneChannelDefaultsBlockAddress;
    var length = AnyToneD878CodeplugEncoder.ZoneChannelDefaultsBlockLength;
    var subRegions = new (string Name, int Offset)[]
    {
        ("PowerOnAndOptionalSettings", 0x0000),
        ("ZoneAChannel", AnyToneD878CodeplugEncoder.ZoneAChannelOffset),
        ("ZoneBChannel", AnyToneD878CodeplugEncoder.ZoneBChannelOffset),
        ("PowerOnSettings", 0x0600),
        ("AprsGeneralSettings", 0x1000),
        ("AprsSendingText", 0x1200),
        ("GpsTemplateText", 0x1280),
        ("MoreOptionalSettings", 0x1400),
        ("AnalogAprsList", 0x1800),
    };

    Thread.Sleep(1500);
    using var verifyRadio = new AnyToneD878Transport(portName);
    try
    {
        verifyRadio.Open();
        verifyRadio.StartProgrammingSession();
        var deviceId = verifyRadio.ReadDeviceId();
        var readBack = new byte[length];
        var off = 0;
        while (off < length)
        {
            var chunkLength = (byte)Math.Min(0xFF, length - off);
            verifyRadio.ReadMemory(address + (uint)off, chunkLength).CopyTo(readBack, off);
            off += chunkLength;
        }
        verifyRadio.EndProgrammingSession();
        verifyRadio.Close();

        Console.WriteLine($"Device identifier: {deviceId}");
        var allMatch = true;
        foreach (var (name, regionOffset) in subRegions)
        {
            if (!baseline.HasRegion(name))
            {
                Console.WriteLine($"  {name}: no baseline data, skipped");
                continue;
            }
            var expected = baseline.GetRegion(name).ToArray();
            var actual = readBack.AsSpan(regionOffset, expected.Length);
            var matches = actual.SequenceEqual(expected);
            allMatch &= matches;
            Console.WriteLine($"  {name}: {(matches ? "MATCH" : "MISMATCH")}");
        }
        Console.WriteLine($"Password byte (PowerOnAndOptionalSettings offset 7): 0x{readBack[7]:x2}");
        Console.WriteLine(allMatch
            ? "ALL REGIONS MATCH the baseline - Restorer's 9-region fix confirmed working on real hardware."
            : "At least one region did NOT match - the Restore path still has a real gap.");
        return allMatch ? 0 : 1;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Verification read failed: {ex.Message}");
        return 1;
    }
}

// Restores exactly ONE shared erase block from a baseline, in its own isolated session, leaving
// every other block (including the boot-critical ZoneChannelDefaults) untouched. Built 2026-07-21
// to restore GeneralUsedBitmapsBlock (0x024C0000) after a sparse-write bug erased it, without
// re-writing the already-good boot block that test-restore would also touch. Doubles as an MMDVM
// experiment: restoring the official-CPS-captured content of this block onto a radio that otherwise
// has HamNetProgrammer's channels tests whether this block's content (channel bitmap etc.) is the
// missing piece for MMDVM reception.
static int RunRestoreBlock(string[] args)
{
    if (args.Length < 3)
    {
        Console.WriteLine("Usage: HamNetProgrammer.Cli restore-block <port> <baselineSessionFolder> <roaming|generalbitmaps|zonedefaults>");
        return 1;
    }

    var portName = args[0];
    var sessionFolder = args[1];
    var blockName = args[2].ToLowerInvariant();

    var baselinePath = Path.Combine(sessionFolder, "baseline_before.bin");
    var manifestPath = Path.Combine(sessionFolder, "baseline_before.manifest.csv");
    if (!File.Exists(baselinePath) || !File.Exists(manifestPath))
    {
        Console.WriteLine($"Could not find baseline_before.bin/.manifest.csv in {sessionFolder}");
        return 1;
    }

    var baseline = DumpReader.Load(baselinePath, manifestPath);

    Func<AnyToneD878Transport, EncodedRegion> build = blockName switch
    {
        "roaming" => radio => AnyToneD878CodeplugRestorer.BuildRestoredRoamingBlock(radio, baseline),
        "generalbitmaps" => radio => AnyToneD878CodeplugRestorer.BuildRestoredGeneralUsedBitmapsBlock(radio, baseline),
        "zonedefaults" => radio => AnyToneD878CodeplugRestorer.BuildRestoredZoneChannelDefaults(radio, baseline),
        _ => null!,
    };
    if (build is null)
    {
        Console.WriteLine($"Unknown block '{blockName}' - expected roaming, generalbitmaps, or zonedefaults.");
        return 1;
    }

    Console.WriteLine($"Restoring ONLY the {blockName} block from baseline (all other blocks left untouched)...");
    var result = AnyToneD878CodeplugWriter.WriteAndVerifySharedBlock(portName, build,
        msg => Console.WriteLine($"  {msg}"));

    if (!result.Verified)
    {
        Console.WriteLine($"FAILED: {result.Error}");
        Console.WriteLine("Do not assume the radio is in a good state. Take a fresh backup and check before writing again.");
        return 1;
    }

    Console.WriteLine($"{result.RegionName} restored and verified (attempt {result.Attempts}).");
    return 0;
}

// Generic multi-chunk read in ONE session (unlike peek=, which is capped at 64 bytes) - for
// inspecting a wider region without paying a separate reboot cost per 64-byte chunk.
static int RunReadRegion(string[] readArgs)
{
    if (readArgs.Length < 3)
    {
        Console.WriteLine("Usage: HamNetProgrammer.Cli read-region <port> <hexAddress> <lengthBytes>");
        return 1;
    }
    var portName = readArgs[0];
    var address = Convert.ToUInt32(readArgs[1], 16);
    var length = int.Parse(readArgs[2]);

    using var radio = new AnyToneD878Transport(portName);
    radio.Open();
    radio.StartProgrammingSession();
    var deviceId = radio.ReadDeviceId();
    Console.WriteLine($"Device identifier: {deviceId}");

    var buffer = new byte[length];
    var offset = 0;
    while (offset < length)
    {
        var chunkLength = (byte)Math.Min(0xFF, length - offset);
        radio.ReadMemory(address + (uint)offset, chunkLength).CopyTo(buffer, offset);
        offset += chunkLength;
    }
    radio.EndProgrammingSession();
    radio.Close();

    for (var i = 0; i < buffer.Length; i += 32)
    {
        var chunk = buffer.AsSpan(i, Math.Min(32, buffer.Length - i));
        var ascii = string.Concat(chunk.ToArray().Select(b => b is >= 32 and < 127 ? (char)b : '.'));
        Console.WriteLine($"0x{address + i:x8}: {Convert.ToHexString(chunk),-64} {ascii}");
    }
    return 0;
}

// Diagnostic for the 2026-07-19 "does writing block B corrupt already-good block A" investigation
// (see AnyToneD878CodeplugWriter's remarks). Reads a block's live content ONCE, then writes that
// exact same content back to the SAME address N times, each in its own isolated Start/End session
// - a real physical erase+program cycle every time (the flash controller can't selectively skip
// the erase just because the new bytes happen to match the old ones), but semantically a no-op so
// it never touches anything this radio actually depends on. Deliberately never reads or writes
// anywhere near a DIFFERENT block - the point is to see whether repeatedly cycling ONE block's
// physical flash disturbs an unrelated block elsewhere on the same die, checked separately with
// read-region before and after.
static int RunStressBlock(string[] stressArgs)
{
    if (stressArgs.Length < 4)
    {
        Console.WriteLine("Usage: HamNetProgrammer.Cli stress-block <port> <hexAddress> <lengthBytes> <cycles>");
        return 1;
    }
    var portName = stressArgs[0];
    var address = Convert.ToUInt32(stressArgs[1], 16);
    var length = int.Parse(stressArgs[2]);
    var cycles = int.Parse(stressArgs[3]);

    byte[] content;
    using (var radio = new AnyToneD878Transport(portName))
    {
        Console.WriteLine($"Opening {portName} to read the block once (this content gets written back unchanged every cycle)...");
        radio.Open();
        radio.StartProgrammingSession();
        Console.WriteLine($"Device identifier: {radio.ReadDeviceId()}");
        content = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var chunkLength = (byte)Math.Min(0xFF, length - offset);
            radio.ReadMemory(address + (uint)offset, chunkLength).CopyTo(content, offset);
            offset += chunkLength;
        }
        radio.EndProgrammingSession();
    }
    Console.WriteLine($"Read {length:N0} bytes at 0x{address:x8}. Now cycling {cycles} isolated write sessions...");

    for (var cycle = 1; cycle <= cycles; cycle++)
    {
        var deadline = DateTime.Now + TimeSpan.FromSeconds(45);
        while (DateTime.Now < deadline && SerialPort.GetPortNames().Contains(portName)) Thread.Sleep(500);
        while (DateTime.Now < deadline && !SerialPort.GetPortNames().Contains(portName)) Thread.Sleep(500);
        if (!SerialPort.GetPortNames().Contains(portName))
        {
            Console.WriteLine($"WARNING: port did not re-enumerate within 45s before cycle {cycle} - stopping.");
            return 1;
        }
        Thread.Sleep(1500);

        using var radio = new AnyToneD878Transport(portName);
        radio.Open();
        radio.StartProgrammingSession();
        radio.ReadDeviceId();
        for (var offset = 0; offset < length; offset += 16)
            radio.WriteMemory(address + (uint)offset, content.AsSpan(offset, 16));
        radio.EndProgrammingSession();
        radio.Close();
        Console.WriteLine($"  Cycle {cycle}/{cycles} written and committed.");
    }

    Console.WriteLine("Done. Check the OTHER block(s) now with read-region.");
    return 0;
}

// Writes ONLY the talkgroup list (not the Callsign Database - that's much larger/slower and
// unrelated to this specific bug class) and updates the contact-sync fingerprint on success.
// Exists because "write-codeplug" deliberately never touches the talkgroup list (see
// AnyToneD878CodeplugEncoder.Build's includeTalkGroups remarks) - if channels get rewritten
// (recomputing ContactIndex fresh from the CURRENT Contacts table) without ever re-syncing the
// list itself, a channel's index can point at a stale slot on the radio that no longer holds the
// contact it's supposed to (confirmed on real hardware 2026-07-19: a channel's ContactIndex was
// provably correct per today's Contacts table, but the on-device TalkGroupList slot it pointed at
// still held an unrelated contact from an earlier, no-longer-current sync).
static int RunSyncTalkGroups(string[] syncArgs)
{
    if (syncArgs.Length < 1)
    {
        Console.WriteLine("Usage: HamNetProgrammer.Cli sync-talkgroups <port> [dbPath]");
        return 1;
    }

    var portName = syncArgs[0];
    var dbPath = syncArgs.Length > 1
        ? syncArgs[1]
        : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data", "codeplug.db"));

    using var db = CodeplugDatabase.OpenOrCreate(dbPath);
    var warnings = new List<string>();
    var regions = AnyToneD878CodeplugEncoder.BuildTalkGroupsOnly(db, warnings);
    var fingerprint = AnyToneD878CodeplugEncoder.GetContactIndexFingerprint(db);
    var totalBytes = regions.Sum(r => (long)r.Data.Length);
    Console.WriteLine($"Encoded {regions.Count} region(s), {totalBytes:N0} bytes to write.");
    foreach (var w in warnings) Console.WriteLine($"  Warning: {w}");

    // Each TalkGroupList[N] bank region (see AnyToneD878CodeplugEncoder's remarks on bank-based
    // addressing - real hardware corruption 2026-07-19 traced back to treating this as one flat
    // array) gets written in its own committed session and read-back verified. Each bank is at
    // most 100,000 bytes, comfortably under the size that's separately confirmed unsafe in one
    // session (~558,000 bytes silently failed) - a generous maxBytesPerSession here just means
    // WriteRegionChunkedAndVerify writes each bank as a single chunk, no further splitting needed.
    var talkGroupBankRegions = regions.Where(r => r.Name.StartsWith("TalkGroupList[")).OrderBy(r => r.Name).ToList();
    var otherRegions = regions.Where(r => !r.Name.StartsWith("TalkGroupList[")).ToList();

    try
    {
        using (var radio = new AnyToneD878Transport(portName))
        {
            Console.WriteLine($"Opening {portName}...");
            radio.Open();
            radio.StartProgrammingSession();
            Console.WriteLine($"Device identifier: {radio.ReadDeviceId()}");

            var started = DateTime.Now;
            AnyToneD878CodeplugWriter.WriteSafeRegions(radio, otherRegions, (region, index, total, written, totalW) =>
            {
                var elapsed = DateTime.Now - started;
                Console.WriteLine($"  [{index}/{total}] {region.Name} - {written:N0}/{totalW:N0} bytes, {elapsed.TotalSeconds:F0}s elapsed");
            });

            Console.WriteLine("Ending programming session (this commits the write - device will drop off USB and re-enumerate)...");
            radio.EndProgrammingSession();
        }

        var anyBankFailed = false;
        foreach (var bankRegion in talkGroupBankRegions)
        {
            const int maxBytesPerSession = 200_000;
            var verified = AnyToneD878CodeplugWriter.WriteRegionChunkedAndVerify(portName, bankRegion, maxBytesPerSession,
                msg => Console.WriteLine($"  {msg}"));
            if (!verified)
            {
                Console.WriteLine($"FAILED - {bankRegion.Name} did not verify correctly.");
                anyBankFailed = true;
            }
        }

        if (anyBankFailed)
        {
            Console.WriteLine("Do not assume the radio's talkgroup list is correct.");
            return 1;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error: {ex.Message}");
        return 1;
    }

    using (var updateCmd = db.CreateCommand())
    {
        updateCmd.CommandText = "UPDATE RadioSettings SET LastSyncedContactCount = $count, LastSyncedMaxContactId = $maxId WHERE Id = 1;";
        updateCmd.Parameters.AddWithValue("$count", fingerprint.Count);
        updateCmd.Parameters.AddWithValue("$maxId", fingerprint.MaxId);
        updateCmd.ExecuteNonQuery();
    }

    Console.WriteLine("Done. Contact-sync fingerprint updated.");
    return 0;
}

// Names which specific regions differ between two dumps - the restore/verify flows only ever
// logged a COUNT of mismatches ("2 region(s) still differ"), not which ones, which is exactly the
// piece of information needed to actually act on a failed verification. Read-only, offline - just
// diffs two already-captured .bin/.manifest.csv pairs on disk.
static int RunCompareDumps(string[] compareArgs)
{
    if (compareArgs.Length < 4)
    {
        Console.WriteLine("Usage: HamNetProgrammer.Cli compare-dumps <beforeBin> <beforeManifest> <afterBin> <afterManifest>");
        return 1;
    }

    var before = DumpReader.Load(compareArgs[0], compareArgs[1]);
    var after = DumpReader.Load(compareArgs[2], compareArgs[3]);
    var result = DumpComparer.Compare(before, after);

    Console.WriteLine($"{result.RegionsCompared} region(s) compared, {result.MismatchedRegionNames.Count} mismatch(es), {result.SkippedRegionNames.Count} skipped (not comparable).");
    if (result.MismatchedRegionNames.Count > 0)
    {
        Console.WriteLine("Mismatched regions:");
        foreach (var name in result.MismatchedRegionNames) Console.WriteLine($"  {name}");
    }
    return result.AllMatch ? 0 : 1;
}

// Read-only sanity check - no radio, no database writes. Answers "if a talkgroup was carried
// over wrong (e.g. a Brandmeister-era label that doesn't mean the same thing on FreeDMR), how
// would we know?" - see TalkGroupAuditor's remarks. (A second check, comparing each channel's own
// name against its linked contact's name, was tried and dropped 2026-07-22 - too noisy against
// this project's zone-suffix abbreviation convention, e.g. "Eastmids" vs "East Midlands" shares no
// long substring even though they're obviously the same thing to a person. 453 findings, mostly
// false positives - not worth shipping.)
static async Task<int> RunAuditTalkGroups(string[] auditArgs)
{
    var dbPath = auditArgs.Length > 0
        ? auditArgs[0]
        : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data", "codeplug.db"));

    using var db = CodeplugDatabase.OpenOrCreate(dbPath);

    Console.WriteLine("Fetching the full talkgroup directory (Brandmeister + TGIF + FreeDMR)...");
    var networkData = await TalkGroupNetworkImporter.FetchAsync(networkFilter: null);
    Console.WriteLine($"  {networkData.Count:N0} entries fetched.");

    var networkFindings = TalkGroupAuditor.AuditAgainstNetwork(db, networkData);

    Console.WriteLine();
    Console.WriteLine($"=== Talkgroup name vs. network directory ({networkFindings.Count} finding(s)) ===");
    foreach (var f in networkFindings.OrderBy(f => f.Kind).ThenBy(f => f.DmrId))
    {
        Console.WriteLine($"  [{f.Kind}] TG{f.DmrId} '{f.LocalName}'");
        foreach (var d in f.Details) Console.WriteLine($"    {d}");
    }

    Console.WriteLine();
    Console.WriteLine("Advisory only - review each finding, nothing here was changed automatically.");
    return 0;
}

// Local-database-only cleanup - no radio involved. Fixes the "two SOARC entries" class of
// problem: a legacy/manually-created contact (Network NULL) and a later network-tagged import
// both existing for the same real-world talkgroup, splitting search results and which channels
// reference which row (see DuplicateTalkGroupMerger's remarks for why TalkGroupNetworkImporter
// itself deliberately never fixes this on its own).
static int RunMergeDuplicateTalkGroups(string[] mergeArgs)
{
    var dbPath = mergeArgs.Length > 0
        ? mergeArgs[0]
        : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data", "codeplug.db"));

    using var db = CodeplugDatabase.OpenOrCreate(dbPath);
    var duplicateGroups = DuplicateTalkGroupMerger.FindDuplicates(db);
    if (duplicateGroups.Count == 0)
    {
        Console.WriteLine("No duplicate talkgroups found.");
        return 0;
    }

    Console.WriteLine($"Found {duplicateGroups.Count} DmrId(s) with duplicate Group contacts:");
    var merged = 0;
    var skipped = 0;
    foreach (var group in duplicateGroups)
    {
        var names = string.Join(" | ", group.Contacts.Select(c => $"#{c.Id} '{c.Name}' (Network={c.Network ?? "none"})"));
        Console.WriteLine($"  TG{group.DmrId}: {names}");

        var result = DuplicateTalkGroupMerger.Merge(db, group);
        if (result is null)
        {
            Console.WriteLine("    Skipped - ambiguous (no single network-tagged row to prefer over the others). Resolve manually.");
            skipped++;
            continue;
        }

        Console.WriteLine($"    Kept #{result.KeptId} '{result.KeptName}', removed {string.Join(",", result.RemovedIds)} " +
                           $"({result.ChannelsRepointed} channel(s), {result.GroupListEntriesRepointed} group-list entry(ies) repointed).");
        merged++;
    }

    Console.WriteLine($"Done. {merged} DmrId(s) merged, {skipped} skipped for manual review.");
    return 0;
}

static int RunHealthCheck(string[] healthArgs)
{
    var dbPath = healthArgs.Length > 0
        ? healthArgs[0]
        : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data", "codeplug.db"));

    using var db = CodeplugDatabase.OpenOrCreate(dbPath);
    var findings = CodeplugHealthCheck.Run(db);

    if (findings.Count == 0)
    {
        Console.WriteLine("No issues found.");
        return 0;
    }

    Console.WriteLine($"{findings.Count} category(ies) with findings:");
    foreach (var finding in findings)
    {
        Console.WriteLine($"  {finding.Category}: {finding.Summary}");
        foreach (var detail in finding.Details)
            Console.WriteLine($"    - {detail}");
    }

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
    var encodeWarnings = new List<string>();
    var regions = AnyToneD878CodeplugEncoder.Build(db, encodeWarnings);
    var totalBytes = regions.Sum(r => (long)r.Data.Length);
    Console.WriteLine($"Encoded {regions.Count} regions, {totalBytes:N0} bytes to write.");
    foreach (var warning in encodeWarnings) Console.WriteLine($"  Warning: {warning}");

    // ChannelBank[N] regions (channel table + TX Color Code table combined, see
    // AnyToneD878CodeplugEncoder.EncodeChannels' remarks) each get their own committed,
    // read-back-verified session, same as TalkGroupList[N] below - kept OUT of the big bundled
    // write both because it was found to overflow that session's reliability threshold once
    // these were included (2026-07-21) and, more importantly, because splitting a bank's two
    // halves across separate sessions would risk a same-erase-block disturb between them.
    var channelBankRegions = regions.Where(r => r.Name.StartsWith("ChannelBank[")).OrderBy(r => r.Name).ToList();
    var sharedBlockFailures = new List<string>();

    try
    {
        using (var radio = new AnyToneD878Transport(portName))
        {
            Console.WriteLine($"Opening {portName}...");
            radio.Open();

            Console.WriteLine("Starting programming session (radio should show 'PC Mode')...");
            radio.StartProgrammingSession();
            Console.WriteLine($"Device identifier: {radio.ReadDeviceId()}");

            var started = DateTime.Now;
            var bundledRegions = regions.Where(r => !r.Name.StartsWith("ChannelBank[")).ToList();
            AnyToneD878CodeplugWriter.WriteSafeRegions(radio, bundledRegions, (region, index, total, written, totalW) =>
            {
                var elapsed = DateTime.Now - started;
                Console.WriteLine($"  [{index}/{total}] {region.Name} done - {written:N0}/{totalW:N0} bytes, {elapsed.TotalSeconds:F0}s elapsed");
            });

            Console.WriteLine("Ending programming session (this commits the write - device will drop off USB and re-enumerate)...");
            radio.EndProgrammingSession();
        }

        foreach (var bankRegion in channelBankRegions)
        {
            const int maxBytesPerSession = 20_000; // each bank is at most ~16,384 bytes - one chunk per bank
            var verified = AnyToneD878CodeplugWriter.WriteRegionChunkedAndVerify(portName, bankRegion, maxBytesPerSession,
                msg => Console.WriteLine($"  {msg}"));
            if (!verified)
                sharedBlockFailures.Add($"{bankRegion.Name} did not verify correctly.");
        }

        // Each shared-block region gets its own isolated session, built/written/verified/retried
        // end to end by WriteAndVerifySharedBlock. ZoneChannelDefaults MUST be written LAST - see
        // AnyToneD878CodeplugEncoder.ZoneChannelDefaultsBlockAddress's remarks: it starts at exactly
        // GeneralUsedBitmapsBlockAddress + GeneralUsedBitmapsBlockLength, i.e. the two are physically
        // contiguous erase blocks on the same flash die, and writing GeneralUsedBitmapsBlock was
        // proven on real hardware (2026-07-19) to corrupt an already-good, untouched
        // ZoneChannelDefaults purely as a program/erase-disturb side effect - no amount of session
        // isolation or write-verify-retry on ZoneChannelDefaults itself helps if something else
        // writes its neighbor afterward. RoamingBlock is at a completely different address and was
        // confirmed NOT to cause this, so its position relative to the other two doesn't matter.
        WriteSharedBlockAndCheck("RoamingBlock", AnyToneD878CodeplugWriter.HasRoamingBlock(regions),
            r => AnyToneD878CodeplugWriter.BuildRoamingBlockRegion(r, regions));
        WriteSharedBlockAndCheck("GeneralUsedBitmapsBlock", AnyToneD878CodeplugWriter.HasGeneralUsedBitmapsBlock(regions),
            r => AnyToneD878CodeplugWriter.BuildGeneralUsedBitmapsBlockRegion(r, regions));
        WriteSharedBlockAndCheck("ZoneChannelDefaults", AnyToneD878CodeplugWriter.HasZoneChannelDefaults(regions),
            r => AnyToneD878CodeplugWriter.BuildZoneChannelDefaultsRegion(r, regions));

        if (sharedBlockFailures.Count > 0)
        {
            Console.WriteLine("FAILED - one or more shared blocks did not verify correctly after retries:");
            foreach (var f in sharedBlockFailures) Console.WriteLine($"  {f}");
            Console.WriteLine("Do not assume the radio is in a good state. Take a fresh backup and check before writing again.");
            return 1;
        }

        Console.WriteLine("Done.");
        return 0;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error: {ex.Message}");
        return 1;
    }

    void WriteSharedBlockAndCheck(string label, bool needed, Func<AnyToneD878Transport, EncodedRegion> build)
    {
        if (!needed) return;

        var result = AnyToneD878CodeplugWriter.WriteAndVerifySharedBlock(portName, build, msg => Console.WriteLine($"  [{label}] {msg}"));
        if (!result.Verified)
            sharedBlockFailures.Add(result.Error ?? $"{label} failed to verify.");
    }
}

// Decodes a full radio memory dump back into the SQLite database - the reverse of write-codeplug,
// for a user who already has a working radio and wants its configuration as a starting point.
// Gated on RadioRiskTier.Validated (D878UV only): reads are safe on the radio regardless of model,
// but silently importing an unverified model's mis-decoded bytes into the shared database is not
// the same kind of safe - a bad decode fails invisibly, unlike a bad write, which fails loudly.
static int RunReadCodeplug(string[] readArgs)
{
    if (readArgs.Length < 1)
    {
        Console.WriteLine("Usage: HamNetProgrammer.Cli read-codeplug <port> [dbPath]");
        return 1;
    }

    var portName = readArgs[0];
    var dbPath = readArgs.Length > 1
        ? readArgs[1]
        : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data", "codeplug.db"));

    try
    {
        string deviceId;
        string binaryPath, manifestPath;
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var dumpDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "dumps"));
        Directory.CreateDirectory(dumpDir);
        binaryPath = Path.Combine(dumpDir, $"read_{stamp}.bin");
        manifestPath = Path.Combine(dumpDir, $"read_{stamp}.manifest.csv");

        using (var radio = new AnyToneD878Transport(portName))
        {
            Console.WriteLine($"Opening {portName}...");
            radio.Open();
            radio.StartProgrammingSession();
            deviceId = radio.ReadDeviceId();
            Console.WriteLine($"Device identifier: {deviceId}");

            var profile = RadioRiskCatalog.Lookup(deviceId);
            if (profile.Tier != RadioRiskTier.Validated)
            {
                Console.WriteLine($"FAILED - {profile.ModelLabel} ({profile.Tier} risk) is not a hardware-verified model for Read Codeplug.");
                Console.WriteLine("Decoding an unverified model's memory layout risks silently importing wrong data into the shared database.");
                Console.WriteLine("Use the Desktop app's \"Contribute a Memory Sample\" instead - it only reads, never decodes/imports.");
                radio.EndProgrammingSession();
                return 1;
            }

            Console.WriteLine($"Dumping full memory to {binaryPath}...");
            var results = AnyToneD878MemoryDumper.Dump(radio, binaryPath, manifestPath, (region, index, total, bytesDone, totalBytes) =>
            {
                if (index == 1 || index % 25 == 0 || index == total)
                    Console.WriteLine($"  [{index}/{total}] {region.Name} - {bytesDone:N0}/{totalBytes:N0} bytes");
            });
            radio.EndProgrammingSession();

            var failed = results.Count(r => !r.Succeeded);
            Console.WriteLine($"Dump complete: {results.Count} regions, {failed} failed.");
        }

        Console.WriteLine("Decoding and importing into the database...");
        var dump = DumpReader.Load(binaryPath, manifestPath);
        using var db = CodeplugDatabase.OpenOrCreate(dbPath);
        var result = AnyToneD878CodeplugImporter.Import(db, dump);

        foreach (var category in result.Categories)
        {
            Console.WriteLine($"  {category.Category}: {category.Matched} matched, {category.New} new, {category.Skipped} skipped.");
            foreach (var label in category.NewItemLabels)
                Console.WriteLine($"    + {label}");
        }
        foreach (var warning in result.Warnings)
            Console.WriteLine($"  Warning: {warning}");

        Console.WriteLine("Done.");
        return 0;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error: {ex.Message}");
        return 1;
    }
}

// Experimental variant of write-codeplug (2026-07-19): instead of isolating
// GeneralUsedBitmapsBlock/ZoneChannelDefaults into their own sessions, sweeps the full
// 0x02480000-0x02600000 range in ONE session - the same footprint and order a real USB capture
// showed RT Systems using successfully. RoamingBlock (0x01040000, unrelated address) still gets
// its own isolated session same as before.
static int RunWriteWideSweep(string[] writeArgs)
{
    if (writeArgs.Length < 1)
    {
        Console.WriteLine("Usage: HamNetProgrammer.Cli write-wide-sweep <port> [dbPath]");
        return 1;
    }

    var portName = writeArgs[0];
    var dbPath = writeArgs.Length > 1
        ? writeArgs[1]
        : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data", "codeplug.db"));

    using var db = CodeplugDatabase.OpenOrCreate(dbPath);
    var encodeWarnings = new List<string>();
    var regions = AnyToneD878CodeplugEncoder.Build(db, encodeWarnings);
    var totalBytes = regions.Sum(r => (long)r.Data.Length);
    Console.WriteLine($"Encoded {regions.Count} regions, {totalBytes:N0} bytes to write.");
    foreach (var warning in encodeWarnings) Console.WriteLine($"  Warning: {warning}");

    // See RunWriteCodeplug's remarks on channelBankRegions - same reasoning applies here.
    var channelBankRegions = regions.Where(r => r.Name.StartsWith("ChannelBank[")).OrderBy(r => r.Name).ToList();

    try
    {
        using (var radio = new AnyToneD878Transport(portName))
        {
            Console.WriteLine($"Opening {portName}...");
            radio.Open();
            radio.StartProgrammingSession();
            Console.WriteLine($"Device identifier: {radio.ReadDeviceId()}");

            var started = DateTime.Now;
            var bundledRegions = regions.Where(r => !r.Name.StartsWith("ChannelBank[")).ToList();
            AnyToneD878CodeplugWriter.WriteSafeRegions(radio, bundledRegions, (region, index, total, written, totalW) =>
            {
                var elapsed = DateTime.Now - started;
                Console.WriteLine($"  [{index}/{total}] {region.Name} done - {written:N0}/{totalW:N0} bytes, {elapsed.TotalSeconds:F0}s elapsed");
            });

            Console.WriteLine("Ending programming session (this commits the write - device will drop off USB and re-enumerate)...");
            radio.EndProgrammingSession();
        }

        var failures = new List<string>();

        foreach (var bankRegion in channelBankRegions)
        {
            const int maxBytesPerSession = 20_000; // each bank is at most ~16,384 bytes - one chunk per bank
            var verified = AnyToneD878CodeplugWriter.WriteRegionChunkedAndVerify(portName, bankRegion, maxBytesPerSession,
                msg => Console.WriteLine($"  {msg}"));
            if (!verified)
                failures.Add($"{bankRegion.Name} did not verify correctly.");
        }

        if (AnyToneD878CodeplugWriter.HasRoamingBlock(regions))
        {
            var roamingResult = AnyToneD878CodeplugWriter.WriteAndVerifySharedBlock(portName,
                r => AnyToneD878CodeplugWriter.BuildRoamingBlockRegion(r, regions),
                msg => Console.WriteLine($"  [RoamingBlock] {msg}"));
            if (!roamingResult.Verified)
                failures.Add(roamingResult.Error ?? "RoamingBlock failed to verify.");
        }

        if (AnyToneD878CodeplugWriter.HasGeneralUsedBitmapsBlock(regions) || AnyToneD878CodeplugWriter.HasZoneChannelDefaults(regions))
        {
            var verified = AnyToneD878CodeplugWriter.WriteWideSettingsSweepAndVerify(portName, regions,
                msg => Console.WriteLine($"  [WideSettingsSweep] {msg}"));
            if (!verified)
                failures.Add("WideSettingsSweep did not verify correctly.");
        }

        if (failures.Count > 0)
        {
            Console.WriteLine("FAILED - one or more shared regions did not verify correctly:");
            foreach (var f in failures) Console.WriteLine($"  {f}");
            Console.WriteLine("Do not assume the radio is in a good state. Take a fresh backup and check before writing again.");
            return 1;
        }

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
    var encodeWarnings = new List<string>();
    var regions = AnyToneD878CodeplugEncoder.Build(db, encodeWarnings);
    foreach (var warning in encodeWarnings) Console.WriteLine($"Warning: {warning}");

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
