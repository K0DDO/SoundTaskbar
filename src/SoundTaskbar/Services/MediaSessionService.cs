using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Media.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace SoundTaskbar.Services;

public sealed class MediaSessionSnapshot
{
    public string Title { get; init; } = string.Empty;
    public string Artist { get; init; } = string.Empty;
    public string AppId { get; init; } = string.Empty;
    public bool IsPlaying { get; init; }
    public bool CanPlay { get; init; }
    public bool CanPause { get; init; }
    public bool CanSkipNext { get; init; }
    public bool CanSkipPrevious { get; init; }
    public TimeSpan Position { get; init; }
    public TimeSpan Duration { get; init; }
    public BitmapImage? Artwork { get; init; }
    public bool HasSession { get; init; }
}

public sealed class MediaSessionService : IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private bool _disposed;

    public event EventHandler? Changed;

    public async Task StartAsync()
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _manager.CurrentSessionChanged += OnCurrentSessionChanged;
        _manager.SessionsChanged += OnSessionsChanged;
        AttachSession(_manager.GetCurrentSession());
        RaiseChanged();
    }

    public MediaSessionSnapshot Capture()
    {
        if (_session is null)
        {
            return new MediaSessionSnapshot();
        }

        var playback = _session.GetPlaybackInfo();
        var timeline = _session.GetTimelineProperties();
        var controls = playback.Controls;

        return new MediaSessionSnapshot
        {
            HasSession = true,
            AppId = _session.SourceAppUserModelId ?? string.Empty,
            IsPlaying = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            CanPlay = controls.IsPlayEnabled,
            CanPause = controls.IsPauseEnabled,
            CanSkipNext = controls.IsNextEnabled,
            CanSkipPrevious = controls.IsPreviousEnabled,
            Position = timeline.Position,
            Duration = timeline.EndTime > timeline.StartTime
                ? timeline.EndTime - timeline.StartTime
                : TimeSpan.Zero
        };
    }

    public async Task<MediaSessionSnapshot> CaptureFullAsync()
    {
        var snapshot = Capture();
        if (_session is null)
        {
            return snapshot;
        }

        var props = await _session.TryGetMediaPropertiesAsync();
        if (props is null)
        {
            return snapshot;
        }

        BitmapImage? artwork = null;
        if (props.Thumbnail is not null)
        {
            artwork = await ToBitmapImageAsync(props.Thumbnail);
        }

        return new MediaSessionSnapshot
        {
            HasSession = true,
            Title = props.Title ?? string.Empty,
            Artist = props.Artist ?? string.Empty,
            AppId = snapshot.AppId,
            IsPlaying = snapshot.IsPlaying,
            CanPlay = snapshot.CanPlay,
            CanPause = snapshot.CanPause,
            CanSkipNext = snapshot.CanSkipNext,
            CanSkipPrevious = snapshot.CanSkipPrevious,
            Position = snapshot.Position,
            Duration = snapshot.Duration,
            Artwork = artwork
        };
    }

    public Task<bool> PlayAsync() =>
        _session is null ? Task.FromResult(false) : _session.TryPlayAsync().AsTask();

    public Task<bool> PauseAsync() =>
        _session is null ? Task.FromResult(false) : _session.TryPauseAsync().AsTask();

    public Task<bool> SkipNextAsync() =>
        _session is null ? Task.FromResult(false) : _session.TrySkipNextAsync().AsTask();

    public Task<bool> SkipPreviousAsync() =>
        _session is null ? Task.FromResult(false) : _session.TrySkipPreviousAsync().AsTask();

    public Task<bool> TogglePlayPauseAsync()
    {
        var snapshot = Capture();
        return snapshot.IsPlaying ? PauseAsync() : PlayAsync();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DetachSession();

        if (_manager is not null)
        {
            _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
            _manager.SessionsChanged -= OnSessionsChanged;
            _manager = null;
        }
    }

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
    {
        AttachSession(sender.GetCurrentSession());
        RaiseChanged();
    }

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
    {
        AttachSession(sender.GetCurrentSession());
        RaiseChanged();
    }

    private void AttachSession(GlobalSystemMediaTransportControlsSession? session)
    {
        DetachSession();
        _session = session;
        if (_session is null)
        {
            return;
        }

        _session.MediaPropertiesChanged += OnSessionChanged;
        _session.PlaybackInfoChanged += OnSessionChanged;
        _session.TimelinePropertiesChanged += OnSessionChanged;
    }

    private void DetachSession()
    {
        if (_session is null)
        {
            return;
        }

        _session.MediaPropertiesChanged -= OnSessionChanged;
        _session.PlaybackInfoChanged -= OnSessionChanged;
        _session.TimelinePropertiesChanged -= OnSessionChanged;
        _session = null;
    }

    private void OnSessionChanged(GlobalSystemMediaTransportControlsSession sender, object args) =>
        RaiseChanged();

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

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
