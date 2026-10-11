using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using SoundTaskbar.Native;

namespace SoundTaskbar.Services;

/// <summary>
/// Pins a top-level tool window onto the taskbar strip (left of the tray).
/// Survives Win+D / "Show desktop" by owning to the taskbar and restoring if Windows hides us.
/// </summary>
public sealed class TaskbarHostService : IDisposable
{
    private readonly Window _window;
    private readonly DispatcherTimer _geometryTimer;
    private readonly DispatcherTimer _visibilityTimer;
    private IntPtr _hwnd;
    private bool _disposed;
    private int _lastX = int.MinValue;
    private int _lastY = int.MinValue;
    private int _lastWidth = int.MinValue;
    private int _lastHeight = int.MinValue;

    public TaskbarHostService(Window window)
    {
        _window = window;
        _geometryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _geometryTimer.Tick += (_, _) => PositionOnTaskbar(forceZOrder: false);

        // Show-desktop hides top-level windows; bring ours back quickly.
        _visibilityTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _visibilityTimer.Tick += (_, _) => EnsureVisible();

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    public void Start()
    {
        _hwnd = new WindowInteropHelper(_window).EnsureHandle();
        ApplyWindowStyles(_hwnd);
        AttachOwnerToTaskbar();
        PositionOnTaskbar(forceZOrder: true);
        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOWNOACTIVATE);
        _geometryTimer.Start();
        _visibilityTimer.Start();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _geometryTimer.Stop();
        _visibilityTimer.Stop();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        _window.Dispatcher.BeginInvoke(() =>
        {
            AttachOwnerToTaskbar();
            PositionOnTaskbar(forceZOrder: true);
            NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOWNOACTIVATE);
        });

    private void EnsureVisible()
    {
        if (_disposed || _hwnd == IntPtr.Zero || !NativeMethods.IsWindow(_hwnd))
        {
            return;
        }

        AttachOwnerToTaskbar();

        var hidden = !NativeMethods.IsWindowVisible(_hwnd) || IsCloaked(_hwnd);
        if (!hidden)
        {
            // Also recover if show-desktop moved us off the strip.
            if (NativeMethods.GetWindowRect(_hwnd, out var current) &&
                (_lastX != int.MinValue) &&
                (Math.Abs(current.Left - _lastX) > 2 || Math.Abs(current.Top - _lastY) > 2))
            {
                PositionOnTaskbar(forceZOrder: true);
            }

            return;
        }

        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOWNOACTIVATE);
        PositionOnTaskbar(forceZOrder: true);
        if (_window.Visibility != Visibility.Visible)
        {
            _window.Show();
        }
    }

    private void AttachOwnerToTaskbar()
    {
        var taskbar = NativeMethods.FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero || _hwnd == IntPtr.Zero)
        {
            return;
        }

        // Owner (not WS_CHILD parent): keeps us tied to the shell so Show Desktop is less likely
        // to leave the widget permanently hidden, without the Win11 SetParent render bug.
        var currentOwner = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWLP_HWNDPARENT);
        if (currentOwner != taskbar)
        {
            NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWLP_HWNDPARENT, taskbar);
        }
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

    private void PositionOnTaskbar(bool forceZOrder)
    {
        if (_disposed || _hwnd == IntPtr.Zero || !NativeMethods.IsWindow(_hwnd))
        {
            return;
        }

        var taskbar = NativeMethods.FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero || !NativeMethods.IsWindow(taskbar) || !NativeMethods.IsWindowVisible(taskbar))
        {
            return;
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
        var desiredWidth = (int)Math.Round(380 * scale);

        var isHorizontal = taskbarRect.Width >= taskbarRect.Height;
        int x;
        int y;
        int width;
        int height;

        if (isHorizontal)
        {
            height = taskbarRect.Height;
            var available = trayRect.Left - taskbarRect.Left - gap * 2;
            width = Math.Clamp(desiredWidth, Scale(taskbar, 220), Math.Max(Scale(taskbar, 220), available));

            x = trayRect.Left - gap - width;
            if (x < taskbarRect.Left + gap)
            {
                x = taskbarRect.Left + gap;
            }

            y = taskbarRect.Top;
        }
        else
        {
            width = taskbarRect.Width;
            height = (int)Math.Round(72 * scale);
            x = taskbarRect.Left;
            y = trayRect.Top - gap - height;
            if (y < taskbarRect.Top + gap)
            {
                y = taskbarRect.Top + gap;
            }
        }

        var geometryChanged =
            x != _lastX ||
            y != _lastY ||
            width != _lastWidth ||
            height != _lastHeight;

        if (!geometryChanged && !forceZOrder)
        {
            return;
        }

        if (geometryChanged)
        {
            _lastX = x;
            _lastY = y;
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
        }

        var flags = NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_ASYNCWINDOWPOS;
        var insertAfter = forceZOrder ? NativeMethods.HWND_TOPMOST : IntPtr.Zero;
        if (!forceZOrder)
        {
            flags |= NativeMethods.SWP_NOZORDER;
        }

        if (!geometryChanged)
        {
            flags |= NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE;
        }

        NativeMethods.SetWindowPos(_hwnd, insertAfter, x, y, width, height, flags);
    }

    private static void ApplyWindowStyles(IntPtr hwnd)
    {
        var ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        ex |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TOPMOST;
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, ex);
    }

    private static int Scale(IntPtr hwnd, int value)
    {
        var dpi = NativeMethods.GetDpiForWindow(hwnd);
        var scale = dpi <= 0 ? 1.0 : dpi / 96.0;
        return (int)Math.Round(value * scale);
    }
}
