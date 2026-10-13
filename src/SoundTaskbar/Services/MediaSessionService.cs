using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Media.Imaging;
using SoundTaskbar.Helpers;
using Windows.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace SoundTaskbar.Services;

public sealed class MediaSessionSnapshot
{
    public string SessionKey { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Artist { get; init; } = string.Empty;
    public string AppId { get; init; } = string.Empty;
    public bool IsPlaying { get; init; }
    public bool IsParkable { get; init; }
    public bool CanPlay { get; init; }
    public bool CanPause { get; init; }
    public bool CanSkipNext { get; init; }
    public bool CanSkipPrevious { get; init; }
    public bool CanSeek { get; init; }
    public bool IsVideo { get; init; }
    public TimeSpan Position { get; init; }
    public TimeSpan Duration { get; init; }
    public BitmapImage? Artwork { get; init; }
    public bool HasSession { get; init; }
}

public sealed class MediaSessionService : IDisposable
{
    public const int SeekStepSeconds = 10;

    private readonly object _gate = new();
    private readonly Dictionary<string, GlobalSystemMediaTransportControlsSession> _sessions =
        new(StringComparer.OrdinalIgnoreCase);

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private string? _focusedKey;
    private bool _disposed;

    public event EventHandler? Changed;
    public event EventHandler? TimelineChanged;

    public string? FocusedKey
    {
        get
        {
            lock (_gate)
            {
                return _focusedKey;
            }
        }
    }

    public async Task StartAsync()
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _manager.CurrentSessionChanged += OnCurrentSessionChanged;
        _manager.SessionsChanged += OnSessionsChanged;
        RebuildSessions();
        RaiseChanged();
    }

    public IReadOnlyList<MediaSessionSnapshot> CaptureAll()
    {
        lock (_gate)
        {
            if (_sessions.Count == 0)
            {
                return Array.Empty<MediaSessionSnapshot>();
            }

            var list = new List<MediaSessionSnapshot>(_sessions.Count);
            foreach (var (key, session) in _sessions)
            {
                list.Add(CaptureSession(key, session, artwork: null));
            }

            return list;
        }
    }

    public async Task<IReadOnlyList<MediaSessionSnapshot>> CaptureAllFullAsync()
    {
        List<(string Key, GlobalSystemMediaTransportControlsSession Session)> pairs;
        lock (_gate)
        {
            pairs = _sessions.Select(kv => (kv.Key, kv.Value)).ToList();
        }

        if (pairs.Count == 0)
        {
            return Array.Empty<MediaSessionSnapshot>();
        }

        // Fetch media props in parallel — SMTC calls are I/O bound; then snapshot under lock.
        var mediaTasks = pairs.Select(async pair =>
        {
            BitmapImage? artwork = null;
            var title = string.Empty;
            var artist = string.Empty;
            MediaPlaybackType? playbackType = (MediaPlaybackType?)null;
            try
            {
                var props = await pair.Session.TryGetMediaPropertiesAsync();
                if (props is not null)
                {
                    title = props.Title ?? string.Empty;
                    artist = props.Artist ?? string.Empty;
                    playbackType = props.PlaybackType;
                    if (props.Thumbnail is not null)
                    {
                        artwork = await ToBitmapImageAsync(props.Thumbnail);
                    }
                }
            }
            catch
            {
                // keep empty media props
            }

            return (pair.Key, pair.Session, title, artist, artwork, playbackType);
        });

        var media = await Task.WhenAll(mediaTasks);
        var results = new List<MediaSessionSnapshot>(media.Length);
        lock (_gate)
        {
            foreach (var (key, session, title, artist, artwork, playbackType) in media)
            {
                if (!_sessions.ContainsKey(key))
                {
                    continue;
                }

                var snap = CaptureSession(key, session, artwork);
                results.Add(CloneWithMedia(snap, title, artist, artwork, playbackType));
            }
        }

        return results;
    }

    public MediaSessionSnapshot Capture(string? sessionKey = null)
    {
        lock (_gate)
        {
            var key = ResolveKey(sessionKey);
            if (key is null || !_sessions.TryGetValue(key, out var session))
            {
                return new MediaSessionSnapshot();
            }

            return CaptureSession(key, session, artwork: null);
        }
    }

