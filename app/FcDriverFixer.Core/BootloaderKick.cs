using System.IO.Ports;

namespace FcDriverFixer.Core;

/// <summary>
/// Reboots a Betaflight/STM32 flight controller from normal (VCP) mode into DFU
/// (ROM bootloader) mode over serial. Verified on hardware 2026-07-18: a single
/// MSP_SET_REBOOT frame flips COM6 -> VID_0483&amp;PID_DF11 in ~2s.
///
/// A board in bootloader mode is inert (motors cannot spin) and a USB replug always
/// restores normal firmware.
/// </summary>
public static class BootloaderKick
{
    private const byte MSP_SET_REBOOT = 0x44;              // 68
    private const byte MSP_REBOOT_BOOTLOADER_ROM = 0x01;

    /// <summary>
    /// Send the reboot-to-bootloader command. Tries MSP first (deterministic on
    /// Betaflight 4.x), then falls back to the CLI "bl" command for older firmware.
    /// </summary>
    public static void Send(string portName, int baud = 115200)
    {
        // MSP v1 frame:  '$' 'M' '<' <size> <cmd> <data...> <crc>
        // crc = XOR of size, cmd and every data byte.
        byte size = 0x01;
        byte crc = (byte)(size ^ MSP_SET_REBOOT ^ MSP_REBOOT_BOOTLOADER_ROM);
        byte[] mspFrame = { 0x24, 0x4D, 0x3C, size, MSP_SET_REBOOT, MSP_REBOOT_BOOTLOADER_ROM, crc };

        using var port = new SerialPort(portName, baud, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 800,
            WriteTimeout = 800,
        };
        port.Open();

        // Primary: MSP reboot-to-bootloader.
        port.Write(mspFrame, 0, mspFrame.Length);
        Thread.Sleep(300);

        // Fallback: CLI. Enter CLI with '#', then send 'bl'. Harmless if the board
        // already took the MSP command and vanished (write throws, we swallow it).
        try
        {
            port.Write("#");
            Thread.Sleep(300);
            port.DiscardInBuffer();
            port.Write("bl\r");
            Thread.Sleep(200);
        }
        catch (Exception)
        {
            // Port dropped because the MSP reboot already succeeded — expected.
        }
    }
}
