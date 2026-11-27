using System.IO;
using System.Windows;
using CefSharp;
using CefSharp.Wpf;

namespace BlatmetroBrowser;

public partial class App : Application
{
    private bool _cefInitialized;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Blatmetro",
            "Browser");

        try
        {
            Directory.CreateDirectory(appData);
            Directory.CreateDirectory(Path.Combine(appData, "Cache"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            MessageBox.Show(
                $"Не удалось создать папку данных браузера: {ex.Message}",
                "Blatmetro Browser",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
            return;
        }

        var subprocessPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(subprocessPath))
        {
            MessageBox.Show(
                "Не удалось определить путь к исполняемому файлу Blatmetro Browser.",
                "Blatmetro Browser",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
            return;
        }

        var settings = new CefSettings
        {
            CachePath = Path.Combine(appData, "Cache"),
            BrowserSubprocessPath = subprocessPath,
            LogFile = Path.Combine(appData, "cef.log"),
            LogSeverity = LogSeverity.Warning,
            Locale = "ru"
        };

        // Reduce optional Chromium background traffic. Website traffic is unaffected.
        settings.CefCommandLineArgs.Add("disable-background-networking", "1");
        settings.CefCommandLineArgs.Add("disable-component-update", "1");
        settings.CefCommandLineArgs.Add("disable-sync", "1");
        settings.CefCommandLineArgs.Add("disable-domain-reliability", "1");
        settings.CefCommandLineArgs.Add("disable-breakpad", "1");
        settings.CefCommandLineArgs.Add("no-first-run", "1");

        // Work around Chromium's auto-de-elevation early exit on affected Windows/CEF builds.
        settings.CefCommandLineArgs.Add("do-not-de-elevate", string.Empty);

        try
        {
            // Self-hosted child processes are dispatched in Program.Main before WPF starts.
            // CefSharp requires dependency checking to be disabled in this configuration.
            _cefInitialized = Cef.Initialize(
                settings,
                performDependencyCheck: false,
                browserProcessHandler: null);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Не удалось запустить Chromium: {ex.Message}\n\nЖурнал: {settings.LogFile}",
                "Blatmetro Browser",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
            return;
        }

        if (!_cefInitialized)
        {
            MessageBox.Show(
                $"Не удалось запустить Chromium. Код завершения: {Cef.GetExitCode()}\n\nЖурнал: {settings.LogFile}",
                "Blatmetro Browser",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_cefInitialized)
        {
            Cef.Shutdown();
        }

        base.OnExit(e);
    }
}
