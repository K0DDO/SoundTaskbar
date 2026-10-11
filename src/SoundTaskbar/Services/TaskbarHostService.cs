using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using SoundTaskbar.Native;

namespace SoundTaskbar.Services;

/// <summary>
/// Hosts a WPF window as a child of the Windows taskbar (Shell_TrayWnd),
/// so the UI sits in the taskbar strip rather than as a floating window.
/// </summary>
public sealed class TaskbarHostService : IDisposable
{
    private readonly Window _window;
    private readonly DispatcherTimer _timer;
    private IntPtr _hwnd;
    private IntPtr _taskbar;
    private bool _hooked;
    private bool _disposed;

    public TaskbarHostService(Window window)
    {
        _window = window;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _timer.Tick += (_, _) => AttachAndPosition();
    }

    public void Start()
    {
        _hwnd = new WindowInteropHelper(_window).EnsureHandle();
        ApplyWindowStyles(_hwnd);

        if (!_hooked && PresentationSource.FromVisual(_window) is HwndSource source)
        {
            source.AddHook(WndProc);
            _hooked = true;
        }

        AttachAndPosition();
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

        if (_hwnd != IntPtr.Zero && NativeMethods.IsWindow(_hwnd))
        {
            // Detach from taskbar so explorer is not left with a dangling child.
            NativeMethods.SetParent(_hwnd, IntPtr.Zero);
            var style = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_STYLE);
            style = (style & ~NativeMethods.WS_CHILD) | NativeMethods.WS_POPUP;
            NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_STYLE, style);
        }
    }

    private void AttachAndPosition()
    {
        if (_disposed || _hwnd == IntPtr.Zero)
        {
            return;
        }

        var taskbar = NativeMethods.FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero || !NativeMethods.IsWindow(taskbar))
        {
            return;
        }

        _taskbar = taskbar;

        var style = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_STYLE);
        style = (style & ~NativeMethods.WS_POPUP & ~NativeMethods.WS_CAPTION & ~NativeMethods.WS_THICKFRAME & ~NativeMethods.WS_SYSMENU)
                | NativeMethods.WS_CHILD;
        NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_STYLE, style);

        if (NativeMethods.GetParent(_hwnd) != taskbar)
        {
            NativeMethods.SetParent(_hwnd, taskbar);
        }

        PositionOnTaskbar(taskbar, _hwnd);
    }

    private void PositionOnTaskbar(IntPtr taskbar, IntPtr hwnd)
    {
        if (!NativeMethods.GetWindowRect(taskbar, out var taskbarRect))
        {
            return;
        }

        var tray = NativeMethods.FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        if (tray == IntPtr.Zero || !NativeMethods.GetWindowRect(tray, out var trayRect))
        {
            trayRect = taskbarRect;
            trayRect.Left = taskbarRect.Right - Scale(taskbar, 120);
        }

        var dpi = NativeMethods.GetDpiForWindow(taskbar);
        var scale = dpi <= 0 ? 1.0 : dpi / 96.0;

        var isHorizontal = taskbarRect.Width >= taskbarRect.Height;
        var gap = (int)Math.Round(8 * scale);
        var desiredWidth = (int)Math.Round(360 * scale);
        var desiredHeight = (int)Math.Round(40 * scale);

        int x;
        int y;
        int width;
        int height;

        if (isHorizontal)
        {
            height = taskbarRect.Height;
            width = Math.Min(desiredWidth, Math.Max(Scale(taskbar, 200), trayRect.Left - taskbarRect.Left - gap));
            if (width < Scale(taskbar, 160))
            {
                // Fallback: sit near the left of the tray even if space is tight.
                width = desiredWidth;
            }

            var screenX = trayRect.Left - gap - width;
            if (screenX < taskbarRect.Left + gap)
            {
                screenX = taskbarRect.Left + gap;
            }

            var screenY = taskbarRect.Top + Math.Max(0, (taskbarRect.Height - height) / 2);
            var point = new NativeMethods.POINT { X = screenX, Y = screenY };
            NativeMethods.ScreenToClient(taskbar, ref point);
            x = point.X;
            y = point.Y;
        }
        else
        {
            // Vertical taskbar: place above the tray area.
            width = taskbarRect.Width;
            height = Math.Min(desiredHeight + Scale(taskbar, 24), Math.Max(Scale(taskbar, 120), trayRect.Top - taskbarRect.Top - gap));
            var screenX = taskbarRect.Left;
            var screenY = trayRect.Top - gap - height;
            if (screenY < taskbarRect.Top + gap)
            {
                screenY = taskbarRect.Top + gap;
            }

            var point = new NativeMethods.POINT { X = screenX, Y = screenY };
            NativeMethods.ScreenToClient(taskbar, ref point);
            x = point.X;
            y = point.Y;
        }

        // Keep WPF logical size roughly in sync for layout.
        _window.Width = width / scale;
        _window.Height = height / scale;

        NativeMethods.SetWindowPos(
            hwnd,
            IntPtr.Zero,
            x,
            y,
            width,
            height,
            NativeMethods.SWP_NOZORDER |
            NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_SHOWWINDOW |
            NativeMethods.SWP_ASYNCWINDOWPOS |
            NativeMethods.SWP_FRAMECHANGED);
    }

    private static void ApplyWindowStyles(IntPtr hwnd)
    {
        var ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        ex |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, ex);
    }

    private static int Scale(IntPtr hwnd, int value)
    {
        var dpi = NativeMethods.GetDpiForWindow(hwnd);
        var scale = dpi <= 0 ? 1.0 : dpi / 96.0;
        return (int)Math.Round(value * scale);
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Avoid UI Automation probes freezing explorer when our window is a taskbar child.
        if (msg == NativeMethods.WM_GETOBJECT)
        {
            handled = true;
            return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }
}
