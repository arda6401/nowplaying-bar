using System.Drawing;
using System.Threading;
using System.Windows;
using Forms = System.Windows.Forms;
using NowPlayingBar.Services;
using NowPlayingBar.Views;

namespace NowPlayingBar;

public partial class App : Application
{
    private const string MutexName = @"Global\NowPlayingBar_SingleInstance_Mutex";
    private Mutex? _mutex;
    private bool _hasHandle;

    private SettingsService? _settingsService;
    private ThemeService? _themeService;
    private MediaService? _mediaService;
    private TaskbarLocator? _taskbarLocator;
    private HotkeyService? _hotkeyService;

    private OverlayWindow? _overlayWindow;
    private SettingsWindow? _settingsWindow;
    private Forms.NotifyIcon? _notifyIcon;

    private bool _manualHide;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 1. Single instance check
        try
        {
            _mutex = new Mutex(true, MutexName, out _hasHandle);
        }
        catch (Exception ex)
        {
            Logger.LogError("Exception creating named mutex", ex);
        }

        if (!_hasHandle)
        {
            Logger.LogWarning("Another instance of NowPlayingBar is already running. Exiting.");
            Shutdown();
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Logger.LogError("Unhandled Exception in AppDomain", args.ExceptionObject as Exception);
        };

        DispatcherUnhandledException += (_, args) =>
        {
            Logger.LogError("Dispatcher Unhandled Exception", args.Exception);
            args.Handled = true; // Prevent crash
        };

        // 2. Initialize Services
        _settingsService = new SettingsService();
        _themeService = new ThemeService();
        _mediaService = new MediaService(_settingsService);
        _taskbarLocator = new TaskbarLocator(_settingsService);

        // 3. Initialize Overlay
        _overlayWindow = new OverlayWindow(
            _mediaService,
            _taskbarLocator,
            _settingsService,
            _themeService,
            OpenSettings);

        _overlayWindow.Show();

        // 4. Setup Tray Icon
        SetupNotifyIcon();

        // 5. Initialize Media Subsystem
        await _mediaService.InitializeAsync();

        // 6. Setup Global Hotkeys
        SetupHotkeys();
    }

    private void SetupHotkeys()
    {
        if (_settingsService == null || _mediaService == null) return;

        _hotkeyService = new HotkeyService(_settingsService);

        _hotkeyService.PlayPauseRequested += async () =>
        {
            if (_mediaService != null)
            {
                await _mediaService.TryTogglePlayPauseAsync();
            }
        };

        _hotkeyService.NextTrackRequested += async () =>
        {
            if (_mediaService != null)
            {
                await _mediaService.TrySkipNextAsync();
            }
        };

        _hotkeyService.PreviousTrackRequested += async () =>
        {
            if (_mediaService != null)
            {
                await _mediaService.TryRestartOrPreviousAsync();
            }
        };

        _hotkeyService.ToggleWidgetRequested += () =>
        {
            Dispatcher.Invoke(ToggleWidgetVisibility);
        };
    }

    private void SetupNotifyIcon()
    {
        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "Şimdi Çalıyor",
            Icon = CreateAppIcon(),
            Visible = true
        };

        var contextMenu = new Forms.ContextMenuStrip();

        var showHideItem = new Forms.ToolStripMenuItem("Widget'ı Gizle", null, (_, _) => ToggleWidgetVisibility());
        var settingsItem = new Forms.ToolStripMenuItem("Ayarlar...", null, (_, _) => OpenSettings());

        var startWithWindowsItem = new Forms.ToolStripMenuItem("Windows ile Birlikte Başlat")
        {
            Checked = _settingsService?.Current.StartWithWindows ?? false,
            CheckOnClick = true
        };
        startWithWindowsItem.Click += (_, _) =>
        {
            if (_settingsService != null)
            {
                var s = _settingsService.Current;
                s.StartWithWindows = startWithWindowsItem.Checked;
                _settingsService.Save(s);
            }
        };

        var exitItem = new Forms.ToolStripMenuItem("Çıkış", null, (_, _) => ShutdownApp());

        contextMenu.Items.Add(showHideItem);
        contextMenu.Items.Add(settingsItem);
        contextMenu.Items.Add(startWithWindowsItem);
        contextMenu.Items.Add(new Forms.ToolStripSeparator());
        contextMenu.Items.Add(exitItem);

        _notifyIcon.ContextMenuStrip = contextMenu;
        _notifyIcon.DoubleClick += (_, _) => OpenSettings();
    }

    private void ToggleWidgetVisibility()
    {
        if (_overlayWindow == null) return;

        _manualHide = !_manualHide;
        if (_manualHide)
        {
            _overlayWindow.Hide();
            if (_notifyIcon?.ContextMenuStrip?.Items[0] is Forms.ToolStripMenuItem item)
            {
                item.Text = "Widget'ı Göster";
            }
        }
        else
        {
            _overlayWindow.Show();
            if (_notifyIcon?.ContextMenuStrip?.Items[0] is Forms.ToolStripMenuItem item)
            {
                item.Text = "Widget'ı Gizle";
            }
        }
    }

    private void OpenSettings()
    {
        if (_settingsWindow != null && _settingsWindow.IsLoaded)
        {
            _settingsWindow.Activate();
            return;
        }

        if (_settingsService != null)
        {
            _settingsWindow = new SettingsWindow(_settingsService);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
            _settingsWindow.Activate();
        }
    }

    private static Icon CreateAppIcon()
    {
        try
        {
            // Draw a compact musical note icon for the tray
            using var bitmap = new Bitmap(32, 32);
            using var g = Graphics.FromImage(bitmap);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            // Background circle
            using var bgBrush = new SolidBrush(Color.FromArgb(0, 120, 215));
            g.FillEllipse(bgBrush, 2, 2, 28, 28);

            // Note shape
            using var pen = new Pen(Color.White, 3);
            using var noteBrush = new SolidBrush(Color.White);

            // Stems and note heads
            g.FillEllipse(noteBrush, 7, 18, 6, 6);
            g.FillEllipse(noteBrush, 17, 15, 6, 6);
            g.DrawLine(pen, 12, 19, 12, 9);
            g.DrawLine(pen, 22, 16, 22, 6);
            g.DrawLine(pen, 11, 9, 23, 6);

            var iconHandle = bitmap.GetHicon();
            return Icon.FromHandle(iconHandle);
        }
        catch
        {
            return SystemIcons.Application;
        }
    }

    private void ShutdownApp()
    {
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }

            _overlayWindow?.Close();
            _settingsWindow?.Close();

            _hotkeyService?.Dispose();
            _mediaService?.Dispose();
            _themeService?.Dispose();

            if (_hasHandle && _mutex != null)
            {
                try
                {
                    _mutex.ReleaseMutex();
                }
                catch { }
                _mutex.Dispose();
                _mutex = null;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("Error during shutdown", ex);
        }

        base.OnExit(e);
    }
}
