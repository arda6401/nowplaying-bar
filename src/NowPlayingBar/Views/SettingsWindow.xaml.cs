using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NowPlayingBar.Services;

namespace NowPlayingBar.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsService _settingsService;
    private bool _isLoading = true;
    private bool _isUpdatingFromSlider = false;

    // Hotkey recording state
    private Border? _recordingBox;
    private string? _recordingTarget; // "PlayPause", "Next", "Previous", "Toggle"
    private bool _isRecording = false;

    // Current hotkey assignments (empty string = unassigned)
    private string _hotkeyPlayPause = "";
    private string _hotkeyNext = "";
    private string _hotkeyPrevious = "";
    private string _hotkeyToggle = "";

    private static readonly SolidColorBrush BrushRecording   = new(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly SolidColorBrush BrushAssigned    = new(Color.FromRgb(0x02, 0x84, 0xC7));
    private static readonly SolidColorBrush BrushUnassigned  = new(Color.FromRgb(0x2D, 0x3E, 0x56));
    private static readonly SolidColorBrush BrushFgRecording = new(Color.FromRgb(0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush BrushFgAssigned  = new(Color.FromRgb(0x38, 0xBD, 0xF8));
    private static readonly SolidColorBrush BrushFgEmpty     = new(Color.FromRgb(0x94, 0xA3, 0xB8));

    public SettingsWindow(SettingsService settingsService)
    {
        InitializeComponent();
        _settingsService = settingsService;
        LoadSettings();

        // PreviewKeyDown = sistem yutmadan önce yakala (medya/özel tuşlar dahil)
        PreviewKeyDown += SettingsWindow_PreviewKeyDown;
    }

    private void LoadSettings()
    {
        _isLoading = true;
        var s = _settingsService.Current;

        SliderOffsetX.Value = s.OffsetX;
        TxtOffsetX.Text = Math.Round(s.OffsetX).ToString();

        SliderOffsetY.Value = s.OffsetY;
        TxtOffsetY.Text = Math.Round(s.OffsetY).ToString();

        SliderMaxWidth.Value = s.MaxWidth;
        LblMaxWidth.Text = $"{Math.Round(s.MaxWidth)} px";

        SliderFontSize.Value = s.FontSize;
        LblFontSize.Text = $"{Math.Round(s.FontSize)} pt";

        SliderMarqueeSpeed.Value = s.MarqueeSpeed;
        LblMarqueeSpeed.Text = $"{Math.Round(s.MarqueeSpeed)} px/sn";

        TxtFormat.Text = s.TextFormat;

        ChkShowAlbumArt.IsChecked = s.ShowAlbumArt;
        ChkShowOnlyWhenPlaying.IsChecked = s.ShowOnlyWhenPlaying;
        ChkClickThrough.IsChecked = s.ClickThrough;
        ChkCleanYoutube.IsChecked = s.CleanYoutubeTitle;
        ChkVolumeScroll.IsChecked = s.VolumeScrollEnabled;
        ChkStartWithWindows.IsChecked = s.StartWithWindows;

        // Hotkeys
        _hotkeyPlayPause = s.HotkeyPlayPause ?? "";
        _hotkeyNext      = s.HotkeyNext      ?? "";
        _hotkeyPrevious  = s.HotkeyPrevious  ?? "";
        _hotkeyToggle    = s.HotkeyToggleWidget ?? "";

        RefreshHotkeyLabel(HotkeyBoxPlayPause, HotkeyLblPlayPause, _hotkeyPlayPause);
        RefreshHotkeyLabel(HotkeyBoxNext,      HotkeyLblNext,      _hotkeyNext);
        RefreshHotkeyLabel(HotkeyBoxPrevious,  HotkeyLblPrevious,  _hotkeyPrevious);
        RefreshHotkeyLabel(HotkeyBoxToggle,    HotkeyLblToggle,    _hotkeyToggle);

        _isLoading = false;
    }

    // ─── Hotkey Recording ─────────────────────────────────────────────────────

    private void OnHotkeyBoxClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border box) return;

        // If clicking the already-recording box → cancel
        if (_isRecording && _recordingBox == box)
        {
            CancelRecording();
            return;
        }

        // Cancel any previous recording first
        if (_isRecording)
            CancelRecording();

        _recordingBox    = box;
        _recordingTarget = box.Tag as string;
        _isRecording     = true;

        box.BorderBrush = BrushRecording;
        if (box.Child is TextBlock lbl)
        {
            lbl.Text       = "Tuşa basın...";
            lbl.Foreground = BrushFgRecording;
        }
    }

    private void SettingsWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_isRecording || _recordingBox == null) return;

        // Medya/özel tuşları da buradan yakala — sistem yutmadan önce
        // Gerçek tuşu çöz: System tuşları (Alt+X gibi) SystemKey'de gelir
        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            Key.DeadCharProcessed => e.DeadCharProcessedKey,
            _ => e.Key
        };

        // Escape = iptal
        if (key == Key.Escape)
        {
            e.Handled = true;
            CancelRecording();
            return;
        }

        // Modifier tuşlarını bekle (henüz esas tuş yok)
        if (HotkeyGesture.IsModifierKey(key)) return;

        e.Handled = true; // sistemin medya tuşunu işlemesini engelle

        var modifiers = Keyboard.Modifiers;

        // Medya / özel tuşlar için modifier zorunlu değil
        bool isSpecialKey = IsSpecialKey(key);
        if (!isSpecialKey && modifiers == ModifierKeys.None)
        {
            // Normal tuş, modifier olmadan — uyar ama kaydetme
            if (_recordingBox.Child is TextBlock lbl2)
            {
                lbl2.Text = "Modifier ekle (Ctrl/Alt/Shift)...";
            }
            return;
        }

        var gesture = new HotkeyGesture(modifiers, key);
        var text    = gesture.ToString();

        switch (_recordingTarget)
        {
            case "PlayPause": _hotkeyPlayPause = text; break;
            case "Next":      _hotkeyNext      = text; break;
            case "Previous":  _hotkeyPrevious  = text; break;
            case "Toggle":    _hotkeyToggle    = text; break;
        }

        RefreshHotkeyLabel(_recordingBox, _recordingBox.Child as TextBlock, text);
        _recordingBox    = null;
        _recordingTarget = null;
        _isRecording     = false;

        if (!_isLoading)
            ApplyLiveChanges();
    }

    /// <summary>
    /// Medya tuşları, fonksiyon tuşları (F1-F24), NumLock, Pause vb. —
    /// bunlar modifier olmadan da global hotkey olarak kaydedilebilir.
    /// </summary>
    private static bool IsSpecialKey(Key key)
    {
        if (key is Key.MediaPlayPause or Key.MediaNextTrack or Key.MediaPreviousTrack or Key.MediaStop
                or Key.VolumeUp or Key.VolumeDown or Key.VolumeMute
                or Key.BrowserBack or Key.BrowserForward or Key.BrowserRefresh
                or Key.LaunchApplication1 or Key.LaunchApplication2 or Key.LaunchMail
                or Key.PrintScreen or Key.Scroll or Key.Pause
                or Key.Insert or Key.Delete or Key.Home or Key.End
                or Key.PageUp or Key.PageDown)
            return true;

        // F1–F24
        if (key >= Key.F1 && key <= Key.F24)
            return true;

        return false;
    }
    private void OnHotkeyClear(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;

        // Cancel recording if active on this target
        if (_isRecording && _recordingTarget == btn.Tag as string)
            CancelRecording();

        switch (btn.Tag as string)
        {
            case "PlayPause":
                _hotkeyPlayPause = "";
                RefreshHotkeyLabel(HotkeyBoxPlayPause, HotkeyLblPlayPause, "");
                break;
            case "Next":
                _hotkeyNext = "";
                RefreshHotkeyLabel(HotkeyBoxNext, HotkeyLblNext, "");
                break;
            case "Previous":
                _hotkeyPrevious = "";
                RefreshHotkeyLabel(HotkeyBoxPrevious, HotkeyLblPrevious, "");
                break;
            case "Toggle":
                _hotkeyToggle = "";
                RefreshHotkeyLabel(HotkeyBoxToggle, HotkeyLblToggle, "");
                break;
        }

        if (!_isLoading)
            ApplyLiveChanges();
    }

    private void CancelRecording()
    {
        if (_recordingBox == null) return;

        // Restore original label
        var target = _recordingTarget;
        var current = target switch
        {
            "PlayPause" => _hotkeyPlayPause,
            "Next"      => _hotkeyNext,
            "Previous"  => _hotkeyPrevious,
            "Toggle"    => _hotkeyToggle,
            _           => ""
        };
        RefreshHotkeyLabel(_recordingBox, _recordingBox.Child as TextBlock, current);

        _recordingBox    = null;
        _recordingTarget = null;
        _isRecording     = false;
    }

    private static void RefreshHotkeyLabel(Border box, TextBlock? lbl, string? text)
    {
        if (lbl == null) return;

        bool empty = string.IsNullOrWhiteSpace(text);
        lbl.Text       = empty ? "Atanmamış" : text;
        lbl.Foreground = empty ? BrushFgEmpty : BrushFgAssigned;
        box.BorderBrush = empty ? BrushUnassigned : BrushAssigned;
    }

    // ─── Slider / Text / Option Handlers ─────────────────────────────────────

    private void OnLiveSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isLoading) return;

        _isUpdatingFromSlider = true;
        if (sender == SliderOffsetX)
            TxtOffsetX.Text = Math.Round(SliderOffsetX.Value).ToString();
        else if (sender == SliderOffsetY)
            TxtOffsetY.Text = Math.Round(SliderOffsetY.Value).ToString();
        else if (sender == SliderMaxWidth)
            LblMaxWidth.Text = $"{Math.Round(SliderMaxWidth.Value)} px";
        else if (sender == SliderFontSize)
            LblFontSize.Text = $"{Math.Round(SliderFontSize.Value)} pt";
        else if (sender == SliderMarqueeSpeed)
            LblMarqueeSpeed.Text = $"{Math.Round(SliderMarqueeSpeed.Value)} px/sn";
        _isUpdatingFromSlider = false;

        ApplyLiveChanges();
    }

    private void OnOffsetXTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoading || _isUpdatingFromSlider) return;

        if (double.TryParse(TxtOffsetX.Text, out var val))
        {
            SliderOffsetX.Value = Math.Clamp(val, SliderOffsetX.Minimum, SliderOffsetX.Maximum);
            ApplyLiveChanges();
        }
    }

    private void OnOffsetYTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoading || _isUpdatingFromSlider) return;

        if (double.TryParse(TxtOffsetY.Text, out var val))
        {
            SliderOffsetY.Value = Math.Clamp(val, SliderOffsetY.Minimum, SliderOffsetY.Maximum);
            ApplyLiveChanges();
        }
    }

    private void OnLiveTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoading) return;
        ApplyLiveChanges();
    }

    private void OnLiveOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        ApplyLiveChanges();
    }

    private void ApplyLiveChanges()
    {
        double offsetX = double.TryParse(TxtOffsetX.Text, out var ox) ? ox : SliderOffsetX.Value;
        double offsetY = double.TryParse(TxtOffsetY.Text, out var oy) ? oy : SliderOffsetY.Value;

        var s = new AppSettings
        {
            OffsetX          = offsetX,
            OffsetY          = offsetY,
            MaxWidth         = Math.Round(SliderMaxWidth.Value),
            FontSize         = Math.Round(SliderFontSize.Value),
            MarqueeSpeed     = Math.Round(SliderMarqueeSpeed.Value),
            TextFormat       = string.IsNullOrWhiteSpace(TxtFormat.Text) ? "{artist} - {title}" : TxtFormat.Text.Trim(),
            ShowAlbumArt     = ChkShowAlbumArt.IsChecked == true,
            ShowOnlyWhenPlaying = ChkShowOnlyWhenPlaying.IsChecked == true,
            ClickThrough     = ChkClickThrough.IsChecked == true,
            CleanYoutubeTitle = ChkCleanYoutube.IsChecked == true,
            VolumeScrollEnabled = ChkVolumeScroll.IsChecked == true,
            StartWithWindows = ChkStartWithWindows.IsChecked == true,

            HotkeyPlayPause    = _hotkeyPlayPause,
            HotkeyNext         = _hotkeyNext,
            HotkeyPrevious     = _hotkeyPrevious,
            HotkeyToggleWidget = _hotkeyToggle,
        };

        _settingsService.Save(s);
    }

    private void OnDoneClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
