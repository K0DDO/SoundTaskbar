using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using SoundTaskbar.Native;

namespace SoundTaskbar.Services;

public readonly record struct TaskbarLayout(
    double DpiScale,
    double WindowWidth,
    double WindowHeight,
    double WidgetLeft,
    double WidgetWidth,
    double TrayWidth);

/// <summary>
/// FluentFlyout-style host: child of Shell_TrayWnd sized to the full taskbar,
/// clipped with SetWindowRgn to the widget band. Survives Win+D and stays visible
/// on Win11 where a small child window is covered by the XAML taskbar layer.
/// </summary>
public sealed class TaskbarHostService : IDisposable
{
    private readonly Window _window;
    private readonly DispatcherTimer _timer;
    private IntPtr _hwnd;
    private bool _hooked;
    private bool _disposed;
    private int _lastWidth = int.MinValue;
    private int _lastHeight = int.MinValue;
    private int _lastRegionLeft = int.MinValue;
    private int _lastRegionWidth = int.MinValue;
    private int _lastRevealWidth = int.MinValue;
    private double _desiredWidgetWidthDip = 310;
    private double _revealFraction = 1;
    private IntPtr _taskbar;
    private IntPtr _tray;

    public TaskbarHostService(Window window)
    {
        _window = window;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _timer.Tick += (_, _) => AttachAndPosition();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    public event EventHandler<TaskbarLayout>? LayoutChanged;

    public double DesiredWidgetWidthDip
    {
        get => _desiredWidgetWidthDip;
        set
        {
            // Sub-pixel threshold: avoid Attach spam during shell width animation.
            var clamped = Math.Clamp(value, 220, 720);
            if (Math.Abs(_desiredWidgetWidthDip - clamped) < 0.25)
            {
                return;
            }

            _desiredWidgetWidthDip = clamped;
            UpdateRegion();
        }
    }

    /// <summary>
    /// 0 = fully hidden (empty region), 1 = whole widget band. The visible band is
    /// anchored at the tray edge so hide/show wipes toward the tray; layout is untouched.
    /// </summary>
    public double RevealFraction
    {
        get => _revealFraction;
        set
        {
            var clamped = Math.Clamp(value, 0, 1);
            if (Math.Abs(_revealFraction - clamped) < 0.0005)
            {
                return;
            }

            _revealFraction = clamped;
            UpdateRegion();
        }
    }

    /// <summary>Re-apply region after a morph ends (same as AttachAndPosition).</summary>
    public void FlushRegion() => AttachAndPosition();

    public void Start()
    {
        _hwnd = new WindowInteropHelper(_window).EnsureHandle();

        if (!_hooked && PresentationSource.FromVisual(_window) is HwndSource source)
        {
            source.AddHook(WndProc);
            _hooked = true;
        }

        AttachAndPosition();
        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOWNOACTIVATE);
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
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;

        if (_hwnd != IntPtr.Zero && NativeMethods.IsWindow(_hwnd))
        {
            NativeMethods.SetWindowRgn(_hwnd, IntPtr.Zero, true);
            NativeMethods.SetParent(_hwnd, IntPtr.Zero);
            var style = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_STYLE);
            style = (style & ~NativeMethods.WS_CHILD) | NativeMethods.WS_POPUP;
            NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_STYLE, style);
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        _window.Dispatcher.BeginInvoke(AttachAndPosition);

    private void AttachAndPosition()
    {
        if (_disposed || _hwnd == IntPtr.Zero || !NativeMethods.IsWindow(_hwnd))
        {
            return;
        }

        var taskbar = NativeMethods.FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero || !NativeMethods.IsWindow(taskbar))
        {
            return;
        }

