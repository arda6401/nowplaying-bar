using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NowPlayingBar.Native;
using NowPlayingBar.Services;

namespace NowPlayingBar.Views;

public partial class OverlayWindow : Window
{
    private readonly MediaService _mediaService;
    private readonly TaskbarLocator _taskbarLocator;
    private readonly SettingsService _settingsService;
    private readonly ThemeService _themeService;
    private readonly Action _openSettingsAction;

    private readonly DispatcherTimer _topmostTimer;
    private IntPtr _hwnd = IntPtr.Zero;
    private bool _isFadingOut;
    private bool _shouldBeVisible;

    private readonly SolidColorBrush _accentBrush = new(ColorExtractor.DefaultAccent);
    private readonly SolidColorBrush _capsuleBorderBrush = new(Color.FromRgb(0x25, 0x32, 0x44));
    private readonly SolidColorBrush _albumBorderBrush = new(Color.FromRgb(0x28, 0x36, 0x4A));
    private ImageSource? _currentImageSource;

    public Brush TextBrush => _themeService.TextBrush;
    public Brush HoverBrush => _themeService.BackgroundHoverBrush;

    public OverlayWindow(
        MediaService mediaService,
        TaskbarLocator taskbarLocator,
        SettingsService settingsService,
        ThemeService themeService,
        Action openSettingsAction)
    {
        InitializeComponent();

        _mediaService = mediaService;
        _taskbarLocator = taskbarLocator;
        _settingsService = settingsService;
        _themeService = themeService;
        _openSettingsAction = openSettingsAction;

        DataContext = this;

        // Hook dynamic brushes
        ProgressFill.Background = _accentBrush;
        ProgressThumbDot.Fill = _accentBrush;
        CapsuleBorder.BorderBrush = _capsuleBorderBrush;
        AlbumArtContainer.BorderBrush = _albumBorderBrush;

        _mediaService.MediaChanged += MediaService_MediaChanged;
        _mediaService.PropertyChanged += MediaService_PropertyChanged;
        _settingsService.SettingsChanged += SettingsService_SettingsChanged;
        _themeService.ThemeChanged += ThemeService_ThemeChanged;

        _topmostTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(1500)
        };
        _topmostTimer.Tick += TopmostTimer_Tick;

        ProgressBarContainer.SizeChanged += (_, _) => UpdateProgressBar();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var helper = new WindowInteropHelper(this);
        _hwnd = helper.Handle;

        ApplyWindowStyles();

        // Hook window messages for DPI and display changes
        var source = HwndSource.FromHwnd(_hwnd);
        source?.AddHook(WndProc);

        ApplySettings();
        ApplyTheme();
        UpdateMediaDisplay();
        UpdatePosition();

