using System.IO;
using Microsoft.Win32;

namespace SoundTaskbar.Services;

/// <summary>Per-user autostart via HKCU Run (no admin rights needed).</summary>
public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SoundTaskbar";

    private static string ExePath => Environment.ProcessPath ?? string.Empty;

    private static string Command => $"\"{ExePath}\"";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enabled)
        {
            key.SetValue(ValueName, Command);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    /// <summary>
    /// First launch turns autostart on; later launches only repoint it if the exe was moved.
    /// Running via `dotnet run` (bin\Debug) never touches the registry.
    /// </summary>
    public static void EnsureDefault()
    {
        if (string.IsNullOrEmpty(ExePath) ||
            ExePath.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SoundTaskbar");
        var marker = Path.Combine(dir, "autostart.initialized");
        if (!File.Exists(marker))
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(marker, DateTime.Now.ToString("O"));
            SetEnabled(true);
            return;
        }

        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        if (key?.GetValue(ValueName) is string current &&
            !string.Equals(current, Command, StringComparison.OrdinalIgnoreCase))
        {
            SetEnabled(true);
        }
    }
}
