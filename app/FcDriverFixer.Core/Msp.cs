using System.IO.Ports;
using System.Text;

namespace FcDriverFixer.Core;

/// <summary>
/// Minimal MSP (MultiWii Serial Protocol) client, enough to ask a Betaflight family
/// flight controller what it is and to tell it to reboot into its bootloader.
///
/// Frame format (MSP v1):
///   request   '$' 'M' '&lt;' [size] [cmd] [data...] [crc]
///   response  '$' 'M' '&gt;' [size] [cmd] [data...] [crc]
///   error     '$' 'M' '!' ...
///   crc = XOR of size, cmd and every data byte
/// </summary>
public static class Msp
{
    // Commands we use.
    public const byte ApiVersion = 1;
    public const byte FcVariant = 2;
    public const byte FcVersion = 3;
    public const byte BoardInfo = 4;
    public const byte BuildInfo = 5;
    public const byte Name = 10;
    public const byte SetReboot = 68;

    public const byte RebootToBootloaderRom = 1;

    /// <summary>Build an MSP v1 request frame.</summary>
    public static byte[] BuildRequest(byte cmd, params byte[] data)
    {
        byte size = (byte)data.Length;
        byte crc = (byte)(size ^ cmd);
        foreach (byte b in data) crc ^= b;

        var frame = new byte[6 + data.Length];
        frame[0] = 0x24; frame[1] = 0x4D; frame[2] = 0x3C;   // '$' 'M' '<'
        frame[3] = size; frame[4] = cmd;
        Array.Copy(data, 0, frame, 5, data.Length);
        frame[^1] = crc;
        return frame;
    }

    /// <summary>
    /// Send a request and return the response payload, or null if the board did not
    /// answer with a matching frame. Never throws for a protocol-level miss.
    /// </summary>
    public static byte[]? Request(SerialPort port, byte cmd, int waitMs = 200)
    {
        try
        {
            port.DiscardInBuffer();
            byte[] req = BuildRequest(cmd);
            port.Write(req, 0, req.Length);
            Thread.Sleep(waitMs);

            int n = port.BytesToRead;
            if (n < 6) return null;
            var buf = new byte[n];
            int read = port.Read(buf, 0, n);

            // Scan for the response header rather than assuming it starts at zero:
            // there can be leftover bytes in the buffer from earlier traffic.
            for (int i = 0; i + 5 < read; i++)
            {
                if (buf[i] != 0x24 || buf[i + 1] != 0x4D || buf[i + 2] != 0x3E) continue;
                if (buf[i + 4] != cmd) continue;

                int size = buf[i + 3];
                if (i + 5 + size > read) return null;
                var payload = new byte[size];
                Array.Copy(buf, i + 5, payload, 0, size);
                return payload;
            }
        }
        catch (Exception)
        {
            // Port vanished mid-conversation, or is held by something else.
        }
        return null;
    }

    internal static string? Ascii(byte[] b, int start, int len)
    {
        if (b.Length < start + len || len <= 0) return null;
        string s = Encoding.ASCII.GetString(b, start, len);
        int nul = s.IndexOf('\0');
        if (nul >= 0) s = s[..nul];
        s = s.Trim();
        return s.Length == 0 ? null : s;
    }
}

/// <summary>What the flight controller reports about itself.</summary>
public sealed record FcInfo(
    string? Firmware,     // "Betaflight"
    string? Version,      // "4.5.1"
    string? Target,       // "STM32H743"
    string? BoardName,    // manufacturer's board name, when the firmware reports one
    string? Manufacturer,
    string? Built,        // "Oct  2 2024"
    string? CraftName)
{
    /// <summary>One line suitable for the UI, e.g. "Betaflight 4.5.1 on an STM32H743".</summary>
    public string? Summary
    {
        get
        {
            if (Firmware is null) return null;
            var s = new StringBuilder(Firmware);
            if (Version is not null) s.Append(' ').Append(Version);

            // Prefer the manufacturer's board name; fall back to the target/MCU.
            string? hw = BoardName ?? Target;
            if (hw is not null) s.Append(" on ").Append(Article(hw)).Append(hw);
            return s.ToString();
        }
    }

    private static string Article(string word) =>
        word.Length > 0 && "AEIOU".Contains(char.ToUpperInvariant(word[0])) ? "an " : "a ";
}

