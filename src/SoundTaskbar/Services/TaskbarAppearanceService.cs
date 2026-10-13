using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using SoundTaskbar.Native;

namespace SoundTaskbar.Services;

public sealed class TaskbarAppearance : IEquatable<TaskbarAppearance>
{
    public required Color Foreground { get; init; }
    public required Color MutedForeground { get; init; }
    public required Color ProgressTrack { get; init; }
    public required Color ProgressFill { get; init; }
    public required bool IsLight { get; init; }
    /// <summary>Sampled tray/taskbar pixel for edge scrims (transparent bar / wallpaper).</summary>
    public Color? TraySample { get; init; }

    public bool Equals(TaskbarAppearance? other) =>
        other is not null &&
        Foreground == other.Foreground &&
        MutedForeground == other.MutedForeground &&
        ProgressTrack == other.ProgressTrack &&
        ProgressFill == other.ProgressFill &&
        IsLight == other.IsLight &&
        TraySample == other.TraySample;

    public override bool Equals(object? obj) => Equals(obj as TaskbarAppearance);

    public override int GetHashCode() =>
        HashCode.Combine(Foreground, MutedForeground, ProgressTrack, ProgressFill, IsLight, TraySample);
}

/// <summary>
/// Panel stays fully transparent so TranslucentTB / system taskbar color shows through.
/// Only foreground contrast is adapted from a tray sample.
/// </summary>
public sealed class TaskbarAppearanceService : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly Window _window;
    private readonly Windows.UI.ViewManagement.UISettings _uiSettings = new();
    private bool _disposed;
    private TaskbarAppearance? _last;

    public TaskbarAppearanceService(Window window)
    {
        _window = window;
        // Theme changes also come from UserPreferenceChanged; poll is for TranslucentTB / wallpaper.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _timer.Tick += (_, _) => Refresh();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public event EventHandler<TaskbarAppearance>? Changed;

    public TaskbarAppearance Current { get; private set; } = Create(isLight: false);

    public void Start()
    {
        Refresh(force: true);
        _timer.Start();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) =>
        _window.Dispatcher.BeginInvoke(() => Refresh(force: true));

    private void Refresh(bool force = false)
    {
        if (_disposed)
        {
            return;
        }

        var appearance = Detect();
        if (!force && _last is not null && _last.Equals(appearance))
        {
            return;
        }

        _last = appearance;
        Current = appearance;
        Changed?.Invoke(this, appearance);
    }

    private TaskbarAppearance Detect()
    {
        var sample = SampleTrayColor();
        var isLight = sample is not null
            ? PerceivedBrightness(sample.Value) > 160
            : IsLightTaskbar();
        return Create(isLight, GetAccentColor(isLight), sample);
    }

    private static TaskbarAppearance Create(bool isLight, Color? accent = null, Color? traySample = null)
    {
        var fill = accent ?? Color.FromRgb(0x00, 0x78, 0xD4);
        return new TaskbarAppearance
        {
            Foreground = isLight ? Color.FromRgb(0x1A, 0x1A, 0x1A) : Color.FromRgb(0xF5, 0xF5, 0xF5),
            MutedForeground = isLight ? Color.FromRgb(0x4A, 0x4A, 0x4A) : Color.FromRgb(0xC8, 0xCD, 0xD6),
            ProgressTrack = isLight ? Color.FromArgb(0x55, 0x00, 0x00, 0x00) : Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF),
            ProgressFill = fill,
            IsLight = isLight,
            TraySample = traySample
        };
    }

    private Color GetAccentColor(bool isLight)
    {
        try
        {
            // Contrast-tuned accent shade for the current taskbar theme.
            var accent = _uiSettings.GetColorValue(
                isLight
                    ? Windows.UI.ViewManagement.UIColorType.AccentDark1
                    : Windows.UI.ViewManagement.UIColorType.AccentLight2);
            if (accent.A == 0)
            {
                accent = _uiSettings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent);
            }

            return Color.FromArgb(accent.A, accent.R, accent.G, accent.B);
        }
        catch
        {
            // fall through to registry
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int abgr)
            {
                return Color.FromRgb(
                    (byte)(abgr & 0xFF),
                    (byte)((abgr >> 8) & 0xFF),
                    (byte)((abgr >> 16) & 0xFF));
            }
        }
        catch
        {
            // ignore
        }

        return Color.FromRgb(0x00, 0x78, 0xD4);
    }

    private static Color? SampleTrayColor()
    {
        var taskbar = NativeMethods.FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero)
        {
            return null;
        }

        var tray = NativeMethods.FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        if (tray == IntPtr.Zero || !NativeMethods.GetWindowRect(tray, out var trayRect) || trayRect.Width < 8)
        {
            if (!NativeMethods.GetWindowRect(taskbar, out var bar))
            {
                return null;
            }

            return SampleScreenColor(bar.Left + 16, bar.Top + Math.Max(2, bar.Height / 2));
        }

        var y = trayRect.Top + Math.Max(2, trayRect.Height / 2);
        return SampleScreenColor(trayRect.Left + 8, y);
    }

    private static Color? SampleScreenColor(int x, int y)
    {
        var hdc = NativeMethods.GetDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var pixel = NativeMethods.GetPixel(hdc, x, y);
            if (pixel == unchecked((int)0xFFFFFFFF))
            {
                return null;
            }

            return Color.FromRgb(
                (byte)(pixel & 0xFF),
                (byte)((pixel >> 8) & 0xFF),
                (byte)((pixel >> 16) & 0xFF));
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, hdc);
        }
    }

    private static double PerceivedBrightness(Color c) =>
        (0.299 * c.R) + (0.587 * c.G) + (0.114 * c.B);

    private static bool IsLightTaskbar()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var system = key?.GetValue("SystemUsesLightTheme");
            return system is int s && s != 0;
        }
        catch
        {
            return false;
        }
    }
}