        _topmostTimer.Start();
    }

    private void ApplyWindowStyles()
    {
        if (_hwnd == IntPtr.Zero) return;

        var exStyle = (int)NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE);
        exStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;

        if (_settingsService.Current.ClickThrough)
        {
            exStyle |= NativeMethods.WS_EX_TRANSPARENT;
        }
        else
        {
            exStyle &= ~NativeMethods.WS_EX_TRANSPARENT;
        }

        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_DPICHANGED ||
            msg == NativeMethods.WM_DISPLAYCHANGE ||
            msg == NativeMethods.WM_SETTINGCHANGE)
        {
            Dispatcher.InvokeAsync(() =>
            {
                UpdatePosition();
                ReassertTopmost();
            }, DispatcherPriority.Render);
        }

        return IntPtr.Zero;
    }

    private void TopmostTimer_Tick(object? sender, EventArgs e)
    {
        EvaluateVisibilityAndPosition();
    }

    private void EvaluateVisibilityAndPosition()
    {
        // 1. Media existence check (FASTEST: avoids costly Win32 window queries when idle)
        if (!_mediaService.HasMedia)
        {
            SetTargetVisibility(false);
            return;
        }

        // 2. Playback status check (if configured to show only when playing)
        if (_settingsService.Current.ShowOnlyWhenPlaying && !_mediaService.IsPlaying)
        {
            SetTargetVisibility(false);
            return;
        }

        // 3. Fullscreen app check (only needed when media is actually active)
        if (_taskbarLocator.IsFullscreenAppRunning())
        {
            SetTargetVisibility(false);
            return;
        }

        SetTargetVisibility(true);
        UpdatePosition();
        ReassertTopmost();
    }

    private void ReassertTopmost()
    {
        if (_hwnd == IntPtr.Zero || !_shouldBeVisible) return;

        NativeMethods.SetWindowPos(
            _hwnd,
            NativeMethods.HWND_TOPMOST,
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }

    public void UpdatePosition()
    {
        if (_hwnd == IntPtr.Zero) return;

        var desiredWidth = Math.Max(240, _settingsService.Current.MaxWidth);
        var desiredHeight = 52.0;

        var pos = _taskbarLocator.CalculateOverlayPosition(desiredWidth, desiredHeight, _hwnd);
        if (pos != null)
        {
            // Only update WPF properties if position or size actually changed to avoid redundant layout passes
            if (Math.Abs(Left - pos.Left) >= 0.5) Left = pos.Left;
            if (Math.Abs(Top - pos.Top) >= 0.5) Top = pos.Top;
            if (Math.Abs(Width - pos.Width) >= 0.5) Width = pos.Width;
            if (Math.Abs(Height - pos.Height) >= 0.5) Height = pos.Height;
        }
    }

    private void MediaService_MediaChanged(object? sender, EventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            UpdateMediaDisplay();
            EvaluateVisibilityAndPosition();
        }, DispatcherPriority.Normal);
    }

    private void MediaService_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MediaService.ProgressFraction))
        {
            Dispatcher.InvokeAsync(UpdateProgressBar, DispatcherPriority.Normal);
        }
        else if (e.PropertyName == nameof(MediaService.IsRepeatOn))
        {
            Dispatcher.InvokeAsync(UpdateRepeatButton, DispatcherPriority.Normal);
        }
    }

    private void SettingsService_SettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            ApplySettings();
            UpdateMediaDisplay();
            EvaluateVisibilityAndPosition();
        }, DispatcherPriority.Normal);
    }

    private void ThemeService_ThemeChanged(object? sender, EventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            ApplyTheme();
        }, DispatcherPriority.Normal);
    }

    private void ApplySettings()
    {
        ApplyWindowStyles();

        AlbumArtContainer.Visibility = _settingsService.Current.ShowAlbumArt
            ? Visibility.Visible
            : Visibility.Collapsed;

        SongText.FontSize = _settingsService.Current.FontSize;
        SongText.Speed = _settingsService.Current.MarqueeSpeed;

        var maxWidth = Math.Max(240, _settingsService.Current.MaxWidth);
        Width = maxWidth;

        // Controls take ~136px (Prev, Play, Next, Repeat, Separator) + Album Art ~48px
        double reserved = 136;
        if (_settingsService.Current.ShowAlbumArt)
        {
            reserved += 48;
        }

        var textWidth = Math.Max(60, maxWidth - reserved);
        SongText.Width = textWidth;
        ArtistText.MaxWidth = textWidth;

        UpdatePosition();
        UpdateDynamicColors(_mediaService.Thumbnail);
    }

    private void ApplyTheme()
    {
        SongText.TextBrush = Brushes.White;
    }

    private void UpdateMediaDisplay()
    {
        SongText.Text = string.IsNullOrWhiteSpace(_mediaService.Title) ? _mediaService.FormattedText : _mediaService.Title;
        ArtistText.Text = string.IsNullOrWhiteSpace(_mediaService.Artist) ? "—" : _mediaService.Artist;

        UpdateThumbnailWithCrossfade(_mediaService.Thumbnail);
        UpdateDynamicColors(_mediaService.Thumbnail);

        // Play / Pause state
        if (_mediaService.IsPlaying)
        {
            PlayIcon.Visibility = Visibility.Collapsed;
            PauseIcon.Visibility = Visibility.Visible;
            PauseBadge.Visibility = Visibility.Collapsed;
        }
        else
        {
            PlayIcon.Visibility = Visibility.Visible;
            PauseIcon.Visibility = Visibility.Collapsed;
            PauseBadge.Visibility = Visibility.Visible;
        }

        UpdateProgressBar();
        UpdateRepeatButton();
    }

    private void UpdateThumbnailWithCrossfade(ImageSource? newSource)
    {
        if (Equals(_currentImageSource, newSource)) return;

        var oldSource = _currentImageSource;
        _currentImageSource = newSource;

        if (!_settingsService.Current.ShowAlbumArt || newSource == null)
        {
            if (ThumbnailImage.Opacity > 0)
            {
                var fadeOut = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(250));
                fadeOut.Completed += (_, _) =>
                {
                    ThumbnailImage.Source = null;
                    ThumbnailImagePrev.Source = null;
                };
                ThumbnailImage.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            }
            else
            {
                ThumbnailImage.Source = null;
                ThumbnailImagePrev.Source = null;
                ThumbnailImage.Opacity = 0;
            }
            return;
        }

        if (oldSource != null)
        {
            ThumbnailImagePrev.Source = oldSource;
            ThumbnailImagePrev.Opacity = 1.0;

            ThumbnailImage.Source = newSource;
            ThumbnailImage.Opacity = 0.0;

            var fadeOutPrev = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(350))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
            };
            fadeOutPrev.Completed += (_, _) => ThumbnailImagePrev.Source = null;
            ThumbnailImagePrev.BeginAnimation(UIElement.OpacityProperty, fadeOutPrev);

            var fadeInCur = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(350))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
            };
            ThumbnailImage.BeginAnimation(UIElement.OpacityProperty, fadeInCur);
        }
        else
        {
            ThumbnailImagePrev.Source = null;
            ThumbnailImagePrev.Opacity = 0;

            ThumbnailImage.Source = newSource;
            ThumbnailImage.Opacity = 0.0;

            var fadeIn = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(300))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
            };
            ThumbnailImage.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        }
    }

    private void UpdateDynamicColors(BitmapSource? thumbnail)
    {
        if (!_settingsService.Current.DynamicColorEnabled || thumbnail == null || !_mediaService.HasMedia)
        {
            ApplyColorAnimation(ColorExtractor.DefaultAccent,
                Color.FromRgb(0x25, 0x32, 0x44),
                Color.FromRgb(0x28, 0x36, 0x4A));
            return;
        }

        // Offload color extraction from UI thread
        Task.Run(() =>
        {
            var targetAccent = ColorExtractor.ExtractDominantColor(thumbnail);
            var targetCapsuleBorder = Color.FromArgb(0x55, targetAccent.R, targetAccent.G, targetAccent.B);
            var targetAlbumBorder = Color.FromArgb(0x88, targetAccent.R, targetAccent.G, targetAccent.B);

            Dispatcher.InvokeAsync(() =>
            {
                ApplyColorAnimation(targetAccent, targetCapsuleBorder, targetAlbumBorder);
            }, DispatcherPriority.Background);
        });
    }

    private void ApplyColorAnimation(Color targetAccent, Color targetCapsuleBorder, Color targetAlbumBorder)
    {
        if (_accentBrush.Color == targetAccent &&
            _capsuleBorderBrush.Color == targetCapsuleBorder &&
            _albumBorderBrush.Color == targetAlbumBorder)
        {
            return;
        }

        var duration = TimeSpan.FromMilliseconds(350);
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseInOut };

        var accentAnim = new ColorAnimation(targetAccent, duration) { EasingFunction = ease };
        var capsuleAnim = new ColorAnimation(targetCapsuleBorder, duration) { EasingFunction = ease };
        var albumAnim = new ColorAnimation(targetAlbumBorder, duration) { EasingFunction = ease };

        _accentBrush.BeginAnimation(SolidColorBrush.ColorProperty, accentAnim);
        _capsuleBorderBrush.BeginAnimation(SolidColorBrush.ColorProperty, capsuleAnim);
        _albumBorderBrush.BeginAnimation(SolidColorBrush.ColorProperty, albumAnim);
    }

    private void UpdateRepeatButton()
    {
        if (_mediaService.IsRepeatOn)
        {
            RepeatIcon.Opacity = 1.0;
            RepeatIcon.Fill = _accentBrush;
            RepeatButton.ToolTip = "Döngü: Açık";
        }
        else
        {
            RepeatIcon.Opacity = 0.45;
            RepeatIcon.Fill = new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF0));
            RepeatButton.ToolTip = "Döngü: Kapalı";
        }
    }

    private void UpdateProgressBar()
    {
        if (ProgressBarContainer.ActualWidth > 0)
        {
            var targetWidth = ProgressBarContainer.ActualWidth * _mediaService.ProgressFraction;
            ProgressFill.Width = Math.Max(0, Math.Min(targetWidth, ProgressBarContainer.ActualWidth));

            // Position thumb dot
            Canvas.SetLeft(ProgressThumbDot, Math.Max(-4, targetWidth - 4));
            ProgressThumbDot.Visibility = targetWidth > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void SetTargetVisibility(bool visible)
    {
        if (_shouldBeVisible == visible) return;
        _shouldBeVisible = visible;

        if (visible)
        {
            _isFadingOut = false;
            Visibility = Visibility.Visible;
            var fadeIn = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(250));
            BeginAnimation(OpacityProperty, fadeIn);
        }
        else
        {
            if (_isFadingOut) return;
            _isFadingOut = true;

            var fadeOut = new DoubleAnimation(Opacity, 0.0, TimeSpan.FromMilliseconds(250));
            fadeOut.Completed += (_, _) =>
            {
                if (!_shouldBeVisible)
                {
                    Visibility = Visibility.Collapsed;
                }
                _isFadingOut = false;
            };
            BeginAnimation(OpacityProperty, fadeOut);
        }
    }

    private async void OnPrevClick(object sender, RoutedEventArgs e)
    {
        if (_settingsService.Current.ClickThrough) return;
        await _mediaService.TryRestartOrPreviousAsync();
    }

    private async void OnPlayPauseClick(object sender, RoutedEventArgs e)
    {
        if (_settingsService.Current.ClickThrough) return;
        await _mediaService.TryTogglePlayPauseAsync();
    }

    private async void OnNextClick(object sender, RoutedEventArgs e)
    {
        if (_settingsService.Current.ClickThrough) return;
        await _mediaService.TrySkipNextAsync();
    }

    private async void OnRepeatClick(object sender, RoutedEventArgs e)
    {
        if (_settingsService.Current.ClickThrough) return;
        await _mediaService.TryToggleRepeatAsync();
        UpdateRepeatButton();
    }

    private async void OnProgressBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_settingsService.Current.ClickThrough) return;
        if (ProgressBarContainer.ActualWidth <= 0) return;

        var clickX = e.GetPosition(ProgressBarContainer).X;
        var fraction = clickX / ProgressBarContainer.ActualWidth;
        await _mediaService.SeekToFractionAsync(fraction);
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_settingsService.Current.ClickThrough) return;
        if (!_settingsService.Current.VolumeScrollEnabled) return;

        e.Handled = true;

        int steps = Math.Max(1, Math.Abs(e.Delta) / 120);
        for (int i = 0; i < steps; i++)
        {
            if (e.Delta > 0)
            {
                NativeMethods.VolumeUp();
            }
            else if (e.Delta < 0)
            {
                NativeMethods.VolumeDown();
            }
        }
    }

    private void OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_settingsService.Current.ClickThrough) return;
        if (FindResource("WidgetContextMenu") is ContextMenu menu)
        {
            menu.PlacementTarget = RootContainer;
            menu.IsOpen = true;
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        _openSettingsAction();
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }

    protected override void OnClosed(EventArgs e)
    {
        _topmostTimer.Stop();
        _mediaService.MediaChanged -= MediaService_MediaChanged;
        _mediaService.PropertyChanged -= MediaService_PropertyChanged;
        _settingsService.SettingsChanged -= SettingsService_SettingsChanged;
        _themeService.ThemeChanged -= ThemeService_ThemeChanged;

        base.OnClosed(e);
    }
}
