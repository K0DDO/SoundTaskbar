using System.Diagnostics;
using System.Runtime.InteropServices;
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
    public required bool UseBlur { get; init; }
    public required bool IsLight { get; init; }

    public bool Equals(TaskbarAppearance? other) =>
        other is not null &&
        PanelColor == other.PanelColor &&
        Foreground == other.Foreground &&
        MutedForeground == other.MutedForeground &&
        ProgressTrack == other.ProgressTrack &&
        UseBlur == other.UseBlur &&
        IsLight == other.IsLight;

    public override bool Equals(object? obj) => Equals(obj as TaskbarAppearance);
    public override int GetHashCode() => HashCode.Combine(PanelColor, Foreground, UseBlur, IsLight);
}

/// <summary>
/// Tracks taskbar look including TranslucentTB dynamic accents.
/// Samples the taskbar strip and only reapplies composition when the look actually changes.
/// </summary>
public sealed class TaskbarAppearanceService : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly Window _window;
    private IntPtr _hwnd;
    private bool _disposed;
    private TaskbarAppearance? _last;

    public TaskbarAppearanceService(Window window)
    {
        _window = window;
        // TranslucentTB can restyle the bar often — poll, but apply only on real deltas.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _timer.Tick += (_, _) => Refresh();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public event EventHandler<TaskbarAppearance>? Changed;

    public TaskbarAppearance Current { get; private set; } = CreateFallback(isLight: false, transparent: true);

    public void Start()
    {
        _hwnd = new System.Windows.Interop.WindowInteropHelper(_window).EnsureHandle();
        Refresh();
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
        _window.Dispatcher.BeginInvoke(Refresh);

    private void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        var appearance = Detect();
        if (_last is not null && _last.Equals(appearance))
        {
            return;
        }

        ApplyBlur(_hwnd, appearance.UseBlur, appearance.PanelColor);
        _last = appearance;
        Current = appearance;
        Changed?.Invoke(this, appearance);
    }

    private TaskbarAppearance Detect()
    {
        var themeLight = IsLightTaskbar();
        var registryTransparent = IsTransparencyEnabled();
        var translucentTb = IsTranslucentTbRunning();
        var sample = SampleTaskbar();

        var transparent = registryTransparent || translucentTb || sample.LooksTransparent;
        var isLight = sample.HasColor
            ? PerceivedBrightness(sample.Average) > 160
            : themeLight;

        Color panel;
        if (transparent)
        {
            if (sample.HasColor)
            {
                var a = (byte)(translucentTb ? 0x40 : 0x55);
                panel = Color.FromArgb(a, sample.Average.R, sample.Average.G, sample.Average.B);
            }
            else
            {
                panel = isLight
                    ? Color.FromArgb(0x55, 0xF3, 0xF3, 0xF3)
                    : Color.FromArgb(0x55, 0x20, 0x20, 0x20);
            }
        }
        else if (sample.HasColor)
        {
            panel = Color.FromArgb(0xFF, sample.Average.R, sample.Average.G, sample.Average.B);
        }
        else
        {
            panel = isLight
                ? Color.FromRgb(0xF3, 0xF3, 0xF3)
                : Color.FromRgb(0x20, 0x20, 0x20);
        }

        panel = Quantize(panel, step: 8);

        return new TaskbarAppearance
        {
            PanelColor = panel,
            Foreground = isLight ? Color.FromRgb(0x1A, 0x1A, 0x1A) : Color.FromRgb(0xF2, 0xF4, 0xF7),
            MutedForeground = isLight ? Color.FromRgb(0x5A, 0x5A, 0x5A) : Color.FromRgb(0xC0, 0xC6, 0xD0),
            ProgressTrack = isLight ? Color.FromArgb(0x55, 0x00, 0x00, 0x00) : Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF),
            UseBlur = transparent,
            IsLight = isLight
        };
    }

    private static TaskbarAppearance CreateFallback(bool isLight, bool transparent) =>
        new()
        {
            PanelColor = transparent
                ? (isLight ? Color.FromArgb(0x55, 0xF3, 0xF3, 0xF3) : Color.FromArgb(0x55, 0x20, 0x20, 0x20))
                : (isLight ? Color.FromRgb(0xF3, 0xF3, 0xF3) : Color.FromRgb(0x20, 0x20, 0x20)),
            Foreground = isLight ? Color.FromRgb(0x1A, 0x1A, 0x1A) : Color.FromRgb(0xF2, 0xF4, 0xF7),
            MutedForeground = isLight ? Color.FromRgb(0x5A, 0x5A, 0x5A) : Color.FromRgb(0xC0, 0xC6, 0xD0),
            ProgressTrack = isLight ? Color.FromArgb(0x55, 0x00, 0x00, 0x00) : Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF),
            UseBlur = transparent,
            IsLight = isLight
        };

    private static bool IsTranslucentTbRunning()
    {
        try
        {
            return Process.GetProcessesByName("TranslucentTB").Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsTransparencyEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("EnableTransparency");
            return value is int i ? i != 0 : true;
        }
        catch
        {
            return true;
        }
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

    private SampleResult SampleTaskbar()
    {
        var taskbar = NativeMethods.FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero || !NativeMethods.GetWindowRect(taskbar, out var rect))
        {
            return SampleResult.Empty;
        }

        // Sample on the left side of the bar so we don't read our own overlay (near the tray).
        var y = rect.Top + Math.Max(2, rect.Height / 2);
        var points = new (int X, int Y)[]
        {
            (rect.Left + 24, y),
            (rect.Left + 48, y),
            (rect.Left + Math.Min(120, Math.Max(60, rect.Width / 8)), y)
        };

        var colors = new List<Color>(points.Length);
        foreach (var (x, py) in points)
        {
            if (x >= rect.Right)
            {
                continue;
            }

            var c = SampleScreenColor(x, py);
            if (c is not null)
            {
                colors.Add(c.Value);
            }
        }

        if (colors.Count == 0)
        {
            return SampleResult.Empty;
        }

        double r = 0, g = 0, b = 0;
        foreach (var c in colors)
        {
            r += c.R;
            g += c.G;
            b += c.B;
        }

        var avg = Color.FromRgb(
            (byte)Math.Round(r / colors.Count),
            (byte)Math.Round(g / colors.Count),
            (byte)Math.Round(b / colors.Count));

        double variance = 0;
        foreach (var c in colors)
        {
            variance += Math.Abs(PerceivedBrightness(c) - PerceivedBrightness(avg));
        }

        variance /= colors.Count;
        // High variance across nearby pixels ≈ see-through taskbar (wallpaper/TranslucentTB).
        var looksTransparent = variance > 12;

        return new SampleResult(avg, looksTransparent, true);
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

            var r = (byte)(pixel & 0xFF);
            var g = (byte)((pixel >> 8) & 0xFF);
            var b = (byte)((pixel >> 16) & 0xFF);
            return Color.FromRgb(r, g, b);
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, hdc);
        }
    }

    private static Color Quantize(Color c, byte step)
    {
        static byte Q(byte v, byte s) => (byte)(Math.Clamp((v + s / 2) / s * s, 0, 255));
        return Color.FromArgb(c.A, Q(c.R, step), Q(c.G, step), Q(c.B, step));
    }

    private static double PerceivedBrightness(Color c) =>
        (0.299 * c.R) + (0.587 * c.G) + (0.114 * c.B);

    private static void ApplyBlur(IntPtr hwnd, bool enable, Color tint)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var accent = new AccentPolicy
        {
            AccentState = enable
                ? AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND
                : AccentState.ACCENT_DISABLED,
            AccentFlags = 2,
            GradientColor = enable
                ? ((uint)tint.A << 24) | ((uint)tint.B << 16) | ((uint)tint.G << 8) | tint.R
                : 0
        };

        var accentSize = Marshal.SizeOf<AccentPolicy>();
        var accentPtr = Marshal.AllocHGlobal(accentSize);
        try
        {
            Marshal.StructureToPtr(accent, accentPtr, false);
            var data = new NativeMethods.WindowCompositionAttributeData
            {
                Attribute = 19,
                Data = accentPtr,
                SizeOfData = accentSize
            };
            NativeMethods.SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(accentPtr);
        }
    }

    private readonly record struct SampleResult(Color Average, bool LooksTransparent, bool HasColor)
    {
        public static SampleResult Empty => new(default, false, false);
    }

    private enum AccentState
    {
        ACCENT_DISABLED = 0,
        ACCENT_ENABLE_GRADIENT = 1,
        ACCENT_ENABLE_TRANSPARENTGRADIENT = 2,
        ACCENT_ENABLE_BLURBEHIND = 3,
        ACCENT_ENABLE_ACRYLICBLURBEHIND = 4,
        ACCENT_ENABLE_HOSTBACKDROP = 5
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public AccentState AccentState;
        public int AccentFlags;
        public uint GradientColor;
        public int AnimationId;
    }
}
