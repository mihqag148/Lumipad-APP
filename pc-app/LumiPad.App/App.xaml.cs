using System.Diagnostics;
using System.Threading;
using System.Windows;

namespace LumiPad.App;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName =
        @"Local\Lumi3D.LumiMacropad.SingleInstance";

    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 1.17.2+ instances cooperate through a named mutex.
        _singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            name: SingleInstanceMutexName,
            createdNew: out bool createdNew);

        if (!createdNew)
        {
            System.Windows.MessageBox.Show(
                "Lumi Macropad is already running. Open it from the system tray instead of starting another copy.",
                "Lumi Macropad",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // Older builds (1.17.1 and earlier) did not own the mutex and could stay
        // hidden in the tray while holding PIXEL PRO COM ports. Detect those
        // stale copies before opening the new window.
        Process current = Process.GetCurrentProcess();
        Process[] olderCopies = Process.GetProcessesByName(current.ProcessName)
            .Where(p => p.Id != current.Id)
            .ToArray();

        if (olderCopies.Length > 0)
        {
            MessageBoxResult result = System.Windows.MessageBox.Show(
                "An older Lumi Macropad process is still running in the background and may be holding the PIXEL PRO COM port.\n\nClose the older process and continue?",
                "Lumi Macropad",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
            {
                Shutdown();
                return;
            }

            foreach (Process process in olderCopies)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(3000);
                }
                catch
                {
                    // If Windows refuses termination, stop here instead of
                    // opening another copy that will fight for the COM port.
                    System.Windows.MessageBox.Show(
                        "The older Lumi Macropad process could not be closed. Exit it from Task Manager or the system tray, then start LumiPad again.",
                        "Lumi Macropad",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    Shutdown();
                    return;
                }
            }

            Thread.Sleep(350);
        }

        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        catch
        {
        }

        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
        base.OnExit(e);
    }
}
