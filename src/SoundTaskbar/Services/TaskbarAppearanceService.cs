using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using SoundTaskbar.Native;

namespace SoundTaskbar.Services;

public sealed class TaskbarAppearance : IEquatable<TaskbarAppearance>
{
    public required Color PanelColor { get; init; }
    public required Color Foreground { get; init; }
    public required Color MutedForeground { get; init; }
    public required Color ProgressTrack { get; init; }
    public required bool IsLight { get; init; }

    public bool Equals(TaskbarAppearance? other) =>
        other is not null &&
        PanelColor == other.PanelColor &&
        Foreground == other.Foreground &&
        MutedForeground == other.MutedForeground &&
        ProgressTrack == other.ProgressTrack &&
        IsLight == other.IsLight;

    public override bool Equals(object? obj) => Equals(obj as TaskbarAppearance);
    public override int GetHashCode() => HashCode.Combine(PanelColor, Foreground, IsLight);
}

/// <summary>
/// Matches the real taskbar / TranslucentTB color by sampling TrayNotifyWnd pixels
/// (outside our widget). Opaque fill only — child windows cannot use WPF layered transparency.
/// </summary>
public sealed class TaskbarAppearanceService : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly Window _window;
    private bool _disposed;
    private TaskbarAppearance? _last;

    public TaskbarAppearanceService(Window window)
    {
        _window = window;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Refresh();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public event EventHandler<TaskbarAppearance>? Changed;

    public TaskbarAppearance Current { get; private set; } = CreateFallback(isLight: false);

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
        if (!force && _last is not null && ColorDistance(_last.PanelColor, appearance.PanelColor) < 10 &&
            _last.IsLight == appearance.IsLight)
        {
            return;
        }

        _last = appearance;
        Current = appearance;
        Changed?.Invoke(this, appearance);
    }

    private static TaskbarAppearance Detect()
    {
        var themeLight = IsLightTaskbar();
        var sample = SampleTaskbarChrome();
        var isLight = sample is not null
            ? PerceivedBrightness(sample.Value) > 160
            : themeLight;

        var panel = sample is not null
            ? Color.FromRgb(sample.Value.R, sample.Value.G, sample.Value.B)
            : (isLight ? Color.FromRgb(0xF3, 0xF3, 0xF3) : Color.FromRgb(0x2C, 0x2C, 0x2C));

        // Soften TranslucentTB noise a bit without lagging big accent shifts.
        panel = Quantize(panel, step: 6);

        return new TaskbarAppearance
        {
            PanelColor = panel,
            Foreground = isLight ? Color.FromRgb(0x1A, 0x1A, 0x1A) : Color.FromRgb(0xF2, 0xF4, 0xF7),
            MutedForeground = isLight ? Color.FromRgb(0x5A, 0x5A, 0x5A) : Color.FromRgb(0xC0, 0xC6, 0xD0),
            ProgressTrack = isLight ? Color.FromArgb(0x40, 0x00, 0x00, 0x00) : Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF),
            IsLight = isLight
        };
    }

    private static TaskbarAppearance CreateFallback(bool isLight) =>
        new()
        {
            PanelColor = isLight ? Color.FromRgb(0xF3, 0xF3, 0xF3) : Color.FromRgb(0x2C, 0x2C, 0x2C),
            Foreground = isLight ? Color.FromRgb(0x1A, 0x1A, 0x1A) : Color.FromRgb(0xF2, 0xF4, 0xF7),
            MutedForeground = isLight ? Color.FromRgb(0x5A, 0x5A, 0x5A) : Color.FromRgb(0xC0, 0xC6, 0xD0),
            ProgressTrack = isLight ? Color.FromArgb(0x40, 0x00, 0x00, 0x00) : Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF),
            IsLight = isLight
        };

    private static Color? SampleTaskbarChrome()
    {
        var taskbar = NativeMethods.FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero || !NativeMethods.GetWindowRect(taskbar, out var taskbarRect))
        {
            return null;
        }

        // Prefer TrayNotifyWnd: same chrome as the bar, and not covered by our widget.
        var tray = NativeMethods.FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        var colors = new List<Color>();

        if (tray != IntPtr.Zero && NativeMethods.GetWindowRect(tray, out var trayRect) && trayRect.Width > 8)
        {
            var y = trayRect.Top + Math.Max(2, trayRect.Height / 2);
            TryAdd(colors, trayRect.Left + 4, y);
            TryAdd(colors, trayRect.Left + 12, y);
            TryAdd(colors, trayRect.Left + Math.Min(28, trayRect.Width / 3), y);
        }

        // Fallback: far-left of the taskbar (Start area padding), also usually free of our widget.
        if (colors.Count == 0)
        {
            var y = taskbarRect.Top + Math.Max(2, taskbarRect.Height / 2);
            TryAdd(colors, taskbarRect.Left + 8, y);
            TryAdd(colors, taskbarRect.Left + 20, y);
        }

        if (colors.Count == 0)
        {
            return null;
        }

        // If TranslucentTB is clear, nearby pixels can differ a lot — median is stabler than average.
        colors.Sort((a, b) => PerceivedBrightness(a).CompareTo(PerceivedBrightness(b)));
        return colors[colors.Count / 2];
    }

    private static void TryAdd(List<Color> colors, int x, int y)
    {
        var c = SampleScreenColor(x, y);
        if (c is not null)
        {
            colors.Add(c.Value);
        }
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

    private static Color Quantize(Color c, byte step)
    {
        static byte Q(byte v, byte s) => (byte)(Math.Clamp((v + s / 2) / s * s, 0, 255));
        return Color.FromRgb(Q(c.R, step), Q(c.G, step), Q(c.B, step));
    }

    private static double PerceivedBrightness(Color c) =>
        (0.299 * c.R) + (0.587 * c.G) + (0.114 * c.B);

    private static double ColorDistance(Color a, Color b)
    {
        var dr = a.R - b.R;
        var dg = a.G - b.G;
        var db = a.B - b.B;
        return Math.Sqrt(dr * dr + dg * dg + db * db);
    }

    private static bool IsLightTaskbar()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var system = key?.GetValue("SystemUsesLightTheme");
            if (system is int s)
            {
                return s != 0;
            }

            var apps = key?.GetValue("AppsUseLightTheme");
            return apps is int a && a != 0;
        }
        catch
        {
            return false;
        }
    }

}
