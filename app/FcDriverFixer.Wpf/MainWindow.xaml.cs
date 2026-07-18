using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using FcDriverFixer.Core;

namespace FcDriverFixer.Wpf;

public partial class MainWindow : Window
{
    private DiagnosisResult? _current;
    private bool _busy;

    // Status palette, mirrored from App.xaml so we can drive ring/glow/glyph from code.
    private static readonly Color Accent = Color.FromRgb(0x38, 0xBD, 0xF8);
    private static readonly Color Ready = Color.FromRgb(0x34, 0xD3, 0x99);
    private static readonly Color Warn = Color.FromRgb(0xFB, 0xBF, 0x24);
    private static readonly Color Danger = Color.FromRgb(0xF8, 0x71, 0x71);

    public MainWindow()
    {
        InitializeComponent();

        MinBtn.Click += (_, _) => WindowState = WindowState.Minimized;
        CloseBtn.Click += (_, _) => Close();
        PrimaryBtn.Click += async (_, _) => { if (_current is not null) await RunAction(_current.Action); };
        UndoBtn.Click += async (_, _) => { if (_current is not null) await RunAction(_current.SecondaryAction); };
        RescanBtn.Click += async (_, _) => await Diagnose();

        Loaded += async (_, _) =>
        {
            // Dev-only: --preview:<FcState> renders a representative card for that state so
            // the design can be checked without staging real hardware conditions.
            var preview = PreviewState();
            if (preview is not null)
            {
                _current = preview;
                Render(preview);

                // --preview:working also shows the progress UI mid-operation.
                if (Environment.GetCommandLineArgs().Any(a =>
                        a.Equals("--preview:working", StringComparison.OrdinalIgnoreCase)))
                {
                    ProgressPanel.Visibility = Visibility.Visible;
                    StepText.Text = "Installing the WinUSB driver (this can take up to 30 seconds)…";
                    PrimaryBtn.IsEnabled = false;
                    AnimateBar(75);
                }
                return;
            }

            await Diagnose();
        };
    }

    /// <summary>Dev-only design preview. Returns null in normal use.</summary>
    private static DiagnosisResult? PreviewState()
    {
        string? arg = Environment.GetCommandLineArgs()
            .FirstOrDefault(a => a.StartsWith("--preview:", StringComparison.OrdinalIgnoreCase));
        if (arg is null) return null;

        return arg["--preview:".Length..].ToLowerInvariant() switch
        {
            "working" or "normalmode" => new DiagnosisResult(FcState.NormalMode,
                "Your board is connected on COM6.",
                "It's running normally and Betaflight can read its settings. To flash new firmware " +
                "it needs to switch to bootloader (DFU) mode — click below and I'll do that and " +
                "install the flashing driver.",
                "Prepare for flashing", FixAction.PrepareAndFix, null, FixAction.None, null, "COM6"),

            "dfuneedsfix" => new DiagnosisResult(FcState.DfuNeedsFix,
                "Your board is in DFU mode with the wrong driver.",
                "Windows bound '(none)' to the bootloader instead of WinUSB, which is why firmware " +
                "flashing fails. One click installs the correct driver.",
                "Fix the driver", FixAction.FixDfu, null, FixAction.None, null, null),

            "vcpdriverwrong" => new DiagnosisResult(FcState.VcpDriverWrong,
                "Your board's normal-mode driver was replaced.",
                "Something (usually Zadig) bound 'WinUSB' to the serial port instead of the standard " +
                "Windows driver, so Betaflight can't open it as a COM port. Click to put the correct " +
                "driver back.",
                "Restore normal-mode driver", FixAction.RepairVcp, null, FixAction.None, null, null),

            "nothingdetected" => new DiagnosisResult(FcState.NothingDetected,
                "No flight controller detected.",
                "Nothing that looks like an FC is on the USB bus. The most common cause is a " +
                "charge-only USB cable — try a different cable that supports data. Also check the " +
                "board has power (LEDs on) and try another USB port.",
                "Rescan", FixAction.Rescan, null, FixAction.None, null, null),

            _ => new DiagnosisResult(FcState.DfuReady,
                "Your board is ready to flash.",
                "It's in bootloader (DFU) mode with the correct WinUSB driver already bound. " +
                "Betaflight Configurator can flash firmware now — no fix needed.",
                "", FixAction.None, "Undo (remove installed driver)", FixAction.Undo, null, null),
        };
    }

