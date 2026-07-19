using Microsoft.Win32;

namespace FcDriverFixer.Core;

public enum FcState
{
    /// No FC-related USB device seen at all. Most often a charge-only cable.
    NothingDetected,
    /// FC running normally as a virtual COM port (or via a UART bridge). Healthy.
    NormalMode,
    /// FC in DFU/bootloader mode but NOT bound to WinUSB. The thing to fix.
    DfuNeedsFix,
    /// FC in DFU mode and already WinUSB-bound. Ready to flash.
    DfuReady,
    /// VCP present but its serial driver was replaced (often by Zadig) — a self-inflicted
    /// brick where the board no longer appears as a COM port in normal use.
    VcpDriverWrong,
}

/// <summary>What a button will actually do. Kept explicit so the UI/CLI don't re-derive it.</summary>
public enum FixAction
{
    None,
    Rescan,
    PrepareAndFix,   // normal mode: kick to DFU then install WinUSB
    FixDfu,          // DFU present, wrong driver: install WinUSB
    RepairVcp,       // restore the stock serial driver on a Zadig-damaged COM port
    Undo,            // remove the WinUSB package we (or ImpulseRC) installed on DFU
}

public sealed record DiagnosisResult(
    FcState State,
    string Headline,
    string Detail,
    string ActionLabel, FixAction Action,           // primary action ("" = none)
    string? SecondaryLabel, FixAction SecondaryAction, // optional secondary (e.g. Undo)
    LibWdi.UsbDevice? Target,
    string? ComPort);

public static class Diagnoser
{
    private const string UsbSer = "usbser";

    public static DiagnosisResult Run()
    {
        var devices = LibWdi.ListDevices();

        LibWdi.UsbDevice? dfu = null, vcp = null, bridge = null;
        foreach (var d in devices)
        {
            var known = FcCatalog.Match(d.Vid, d.Pid);
            if (known is null) continue;
            switch (known.Value.Role)
            {
                case UsbRole.Dfu when dfu is null: dfu = d; break;
                case UsbRole.Vcp when vcp is null: vcp = d; break;
                case UsbRole.UartBridge when bridge is null: bridge = d; break;
            }
        }

        // DFU present -> highest priority; it's the flashing path.
        if (dfu is { } dfuDev)
        {
            bool winusb = string.Equals(dfuDev.Driver, FcCatalog.WinUsbService,
                                        StringComparison.OrdinalIgnoreCase);
            if (winusb)
            {
                // Ready. Offer Undo only if the binding is a removable OEM package.
                bool removable = DriverStore.HasOemBinding(dfuDev.Vid, dfuDev.Pid);
                return new DiagnosisResult(
                    FcState.DfuReady,
                    "Your board is ready to flash.",
                    "It's in bootloader (DFU) mode with the correct WinUSB driver already bound. " +
                    "Betaflight Configurator can flash firmware now — no fix needed.",
                    ActionLabel: "", Action: FixAction.None,
                    SecondaryLabel: removable ? "Undo (remove installed driver)" : null,
                    SecondaryAction: removable ? FixAction.Undo : FixAction.None,
                    Target: dfuDev, ComPort: null);
            }

            return new DiagnosisResult(
                FcState.DfuNeedsFix,
                "Your board is in DFU mode with the wrong driver.",
                $"Windows bound '{dfuDev.Driver ?? "(none)"}' to the bootloader instead of WinUSB, " +
                "which is why firmware flashing fails. One click installs the correct driver.",
                ActionLabel: "Fix the driver", Action: FixAction.FixDfu,
                SecondaryLabel: null, SecondaryAction: FixAction.None,
                Target: dfuDev, ComPort: null);
        }

        // VCP present. Healthy if the stock serial driver is bound; damaged otherwise.
        if (vcp is { } vcpDev)
        {
            bool healthy = vcpDev.Driver is null ||
                           string.Equals(vcpDev.Driver, UsbSer, StringComparison.OrdinalIgnoreCase);

            if (!healthy && DriverStore.HasOemBinding(vcpDev.Vid, vcpDev.Pid))
            {
                return new DiagnosisResult(
                    FcState.VcpDriverWrong,
                    "Your board's normal-mode driver was replaced.",
                    $"Something (usually Zadig) bound '{vcpDev.Driver}' to the serial port instead of " +
                    "the standard Windows driver, so Betaflight can't open it as a COM port. " +
                    "Click to put the correct driver back.",
                    ActionLabel: "Restore normal-mode driver", Action: FixAction.RepairVcp,
                    SecondaryLabel: null, SecondaryAction: FixAction.None,
                    Target: vcpDev, ComPort: null);
            }

            string? com = ComPortLocator.Find(vcpDev.Vid, vcpDev.Pid);
            return new DiagnosisResult(
                FcState.NormalMode,
                com is null ? "Your board is connected and healthy."
                            : $"Your board is connected on {com}.",
                "It's running normally and Betaflight can read its settings. " +
                "To flash new firmware it needs to switch to bootloader (DFU) mode — " +
                "click below and I'll do that and install the flashing driver.",
                ActionLabel: "Prepare for flashing", Action: FixAction.PrepareAndFix,
                SecondaryLabel: null, SecondaryAction: FixAction.None,
                Target: vcpDev, ComPort: com);
        }

        if (bridge is { } bridgeDev)
        {
            return new DiagnosisResult(
                FcState.NormalMode,
                "A USB-serial adapter is connected.",
                $"Detected {FcCatalog.Match(bridgeDev.Vid, bridgeDev.Pid)?.Name ?? "a UART bridge"}. " +
                "If your flight controller connects through this, it should appear in Betaflight. " +
                "Bootloader flashing on bridge-based boards uses the physical BOOT button.",
                ActionLabel: "", Action: FixAction.None,
                SecondaryLabel: null, SecondaryAction: FixAction.None,
                Target: bridgeDev, ComPort: null);
        }

        return new DiagnosisResult(
            FcState.NothingDetected,
            "No flight controller detected.",
            "Nothing that looks like an FC is on the USB bus. The most common cause is a " +
            "charge-only USB cable — try a different cable that supports data. Also check the " +
            "board has power (LEDs on) and try another USB port.",
            ActionLabel: "Rescan", Action: FixAction.Rescan,
            SecondaryLabel: null, SecondaryAction: FixAction.None,
            Target: null, ComPort: null);
    }
}

