using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SoundTaskbar.Helpers;
using SoundTaskbar.Services;
using Visibility = System.Windows.Visibility;

namespace SoundTaskbar.ViewModels;

public sealed class MediaViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly MediaSessionService _media;
    private readonly AppVolumeService _volume;
    private readonly DispatcherTimer _uiTicker;
    private readonly DispatcherTimer _volumeTicker;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private bool _disposed;

    private string _title = "Нет активного медиа";
    private string _artist = string.Empty;
    private string _appId = string.Empty;
    private string _timeText = "0:00 / 0:00";
    private double _progress;
    private bool _isPlaying;
    private bool _hasSession;
    private bool _canSkipNext;
    private bool _canSkipPrevious;
    private bool _canSeek;
    private bool _canTogglePlayback;
    private BitmapImage? _artwork;
    private float _volumeLevel = 1f;
    private bool _isMuted;
    private bool _canControlVolume;
    private bool _isVolumeEditorOpen;
    private double _peakLevel;
    private bool _suppressVolumeWrite;

    private TimeSpan _anchorPosition;
    private TimeSpan _duration;
    private double _anchorSeconds;
    private double _playbackRate;

    public const double MeterHeight = 24;

    public MediaViewModel()
    {
        _media = new MediaSessionService();
        _volume = new AppVolumeService();
        _media.Changed += OnMediaChanged;
        _media.TimelineChanged += OnTimelineChanged;

        PreviousCommand = new RelayCommand(
            async _ => await _media.PreviousOrSeekBackAsync(),
            _ => CanGoPrevious);
        PlayPauseCommand = new RelayCommand(async _ => await _media.TogglePlayPauseAsync(), _ => CanTogglePlayback);
        NextCommand = new RelayCommand(
            async _ => await _media.NextOrSeekForwardAsync(),
            _ => CanGoNext);

        // SMTC position updates sparsely — interpolate locally for a smooth progress bar.
        _uiTicker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _uiTicker.Tick += (_, _) => TickProgress();

        // Peak meter needs a fast poll; volume number can update with it.
        _volumeTicker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _volumeTicker.Tick += (_, _) => RefreshVolume();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public RelayCommand PreviousCommand { get; }
    public RelayCommand PlayPauseCommand { get; }
    public RelayCommand NextCommand { get; }

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

    public string AppId
    {
        get => _appId;
        private set => SetField(ref _appId, value);
    }

    public string TimeText
    {
        get => _timeText;
        private set => SetField(ref _timeText, value);
    }

    public double Progress
    {
        get => _progress;
        private set => SetField(ref _progress, value);
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (SetField(ref _isPlaying, value))
            {
                OnPropertyChanged(nameof(PlayPauseLabel));
            }
        }
    }

    public string PlayPauseLabel => IsPlaying ? "❚❚" : "▶";

    public bool HasSession
    {
        get => _hasSession;
        private set => SetField(ref _hasSession, value);
    }

    public bool CanSkipNext
    {
        get => _canSkipNext;
        private set
        {
            if (SetField(ref _canSkipNext, value))
            {
                OnPropertyChanged(nameof(CanGoNext));
                OnPropertyChanged(nameof(NextGlyph));
                OnPropertyChanged(nameof(NextToolTip));
            }
        }
    }

    public bool CanSkipPrevious
    {
        get => _canSkipPrevious;
        private set
        {
            if (SetField(ref _canSkipPrevious, value))
            {
                OnPropertyChanged(nameof(CanGoPrevious));
                OnPropertyChanged(nameof(PreviousGlyph));
                OnPropertyChanged(nameof(PreviousToolTip));
            }
        }
    }

    public bool CanSeek
    {
        get => _canSeek;
        private set
        {
            if (SetField(ref _canSeek, value))
            {
                OnPropertyChanged(nameof(CanGoNext));
                OnPropertyChanged(nameof(CanGoPrevious));
                OnPropertyChanged(nameof(NextGlyph));
                OnPropertyChanged(nameof(PreviousGlyph));
                OnPropertyChanged(nameof(NextToolTip));
                OnPropertyChanged(nameof(PreviousToolTip));
            }
        }
    }

    public bool CanGoNext => CanSkipNext || CanSeek;

    public bool CanGoPrevious => CanSkipPrevious || CanSeek;

    public string NextGlyph => CanSkipNext ? "⏭" : "⏩";

    public string PreviousGlyph => CanSkipPrevious ? "⏮" : "⏪";

    public string NextToolTip => CanSkipNext ? "Следующий" : "Вперёд 10с";

    public string PreviousToolTip => CanSkipPrevious ? "Предыдущий" : "Назад 10с";

    public bool CanTogglePlayback
    {
        get => _canTogglePlayback;
        private set => SetField(ref _canTogglePlayback, value);
    }

    public BitmapImage? Artwork
    {
        get => _artwork;
        private set => SetField(ref _artwork, value);
    }

    public float Volume
    {
        get => _volumeLevel;
        set
        {
            var clamped = Math.Clamp(value, 0f, 1f);
            if (Math.Abs(_volumeLevel - clamped) < 0.0005f)
            {
                return;
            }

            if (!_suppressVolumeWrite && CanControlVolume && !string.IsNullOrWhiteSpace(AppId))
            {
                _volume.TrySetVolume(AppId, clamped);
            }

            _volumeLevel = clamped;
            if (_volumeLevel > 0.001f)
            {
                IsMuted = false;
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(VolumePercent));
            OnPropertyChanged(nameof(VolumeGlyph));
        }
    }

    public int VolumePercent => (int)Math.Round(Volume * 100);

    public bool IsMuted
    {
        get => _isMuted;
        private set
        {
            if (SetField(ref _isMuted, value))
            {
                OnPropertyChanged(nameof(VolumeGlyph));
            }
        }
    }

    public bool CanControlVolume
    {
        get => _canControlVolume;
        private set => SetField(ref _canControlVolume, value);
    }

    public bool IsVolumeEditorOpen
    {
        get => _isVolumeEditorOpen;
        set
        {
            if (SetField(ref _isVolumeEditorOpen, value))
            {
                OnPropertyChanged(nameof(VolumeEditorVisibility));
            }
        }
    }

    public Visibility VolumeEditorVisibility =>
        IsVolumeEditorOpen ? Visibility.Visible : Visibility.Collapsed;

    public double PeakLevel
    {
        get => _peakLevel;
        private set
        {
            if (SetField(ref _peakLevel, value))
            {
                OnPropertyChanged(nameof(PeakCoverHeight));
            }
        }
    }

    public double PeakCoverHeight => Math.Clamp((1.0 - PeakLevel) * MeterHeight, 0, MeterHeight);

    public string VolumeGlyph =>
        !CanControlVolume ? "🔈" :
        IsMuted || Volume <= 0.001f ? "🔇" :
        Volume < 0.35f ? "🔉" : "🔊";

    public async Task InitializeAsync()
    {
        await _media.StartAsync();
        await RefreshAsync(full: true);
        _uiTicker.Start();
        _volumeTicker.Start();
    }

    public void RefreshVolume()
    {
        if (_disposed || string.IsNullOrWhiteSpace(AppId))
        {
            CanControlVolume = false;
            PeakLevel = 0;
            return;
        }

        var state = _volume.GetState(AppId);
        CanControlVolume = state.Found;
        if (!state.Found)
        {
            PeakLevel = 0;
            if (IsVolumeEditorOpen)
            {
                IsVolumeEditorOpen = false;
            }

            return;
        }

        if (!IsVolumeEditorOpen)
        {
            _suppressVolumeWrite = true;
            try
            {
                Volume = state.Volume;
            }
            finally
            {
                _suppressVolumeWrite = false;
            }
        }

        IsMuted = state.IsMuted;
        // Visual loudness ≈ signal peak (and zero when muted).
        PeakLevel = state.IsMuted ? 0 : state.Peak;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _uiTicker.Stop();
        _volumeTicker.Stop();
        _media.Changed -= OnMediaChanged;
        _media.TimelineChanged -= OnTimelineChanged;
        _media.Dispose();
        _volume.Dispose();
        _refreshLock.Dispose();
    }

    private void OnMediaChanged(object? sender, EventArgs e)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        _ = dispatcher.InvokeAsync(async () => await RefreshAsync(full: true));
    }

    private void OnTimelineChanged(object? sender, EventArgs e)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        _ = dispatcher.InvokeAsync(async () => await RefreshAsync(full: false));
    }

    private async Task RefreshAsync(bool full)
    {
        if (_disposed)
        {
            return;
        }

        if (!await _refreshLock.WaitAsync(0))
        {
            return;
        }

        try
        {
            var snapshot = full ? await _media.CaptureFullAsync() : _media.Capture();
            Apply(snapshot, keepArtwork: !full);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private void Apply(MediaSessionSnapshot snapshot, bool keepArtwork)
    {
        HasSession = snapshot.HasSession;
        if (!snapshot.HasSession)
        {
            Title = "Нет активного медиа";
            Artist = "Запусти музыку или видео";
            AppId = string.Empty;
            _duration = TimeSpan.Zero;
            SetTimelineAnchor(TimeSpan.Zero, playing: false);
            TimeText = "0:00 / 0:00";
            Progress = 0;
            IsPlaying = false;
            CanSkipNext = false;
            CanSkipPrevious = false;
            CanSeek = false;
            CanTogglePlayback = false;
            CanControlVolume = false;
            IsVolumeEditorOpen = false;
            PeakLevel = 0;
            Artwork = null;
            return;
        }

        if (!keepArtwork || !string.IsNullOrWhiteSpace(snapshot.Title))
        {
            Title = string.IsNullOrWhiteSpace(snapshot.Title) ? "Без названия" : snapshot.Title;
        }

        if (!keepArtwork || !string.IsNullOrWhiteSpace(snapshot.Artist))
        {
            Artist = string.IsNullOrWhiteSpace(snapshot.Artist) ? snapshot.AppId : snapshot.Artist;
        }

        var appChanged = !string.Equals(AppId, snapshot.AppId, StringComparison.OrdinalIgnoreCase);
        AppId = snapshot.AppId;
        CanSkipNext = snapshot.CanSkipNext;
        CanSkipPrevious = snapshot.CanSkipPrevious;
        CanSeek = snapshot.CanSeek;
        CanTogglePlayback = snapshot.IsPlaying ? snapshot.CanPause : snapshot.CanPlay;

        if (appChanged || !CanControlVolume)
        {
            RefreshVolume();
        }

        var duration = snapshot.Duration < TimeSpan.Zero ? TimeSpan.Zero : snapshot.Duration;
        var position = snapshot.Position < TimeSpan.Zero ? TimeSpan.Zero : snapshot.Position;
        if (duration > TimeSpan.Zero && position > duration)
        {
            position = duration;
        }

        SyncTimeline(position, duration, snapshot.IsPlaying);
        IsPlaying = snapshot.IsPlaying;

        if (!keepArtwork)
        {
            Artwork = snapshot.Artwork;
        }

        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>
    /// SMTC often re-reports a stale Position. Never jump backwards while playing unless it looks like a seek.
    /// </summary>
    private void SyncTimeline(TimeSpan reported, TimeSpan duration, bool playing)
    {
        _duration = duration;

        if (!playing)
        {
            SetTimelineAnchor(reported, playing: false);
            PublishTimeline(reported);
            return;
        }

        var estimated = EstimatePosition();
        var deltaSeconds = (reported - estimated).TotalSeconds;

        // Large jump either way = seek / track change.
        if (Math.Abs(deltaSeconds) >= 1.5 || _playbackRate <= 0)
        {
            SetTimelineAnchor(reported, playing: true);
            PublishTimeline(reported);
            return;
        }

        // Stale SMTC behind our clock — keep extrapolating, do not rewind.
        if (deltaSeconds < 0)
        {
            _playbackRate = 1;
            return;
        }

        // Source is a bit ahead — rebase gently.
        if (deltaSeconds > 0.4)
        {
            SetTimelineAnchor(reported, playing: true);
            PublishTimeline(reported);
        }
        else
        {
            _playbackRate = 1;
        }
    }

    private void SetTimelineAnchor(TimeSpan position, bool playing)
    {
        _anchorPosition = position;
        _anchorSeconds = _clock.Elapsed.TotalSeconds;
        _playbackRate = playing ? 1 : 0;
    }

    private void TickProgress()
    {
        if (!HasSession)
        {
            return;
        }

        PublishTimeline(EstimatePosition());
    }

    private TimeSpan EstimatePosition()
    {
        if (_playbackRate <= 0)
        {
            return _anchorPosition;
        }

        var elapsed = _clock.Elapsed.TotalSeconds - _anchorSeconds;
        var estimated = _anchorPosition + TimeSpan.FromSeconds(elapsed * _playbackRate);
        if (_duration > TimeSpan.Zero && estimated > _duration)
        {
            estimated = _duration;
        }

        if (estimated < TimeSpan.Zero)
        {
            estimated = TimeSpan.Zero;
        }

        return estimated;
    }

    private void PublishTimeline(TimeSpan position)
    {
        var nextText = $"{Format(position)} / {Format(_duration)}";
        var nextProgress = _duration.TotalSeconds <= 0
            ? 0
            : Math.Clamp(position.TotalSeconds / _duration.TotalSeconds, 0, 1);

        // Skip tiny progress repaints that force layered-window redraws.
        if (nextText == TimeText && Math.Abs(nextProgress - Progress) < 0.0025)
        {
            return;
        }

        TimeText = nextText;
        Progress = nextProgress;
    }

    private static string Format(TimeSpan value)
    {
        if (value.TotalHours >= 1)
        {
            return value.ToString(@"h\:mm\:ss");
        }

        return value.ToString(@"m\:ss");
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
