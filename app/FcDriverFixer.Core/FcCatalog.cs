namespace FcDriverFixer.Core;

/// <summary>
/// The USB identities we recognise. Kept as data (not hardcoded logic) so that
/// adding AT32 / GD32 / new bridges later is a table edit, not a rewrite.
/// Only entries marked Verified=true have been confirmed on real hardware.
/// </summary>
public enum UsbRole
{
    /// STM32 (and compatibles) in DFU / ROM bootloader mode. Needs WinUSB to flash.
    Dfu,
    /// Native-USB virtual COM port (the FC running normally).
    Vcp,
    /// External USB-UART bridge chip (CP210x / CH340 / FTDI).
    UartBridge,
}

public readonly record struct KnownDevice(
    ushort Vid, ushort Pid, UsbRole Role, string Name, bool Verified);

public static class FcCatalog
{
    public static readonly IReadOnlyList<KnownDevice> Devices = new List<KnownDevice>
    {
        // --- Verified on hardware (2026-07-18) ---
        new(0x0483, 0xDF11, UsbRole.Dfu, "STM32 DFU bootloader", Verified: true),
        new(0x0483, 0x5740, UsbRole.Vcp, "STM32 virtual COM port", Verified: true),

        // --- Common UART bridges (IDs well-known, not yet bench-verified here) ---
        new(0x10C4, 0xEA60, UsbRole.UartBridge, "Silicon Labs CP210x", Verified: false),
        new(0x1A86, 0x7523, UsbRole.UartBridge, "CH340", Verified: false),
        new(0x1A86, 0x5523, UsbRole.UartBridge, "CH341", Verified: false),
        new(0x0403, 0x6001, UsbRole.UartBridge, "FTDI FT232", Verified: false),

        // --- Other DFU-capable MCUs increasingly seen on cheaper FCs (UNVERIFIED) ---
        // Left commented until confirmed against a real board, so we never claim
        // support we haven't tested.
        // new(0x2E3C, 0xDF11, UsbRole.Dfu, "Artery AT32 DFU", Verified: false),
    };

    public static KnownDevice? Match(ushort vid, ushort pid)
    {
        foreach (var d in Devices)
            if (d.Vid == vid && d.Pid == pid)
                return d;
        return null;
    }

    /// <summary>The WinUSB service name Windows reports when the DFU driver is correct.</summary>
    public const string WinUsbService = "WinUSB";
}
