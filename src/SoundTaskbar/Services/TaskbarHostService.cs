using System.Runtime.InteropServices;
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
/// clipped with SetWindowRgn to the widget band. Survives Win+D / Show Desktop
/// by rejecting hide and restoring if Windows cloaks or minimizes us.
/// </summary>
public sealed class TaskbarHostService : IDisposable
{
    private readonly Window _window;
    private readonly DispatcherTimer _attachTimer;
    private readonly DispatcherTimer _visibilityTimer;
    private readonly DispatcherTimer _regionThrottle;
    private IntPtr _hwnd;
    private bool _hooked;
    private bool _disposed;
    private int _lastWidth = int.MinValue;
    private int _lastHeight = int.MinValue;
    private int _lastRegionLeft = int.MinValue;
    private int _lastRegionWidth = int.MinValue;
    private double _desiredWidgetWidthDip = 310;
    private bool _regionDirty;

    public TaskbarHostService(Window window)
    {
        _window = window;
        _attachTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _attachTimer.Tick += (_, _) => AttachAndPosition(forceBar: false);

        // Show Desktop / minimize-all can hide or cloak us — bring back quickly.
        _visibilityTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _visibilityTimer.Tick += (_, _) => EnsureVisible();

        // Coalesce SetWindowRgn during shell-width animation (was every frame → lag).
        _regionThrottle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(32) };
        _regionThrottle.Tick += (_, _) =>
        {
            if (!_regionDirty)
            {
                return;
            }

            _regionDirty = false;
            AttachAndPosition(forceBar: false);
        };

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    public event EventHandler<TaskbarLayout>? LayoutChanged;

    public double DesiredWidgetWidthDip
    {
        get => _desiredWidgetWidthDip;
        set
        {
            var clamped = Math.Clamp(value, 220, 720);
            if (Math.Abs(_desiredWidgetWidthDip - clamped) < 0.5)
            {
                return;
            }

            _desiredWidgetWidthDip = clamped;
            _regionDirty = true;
            if (!_regionThrottle.IsEnabled)
            {
                _regionThrottle.Start();
            }
        }
    }

    /// <summary>Flush pending region now (end of morph / appear).</summary>
    public void FlushRegion()
    {
        _regionDirty = false;
        AttachAndPosition(forceBar: false);
    }

    public void Start()
    {
        _hwnd = new WindowInteropHelper(_window).EnsureHandle();

        if (!_hooked && PresentationSource.FromVisual(_window) is HwndSource source)
        {
            source.AddHook(WndProc);
            _hooked = true;
        }

        AttachAndPosition(forceBar: true);
        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOWNOACTIVATE);
        _attachTimer.Start();
        _visibilityTimer.Start();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _attachTimer.Stop();
        _visibilityTimer.Stop();
        _regionThrottle.Stop();
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
        _window.Dispatcher.BeginInvoke(() => AttachAndPosition(forceBar: true));

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_GETOBJECT)
        {
            handled = true;
            return IntPtr.Zero;
        }

        if (msg == NativeMethods.WM_WINDOWPOSCHANGING && lParam != IntPtr.Zero)
        {
            var pos = Marshal.PtrToStructure<NativeMethods.WINDOWPOS>(lParam);
            if ((pos.flags & NativeMethods.SWP_HIDEWINDOW) != 0)
            {
                pos.flags &= ~NativeMethods.SWP_HIDEWINDOW;
                pos.flags |= NativeMethods.SWP_SHOWWINDOW;
                Marshal.StructureToPtr(pos, lParam, false);
            }
        }

        if (msg == NativeMethods.WM_SHOWWINDOW && wParam == IntPtr.Zero)
        {
            handled = true;
            _window.Dispatcher.BeginInvoke(() =>
            {
                NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOWNOACTIVATE);
                AttachAndPosition(forceBar: true);
            });
        }

        if (msg == NativeMethods.WM_SIZE && wParam.ToInt32() == NativeMethods.SIZE_MINIMIZED)
        {
            handled = true;
            _window.Dispatcher.BeginInvoke(() =>
            {
                if (_window.WindowState != WindowState.Normal)
                {
                    _window.WindowState = WindowState.Normal;
                }

                NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOWNOACTIVATE);
                AttachAndPosition(forceBar: true);
            });
        }

        return IntPtr.Zero;
    }

    private void EnsureVisible()
    {
        if (_disposed || _hwnd == IntPtr.Zero || !NativeMethods.IsWindow(_hwnd))
        {
            return;
        }

        var parent = NativeMethods.GetParent(_hwnd);
        var taskbar = NativeMethods.FindWindow("Shell_TrayWnd", null);
        if (taskbar != IntPtr.Zero && parent != taskbar)
        {
            AttachAndPosition(forceBar: true);
        }

        var cloaked = IsCloaked(_hwnd);
        var visible = NativeMethods.IsWindowVisible(_hwnd);
        if (visible && !cloaked && _window.Visibility == Visibility.Visible &&
            _window.WindowState == WindowState.Normal)
        {
            return;
        }

        if (_window.WindowState != WindowState.Normal)
        {
            _window.WindowState = WindowState.Normal;
        }

        if (_window.Visibility != Visibility.Visible)
        {
            _window.Visibility = Visibility.Visible;
        }

        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOWNOACTIVATE);
        AttachAndPosition(forceBar: true);
    }

    private static bool IsCloaked(IntPtr hwnd)
    {
        var hr = NativeMethods.DwmGetWindowAttribute(
            hwnd,
            NativeMethods.DWMWA_CLOAKED,
            out var cloaked,
            sizeof(int));
        return hr == 0 && cloaked != 0;
    }

    private void AttachAndPosition(bool forceBar)
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

        var style = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_STYLE);
        style = (style & ~NativeMethods.WS_POPUP & ~NativeMethods.WS_CAPTION & ~NativeMethods.WS_THICKFRAME & ~NativeMethods.WS_SYSMENU)
                | NativeMethods.WS_CHILD
                | NativeMethods.WS_VISIBLE;
        NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_STYLE, style);

        var ex = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
        ex |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
        NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE, ex);

        if (NativeMethods.GetParent(_hwnd) != taskbar)
        {
            NativeMethods.SetParent(_hwnd, taskbar);
            forceBar = true;
        }

        if (!NativeMethods.GetWindowRect(taskbar, out var taskbarRect))
        {
            return;
        }

        var tray = NativeMethods.FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
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

        var barChanged = forceBar || barWidth != _lastWidth || barHeight != _lastHeight;
        var regionChanged = widgetPoint.X != _lastRegionLeft || widgetWidth != _lastRegionWidth;

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

        if (!regionChanged && !barChanged)
        {
            return;
        }

        _lastRegionLeft = widgetPoint.X;
        _lastRegionWidth = widgetWidth;
        _lastWidth = barWidth;
        _lastHeight = barHeight;

        var region = NativeMethods.CreateRectRgn(
            widgetPoint.X,
            0,
            widgetPoint.X + widgetWidth,
            barHeight);
        if (region != IntPtr.Zero)
        {
            if (NativeMethods.SetWindowRgn(_hwnd, region, true) == 0)
            {
                NativeMethods.DeleteObject(region);
            }
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
}