public static class FcInfoReader
{
    /// <summary>
    /// Ask the board on <paramref name="portName"/> what it is running. Returns null if it
    /// does not answer, which is not an error: the port may be held by Betaflight
    /// Configurator, or the board may not speak MSP at all.
    ///
    /// Deliberately quick and quiet, because this runs as part of routine diagnosis.
    /// </summary>
    public static FcInfo? Read(string portName)
    {
        SerialPort? port = null;
        try
        {
            // Deliberately NOT setting DtrEnable. Asserting DTR on an STM32 VCP was
            // observed to leave the port unopenable ("access denied") afterwards, with no
            // process holding it, surviving even a pnputil device restart. Only a physical
            // replug cleared it. BootloaderKick has opened this same port many times
            // without DTR and never wedged it. Since this runs during routine diagnosis,
            // wedging the port would break the reboot-to-bootloader step that the whole
            // tool depends on, so the risk is not worth whatever DTR buys.
            port = new SerialPort(portName, 115200, Parity.None, 8, StopBits.One)
            {
                ReadTimeout = 600,
                WriteTimeout = 600,
            };
            port.Open();

            byte[]? variant = Msp.Request(port, Msp.FcVariant);
            if (variant is null) return null;          // not an MSP device, or busy

            string? firmware = Msp.Ascii(variant, 0, Math.Min(4, variant.Length)) switch
            {
                "BTFL" => "Betaflight",
                "INAV" => "INAV",
                "EMUF" => "EmuFlight",
                "CLFL" => "Cleanflight",
                "ARDU" => "ArduPilot",
                var other => other,
            };

            byte[]? v = Msp.Request(port, Msp.FcVersion);
            string? version = v is { Length: >= 3 } ? $"{v[0]}.{v[1]}.{v[2]}" : null;

            var (target, boardName, manufacturer) = ParseBoardInfo(Msp.Request(port, Msp.BoardInfo));

            byte[]? b = Msp.Request(port, Msp.BuildInfo);
            // Fixed layout: 11 bytes date, 8 bytes time, 7 bytes git revision.
            string? built = b is { Length: >= 11 } ? Msp.Ascii(b, 0, 11) : null;

            byte[]? n = Msp.Request(port, Msp.Name);
            string? craft = n is { Length: > 0 } ? Msp.Ascii(n, 0, n.Length) : null;

            return new FcInfo(firmware, version, target, boardName, manufacturer, built, craft);
        }
        catch (Exception)
        {
            return null;    // busy port, permissions, unplugged mid-read
        }
        finally
        {
            try { port?.Close(); port?.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// MSP_BOARD_INFO is a fixed header followed by three length-prefixed strings.
    ///   [0..3]  board identifier      [4..5] hardware revision
    ///   [6]     fc type               [7]    target capabilities
    ///   [8]     target name length, then the name, then the same pattern for
    ///           board name and manufacturer id.
    /// Layout varies across API versions, so every read is bounds checked and anything
    /// unexpected simply yields nulls rather than throwing.
    /// </summary>
    private static (string? target, string? board, string? manufacturer) ParseBoardInfo(byte[]? d)
    {
        if (d is null || d.Length < 9) return (null, null, null);

        int i = 8;
        string? ReadPrefixed()
        {
            if (i >= d.Length) return null;
            int len = d[i++];
            if (len <= 0 || i + len > d.Length) { i = d.Length; return null; }
            string? s = Msp.Ascii(d, i, len);
            i += len;
            return s;
        }

        string? target = ReadPrefixed();
        string? board = ReadPrefixed();
        string? manufacturer = ReadPrefixed();
        return (target, board, manufacturer);
    }
}
