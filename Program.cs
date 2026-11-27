using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace BlatmetroBrowser;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // CEF child processes (renderer, GPU, utility) must be dispatched before
        // WPF starts and before the single-instance mutex is created.
        var subprocessExitCode = CefSharp.BrowserSubprocess.SelfHost.Main(args);
        if (subprocessExitCode >= 0)
        {
            return subprocessExitCode;
        }

        // Only the main browser process is protected by the single-instance guard.
        using var instanceMutex = new Mutex(
            initiallyOwned: true,
            name: @"Local\BlatmetroBrowser.SingleInstance",
            createdNew: out var createdNew);

        if (!createdNew)
        {
            MessageBox.Show(
                "Blatmetro Browser уже запущен. Продолжайте работу в открытом окне браузера.",
                "Blatmetro Browser",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            ActivateExistingInstance();
            return 0;
        }

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    private static void ActivateExistingInstance()
    {
        using var current = Process.GetCurrentProcess();
        string? currentPath;
        try
        {
            currentPath = current.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return;
        }

        foreach (var process in Process.GetProcessesByName(current.ProcessName))
        {
            using (process)
            {
                if (process.Id == current.Id) continue;
                try
                {
                    var processPath = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(currentPath) ||
                        !string.Equals(processPath, currentPath, StringComparison.OrdinalIgnoreCase)) continue;

                    var handle = process.MainWindowHandle;
                    if (handle == IntPtr.Zero) continue;
                    ShowWindow(handle, 9); // SW_RESTORE
                    SetForegroundWindow(handle);
                    return;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // A process may exit while the second instance is locating its window.
                }
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr windowHandle, int command);
}
