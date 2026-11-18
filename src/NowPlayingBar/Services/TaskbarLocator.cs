using System.Windows;
using NowPlayingBar.Native;

namespace NowPlayingBar.Services;

public record TaskbarPosition(double Left, double Top, double Width, double Height, double DpiScale);

public class TaskbarLocator
{
    private readonly SettingsService _settingsService;

    public TaskbarLocator(SettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public TaskbarPosition? CalculateOverlayPosition(double desiredWidth, double desiredHeight, IntPtr windowHwnd)
    {
        try
        {
            var trayWnd = NativeMethods.FindWindow("Shell_TrayWnd", null);
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

            // Get monitor info
            var hMonitor = NativeMethods.MonitorFromWindow(trayWnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
            var mi = new NativeMethods.MONITORINFO();
            mi.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(NativeMethods.MONITORINFO));
            NativeMethods.GetMonitorInfo(hMonitor, ref mi);

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
            var trayNotifyWnd = NativeMethods.FindWindowEx(trayWnd, IntPtr.Zero, "TrayNotifyWnd", null);
            bool hasValidNotifyRect = false;
            NativeMethods.RECT notifyRect = default;

            if (trayNotifyWnd != IntPtr.Zero && NativeMethods.GetWindowRect(trayNotifyWnd, out notifyRect))
            {
                // In Windows 11 XAML taskbar, TrayNotifyWnd might have 0 width or offscreen coords
                if (notifyRect.Width > 20 && notifyRect.Left > trayRect.Left && notifyRect.Right <= trayRect.Right + 10)
                {
                    hasValidNotifyRect = true;
                }
            }

            var offsetXDpi = _settingsService.Current.OffsetX * dpiScale;
            var offsetYDpi = _settingsService.Current.OffsetY * dpiScale;
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

            return new TaskbarPosition(dipLeft, dipTop, desiredWidth, desiredHeight, dpiScale);
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
            // Method 1: Windows Shell query user notification state
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
                            var trayWnd = NativeMethods.FindWindow("Shell_TrayWnd", null);
                            var progman = NativeMethods.FindWindow("Progman", null);
                            var workerW = NativeMethods.FindWindow("WorkerW", null);

                            if (fgHwnd != trayWnd && fgHwnd != progman && fgHwnd != workerW)
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
