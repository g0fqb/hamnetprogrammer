using System;
using System.Linq;
using System.Text;
using System.Threading;
using LibUsbDotNet.LibUsb;
using LibUsbDotNet.Main;

namespace HamNetProgrammer.Core.Radios.TyT;

/// <summary>
/// Implements the TyT-family DFU command-channel protocol (MD-380/MD-390/UV380/UV390/MD2017/
/// DM-1701) as reverse-engineered by the qdmr project (lib/dfu_libusb.cc, lib/tyt_interface.cc).
/// Unlike the AnyTone D878UV's COM-port protocol, these radios only expose this interface in
/// DFU/bootloader mode (VID 0x0483, PID 0xdf11 - the generic STM32 bootloader ID, shared by many
/// unrelated devices, so Identify() is what actually confirms it's a TyT-family radio) and
/// require Windows to have the device bound to a WinUSB-class driver (e.g. via Zadig) rather than
/// whatever driver it picks by default.
///
/// Everything written to DFU block 0 is a command to the radio; responses to those commands are
/// read back from the same block. Reads/writes elsewhere address 1024-byte-aligned blocks offset
/// by +2 (block = address/1024 + 2) - both quirks are the radio firmware's, not part of the DFU
/// standard.
/// </summary>
public sealed class TytDfuTransport : IDisposable
{
    public const ushort VendorId = 0x0483;
    public const ushort ProductId = 0xdf11;

    private const byte RequestTypeToHost = 0xA1;
    private const byte RequestTypeToDevice = 0x21;
    private const byte RequestDetach = 0;
    private const byte RequestDnload = 1;
    private const byte RequestUpload = 2;
    private const byte RequestGetStatus = 3;
    private const byte RequestClrStatus = 4;
    private const byte RequestGetState = 5;
    private const byte RequestAbort = 6;

    private const int DfuAppIdle = 0;
    private const int DfuAppDetach = 1;
    private const int DfuIdle = 2;
    private const int DfuDnBusy = 4;
    private const int DfuManifestWaitReset = 8;
    private const int DfuError = 10;

    private readonly UsbContext _context = new();
    private IUsbDevice? _device;

    public bool IsOpen => _device?.IsOpen == true;

    /// <summary>Claims the first TyT-family radio found presenting the DFU bootloader interface.
    /// Throws if none is found or the interface can't be claimed (commonly because the device
    /// isn't bound to a WinUSB driver). Does not talk to the radio's command protocol yet - call
    /// <see cref="EnterProgramMode"/> then <see cref="Identify"/> next.</summary>
    public void Open()
    {
        var match = _context.List().FirstOrDefault(d => d.VendorId == VendorId && d.ProductId == ProductId)
            ?? throw new InvalidOperationException(
                $"No TyT-family radio found in DFU mode (VID {VendorId:x4}:PID {ProductId:x4}). " +
                "The radio must be powered on in bootloader/DFU mode, and Windows must have it bound to a WinUSB driver (see Zadig) rather than its default driver.");

        match.Open();
        if (!match.ClaimInterface(0))
        {
            match.Close();
            throw new InvalidOperationException("Could not claim the DFU interface - it may be in use by another program, or not bound to a WinUSB driver.");
        }
        _device = match;
    }

    public void Close()
    {
        if (_device is null) return;
        _device.ReleaseInterface(0);
        _device.Close();
        _device = null;
    }

    public void Dispose()
    {
        Close();
        _context.Dispose();
    }

    /// <summary>Enters programming mode. Must be called before Identify/Read/Write/Erase.</summary>
    public void EnterProgramMode()
    {
        WaitIdle();
        Md380Command(0x91, 0x01);
    }

    /// <summary>Reboots the radio out of programming mode - no response is read back, matching
    /// qdmr (the radio just resets).</summary>
    public void Reboot()
    {
        WaitIdle();
        Download(0, [0x91, 0x05]);
    }

    /// <summary>Reads the radio's identifier string (e.g. "DM-1701" for the Baofeng, "DR780" for
    /// the TyT MD-380, "MD390"/"MD-UV380"/"MD-UV390"/"2017" for the rest of the family) and
    /// completes the zero-address handshake qdmr always performs right after identifying.</summary>
    public string Identify()
    {
        Md380Command(0xa2, 0x01);
        var buffer = new byte[64];
        var received = Upload(0, buffer);
        var nullIndex = Array.IndexOf(buffer, (byte)0, 0, received);
        var length = nullIndex >= 0 ? nullIndex : received;
        var id = Encoding.ASCII.GetString(buffer, 0, length);

        SetAddress(0);
        return id;
    }

    /// <summary>Erases every 0x10000-aligned block covering [start, start+size) - required before
    /// writing, per this radio's explicit-erase model (unlike the AnyTone D878UV's implicit
    /// end-of-session erase). Not used by read-only memory capture.</summary>
    public void Erase(uint start, uint size, Action<uint, uint>? onProgress = null)
    {
        WaitIdle();
        Md380Command(0x91, 0x01);
        Thread.Sleep(100);

        const uint blockSize = 0x10000;
        var alignedStart = start / blockSize * blockSize;
        var end = start + size;
        var alignedEnd = (end + blockSize - 1) / blockSize * blockSize;

        for (var address = alignedStart; address < alignedEnd; address += blockSize)
        {
            EraseBlock(address);
            onProgress?.Invoke(address - alignedStart, alignedEnd - alignedStart);
        }

        SetAddress(0);
    }

