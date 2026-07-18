using System.Windows;

namespace FcDriverFixer.Wpf;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Touch the native engine early so a missing/incompatible libwdi.dll fails with a
        // clear message instead of an obscure crash deeper in the flow.
        try
        {
            Core.LibWdi.SetLogLevel(Core.LibWdi.WDI_LOG_LEVEL_WARNING);
        }
        catch (DllNotFoundException)
        {
            MessageBox.Show("libwdi.dll was not found next to the application. The download may be incomplete.",
                "Plug & Pray", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        catch (BadImageFormatException)
        {
            MessageBox.Show("libwdi.dll is the wrong architecture (64-bit required).",
                "Plug & Pray", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        base.OnStartup(e);
    }
}
