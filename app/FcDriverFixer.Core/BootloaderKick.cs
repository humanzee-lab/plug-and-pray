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
    /// <summary>
    /// Send the reboot-to-bootloader command. Tries MSP first (deterministic on
    /// Betaflight 4.x), then falls back to the CLI "bl" command for older firmware.
    /// </summary>
    public static void Send(string portName, int baud = 115200)
    {
        // Framing lives in Msp so there is only one implementation of it.
        byte[] mspFrame = Msp.BuildRequest(Msp.SetReboot, Msp.RebootToBootloaderRom);

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
