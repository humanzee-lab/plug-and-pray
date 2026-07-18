namespace FcDriverFixer.Core;

/// <summary>
/// Orchestrates the actual repair, safely. Design rules:
///   - We ONLY install WinUSB on a DFU (bootloader) device, never on a healthy COM port,
///     so the tool cannot break a working normal-mode connection.
///   - Every install is reversible: Undo removes the package we added.
///   - We can also repair a COM port whose driver was replaced by Zadig.
/// </summary>
public sealed class Fixer
{
    /// The name stamped into the driver (visible in Device Manager).
    public const string DriverDesc = "Plug & Pray (WinUSB)";
    public const string VendorName = "STMicroelectronics";

    /// <summary>
    /// A progress update. Step/Total describe real work completed (not a fake marquee),
    /// so the UI can show an honest determinate bar. Total = 0 means "no step info".
    /// </summary>
    public sealed record Progress(string Message, int Step = 0, int Total = 0)
    {
        public double Percent => Total <= 0 ? 0 : Math.Clamp(Step * 100.0 / Total, 0, 100);
    }

    public sealed record FixOutcome(bool Success, string Message);

    private readonly Action<Progress>? _report;
    public Fixer(Action<Progress>? report = null) => _report = report;

    private void Say(string msg, int step = 0, int total = 0) => _report?.Invoke(new Progress(msg, step, total));

    /// <summary>Dispatch on the explicit action chosen from the diagnosis.</summary>
    public FixOutcome Perform(FixAction action, DiagnosisResult diag) => action switch
    {
        FixAction.PrepareAndFix => KickThenInstall(diag),
        FixAction.FixDfu => InstallOnDfu(),
        FixAction.RepairVcp => RepairVcp(diag),
        FixAction.Undo => UndoDfu(diag),
        FixAction.Rescan or FixAction.None => new FixOutcome(true, ""),
        _ => new FixOutcome(false, "Nothing to do."),
    };

    private FixOutcome KickThenInstall(DiagnosisResult diag)
    {
        if (diag.ComPort is null)
            return new FixOutcome(false,
                "The board is in normal mode but I couldn't find its COM port to switch it to DFU. " +
                "Put it in bootloader mode manually (BOOT button) and rescan.");

        // 4 real steps: kick -> re-enumerate -> install -> verify
        Say($"Rebooting the board into bootloader mode via {diag.ComPort}...", 1, 4);
        try
        {
            BootloaderKick.Send(diag.ComPort);
        }
        catch (Exception ex)
        {
            return new FixOutcome(false, $"Couldn't send the bootloader command: {ex.Message}");
        }

        Say("Waiting for the board to re-appear in DFU mode...", 2, 4);
        if (!WaitForDfu(TimeSpan.FromSeconds(6)))
            return new FixOutcome(false,
                "The board didn't switch to DFU mode. Some boards need the physical BOOT button — " +
                "hold it while plugging in USB, then rescan.");

        return InstallOnDfu(stepBase: 2, total: 4);
    }

    private FixOutcome InstallOnDfu(int stepBase = 0, int total = 2)
    {
        var dfu = FindDfu();
        if (dfu is null)
            return new FixOutcome(false, "No DFU device present to fix.");

        if (!LibWdi.IsWinUsbSupported())
            return new FixOutcome(false,
                "This build of the driver engine can't install WinUSB (built without WDK support).");

        string extractDir = Path.Combine(Path.GetTempPath(), "PlugAndPray", "driver");
        Say("Installing the WinUSB driver (this can take up to 30 seconds)...", stepBase + 1, total);

        var r = LibWdi.InstallWinUsb(
            dfu.Value.Vid, dfu.Value.Pid, dfu.Value.Mi, dfu.Value.IsComposite,
            DriverDesc, VendorName, extractDir);

        if (!r.Success)
            return new FixOutcome(false, $"Driver install failed: {r.Message}");

        Say("Verifying...", stepBase + 2, total);
        var after = FindDfu();
        bool ok = after is { Driver: { } drv } &&
                  string.Equals(drv, FcCatalog.WinUsbService, StringComparison.OrdinalIgnoreCase);

        return ok
            ? new FixOutcome(true, "Done. WinUSB is installed — your board is ready to flash in Betaflight.")
            : new FixOutcome(true, "Driver installed. If Betaflight still can't see it, unplug and replug the board.");
    }

