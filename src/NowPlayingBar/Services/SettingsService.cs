using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace NowPlayingBar.Services;

public class AppSettings
{
    public double OffsetX { get; set; } = 270;
    public double OffsetY { get; set; } = 0;
    public double MaxWidth { get; set; } = 380;
    public double FontSize { get; set; } = 13;
    public bool ShowAlbumArt { get; set; } = true;
    public bool ShowOnlyWhenPlaying { get; set; } = false;
    public bool ClickThrough { get; set; } = false;
    public string TextFormat { get; set; } = "{artist} - {title}";
    public double MarqueeSpeed { get; set; } = 30; // Pixels per second
    public bool CleanYoutubeTitle { get; set; } = true;
    public bool StartWithWindows { get; set; } = false;
    public bool VolumeScrollEnabled { get; set; } = true;

    // Hotkey settings
    public string HotkeyPlayPause { get; set; } = "";
    public string HotkeyNext { get; set; } = "";
    public string HotkeyPrevious { get; set; } = "";
    public string HotkeyToggleWidget { get; set; } = "";
}

public class SettingsService
{
    private static readonly string AppDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NowPlayingBar");
    private static readonly string SettingsFile = Path.Combine(AppDataFolder, "settings.json");
    private const string RunRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppRegistryName = "NowPlayingBar";

    public AppSettings Current { get; private set; }

    public event EventHandler? SettingsChanged;

    public SettingsService()
    {
        Current = Load();
    }

    public void UpdateLive(AppSettings settings)
    {
        Current = settings;
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                var json = File.ReadAllText(SettingsFile);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings != null)
                {
                    // Check registry to see if StartWithWindows is actually set
                    settings.StartWithWindows = IsStartWithWindowsEnabled();
                    return settings;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("Failed to load settings.json, using defaults", ex);
        }

        var defaultSettings = new AppSettings();
        defaultSettings.StartWithWindows = IsStartWithWindowsEnabled();
        return defaultSettings;
    }

    public void Save(AppSettings settings)
    {
        try
        {
            if (!Directory.Exists(AppDataFolder))
            {
                Directory.CreateDirectory(AppDataFolder);
            }

            SetStartWithWindows(settings.StartWithWindows);

            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(settings, options);
            File.WriteAllText(SettingsFile, json);
            Current = settings;
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Logger.LogError("Failed to save settings.json", ex);
        }
    }

    private bool IsStartWithWindowsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, false);
            return key?.GetValue(AppRegistryName) != null;
        }
        catch (Exception ex)
        {
            Logger.LogError("Failed to read StartWithWindows registry key", ex);
            return false;
        }
    }

    private void SetStartWithWindows(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, true);
            if (key == null) return;

            if (enable)
            {
                var exePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exePath))
                {
                    key.SetValue(AppRegistryName, $"\"{exePath}\"");
                }
            }
            else
            {
                if (key.GetValue(AppRegistryName) != null)
                {
                    key.DeleteValue(AppRegistryName, false);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError($"Failed to update StartWithWindows registry key (enable={enable})", ex);
        }
    }
}