    public async Task<MediaSessionSnapshot> CaptureFullAsync(string? sessionKey = null)
    {
        GlobalSystemMediaTransportControlsSession? session;
        string? key;
        lock (_gate)
        {
            key = ResolveKey(sessionKey);
            session = key is not null && _sessions.TryGetValue(key, out var s) ? s : null;
        }

        if (key is null || session is null)
        {
            return new MediaSessionSnapshot();
        }

        var snapshot = Capture(key);
        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            if (props is null)
            {
                return snapshot;
            }

            BitmapImage? artwork = null;
            if (props.Thumbnail is not null)
            {
                artwork = await ToBitmapImageAsync(props.Thumbnail);
            }

            return CloneWithMedia(
                snapshot,
                props.Title ?? string.Empty,
                props.Artist ?? string.Empty,
                artwork,
                props.PlaybackType);
        }
        catch
        {
            return snapshot;
        }
    }

    public void Focus(string sessionKey)
    {
        lock (_gate)
        {
            if (_sessions.ContainsKey(sessionKey))
            {
                _focusedKey = sessionKey;
            }
        }
    }

    /// <summary>
    /// Rebuild the session map from the OS (used while parked chips are visible).
    /// Returns true when the set of session keys changed.
    /// </summary>
    public bool PollSessions()
    {
        HashSet<string> before;
        lock (_gate)
        {
            before = new HashSet<string>(_sessions.Keys, StringComparer.OrdinalIgnoreCase);
        }

        RebuildSessions();

        lock (_gate)
        {
            if (before.Count != _sessions.Count)
            {
                return true;
            }

            foreach (var key in _sessions.Keys)
            {
                if (!before.Contains(key))
                {
                    return true;
                }
            }

            return false;
        }
    }

    public Task<bool> PlayAsync(string? sessionKey = null) =>
        InvokeAsync(sessionKey, s => s.TryPlayAsync().AsTask());

    public Task<bool> PauseAsync(string? sessionKey = null) =>
        InvokeAsync(sessionKey, s => s.TryPauseAsync().AsTask());

    public Task<bool> SkipNextAsync(string? sessionKey = null) =>
        InvokeAsync(sessionKey, s => s.TrySkipNextAsync().AsTask());

    public Task<bool> SkipPreviousAsync(string? sessionKey = null) =>
        InvokeAsync(sessionKey, s => s.TrySkipPreviousAsync().AsTask());

    public async Task<bool> SeekByAsync(TimeSpan delta, string? sessionKey = null)
    {
        var snapshot = Capture(sessionKey);
        if (!snapshot.HasSession || !snapshot.CanSeek)
        {
            return false;
        }

        var target = snapshot.Position + delta;
        if (target < TimeSpan.Zero)
        {
            target = TimeSpan.Zero;
        }

        if (snapshot.Duration > TimeSpan.Zero && target > snapshot.Duration)
        {
            target = snapshot.Duration;
        }

        return await InvokeAsync(
            snapshot.SessionKey,
            s => s.TryChangePlaybackPositionAsync(target.Ticks).AsTask());
    }

    public Task<bool> TogglePlayPauseAsync(string? sessionKey = null)
    {
        var snapshot = Capture(sessionKey);
        return snapshot.IsPlaying ? PauseAsync(snapshot.SessionKey) : PlayAsync(snapshot.SessionKey);
    }

    public Task<bool> PreviousOrSeekBackAsync(string? sessionKey = null)
    {
        var snapshot = Capture(sessionKey);
        if (snapshot.CanSkipPrevious)
        {
            return SkipPreviousAsync(snapshot.SessionKey);
        }

        return snapshot.CanSeek
            ? SeekByAsync(TimeSpan.FromSeconds(-SeekStepSeconds), snapshot.SessionKey)
            : Task.FromResult(false);
    }

    public Task<bool> NextOrSeekForwardAsync(string? sessionKey = null)
    {
        var snapshot = Capture(sessionKey);
        if (snapshot.CanSkipNext)
        {
            return SkipNextAsync(snapshot.SessionKey);
        }

        return snapshot.CanSeek
            ? SeekByAsync(TimeSpan.FromSeconds(SeekStepSeconds), snapshot.SessionKey)
            : Task.FromResult(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ClearSessions();

        if (_manager is not null)
        {
            _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
            _manager.SessionsChanged -= OnSessionsChanged;
            _manager = null;
        }
    }

    private Task<bool> InvokeAsync(
        string? sessionKey,
        Func<GlobalSystemMediaTransportControlsSession, Task<bool>> action)
    {
        GlobalSystemMediaTransportControlsSession? session;
        lock (_gate)
        {
            var key = ResolveKey(sessionKey);
            session = key is not null && _sessions.TryGetValue(key, out var s) ? s : null;
        }

        return session is null ? Task.FromResult(false) : action(session);
    }

    private string? ResolveKey(string? sessionKey)
    {
        if (!string.IsNullOrWhiteSpace(sessionKey) && _sessions.ContainsKey(sessionKey))
        {
            return sessionKey;
        }

        if (_focusedKey is not null && _sessions.ContainsKey(_focusedKey))
        {
            return _focusedKey;
        }

        return _sessions.Keys.FirstOrDefault();
    }

    private void OnCurrentSessionChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        CurrentSessionChangedEventArgs args)
    {
        RebuildSessions();
        RaiseChanged();
    }

    private void OnSessionsChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        SessionsChangedEventArgs args)
    {
        RebuildSessions();
        RaiseChanged();
    }

    private void RebuildSessions()
    {
        if (_manager is null)
        {
            return;
        }

        lock (_gate)
        {
            ClearSessionsUnlocked();

            var sessions = _manager.GetSessions();
            var appCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            string? currentKey = null;
            var current = _manager.GetCurrentSession();

            foreach (var session in sessions)
            {
                var appId = session.SourceAppUserModelId ?? "unknown";
                appCounts.TryGetValue(appId, out var index);
                appCounts[appId] = index + 1;
                var key = index == 0 ? appId : $"{appId}#{index}";

                _sessions[key] = session;
                session.MediaPropertiesChanged += OnMediaPropertiesChanged;
                session.PlaybackInfoChanged += OnPlaybackInfoChanged;
                session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;

                if (current is not null && ReferenceEquals(session, current))
                {
                    currentKey = key;
                }
            }

            _focusedKey = currentKey ?? _sessions.Keys.FirstOrDefault();
        }
    }

    private void ClearSessions()
    {
        lock (_gate)
        {
            ClearSessionsUnlocked();
        }
    }

    private void ClearSessionsUnlocked()
    {
        foreach (var session in _sessions.Values)
        {
            session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
        }

        _sessions.Clear();
        _focusedKey = null;
    }

    private static MediaSessionSnapshot CaptureSession(
        string key,
        GlobalSystemMediaTransportControlsSession session,
        BitmapImage? artwork)
    {
        try
        {
            var playback = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();
            var controls = playback.Controls;
            var status = playback.PlaybackStatus;
            var isPlaying = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            // Keep chip only while truly paused/playing — Stopped/Closed means app is leaving.
            var isParkable = isPlaying ||
                             status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused;

            var appId = session.SourceAppUserModelId ?? string.Empty;
            return new MediaSessionSnapshot
            {
                HasSession = true,
                SessionKey = key,
                AppId = appId,
                IsPlaying = isPlaying,
                IsParkable = isParkable,
                CanPlay = controls.IsPlayEnabled,
                CanPause = controls.IsPauseEnabled,
                CanSkipNext = controls.IsNextEnabled,
                CanSkipPrevious = controls.IsPreviousEnabled,
                CanSeek = controls.IsPlaybackPositionEnabled,
                IsVideo = MediaKindHelper.IsVideo(
                    playbackType: null,
                    appId,
                    controls.IsPreviousEnabled,
                    controls.IsNextEnabled,
                    controls.IsPlaybackPositionEnabled),
                Position = timeline.Position,
                Duration = timeline.EndTime > timeline.StartTime
                    ? timeline.EndTime - timeline.StartTime
                    : TimeSpan.Zero,
                Artwork = artwork,
                Title = string.Empty,
                Artist = string.Empty
            };
        }
        catch
        {
            return new MediaSessionSnapshot { SessionKey = key, HasSession = false };
        }
    }

    private void OnMediaPropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        MediaPropertiesChangedEventArgs args) =>
        RaiseChanged();

    private void OnPlaybackInfoChanged(
        GlobalSystemMediaTransportControlsSession sender,
        PlaybackInfoChangedEventArgs args) =>
        RaiseChanged();

    private void OnTimelinePropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        TimelinePropertiesChangedEventArgs args) =>
        TimelineChanged?.Invoke(this, EventArgs.Empty);

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private static MediaSessionSnapshot CloneWithMedia(
        MediaSessionSnapshot snap,
        string title,
        string artist,
        BitmapImage? artwork,
        MediaPlaybackType? playbackType) =>
        new()
        {
            HasSession = snap.HasSession,
            SessionKey = snap.SessionKey,
            Title = title,
            Artist = artist,
            AppId = snap.AppId,
            IsPlaying = snap.IsPlaying,
            IsParkable = snap.IsParkable,
            CanPlay = snap.CanPlay,
            CanPause = snap.CanPause,
            CanSkipNext = snap.CanSkipNext,
            CanSkipPrevious = snap.CanSkipPrevious,
            CanSeek = snap.CanSeek,
            IsVideo = MediaKindHelper.IsVideo(
                playbackType,
                snap.AppId,
                snap.CanSkipPrevious,
                snap.CanSkipNext,
                snap.CanSeek),
            Position = snap.Position,
            Duration = snap.Duration,
            Artwork = artwork ?? snap.Artwork
        };

    private static async Task<BitmapImage?> ToBitmapImageAsync(IRandomAccessStreamReference thumbnail)
    {
        try
        {
            using var randomAccessStream = await thumbnail.OpenReadAsync();
            await using var netStream = randomAccessStream.AsStreamForRead();
            using var memory = new MemoryStream();
            await netStream.CopyToAsync(memory);
            memory.Position = 0;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = memory;
            // Taskbar art is 28px — decode small to cut decode CPU / memory.
            image.DecodePixelWidth = 64;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }
}
