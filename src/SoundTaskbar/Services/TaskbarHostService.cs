using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using SoundTaskbar.Native;

namespace SoundTaskbar.Services;

/// <summary>
/// Hosts the widget as a real child of Shell_TrayWnd so Win+D / Show Desktop cannot hide it.
/// Requires AllowsTransparency=false (layered WPF children do not paint).
/// </summary>
public sealed class TaskbarHostService : IDisposable
{
    private readonly Window _window;
    private readonly DispatcherTimer _timer;
    private IntPtr _hwnd;
    private bool _hooked;
    private bool _disposed;
    private int _lastX = int.MinValue;
    private int _lastY = int.MinValue;
    private int _lastWidth = int.MinValue;
    private int _lastHeight = int.MinValue;

    public TaskbarHostService(Window window)
    {
        _window = window;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
        _timer.Tick += (_, _) => AttachAndPosition();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

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

        // Child of the taskbar = survives Show Desktop. Clear layered/popup styles that break painting.
        var style = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_STYLE);
        style = (style & ~NativeMethods.WS_POPUP & ~NativeMethods.WS_CAPTION & ~NativeMethods.WS_THICKFRAME & ~NativeMethods.WS_SYSMENU)
                | NativeMethods.WS_CHILD
                | NativeMethods.WS_VISIBLE;
        NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_STYLE, style);

        var ex = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
        ex |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
        ex &= ~NativeMethods.WS_EX_LAYERED;
        ex &= ~NativeMethods.WS_EX_TOPMOST;
        NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE, ex);

        if (NativeMethods.GetParent(_hwnd) != taskbar)
        {
            NativeMethods.SetParent(_hwnd, taskbar);
        }

        PositionInTaskbar(taskbar);
    }

    private void PositionInTaskbar(IntPtr taskbar)
    {
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
        var desiredWidth = (int)Math.Round(380 * scale);

        var isHorizontal = taskbarRect.Width >= taskbarRect.Height;
        int screenX;
        int screenY;
        int width;
        int height;

        if (isHorizontal)
        {
            height = taskbarRect.Height;
            var available = trayRect.Left - taskbarRect.Left - gap * 2;
            width = Math.Clamp(desiredWidth, Scale(taskbar, 220), Math.Max(Scale(taskbar, 220), available));
            screenX = trayRect.Left - gap - width;
            if (screenX < taskbarRect.Left + gap)
            {
                screenX = taskbarRect.Left + gap;
            }

            screenY = taskbarRect.Top;
        }
        else
        {
            width = taskbarRect.Width;
            height = (int)Math.Round(72 * scale);
            screenX = taskbarRect.Left;
            screenY = trayRect.Top - gap - height;
            if (screenY < taskbarRect.Top + gap)
            {
                screenY = taskbarRect.Top + gap;
            }
        }

        var point = new NativeMethods.POINT { X = screenX, Y = screenY };
        NativeMethods.ScreenToClient(taskbar, ref point);

        if (point.X == _lastX && point.Y == _lastY && width == _lastWidth && height == _lastHeight)
        {
            return;
        }

        _lastX = point.X;
        _lastY = point.Y;
        _lastWidth = width;
        _lastHeight = height;

        var logicalWidth = width / scale;
        var logicalHeight = height / scale;
        if (Math.Abs(_window.Width - logicalWidth) > 0.5)
        {
            _window.Width = logicalWidth;
        }

        if (Math.Abs(_window.Height - logicalHeight) > 0.5)
        {
            _window.Height = logicalHeight;
        }

        NativeMethods.SetWindowPos(
            _hwnd,
            NativeMethods.HWND_TOP,
            point.X,
            point.Y,
            width,
            height,
            NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_SHOWWINDOW |
            NativeMethods.SWP_FRAMECHANGED);
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