        // SetWindowLong always sends WM_STYLECHANGING/CHANGED ? only write on real change.
        var oldStyle = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_STYLE);
        var style = (oldStyle & ~NativeMethods.WS_POPUP & ~NativeMethods.WS_CAPTION & ~NativeMethods.WS_THICKFRAME & ~NativeMethods.WS_SYSMENU)
                    | NativeMethods.WS_CHILD
                    | NativeMethods.WS_VISIBLE;
        if (style != oldStyle)
        {
            NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_STYLE, style);
        }

        var oldEx = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
        var ex = oldEx | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
        if (ex != oldEx)
        {
            NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE, ex);
        }

        if (NativeMethods.GetParent(_hwnd) != taskbar)
        {
            NativeMethods.SetParent(_hwnd, taskbar);
        }

        _taskbar = taskbar;
        _tray = NativeMethods.FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        Position(taskbar, _tray);
    }

    /// <summary>Per-frame path for width / reveal tweens: geometry + region only.</summary>
    private void UpdateRegion()
    {
        if (_disposed || _hwnd == IntPtr.Zero)
        {
            return;
        }

        if (_taskbar == IntPtr.Zero || !NativeMethods.IsWindow(_taskbar) || NativeMethods.GetParent(_hwnd) != _taskbar)
        {
            AttachAndPosition();
            return;
        }

        Position(_taskbar, _tray);
    }

    private void Position(IntPtr taskbar, IntPtr tray)
    {
        if (!NativeMethods.GetWindowRect(taskbar, out var taskbarRect))
        {
            return;
        }

        NativeMethods.RECT trayRect;
        if (tray == IntPtr.Zero || !NativeMethods.GetWindowRect(tray, out trayRect))
        {
            trayRect = taskbarRect;
            trayRect.Left = taskbarRect.Right - Scale(taskbar, 160);
        }

        var dpi = NativeMethods.GetDpiForWindow(taskbar);
        var scale = dpi <= 0 ? 1.0 : dpi / 96.0;
        var gap = (int)Math.Round(6 * scale);
        var desiredWidgetWidth = (int)Math.Round(_desiredWidgetWidthDip * scale);

        var barWidth = taskbarRect.Width;
        var barHeight = taskbarRect.Height;
        var isHorizontal = barWidth >= barHeight;
        if (!isHorizontal)
        {
            desiredWidgetWidth = barWidth;
        }

        var available = Math.Max(Scale(taskbar, 180), trayRect.Left - taskbarRect.Left - gap * 2);
        var widgetWidth = Math.Clamp(desiredWidgetWidth, Scale(taskbar, 180), available);
        var widgetScreenLeft = trayRect.Left - gap - widgetWidth;
        if (widgetScreenLeft < taskbarRect.Left + gap)
        {
            widgetScreenLeft = taskbarRect.Left + gap;
        }

        var topLeft = new NativeMethods.POINT { X = taskbarRect.Left, Y = taskbarRect.Top };
        NativeMethods.ScreenToClient(taskbar, ref topLeft);

        var widgetPoint = new NativeMethods.POINT { X = widgetScreenLeft, Y = taskbarRect.Top };
        NativeMethods.ScreenToClient(taskbar, ref widgetPoint);

        var trayWidthPx = Math.Max(0, taskbarRect.Right - trayRect.Left);

        var barChanged = barWidth != _lastWidth || barHeight != _lastHeight;
        var regionChanged = widgetPoint.X != _lastRegionLeft || widgetWidth != _lastRegionWidth;
        var revealWidth = (int)Math.Round(widgetWidth * _revealFraction);
        var revealChanged = revealWidth != _lastRevealWidth;

        // SetWindowPos only when the taskbar bar itself moves/resizes — not every shell-width frame.
        if (barChanged || _lastWidth == int.MinValue)
        {
            NativeMethods.SetWindowPos(
                _hwnd,
                NativeMethods.HWND_TOP,
                topLeft.X,
                topLeft.Y,
                barWidth,
                barHeight,
                NativeMethods.SWP_NOACTIVATE |
                NativeMethods.SWP_SHOWWINDOW |
                NativeMethods.SWP_FRAMECHANGED |
                NativeMethods.SWP_ASYNCWINDOWPOS);
        }

        if (!regionChanged && !barChanged && !revealChanged)
        {
            return;
        }

        _lastRegionLeft = widgetPoint.X;
        _lastRegionWidth = widgetWidth;
        _lastRevealWidth = revealWidth;
        _lastWidth = barWidth;
        _lastHeight = barHeight;

        var regionRight = widgetPoint.X + widgetWidth;
        var region = revealWidth <= 0
            ? NativeMethods.CreateRectRgn(0, 0, 0, 0)
            : NativeMethods.CreateRectRgn(regionRight - revealWidth, 0, regionRight, barHeight);
        if (region != IntPtr.Zero)
        {
            if (NativeMethods.SetWindowRgn(_hwnd, region, true) == 0)
            {
                NativeMethods.DeleteObject(region);
            }
        }

        if (!regionChanged && !barChanged)
        {
            return;
        }

        var layout = new TaskbarLayout(
            DpiScale: scale,
            WindowWidth: barWidth / scale,
            WindowHeight: barHeight / scale,
            WidgetLeft: widgetPoint.X / scale,
            WidgetWidth: widgetWidth / scale,
            TrayWidth: trayWidthPx / scale);

        if (Math.Abs(_window.Width - layout.WindowWidth) > 0.5)
        {
            _window.Width = layout.WindowWidth;
        }

        if (Math.Abs(_window.Height - layout.WindowHeight) > 0.5)
        {
            _window.Height = layout.WindowHeight;
        }

        LayoutChanged?.Invoke(this, layout);
    }

    private static int Scale(IntPtr hwnd, int value)
    {
        var dpi = NativeMethods.GetDpiForWindow(hwnd);
        var scale = dpi <= 0 ? 1.0 : dpi / 96.0;
        return (int)Math.Round(value * scale);
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_GETOBJECT)
        {
            handled = true;
            return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }
}
