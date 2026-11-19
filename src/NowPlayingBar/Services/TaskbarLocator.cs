using System.Windows;
using NowPlayingBar.Native;

namespace NowPlayingBar.Services;

public record TaskbarPosition(double Left, double Top, double Width, double Height, double DpiScale);

public class TaskbarLocator
{
    private readonly SettingsService _settingsService;

    // Cached Window Handles
    private IntPtr _cachedTrayWnd = IntPtr.Zero;
    private IntPtr _cachedTrayNotifyWnd = IntPtr.Zero;
    private IntPtr _cachedProgman = IntPtr.Zero;
    private IntPtr _cachedWorkerW = IntPtr.Zero;

    // Cached Position Result to avoid recalculating when unchanged
    private TaskbarPosition? _cachedPosition;
    private NativeMethods.RECT _lastTrayRect;
    private NativeMethods.RECT _lastNotifyRect;
    private double _lastDesiredWidth;
    private double _lastDesiredHeight;
    private double _lastDpiScale;
    private double _lastOffsetX;
    private double _lastOffsetY;

    public TaskbarLocator(SettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    private IntPtr GetTrayWindow()
    {
        if (_cachedTrayWnd == IntPtr.Zero || !NativeMethods.IsWindow(_cachedTrayWnd))
        {
            _cachedTrayWnd = NativeMethods.FindWindow("Shell_TrayWnd", null);
            _cachedTrayNotifyWnd = IntPtr.Zero;
        }
        return _cachedTrayWnd;
    }

    public TaskbarPosition? CalculateOverlayPosition(double desiredWidth, double desiredHeight, IntPtr windowHwnd)
    {
        try
        {
            var trayWnd = GetTrayWindow();
            if (trayWnd == IntPtr.Zero)
            {
                return null;
            }

            if (!NativeMethods.GetWindowRect(trayWnd, out var trayRect))
            {
                return null;
            }

            // If taskbar is hidden (e.g. autohide collapsed)
            if (trayRect.Width <= 0 || trayRect.Height <= 0)
            {
                return null;
            }

            // Determine DPI for the monitor/window
            double dpiScale = 1.0;
            if (windowHwnd != IntPtr.Zero)
            {
                var dpi = NativeMethods.GetDpiForWindow(windowHwnd);
                if (dpi > 0)
                {
                    dpiScale = dpi / 96.0;
                }
            }
            if (dpiScale <= 0) dpiScale = 1.0;

            // Try to find TrayNotifyWnd child
            if (_cachedTrayNotifyWnd == IntPtr.Zero || !NativeMethods.IsWindow(_cachedTrayNotifyWnd))
            {
                _cachedTrayNotifyWnd = NativeMethods.FindWindowEx(trayWnd, IntPtr.Zero, "TrayNotifyWnd", null);
            }

            bool hasValidNotifyRect = false;
            NativeMethods.RECT notifyRect = default;

            if (_cachedTrayNotifyWnd != IntPtr.Zero && NativeMethods.GetWindowRect(_cachedTrayNotifyWnd, out notifyRect))
            {
                // In Windows 11 XAML taskbar, TrayNotifyWnd might have 0 width or offscreen coords
                if (notifyRect.Width > 20 && notifyRect.Left > trayRect.Left && notifyRect.Right <= trayRect.Right + 10)
                {
                    hasValidNotifyRect = true;
                }
            }

            var currentSettings = _settingsService.Current;

            // Check if nothing changed to return cached result
            if (_cachedPosition != null &&
                trayRect.Left == _lastTrayRect.Left &&
                trayRect.Top == _lastTrayRect.Top &&
                trayRect.Right == _lastTrayRect.Right &&
                trayRect.Bottom == _lastTrayRect.Bottom &&
                notifyRect.Left == _lastNotifyRect.Left &&
                notifyRect.Top == _lastNotifyRect.Top &&
                notifyRect.Right == _lastNotifyRect.Right &&
                notifyRect.Bottom == _lastNotifyRect.Bottom &&
                Math.Abs(_lastDesiredWidth - desiredWidth) < 0.1 &&
                Math.Abs(_lastDesiredHeight - desiredHeight) < 0.1 &&
                Math.Abs(_lastDpiScale - dpiScale) < 0.001 &&
                Math.Abs(_lastOffsetX - currentSettings.OffsetX) < 0.1 &&
                Math.Abs(_lastOffsetY - currentSettings.OffsetY) < 0.1)
            {
                return _cachedPosition;
            }

            // Get monitor info
            var hMonitor = NativeMethods.MonitorFromWindow(trayWnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
            var mi = new NativeMethods.MONITORINFO();
            mi.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(NativeMethods.MONITORINFO));
            NativeMethods.GetMonitorInfo(hMonitor, ref mi);

            var offsetXDpi = currentSettings.OffsetX * dpiScale;
            var offsetYDpi = currentSettings.OffsetY * dpiScale;
            var widthDpi = desiredWidth * dpiScale;
            var heightDpi = desiredHeight * dpiScale;

            double targetPhysicalX;
            double targetPhysicalY;

            // Check taskbar orientation (Bottom is standard)
            bool isBottom = trayRect.Bottom >= mi.rcMonitor.Bottom && trayRect.Height < trayRect.Width;
            bool isTop = trayRect.Top <= mi.rcMonitor.Top && trayRect.Height < trayRect.Width;

            if (isBottom || isTop)
            {
                // Horizontal taskbar
                if (hasValidNotifyRect)
                {
                    // Position just to the left of the tray notification area
                    targetPhysicalX = notifyRect.Left - widthDpi - (8 * dpiScale) + offsetXDpi;
                }
                else
                {
                    // Fallback: Taskbar right edge minus configured offset
                    targetPhysicalX = trayRect.Right - offsetXDpi - widthDpi;
                }

                // Vertically centered inside taskbar
                targetPhysicalY = trayRect.Top + ((trayRect.Height - heightDpi) / 2.0) + offsetYDpi;
            }
            else
            {
                // Vertical taskbar (Left / Right edge)
                targetPhysicalX = trayRect.Left + ((trayRect.Width - widthDpi) / 2.0) + offsetXDpi;
                targetPhysicalY = trayRect.Bottom - offsetXDpi - heightDpi + offsetYDpi;
            }

            // Convert physical pixels to WPF DIPs
            double dipLeft = targetPhysicalX / dpiScale;
            double dipTop = targetPhysicalY / dpiScale;

            _lastTrayRect = trayRect;
            _lastNotifyRect = notifyRect;
            _lastDesiredWidth = desiredWidth;
            _lastDesiredHeight = desiredHeight;
            _lastDpiScale = dpiScale;
            _lastOffsetX = currentSettings.OffsetX;
            _lastOffsetY = currentSettings.OffsetY;

            _cachedPosition = new TaskbarPosition(dipLeft, dipTop, desiredWidth, desiredHeight, dpiScale);
            return _cachedPosition;
        }
        catch (Exception ex)
        {
            Logger.LogError("Error in CalculateOverlayPosition", ex);
            return null;
        }
    }

    public bool IsFullscreenAppRunning()
    {
        try
        {
            // Method 1: Windows Shell query user notification state (fastest and cleanest)
            if (NativeMethods.SHQueryUserNotificationState(out var state) == 0)
            {
                if (state == NativeMethods.QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN ||
                    state == NativeMethods.QUERY_USER_NOTIFICATION_STATE.QUNS_BUSY ||
                    state == NativeMethods.QUERY_USER_NOTIFICATION_STATE.QUNS_PRESENTATION_MODE)
                {
                    return true;
                }
            }

            // Method 2: Check foreground window vs its monitor rect
            var fgHwnd = NativeMethods.GetForegroundWindow();
            if (fgHwnd != IntPtr.Zero)
            {
                var hMonitor = NativeMethods.MonitorFromWindow(fgHwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
                var mi = new NativeMethods.MONITORINFO();
                mi.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(NativeMethods.MONITORINFO));
                if (NativeMethods.GetMonitorInfo(hMonitor, ref mi))
                {
                    if (NativeMethods.GetWindowRect(fgHwnd, out var fgRect))
                    {
                        // Check if foreground covers monitor completely
                        if (fgRect.Left <= mi.rcMonitor.Left &&
                            fgRect.Top <= mi.rcMonitor.Top &&
                            fgRect.Right >= mi.rcMonitor.Right &&
                            fgRect.Bottom >= mi.rcMonitor.Bottom)
                        {
                            // Verify it's not the desktop or taskbar
                            var trayWnd = GetTrayWindow();

                            if (_cachedProgman == IntPtr.Zero || !NativeMethods.IsWindow(_cachedProgman))
                            {
                                _cachedProgman = NativeMethods.FindWindow("Progman", null);
                            }
                            if (_cachedWorkerW == IntPtr.Zero || !NativeMethods.IsWindow(_cachedWorkerW))
                            {
                                _cachedWorkerW = NativeMethods.FindWindow("WorkerW", null);
                            }

                            if (fgHwnd != trayWnd && fgHwnd != _cachedProgman && fgHwnd != _cachedWorkerW)
                            {
                                return true;
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"Failed to check fullscreen state: {ex.Message}");
        }

        return false;
    }
}
