using FcDriverFixer.Core;

// Headless test harness for the Core logic. Also a real deliverable: a scriptable
// CLI for advanced users.
//
//   fcfix diagnose     show what we detect and what we'd do
//   fcfix list         dump all FC-relevant USB devices
//   fcfix kick <COM>   send the bootloader kick to a COM port
//   fcfix fix          diagnose and perform the recommended action (needs admin)
//   fcfix undo         remove the WinUSB driver we installed on the DFU device (needs admin)
//   fcfix repair       restore the stock serial driver on a Zadig-damaged COM port (needs admin)

string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "diagnose";

switch (cmd)
{
    case "list":
        DumpList();
        break;

    case "diagnose":
        Diagnose();
        break;

    case "kick":
        if (args.Length < 2) { Console.Error.WriteLine("usage: fcfix kick <COMx>"); return 2; }
        Console.WriteLine($"Sending bootloader kick to {args[1]}...");
        BootloaderKick.Send(args[1]);
        Console.WriteLine("Sent. Rescan in a moment with 'fcfix diagnose'.");
        break;

    case "fix":
        return RunAction(null);        // use the diagnosis's recommended primary action

    case "undo":
        return RunAction(FixAction.Undo);

    case "repair":
        return RunAction(FixAction.RepairVcp);

    default:
        Console.Error.WriteLine($"unknown command '{cmd}'");
        return 2;
}
return 0;

void DumpList()
{
    Console.WriteLine($"WinUSB installable by this engine: {LibWdi.IsWinUsbSupported()}");
    Console.WriteLine("FC-relevant USB devices:");
    bool any = false;
    foreach (var d in LibWdi.ListDevices())
    {
        var known = FcCatalog.Match(d.Vid, d.Pid);
        if (known is null) continue;
        any = true;
        Console.WriteLine($"  {d.Vid:X4}:{d.Pid:X4}  {known.Value.Role,-11} " +
                          $"driver={d.Driver ?? "(none)",-10} {known.Value.Name}");
    }
    if (!any) Console.WriteLine("  (none)");
}

void Diagnose()
{
    var r = Diagnoser.Run();
    Console.WriteLine($"State   : {r.State}");
    Console.WriteLine($"Headline: {r.Headline}");
    Console.WriteLine($"Detail  : {r.Detail}");
    if (r.ComPort is not null) Console.WriteLine($"COM port: {r.ComPort}");
    if (!string.IsNullOrEmpty(r.ActionLabel)) Console.WriteLine($"Action  : [{r.ActionLabel}] ({r.Action})");
    if (!string.IsNullOrEmpty(r.SecondaryLabel)) Console.WriteLine($"Also    : [{r.SecondaryLabel}] ({r.SecondaryAction})");
}

// Perform an action. If 'forced' is null, use the diagnosis's recommended primary action.
int RunAction(FixAction? forced)
{
    var diag = Diagnoser.Run();
    Console.WriteLine($"Diagnosis: {diag.Headline}");

    FixAction action = forced ?? diag.Action;
    if (action is FixAction.None or FixAction.Rescan)
    {
        Console.WriteLine("Nothing to do for the current state.");
        return 0;
    }

    var fixer = new Fixer(p => Console.WriteLine($"  ... {p.Message}"));
    var outcome = fixer.Perform(action, diag);
    Console.WriteLine(outcome.Success ? $"OK: {outcome.Message}" : $"FAILED: {outcome.Message}");
    return outcome.Success ? 0 : 1;
}