/// <summary>
/// Finds the COM port assigned to a USB VID/PID by reading the PnP enum registry.
/// Avoids a WMI/System.Management dependency.
/// </summary>
public static class ComPortLocator
{
    /// <summary>
    /// Returns the COM port of a *currently connected* board.
    ///
    /// The registry keeps an instance per device that has EVER been plugged in, each with
    /// its own remembered PortName. A pilot with two quads therefore has several entries
    /// under the same VID/PID, most of them stale. Returning the first match would hand
    /// back a port belonging to a board that is not plugged in, and the reboot-to-
    /// bootloader command would then be sent to a port that does not exist.
    ///
    /// So we intersect the registry candidates with the ports Windows reports as live.
    /// Returning null when nothing matches is deliberate and much safer than guessing:
    /// the caller falls back to telling the user to use the BOOT button.
    /// </summary>
    public static string? Find(ushort vid, ushort pid)
    {
        string prefix = $"VID_{vid:X4}&PID_{pid:X4}";
        try
        {
            var live = new HashSet<string>(
                System.IO.Ports.SerialPort.GetPortNames(), StringComparer.OrdinalIgnoreCase);
            if (live.Count == 0) return null;

            using RegistryKey? usb = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Enum\USB\{prefix}");
            if (usb is null) return null;

            foreach (string instance in usb.GetSubKeyNames())
            {
                using RegistryKey? dp = usb.OpenSubKey($@"{instance}\Device Parameters");
                if (dp?.GetValue("PortName") is string port && live.Contains(port))
                    return port;
            }
        }
        catch
        {
            // Registry access can fail on locked-down machines; treat as "unknown port".
        }
        return null;
    }
}