    /// <summary>Reads a block of radio memory. <paramref name="address"/> must be a multiple of
    /// 1024 (the radio computes the DFU block number as address/1024 + 2, so a non-aligned address
    /// would silently read the wrong block rather than error).</summary>
    public void Read(uint address, byte[] buffer, int length)
    {
        var block = address / 1024 + 2;
        var transferred = Upload(block, buffer, length);
        if (transferred != length)
            throw new InvalidOperationException($"DFU read at 0x{address:x8} got {transferred}/{length} bytes.");
    }

    /// <summary>Writes a block of radio memory. Same 1024-byte alignment requirement as
    /// <see cref="Read"/>. The target block must already be erased via <see cref="Erase"/> -
    /// unlike the AnyTone D878UV, this radio does not erase implicitly.</summary>
    public void Write(uint address, byte[] data, int length)
    {
        var block = address / 1024 + 2;
        Download(block, data, length);
        WaitIdle();
    }

    private void Md380Command(byte a, byte b)
    {
        Download(0, [a, b]);
        Thread.Sleep(100);
        WaitIdle();
    }

    private void SetAddress(uint address)
    {
        Download(0, AddressCommand(0x21, address));
        WaitIdle();
    }

    private void EraseBlock(uint address)
    {
        Download(0, AddressCommand(0x41, address));
        WaitIdle();
    }

    private static byte[] AddressCommand(byte opcode, uint address) =>
    [
        opcode,
        (byte)address,
        (byte)(address >> 8),
        (byte)(address >> 16),
        (byte)(address >> 24),
    ];

    private IUsbDevice Device => _device ?? throw new InvalidOperationException("TyT DFU device is not open.");

    private void Download(uint block, byte[] data) => Download(block, data, data.Length);

    private void Download(uint block, byte[] data, int length)
    {
        var setup = new UsbSetupPacket(RequestTypeToDevice, RequestDnload, (int)block, 0, length);
        var transferred = Device.ControlTransfer(setup, data, 0, length);
        if (transferred != length)
            throw new InvalidOperationException($"DFU download to block {block} sent {transferred}/{length} bytes.");
        GetStatus();
    }

    // Returns the actual number of bytes transferred rather than throwing on a short read - unlike
    // Download (an OUT transfer, where getting fewer bytes accepted than sent is a real anomaly),
    // a short IN transfer is normal USB behavior: the device sends however many bytes it actually
    // has and no more. Confirmed against real hardware - the identify command's documented
    // response is only 32 bytes even though qdmr (and this code) requests a 64-byte buffer.
    private int Upload(uint block, byte[] buffer) => Upload(block, buffer, buffer.Length);

    private int Upload(uint block, byte[] buffer, int length)
    {
        var setup = new UsbSetupPacket(RequestTypeToHost, RequestUpload, (int)block, 0, length);
        var transferred = Device.ControlTransfer(setup, buffer, 0, length);
        GetStatus();
        return transferred;
    }

    private void GetStatus()
    {
        var setup = new UsbSetupPacket(RequestTypeToHost, RequestGetStatus, 0, 0, 6);
        var buffer = new byte[6];
        Device.ControlTransfer(setup, buffer, 0, 6);
    }

    private void ClearStatus()
    {
        var setup = new UsbSetupPacket(RequestTypeToDevice, RequestClrStatus, 0, 0, 0);
        Device.ControlTransfer(setup);
    }

    private int GetState()
    {
        var setup = new UsbSetupPacket(RequestTypeToHost, RequestGetState, 0, 0, 1);
        var buffer = new byte[1];
        Device.ControlTransfer(setup, buffer, 0, 1);
        return buffer[0];
    }

    private void Detach(int timeoutMs)
    {
        var setup = new UsbSetupPacket(RequestTypeToDevice, RequestDetach, timeoutMs, 0, 0);
        Device.ControlTransfer(setup);
    }

    private void Abort()
    {
        var setup = new UsbSetupPacket(RequestTypeToDevice, RequestAbort, 0, 0, 0);
        Device.ControlTransfer(setup);
    }

    /// <summary>Polls the DFU state machine until it reaches dfuIDLE, per qdmr's wait_idle - the
    /// device requires this after every command before it will accept the next one.</summary>
    private void WaitIdle()
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("TyT DFU device did not reach dfuIDLE state in time.");

            var state = GetState();
            switch (state)
            {
                case DfuIdle:
                    return;
                case DfuAppIdle:
                    Detach(1000);
                    break;
                case DfuError:
                    ClearStatus();
                    break;
                case DfuAppDetach:
                case DfuDnBusy:
                case DfuManifestWaitReset:
                    Thread.Sleep(100);
                    continue;
                default:
                    Abort();
                    break;
            }
        }
    }

    /// <summary>True if a device presenting the TyT-family DFU bootloader interface is currently
    /// connected. Doesn't confirm it's actually a TyT radio (the VID/PID is the generic STM32
    /// bootloader ID, shared by unrelated devices) - only <see cref="Identify"/> can confirm that.</summary>
    public static bool AnyDeviceConnected()
    {
        using var context = new UsbContext();
        return context.List().Any(d => d.VendorId == VendorId && d.ProductId == ProductId);
    }
}