    /// <summary>Undo our WinUSB install on the DFU device: remove the OEM package, re-scan.</summary>
    private FixOutcome UndoDfu(DiagnosisResult diag)
    {
        var dfu = diag.Target ?? FindDfu();
        if (dfu is null)
            return new FixOutcome(false, "No DFU device present to undo.");

        var outcome = RestoreStock(dfu.Value.Vid, dfu.Value.Pid, "the flashing driver");
        if (!outcome.Success) return outcome;

        // Clean revert: also remove the self-signed cert the install added to the trust
        // stores, so we leave the machine exactly as we found it.
        Say("Removing the certificate we added...", 3, 3);
        int certs = DriverStore.RemoveLibwdiCert(dfu.Value.Vid, dfu.Value.Pid);

        // After removal there is no inbox DFU driver — the device returns to its
        // unbound state. That's the correct "as we found it" result.
        string certNote = certs > 0 ? $" (and cleaned up {certs} trust-store certificate{(certs == 1 ? "" : "s")})" : "";
        return new FixOutcome(true,
            $"Removed. The WinUSB driver we installed is gone{certNote} and the board is back to how " +
            "Windows had it. Re-run the fix any time to flash again.");
    }

    /// <summary>Repair a COM port whose serial driver was replaced (Zadig damage).</summary>
    private FixOutcome RepairVcp(DiagnosisResult diag)
    {
        var vcp = diag.Target ?? FindByRole(UsbRole.Vcp);
        if (vcp is null)
            return new FixOutcome(false, "No serial device present to repair.");

        var outcome = RestoreStock(vcp.Value.Vid, vcp.Value.Pid, "the replaced serial driver");
        if (!outcome.Success) return outcome;

        // Windows should re-bind the inbox usbser driver on re-scan. Verify.
        Say("Verifying the standard serial driver came back...", 3, 3);
        Thread.Sleep(1500);
        var after = FindByRole(UsbRole.Vcp);
        bool restored = after is { Driver: { } drv } &&
                        string.Equals(drv, "usbser", StringComparison.OrdinalIgnoreCase);

        return restored
            ? new FixOutcome(true, "Fixed. The standard serial driver is back — your board should appear as a COM port in Betaflight again.")
            : new FixOutcome(true, "Removed the wrong driver. Unplug and replug the board; Windows will reinstall the standard serial driver.");
    }

    /// <summary>
    /// Remove EVERY removable OEM driver package bound to a VID/PID, restoring the stock
    /// driver. Iterates because Windows falls back to another matching package after each
    /// removal — a machine with a history of Zadig/ImpulseRC runs can have several. We
    /// remove the bound one, re-scan, see what binds next, and repeat until stock remains.
    /// </summary>
    private FixOutcome RestoreStock(ushort vid, ushort pid, string what, int totalSteps = 3)
    {
        var removed = new List<string>();

        for (int pass = 0; pass < 6; pass++)
        {
            var infs = new List<string>();
            foreach (var b in DriverStore.GetBindings(vid, pid))
                if (b.OemInf is not null && !removed.Contains(b.OemInf) && !infs.Contains(b.OemInf))
                    infs.Add(b.OemInf);

            if (infs.Count == 0) break;   // nothing removable left -> stock reached

            foreach (var inf in infs)
            {
                Say($"Removing {what} ({inf})...", 1, totalSteps);
                var r = DriverStore.DeleteOemDriver(inf);
                if (!r.Ok)
                    return new FixOutcome(false,
                        $"Couldn't remove {inf} (pnputil code {r.Code}). You may need to remove it from Device Manager.");
                removed.Add(inf);
            }

            Say("Re-scanning USB so Windows restores its default driver...", 2, totalSteps);
            DriverStore.ScanDevices();
            Thread.Sleep(1200);
        }

        return removed.Count == 0
            ? new FixOutcome(true, "Already on the stock Windows driver — nothing to undo.")
            : new FixOutcome(true, "");
    }

    private static LibWdi.UsbDevice? FindDfu() => FindByRole(UsbRole.Dfu);

    private static LibWdi.UsbDevice? FindByRole(UsbRole role)
    {
        foreach (var d in LibWdi.ListDevices())
            if (FcCatalog.Match(d.Vid, d.Pid)?.Role == role) return d;
        return null;
    }

    private static bool WaitForDfu(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (FindDfu() is not null) return true;
            Thread.Sleep(400);
        }
        return false;
    }
}
