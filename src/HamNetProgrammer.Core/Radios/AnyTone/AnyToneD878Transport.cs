using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Text;

namespace HamNetProgrammer.Core.Radios.AnyTone;

/// <summary>
/// Implements the AT-D878UV serial programming protocol (PROGRAM/READ/WRITE/END)
/// as reverse-engineered by https://github.com/reald/anytone-flash-tools.
/// </summary>
public sealed class AnyToneD878Transport : IDisposable
{
    private readonly SerialPort _port;

    public AnyToneD878Transport(string portName, int baudRate = 115200)
    {
        _port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 1000,
            WriteTimeout = 1000,
        };
    }

    public bool IsOpen => _port.IsOpen;

    public void Open() => _port.Open();

    // Closing after EndProgrammingSession races the radio's own USB drop-off/re-enumerate: if the
    // underlying device has already physically disappeared by the time this runs (timing-
    // dependent, and more readily hit under VM USB passthrough than on bare metal), SerialPort.Close()
    // can throw (observed as Win32 error 433, "A device which does not exist was specified").
    // There's nothing left to release at that point, so this is a no-op that failed to happen, not
    // a real error - letting it throw here was overwriting an otherwise-successful Read Codeplug
    // (dump + decode + import already committed) with a spurious "Failed" result, since this runs
    // inside the caller's `using` disposal, still within their try/catch.
    public void Close()
    {
        try
        {
            if (_port.IsOpen) _port.Close();
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose() => Close();

    public void StartProgrammingSession()
    {
        Write(Encoding.ASCII.GetBytes("PROGRAM"));
        var response = ReadExact(3);
        if (response[0] != (byte)'Q' || response[1] != (byte)'X' || response[2] != 0x06)
            throw new InvalidOperationException($"Unexpected response to PROGRAM: {Convert.ToHexString(response)}");
    }

    public string ReadDeviceId()
    {
        Write([0x02]);
        var response = ReadUntilQuiet();
        if (response.Length == 0 || response[^1] != 0x06)
            throw new InvalidOperationException($"Unexpected response to device ID query: {Convert.ToHexString(response)}");

        var nullIndex = Array.IndexOf(response, (byte)0);
        var textLength = nullIndex >= 0 ? nullIndex : response.Length - 1;
        return Encoding.ASCII.GetString(response, 0, textLength);
    }

    /// <summary>Reads a block of radio memory. The radio only supports lengths up to 0xff.</summary>
    public byte[] ReadMemory(uint address, byte length = 0x10)
    {
        var request = new byte[6];
        request[0] = 0x52;
        WriteAddress(request, 1, address);
        request[5] = length;
        Write(request);

        var response = ReadExact(length + 8);
        if (response[0] != (byte)'W')
            throw new InvalidOperationException($"Expected 'W' response reading 0x{address:x8}, got 0x{response[0]:x2}");

        var respAddress = ReadAddress(response, 1);
        if (respAddress != address)
            throw new InvalidOperationException($"Address mismatch: requested 0x{address:x8}, got 0x{respAddress:x8}");

        var respLength = response[5];
        var data = response.AsSpan(6, respLength).ToArray();
        var checksum = response[6 + respLength];
        var ack = response[7 + respLength];

        var expectedChecksum = Checksum(response.AsSpan(1, 5 + respLength));
        if (checksum != expectedChecksum)
            throw new InvalidOperationException($"Checksum mismatch reading 0x{address:x8}: expected 0x{expectedChecksum:x2}, got 0x{checksum:x2}");
        if (ack != 0x06)
            throw new InvalidOperationException($"Missing ACK reading 0x{address:x8}");

        return data;
    }

    /// <summary>
    /// Writes exactly 16 bytes at the given address. The radio does not accept other lengths.
    ///
    /// Confirmed on real hardware (2026-07-17): writes are buffered, not applied immediately -
    /// <see cref="ReadMemory"/> within the same session still returns the pre-write flash content.
    /// The actual flash commit happens when <see cref="EndProgrammingSession"/> is sent, at which
    /// point the device drops off USB and re-enumerates ~10-15s later. To verify a write, end the
    /// session, wait for the port to reappear, then open a new session and read back.
    /// </summary>
    public void WriteMemory(uint address, ReadOnlySpan<byte> data)
    {
        if (data.Length != 0x10)
            throw new ArgumentException("The AT-D878UV only accepts 16-byte memory writes.", nameof(data));

        // Request layout: 'W' + address(4) + length(1) + data(16) + checksum(1) + literal 0x06
        var request = new byte[6 + data.Length + 2];
        request[0] = 0x57;
        WriteAddress(request, 1, address);
        request[5] = (byte)data.Length;
        data.CopyTo(request.AsSpan(6));
        request[6 + data.Length] = Checksum(request.AsSpan(1, 5 + data.Length));
        request[7 + data.Length] = 0x06;

        Write(request);
        var response = ReadExact(1);
        if (response[0] != 0x06)
            throw new InvalidOperationException($"Write to 0x{address:x8} was not acknowledged (got 0x{response[0]:x2}).");
    }

    public void EndProgrammingSession()
    {
        Write(Encoding.ASCII.GetBytes("END"));
        var response = ReadExact(1);
        if (response[0] != 0x06)
            throw new InvalidOperationException($"END was not acknowledged (got 0x{response[0]:x2}).");
    }

    private static void WriteAddress(byte[] buffer, int offset, uint address)
    {
        buffer[offset] = (byte)(address >> 24);
        buffer[offset + 1] = (byte)(address >> 16);
        buffer[offset + 2] = (byte)(address >> 8);
        buffer[offset + 3] = (byte)address;
    }

    private static uint ReadAddress(byte[] buffer, int offset) =>
        (uint)(buffer[offset] << 24 | buffer[offset + 1] << 16 | buffer[offset + 2] << 8 | buffer[offset + 3]);

    private static byte Checksum(ReadOnlySpan<byte> data)
    {
        var sum = 0;
        foreach (var b in data) sum += b;
        return (byte)(sum & 0xFF);
    }

    private void Write(byte[] data) => _port.Write(data, 0, data.Length);

    private byte[] ReadExact(int count)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = _port.Read(buffer, offset, count - offset);
            if (read <= 0) throw new TimeoutException("Serial read timed out.");
            offset += read;
        }
        return buffer;
    }

    /// <summary>Reads bytes until the port goes quiet. Used for the device-ID response, whose length varies by firmware.</summary>
    private byte[] ReadUntilQuiet(int maxBytes = 64, int perByteTimeoutMs = 300)
    {
        var originalTimeout = _port.ReadTimeout;
        _port.ReadTimeout = perByteTimeoutMs;
        var buffer = new List<byte>();
        try
        {
            while (buffer.Count < maxBytes)
                buffer.Add((byte)_port.ReadByte());
        }
        catch (TimeoutException)
        {
            // Port went quiet - treat the response as complete.
        }
        finally
        {
            _port.ReadTimeout = originalTimeout;
        }
        return buffer.ToArray();
    }
}
