using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using SoundTaskbar.Services;

namespace SoundTaskbar;

public partial class App : Application
{
    private Mutex? _singleInstance;

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Autostart + manual launch must not stack two widgets on the taskbar.
        _singleInstance = new Mutex(initiallyOwned: true, @"Local\SoundTaskbar.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }

        try
        {
            StartupService.EnsureDefault();
        }
        catch
        {
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ReleaseSingleInstance();
        base.OnExit(e);
    }

    /// <summary>Drop the mutex and spawn a fresh process (fixes stale SMTC / layout). </summary>
    public static void Restart()
    {
        if (Current is App app)
        {
            app.ReleaseSingleInstance();
        }

        var exe = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exe))
        {
            try
            {
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            }
            catch
            {
            }
        }

        Current.Shutdown();
    }

    private void ReleaseSingleInstance()
    {
        if (_singleInstance is null)
        {
            return;
        }

        try
        {
            _singleInstance.ReleaseMutex();
        }
        catch
        {
        }

        _singleInstance.Dispose();
        _singleInstance = null;
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoundTaskbar");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "errors.log"), $"[{DateTime.Now:O}] {e.Exception}{Environment.NewLine}");
        }
        catch
        {
        }

        e.Handled = true;
    }
}
