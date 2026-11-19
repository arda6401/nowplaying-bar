using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace NowPlayingBar.Services;

public class MediaService : INotifyPropertyChanged, IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _activeSession;
    private readonly System.Timers.Timer _fallbackTimer;
    private readonly System.Timers.Timer _progressTimer;
    private readonly SettingsService _settingsService;
    private readonly object _lock = new();

    private string _title = string.Empty;
    private string _artist = string.Empty;
    private string _albumTitle = string.Empty;
    private BitmapImage? _thumbnail;
    private GlobalSystemMediaTransportControlsSessionPlaybackStatus _playbackStatus =
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;
    private bool _hasMedia;
    private Windows.Media.MediaPlaybackAutoRepeatMode _repeatMode =
        Windows.Media.MediaPlaybackAutoRepeatMode.None;

    private TimeSpan _position = TimeSpan.Zero;
    private TimeSpan _duration = TimeSpan.Zero;
    private DateTime _lastTimelineUpdate = DateTime.MinValue;

    public string Title
    {
        get => _title;
        private set => SetField(ref _title, value);
    }

    public string Artist
    {
        get => _artist;
        private set => SetField(ref _artist, value);
    }

    public string AlbumTitle
    {
        get => _albumTitle;
        private set => SetField(ref _albumTitle, value);
    }

    public BitmapImage? Thumbnail
    {
        get => _thumbnail;
        private set => SetField(ref _thumbnail, value);
    }

    private static readonly Regex YoutubeRegex = new(@"\s*-\s*YouTube$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TopicRegex = new(@"\s*-\s*Topic$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public GlobalSystemMediaTransportControlsSessionPlaybackStatus PlaybackStatus
    {
        get => _playbackStatus;
        private set
        {
            if (SetField(ref _playbackStatus, value))
            {
                OnPropertyChanged(nameof(IsPlaying));

                // Manage progress timer dynamically: only run when actively playing
                if (IsPlaying)
                {
                    if (!_progressTimer.Enabled) _progressTimer.Start();
                }
                else
                {
                    if (_progressTimer.Enabled) _progressTimer.Stop();
                }
            }
        }
    }

    public bool IsPlaying => PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

    public bool HasMedia
    {
        get => _hasMedia;
        private set => SetField(ref _hasMedia, value);
    }

    public Windows.Media.MediaPlaybackAutoRepeatMode RepeatMode
    {
        get => _repeatMode;
        private set
        {
            if (SetField(ref _repeatMode, value))
            {
                OnPropertyChanged(nameof(IsRepeatOn));
            }
        }
    }

    /// <summary>True when repeat is Track or List (not None).</summary>
    public bool IsRepeatOn => _repeatMode != Windows.Media.MediaPlaybackAutoRepeatMode.None;

    public TimeSpan Position
    {
        get => _position;
        private set
        {
            if (SetField(ref _position, value))
            {
                OnPropertyChanged(nameof(ProgressFraction));
                OnPropertyChanged(nameof(FormattedTimeline));
            }
        }
    }

    public TimeSpan Duration
    {
        get => _duration;
        private set
        {
            if (SetField(ref _duration, value))
            {
                OnPropertyChanged(nameof(ProgressFraction));
                OnPropertyChanged(nameof(FormattedTimeline));
            }
        }
    }

    public double ProgressFraction
    {
        get
        {
            if (_duration.TotalSeconds <= 0) return 0.0;
            var frac = _position.TotalSeconds / _duration.TotalSeconds;
            return Math.Clamp(frac, 0.0, 1.0);
        }
    }

    public string FormattedTimeline
    {
        get
        {
            if (_duration.TotalSeconds <= 0) return string.Empty;
            var posStr = _duration.TotalHours >= 1 ? _position.ToString(@"hh\:mm\:ss") : _position.ToString(@"mm\:ss");
            var durStr = _duration.TotalHours >= 1 ? _duration.ToString(@"hh\:mm\:ss") : _duration.ToString(@"mm\:ss");
            return $"{posStr} / {durStr}";
        }
    }

    public string FormattedText
    {
        get
        {
            if (!HasMedia) return string.Empty;
            var format = _settingsService.Current.TextFormat;
            var text = format
                .Replace("{artist}", Artist, StringComparison.OrdinalIgnoreCase)
                .Replace("{title}", Title, StringComparison.OrdinalIgnoreCase)
                .Replace("{album}", AlbumTitle, StringComparison.OrdinalIgnoreCase);

            text = text.Trim(' ', '-', '–', ':');
            return string.IsNullOrWhiteSpace(text) ? Title : text;
        }
    }

    public string TooltipText
    {
        get
        {
            if (!HasMedia) return string.Empty;
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Artist)) parts.Add(Artist);
            if (!string.IsNullOrWhiteSpace(Title)) parts.Add(Title);
            if (!string.IsNullOrWhiteSpace(AlbumTitle)) parts.Add(AlbumTitle);
            if (!string.IsNullOrEmpty(FormattedTimeline)) parts.Add(FormattedTimeline);
            return string.Join(" — ", parts);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? MediaChanged;

    public MediaService(SettingsService settingsService)
    {
        _settingsService = settingsService;
        _settingsService.SettingsChanged += (_, _) => OnMediaPropertiesUpdated();

        // 3.5-second recovery timer only for fallback / dropped sessions (events handle regular changes)
        _fallbackTimer = new System.Timers.Timer(3500);
        _fallbackTimer.Elapsed += (_, _) => CheckSessionRecovery();
        _fallbackTimer.AutoReset = true;

        // 1-second progress tick for smooth timeline advancement while playing
        _progressTimer = new System.Timers.Timer(1000);
        _progressTimer.Elapsed += (_, _) => UpdateProgressTick();
        _progressTimer.AutoReset = true;
    }

    public async Task InitializeAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (_manager != null)
            {
                _manager.CurrentSessionChanged += Manager_CurrentSessionChanged;
                _manager.SessionsChanged += Manager_SessionsChanged;
                UpdateActiveSession();
                _fallbackTimer.Start();
                if (IsPlaying)
                {
                    _progressTimer.Start();
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("Failed to initialize SMTC session manager", ex);
        }
    }

    private void Manager_CurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
    {
        UpdateActiveSession();
    }

    private void Manager_SessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
    {
        UpdateActiveSession();
    }

    private void CheckSessionRecovery()
    {
        if (_activeSession == null || !HasMedia)
        {
            UpdateActiveSession();
        }
    }

    private void UpdateProgressTick()
    {
        if (IsPlaying && _duration.TotalSeconds > 0)
        {
            var next = _position.Add(TimeSpan.FromSeconds(1));
            if (next <= _duration)
            {
                Position = next;
            }

            // Seamless loop: when loop is active and track is within 1.5s of ending, rewind to start
            if (IsRepeatOn && _duration.TotalSeconds > 3 && next >= _duration.Subtract(TimeSpan.FromSeconds(1.5)))
            {
                _ = RestartCurrentTrackAsync();
            }
        }
    }

    private void UpdateActiveSession()
    {
        lock (_lock)
        {
            try
            {
                if (_manager == null) return;

                var sessions = _manager.GetSessions();
                GlobalSystemMediaTransportControlsSession? bestSession = null;

                // Priority 1: Session that is actively Playing
                foreach (var s in sessions)
                {
                    try
                    {
                        var info = s.GetPlaybackInfo();
                        if (info != null && info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                        {
                            bestSession = s;
                            break;
                        }
                    }
                    catch { }
                }

                // Priority 2: CurrentSession from manager if available
                if (bestSession == null)
                {
                    try
                    {
                        var current = _manager.GetCurrentSession();
                        if (current != null)
                        {
                            bestSession = current;
                        }
                    }
                    catch { }
                }

                // Priority 3: First available session (e.g. paused)
                if (bestSession == null && sessions.Count > 0)
                {
                    bestSession = sessions[0];
                }

                BindSession(bestSession);
            }
            catch (Exception ex)
            {
                Logger.LogError("Error updating active SMTC session", ex);
            }
        }
    }

    private void BindSession(GlobalSystemMediaTransportControlsSession? newSession)
    {
        if (_activeSession == newSession && newSession != null)
        {
            _ = RefreshSessionDetailsAsync(newSession);
            return;
        }

        if (_activeSession != null)
        {
            try
            {
                _activeSession.MediaPropertiesChanged -= Session_MediaPropertiesChanged;
                _activeSession.PlaybackInfoChanged -= Session_PlaybackInfoChanged;
                _activeSession.TimelinePropertiesChanged -= Session_TimelinePropertiesChanged;
            }
            catch { }
        }

        _activeSession = newSession;

        if (_activeSession != null)
        {
            try
            {
                _activeSession.MediaPropertiesChanged += Session_MediaPropertiesChanged;
                _activeSession.PlaybackInfoChanged += Session_PlaybackInfoChanged;
                _activeSession.TimelinePropertiesChanged += Session_TimelinePropertiesChanged;
            }
            catch { }
            _ = RefreshSessionDetailsAsync(_activeSession);
        }
        else
        {
            ClearMedia();
        }
    }

    private void Session_MediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        _ = RefreshSessionDetailsAsync(sender);
    }

    private void Session_PlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        try
        {
            var playbackInfo = sender.GetPlaybackInfo();
            if (playbackInfo != null)
            {
                PlaybackStatus = playbackInfo.PlaybackStatus;
                if (playbackInfo.AutoRepeatMode.HasValue)
                {
                    RepeatMode = playbackInfo.AutoRepeatMode.Value;
                }
                MediaChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"Failed to read playback info: {ex.Message}");
        }
    }

    private void Session_TimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
    {
        RefreshTimeline(sender);
    }

    private void RefreshTimeline(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            var timeline = session.GetTimelineProperties();
            if (timeline != null)
            {
                Position = timeline.Position;
                Duration = timeline.EndTime;
                _lastTimelineUpdate = DateTime.Now;
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"Failed to read timeline properties: {ex.Message}");
        }
    }

    private async Task RefreshSessionDetailsAsync(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            GlobalSystemMediaTransportControlsSessionMediaProperties? props = null;
            try
            {
                props = await session.TryGetMediaPropertiesAsync();
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"TryGetMediaPropertiesAsync threw: {ex.Message}");
            }

            var playbackInfo = session.GetPlaybackInfo();
            var status = playbackInfo?.PlaybackStatus ?? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;

            if (props == null || (string.IsNullOrWhiteSpace(props.Title) && string.IsNullOrWhiteSpace(props.Artist)))
            {
                PlaybackStatus = status;
                HasMedia = false;
                OnMediaPropertiesUpdated();
                return;
            }

            var title = CleanTitle(props.Title ?? string.Empty);
            var artist = (props.Artist ?? string.Empty).Trim();
            var album = (props.AlbumTitle ?? string.Empty).Trim();

            BitmapImage? thumbnail = null;
            if (props.Thumbnail != null)
            {
                try
                {
                    thumbnail = await LoadThumbnailAsync(props.Thumbnail);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"Failed to load thumbnail: {ex.Message}");
                }
            }

            Title = title;
            Artist = artist;
            AlbumTitle = album;
            Thumbnail = thumbnail;
            PlaybackStatus = status;
            HasMedia = !string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(artist);

            if (playbackInfo?.AutoRepeatMode.HasValue == true)
            {
                RepeatMode = playbackInfo.AutoRepeatMode.Value;
            }

            RefreshTimeline(session);
            OnMediaPropertiesUpdated();
        }
        catch (Exception ex)
        {
            Logger.LogError("Error in RefreshSessionDetailsAsync", ex);
        }
    }

    private string CleanTitle(string rawTitle)
    {
        if (string.IsNullOrWhiteSpace(rawTitle)) return string.Empty;
        var title = rawTitle.Trim();

        if (_settingsService.Current.CleanYoutubeTitle)
        {
            title = YoutubeRegex.Replace(title, "");
            title = TopicRegex.Replace(title, "");
        }

        return title.Trim();
    }

    private static async Task<BitmapImage?> LoadThumbnailAsync(IRandomAccessStreamReference streamRef)
    {
        try
        {
            using var stream = await streamRef.OpenReadAsync();
            if (stream == null || stream.Size == 0) return null;

            using var netStream = stream.AsStreamForRead();
            int capacity = (int)Math.Min(stream.Size, 2 * 1024 * 1024);
            using var memoryStream = new MemoryStream(capacity);
            await netStream.CopyToAsync(memoryStream);
            memoryStream.Position = 0;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = memoryStream;
            bitmap.DecodePixelWidth = 56;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private void ClearMedia()
    {
        Title = string.Empty;
        Artist = string.Empty;
        AlbumTitle = string.Empty;
        Thumbnail = null;
        PlaybackStatus = GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;
        HasMedia = false;
        Position = TimeSpan.Zero;
        Duration = TimeSpan.Zero;
        OnMediaPropertiesUpdated();
    }

    private void OnMediaPropertiesUpdated()
    {
        OnPropertyChanged(nameof(FormattedText));
        OnPropertyChanged(nameof(TooltipText));
        OnPropertyChanged(nameof(IsPlaying));
        OnPropertyChanged(nameof(ProgressFraction));
        OnPropertyChanged(nameof(FormattedTimeline));
        MediaChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<bool> TryTogglePlayPauseAsync()
    {
        if (_activeSession == null) return false;
        try
        {
            return await _activeSession.TryTogglePlayPauseAsync();
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"TryTogglePlayPauseAsync failed: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> TrySkipNextAsync()
    {
        if (_activeSession == null) return false;
        try
        {
            return await _activeSession.TrySkipNextAsync();
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"TrySkipNextAsync failed: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> TryRestartOrPreviousAsync()
    {
        if (_activeSession == null) return false;
        try
        {
            // If track has been playing for more than 3 seconds, restart the song; otherwise go to previous
            if (Position.TotalSeconds > 3.0)
            {
                var success = await _activeSession.TryChangePlaybackPositionAsync(0);
                if (success)
                {
                    Position = TimeSpan.Zero;
                    return true;
                }
            }

            return await _activeSession.TrySkipPreviousAsync();
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"TryRestartOrPreviousAsync failed: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> RestartCurrentTrackAsync()
    {
        if (_activeSession == null) return false;
        try
        {
            var res = await _activeSession.TryChangePlaybackPositionAsync(0);
            Position = TimeSpan.Zero;
            await _activeSession.TryPlayAsync();
            return res;
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"RestartCurrentTrackAsync failed: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> TryToggleRepeatAsync()
    {
        // Toggle loop state
        var newMode = IsRepeatOn
            ? Windows.Media.MediaPlaybackAutoRepeatMode.None
            : Windows.Media.MediaPlaybackAutoRepeatMode.Track;

        RepeatMode = newMode;
        Logger.LogInfo($"Loop mode set to: {RepeatMode}");

        // Attempt SMTC native repeat if supported by the media app
        if (_activeSession != null)
        {
            try
            {
                await _activeSession.TryChangeAutoRepeatModeAsync(newMode);
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"Native SMTC TryChangeAutoRepeatModeAsync threw: {ex.Message}");
            }
        }

        return IsRepeatOn;
    }

    public async Task<bool> SeekToFractionAsync(double fraction)
    {
        if (_activeSession == null || Duration.TotalSeconds <= 0) return false;
        try
        {
            fraction = Math.Clamp(fraction, 0.0, 1.0);
            var targetTicks = (long)(fraction * Duration.Ticks);
            var targetSpan = TimeSpan.FromTicks(targetTicks);
            var res = await _activeSession.TryChangePlaybackPositionAsync(targetTicks);
            if (res)
            {
                Position = targetSpan;
            }
            return res;
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"SeekToFractionAsync failed: {ex.Message}");
            return false;
        }
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    public void Dispose()
    {
        _fallbackTimer.Stop();
        _fallbackTimer.Dispose();

        _progressTimer.Stop();
        _progressTimer.Dispose();

        if (_activeSession != null)
        {
            try
            {
                _activeSession.MediaPropertiesChanged -= Session_MediaPropertiesChanged;
                _activeSession.PlaybackInfoChanged -= Session_PlaybackInfoChanged;
                _activeSession.TimelinePropertiesChanged -= Session_TimelinePropertiesChanged;
            }
            catch { }
        }

        if (_manager != null)
        {
            try
            {
                _manager.CurrentSessionChanged -= Manager_CurrentSessionChanged;
                _manager.SessionsChanged -= Manager_SessionsChanged;
            }
            catch { }
        }

        GC.SuppressFinalize(this);
    }
}
