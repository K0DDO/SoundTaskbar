using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace SoundTaskbar;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
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