    // ---------------------------------------------------------------- diagnose

    private async Task Diagnose()
    {
        if (_busy) return;
        SetBusy(true, "Scanning USB…");
        try
        {
            var result = await Task.Run(Diagnoser.Run);
            _current = result;
            Render(result);
        }
        catch (Exception ex)
        {
            Headline.Text = "Something went wrong.";
            Detail.Text = ex.Message;
            Paint(Danger, "✕");
        }
        finally
        {
            SetBusy(false);
            ProgressPanel.Visibility = Visibility.Collapsed;
        }
    }

    // ---------------------------------------------------------------- actions

    private async Task RunAction(FixAction action)
    {
        if (_busy || _current is null || action == FixAction.None) return;
        if (action == FixAction.Rescan) { await Diagnose(); return; }

        SetBusy(true, "Starting…");
        ProgressPanel.Visibility = Visibility.Visible;
        Bar.Value = 0;

        try
        {
            var diag = _current;
            var outcome = await Task.Run(() =>
            {
                var fixer = new Fixer(p => Dispatcher.Invoke(() => ShowProgress(p)));
                return fixer.Perform(action, diag);
            });

            AnimateBar(100);
            if (!string.IsNullOrEmpty(outcome.Message))
                StepText.Text = outcome.Message;

            await Task.Delay(700);          // let the completed bar register visually
            await Diagnose();               // re-scan so the card shows verified reality
        }
        catch (Exception ex)
        {
            StepText.Text = "Error: " + ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ShowProgress(Fixer.Progress p)
    {
        StepText.Text = p.Message;
        if (p.Total > 0) AnimateBar(p.Percent);
    }

    /// <summary>Glide the bar to a value so movement is visible rather than jumping.</summary>
    private void AnimateBar(double to)
    {
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(450))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Bar.BeginAnimation(System.Windows.Controls.ProgressBar.ValueProperty, anim);
    }

    // ---------------------------------------------------------------- rendering

    private void Render(DiagnosisResult r)
    {
        (Color colour, string glyph) = r.State switch
        {
            FcState.DfuReady => (Ready, "✓"),          // check
            FcState.NormalMode => (Accent, "⚡"),        // bolt: we'll zap it to DFU
            FcState.DfuNeedsFix => (Warn, "!"),         // !
            FcState.VcpDriverWrong => (Warn, "!"),
            _ => (Danger, "✕"),                          // x
        };
        Paint(colour, glyph);

        Headline.Text = r.Headline;
        Detail.Text = r.Detail;

        bool hasPrimary = !string.IsNullOrEmpty(r.ActionLabel);
        PrimaryBtn.Visibility = hasPrimary ? Visibility.Visible : Visibility.Collapsed;
        if (hasPrimary)
        {
            PrimaryBtn.Content = r.ActionLabel;
            PrimaryBtn.IsEnabled = true;
        }

        bool hasSecondary = !string.IsNullOrEmpty(r.SecondaryLabel);
        UndoBtn.Visibility = hasSecondary ? Visibility.Visible : Visibility.Collapsed;
        if (hasSecondary) UndoBtn.Content = r.SecondaryLabel;
    }

    /// <summary>Recolour the status ring, its glow and the glyph.</summary>
    private void Paint(Color c, string glyph)
    {
        Ring.Stroke = new SolidColorBrush(c);
        Glyph.Text = glyph;
        Glyph.Foreground = new SolidColorBrush(c);
        GlowStop.Color = c;
    }

    private void SetBusy(bool busy, string? note = null)
    {
        _busy = busy;
        PrimaryBtn.IsEnabled = !busy && _current is not null && !string.IsNullOrEmpty(_current.ActionLabel);
        UndoBtn.IsEnabled = !busy;
        RescanBtn.IsEnabled = !busy;
        if (note is not null) StepText.Text = note;
    }
}
