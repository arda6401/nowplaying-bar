using System.Windows.Interop;
using NowPlayingBar.Native;

namespace NowPlayingBar.Services;

public class HotkeyService : IDisposable
{
    private const int HOTKEY_PLAY_PAUSE = 9001;
    private const int HOTKEY_NEXT = 9002;
    private const int HOTKEY_PREVIOUS = 9003;
    private const int HOTKEY_TOGGLE_WIDGET = 9004;

    private readonly SettingsService _settingsService;
    private HwndSource? _hwndSource;
    private IntPtr _hwnd = IntPtr.Zero;
    private readonly HashSet<int> _registeredIds = new();

    public event Action? PlayPauseRequested;
    public event Action? NextTrackRequested;
    public event Action? PreviousTrackRequested;
    public event Action? ToggleWidgetRequested;

    public HotkeyService(SettingsService settingsService)
    {
        _settingsService = settingsService;
        _settingsService.SettingsChanged += SettingsService_SettingsChanged;

        InitializeHwnd();
        RegisterHotkeys();
    }

    private void InitializeHwnd()
    {
        try
        {
            var parameters = new HwndSourceParameters("NowPlayingBar_HotkeyHost")
            {
                WindowStyle = 0,
                Width = 0,
                Height = 0,
                PositionX = 0,
                PositionY = 0
            };

            _hwndSource = new HwndSource(parameters);
            _hwndSource.AddHook(HwndHook);
            _hwnd = _hwndSource.Handle;
        }
        catch (Exception ex)
        {
            Logger.LogError("Failed to initialize HotkeyService HwndSource", ex);
        }
    }

    private void SettingsService_SettingsChanged(object? sender, EventArgs e)
    {
        RegisterHotkeys();
    }

    public void RegisterHotkeys()
    {
        if (_hwnd == IntPtr.Zero) return;

        UnregisterAll();

        var s = _settingsService.Current;

        RegisterSingleHotkey(HOTKEY_PLAY_PAUSE, s.HotkeyPlayPause, "Play/Pause");
        RegisterSingleHotkey(HOTKEY_NEXT, s.HotkeyNext, "Next Track");
        RegisterSingleHotkey(HOTKEY_PREVIOUS, s.HotkeyPrevious, "Previous Track");
        RegisterSingleHotkey(HOTKEY_TOGGLE_WIDGET, s.HotkeyToggleWidget, "Toggle Widget");
    }

    private void RegisterSingleHotkey(int id, string? shortcut, string description)
    {
        if (string.IsNullOrWhiteSpace(shortcut)) return;

        if (HotkeyGesture.TryParse(shortcut, out var gesture) && gesture != null && !gesture.IsEmpty)
        {
            var mods = gesture.ToWin32Modifiers();
            var vk = gesture.ToVirtualKey();

            bool success = NativeMethods.RegisterHotKey(_hwnd, id, mods, vk);
            if (success)
            {
                _registeredIds.Add(id);
                Logger.LogInfo($"Successfully registered hotkey for {description}: {gesture}");
            }
            else
            {
                Logger.LogWarning($"Could not register hotkey for {description}: {gesture} (Might be in use by another app)");
            }
        }
    }

    private void UnregisterAll()
    {
        if (_hwnd == IntPtr.Zero) return;

        foreach (var id in _registeredIds)
        {
            try
            {
                NativeMethods.UnregisterHotKey(_hwnd, id);
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"Failed to unregister hotkey {id}: {ex.Message}");
            }
        }
        _registeredIds.Clear();
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            switch (id)
            {
                case HOTKEY_PLAY_PAUSE:
                    PlayPauseRequested?.Invoke();
                    handled = true;
                    break;
                case HOTKEY_NEXT:
                    NextTrackRequested?.Invoke();
                    handled = true;
                    break;
                case HOTKEY_PREVIOUS:
                    PreviousTrackRequested?.Invoke();
                    handled = true;
                    break;
                case HOTKEY_TOGGLE_WIDGET:
                    ToggleWidgetRequested?.Invoke();
                    handled = true;
                    break;
            }
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        _settingsService.SettingsChanged -= SettingsService_SettingsChanged;
        UnregisterAll();

        if (_hwndSource != null)
        {
            _hwndSource.RemoveHook(HwndHook);
            _hwndSource.Dispose();
            _hwndSource = null;
        }

        GC.SuppressFinalize(this);
    }
}
