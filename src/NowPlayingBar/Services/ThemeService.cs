using System.Windows.Media;
using Microsoft.Win32;

namespace NowPlayingBar.Services;

public class ThemeService : IDisposable
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public bool IsDarkTheme { get; private set; } = true;

    public Brush TextBrush => IsDarkTheme
        ? new SolidColorBrush(Color.FromRgb(240, 240, 240))
        : new SolidColorBrush(Color.FromRgb(30, 30, 30));

    public Brush SecondaryTextBrush => IsDarkTheme
        ? new SolidColorBrush(Color.FromArgb(180, 220, 220, 220))
        : new SolidColorBrush(Color.FromArgb(180, 50, 50, 50));

    public Brush BackgroundHoverBrush => IsDarkTheme
        ? new SolidColorBrush(Color.FromArgb(40, 255, 255, 255))
        : new SolidColorBrush(Color.FromArgb(30, 0, 0, 0));

    public event EventHandler? ThemeChanged;

    public ThemeService()
    {
        UpdateTheme();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General || e.Category == UserPreferenceCategory.Color)
        {
            UpdateTheme();
        }
    }

    public void UpdateTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            if (key != null)
            {
                // Taskbar uses SystemUsesLightTheme in Windows 11
                var systemUsesLight = key.GetValue("SystemUsesLightTheme");
                if (systemUsesLight is int sysVal)
                {
                    IsDarkTheme = sysVal == 0;
                }
                else
                {
                    var appsUseLight = key.GetValue("AppsUseLightTheme");
                    if (appsUseLight is int appVal)
                    {
                        IsDarkTheme = appVal == 0;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("Failed to read theme preference from registry", ex);
        }

        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        GC.SuppressFinalize(this);
    }
}
