using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SoundTaskbar.Controls;
using SoundTaskbar.Helpers;
using SoundTaskbar.Services;

namespace SoundTaskbar.ViewModels;

public sealed class MediaViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly MediaSessionService _media;
    private readonly AppVolumeService _volume;
    private readonly DispatcherTimer _uiTicker;
    private readonly DispatcherTimer _volumeTicker;
    private readonly DispatcherTimer _volumeHideTimer;
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
    private bool _seekAnimating;
    private TimeSpan _seekFrom;
    private TimeSpan _seekTo;
    private double _seekAnimStart;
    private double _seekGuardUntil;
    private long _volumeWriteTicks;
    private string _primarySessionKey = string.Empty;
    private bool _isDualPlaying;
    private readonly Dictionary<string, BitmapImage?> _artworkCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Title, string Artist)> _metaCache =
        new(StringComparer.OrdinalIgnoreCase);

    public const double MeterHeight = 24;
    private const double SeekAnimSeconds = 0.3;
    private const int VolumeWriteGraceMs = 280;

    public MediaViewModel()
    {
        _media = new MediaSessionService();
        _volume = new AppVolumeService();
        _media.Changed += OnMediaChanged;
        _media.TimelineChanged += OnTimelineChanged;

        PreviousCommand = new RelayCommand(
            async _ => await GoPreviousAsync(),
            _ => CanGoPrevious);
        PlayPauseCommand = new RelayCommand(
            async _ => await _media.TogglePlayPauseAsync(_primarySessionKey),
            _ => CanTogglePlayback);
        NextCommand = new RelayCommand(
            async _ => await GoNextAsync(),
            _ => CanGoNext);
        ToggleMuteCommand = new RelayCommand(
            _ => ToggleMute(),
            _ => CanControlVolume);

        // SMTC position updates sparsely — interpolate locally for a smooth progress bar.
        _uiTicker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _uiTicker.Tick += (_, _) => TickProgress();

        // Peak meter + live volume sync with the app mixer.
        _volumeTicker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _volumeTicker.Tick += (_, _) => RefreshVolume();

        _volumeHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _volumeHideTimer.Tick += (_, _) =>
        {
            _volumeHideTimer.Stop();
            IsVolumeEditorOpen = false;
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public RelayCommand PreviousCommand { get; }
    public RelayCommand PlayPauseCommand { get; }
    public RelayCommand NextCommand { get; }
    public RelayCommand ToggleMuteCommand { get; }

    public ObservableCollection<SideSessionViewModel> SideSessions { get; } = new();

    public ObservableCollection<SideSessionViewModel> DualSessions { get; } = new();

    public int SeekSeconds => MediaSessionService.SeekStepSeconds;

    public bool IsDualPlaying
    {
        get => _isDualPlaying;
        private set
        {
            if (SetField(ref _isDualPlaying, value))
            {
                OnPropertyChanged(nameof(SingleModeVisibility));
                OnPropertyChanged(nameof(DualModeVisibility));
            }
        }
    }

    public Visibility SingleModeVisibility =>
        IsDualPlaying ? Visibility.Collapsed : Visibility.Visible;

    public Visibility DualModeVisibility =>
        IsDualPlaying ? Visibility.Visible : Visibility.Collapsed;

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
        private set
        {
            if (SetField(ref _appId, value))
            {
                OnPropertyChanged(nameof(SourceName));
            }
        }
    }

    public string SourceName => MediaSourceFormatter.Format(AppId);

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
                OnPropertyChanged(nameof(NextIconKind));
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
                OnPropertyChanged(nameof(PreviousIconKind));
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
                OnPropertyChanged(nameof(NextIconKind));
                OnPropertyChanged(nameof(PreviousIconKind));
            }
        }
    }

    public bool CanGoNext => CanSkipNext || CanSeek;

    public bool CanGoPrevious => CanSkipPrevious || CanSeek;

    public TransportIconKind NextIconKind =>
        CanSkipNext ? TransportIconKind.SkipNext : TransportIconKind.SeekForward;

    public TransportIconKind PreviousIconKind =>
        CanSkipPrevious ? TransportIconKind.SkipPrevious : TransportIconKind.SeekBack;

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
                if (_volume.TrySetVolume(AppId, clamped))
                {
                    _volumeWriteTicks = Environment.TickCount64;
                }
            }

            _volumeLevel = clamped;
            if (_volumeLevel > 0.001f)
            {
                IsMuted = false;
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(VolumePercent));
            OnPropertyChanged(nameof(VolumeGlyph));
            OnPropertyChanged(nameof(MuteGlyph));
            OnPropertyChanged(nameof(IsEffectivelyMuted));

            if (!_suppressVolumeWrite && IsVolumeEditorOpen)
            {
                BounceVolumeHideTimer();
            }
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
                OnPropertyChanged(nameof(MuteGlyph));
                OnPropertyChanged(nameof(IsEffectivelyMuted));
            }
        }
    }

    public bool CanControlVolume
    {
        get => _canControlVolume;
        private set
        {
            if (SetField(ref _canControlVolume, value))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsVolumeEditorOpen
    {
        get => _isVolumeEditorOpen;
        set
        {
            if (!SetField(ref _isVolumeEditorOpen, value))
            {
                return;
            }

            if (value)
            {
                BounceVolumeHideTimer();
            }
            else
            {
                _volumeHideTimer.Stop();
            }
        }
    }

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

    public bool IsEffectivelyMuted => IsMuted || Volume <= 0.001f;

    // Segoe MDL2: Mute / Volume — monochrome so muted can turn red.
    public string MuteGlyph => IsEffectivelyMuted ? "\uE74F" : "\uE767";

    public void ToggleMute()
    {
        if (!CanControlVolume || string.IsNullOrWhiteSpace(AppId))
        {
            return;
        }

        if (_volume.TryToggleMute(AppId))
        {
            _volumeWriteTicks = Environment.TickCount64;
            RefreshVolume();
            if (IsVolumeEditorOpen)
            {
                BounceVolumeHideTimer();
            }
        }
    }

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

        // Always mirror Core Audio except right after our own write (avoid fighting the thumb).
        var recentlyWrote = Environment.TickCount64 - _volumeWriteTicks < VolumeWriteGraceMs;
        if (!recentlyWrote && Math.Abs(state.Volume - _volumeLevel) > 0.004f)
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
        // Peak scaled by session volume so the meter tracks the real mixer level.
        PeakLevel = state.IsMuted
            ? 0
            : Math.Clamp(state.Peak * (0.2f + 0.8f * state.Volume), 0f, 1f);
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
        _volumeHideTimer.Stop();
        _media.Changed -= OnMediaChanged;
        _media.TimelineChanged -= OnTimelineChanged;
        _media.Dispose();
        _volume.Dispose();
        _refreshLock.Dispose();
    }

    private void BounceVolumeHideTimer()
    {
        _volumeHideTimer.Stop();
        _volumeHideTimer.Start();
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
            var snapshots = full
                ? await _media.CaptureAllFullAsync()
                : _media.CaptureAll();
            ApplyAll(snapshots, keepArtwork: !full);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private void ApplyAll(IReadOnlyList<MediaSessionSnapshot> snapshots, bool keepArtwork)
    {
        if (snapshots.Count == 0)
        {
            ClearAllSessions();
            return;
        }

        foreach (var snap in snapshots)
        {
            if (snap.Artwork is not null)
            {
                _artworkCache[snap.SessionKey] = snap.Artwork;
            }

            if (!string.IsNullOrWhiteSpace(snap.Title) || !string.IsNullOrWhiteSpace(snap.Artist))
            {
                _metaCache[snap.SessionKey] = (snap.Title, snap.Artist);
            }
        }

        var enriched = snapshots
            .Select(Enrich)
            .Where(s => s.HasSession)
            .ToList();

        if (enriched.Count == 0)
        {
            ClearAllSessions();
            return;
        }

        var playing = enriched.Where(s => s.IsPlaying).ToList();
        var focusedKey = _media.FocusedKey;

        MediaSessionSnapshot primary;
        if (playing.Count >= 2)
        {
            IsDualPlaying = true;
            primary = playing.FirstOrDefault(s => s.SessionKey == focusedKey) ?? playing[0];
            SyncCollection(DualSessions, playing.Take(2).ToList());
            SideSessions.Clear();
        }
        else
        {
            IsDualPlaying = false;
            DualSessions.Clear();
            primary = playing.FirstOrDefault()
                      ?? enriched.FirstOrDefault(s => s.SessionKey == focusedKey)
                      ?? enriched[0];

            var parked = enriched
                .Where(s => s.SessionKey != primary.SessionKey)
                .OrderByDescending(s => s.IsPlaying)
                .ThenByDescending(s => s.Artwork is not null)
                .Take(2)
                .ToList();
            SyncCollection(SideSessions, parked);
        }

        _media.Focus(primary.SessionKey);
        ApplyPrimary(primary, keepArtwork);
    }

    private MediaSessionSnapshot Enrich(MediaSessionSnapshot snap)
    {
        _artworkCache.TryGetValue(snap.SessionKey, out var art);
            _metaCache.TryGetValue(snap.SessionKey, out var meta);

        var title = !string.IsNullOrWhiteSpace(snap.Title)
            ? snap.Title
            : meta.Title ?? string.Empty;
        var artist = !string.IsNullOrWhiteSpace(snap.Artist)
            ? snap.Artist
            : meta.Artist ?? string.Empty;

        return new MediaSessionSnapshot
        {
            HasSession = snap.HasSession,
            SessionKey = snap.SessionKey,
            Title = title ?? string.Empty,
            Artist = artist ?? string.Empty,
            AppId = snap.AppId,
            IsPlaying = snap.IsPlaying,
            CanPlay = snap.CanPlay,
            CanPause = snap.CanPause,
            CanSkipNext = snap.CanSkipNext,
            CanSkipPrevious = snap.CanSkipPrevious,
            CanSeek = snap.CanSeek,
            Position = snap.Position,
            Duration = snap.Duration,
            Artwork = snap.Artwork ?? art
        };
    }

    private void SyncCollection(ObservableCollection<SideSessionViewModel> target, List<MediaSessionSnapshot> source)
    {
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (source.All(s => s.SessionKey != target[i].SessionKey))
            {
                target.RemoveAt(i);
            }
        }

        foreach (var snap in source)
        {
            var existing = target.FirstOrDefault(s => s.SessionKey == snap.SessionKey);
            if (existing is null)
            {
                existing = new SideSessionViewModel(snap.SessionKey, ToggleSideSessionAsync);
                target.Add(existing);
            }

            existing.Artwork = snap.Artwork;
            existing.Title = string.IsNullOrWhiteSpace(snap.Title) ? "Без названия" : snap.Title;
            existing.SourceName = MediaSourceFormatter.Format(snap.AppId);
            existing.IsPlaying = snap.IsPlaying;
            existing.CanToggle = snap.IsPlaying ? snap.CanPause : snap.CanPlay;
        }

        // Keep visual order matching source.
        for (var i = 0; i < source.Count; i++)
        {
            var key = source[i].SessionKey;
            var currentIndex = -1;
            for (var j = 0; j < target.Count; j++)
            {
                if (target[j].SessionKey == key)
                {
                    currentIndex = j;
                    break;
                }
            }

            if (currentIndex >= 0 && currentIndex != i && i < target.Count)
            {
                target.Move(currentIndex, i);
            }
        }
    }

    private async Task ToggleSideSessionAsync(string sessionKey)
    {
        _media.Focus(sessionKey);
        await _media.TogglePlayPauseAsync(sessionKey);
        await RefreshAsync(full: true);
    }

    private void ClearAllSessions()
    {
        IsDualPlaying = false;
        SideSessions.Clear();
        DualSessions.Clear();
        _primarySessionKey = string.Empty;
        Title = "Нет активного медиа";
        Artist = "Запусти музыку или видео";
        AppId = string.Empty;
        OnPropertyChanged(nameof(SourceName));
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
        HasSession = false;
    }

    private void ApplyPrimary(MediaSessionSnapshot snapshot, bool keepArtwork)
    {
        HasSession = true;
        _primarySessionKey = snapshot.SessionKey;

        if (!keepArtwork || !string.IsNullOrWhiteSpace(snapshot.Title))
        {
            Title = string.IsNullOrWhiteSpace(snapshot.Title) ? "Без названия" : snapshot.Title;
        }

        if (!keepArtwork || !string.IsNullOrWhiteSpace(snapshot.Artist))
        {
            Artist = snapshot.Artist?.Trim() ?? string.Empty;
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

        if (!keepArtwork || snapshot.Artwork is not null)
        {
            Artwork = snapshot.Artwork ?? Artwork;
        }

        CommandManager.InvalidateRequerySuggested();
    }

    private async Task GoPreviousAsync()
    {
        if (!CanSkipPrevious && CanSeek)
        {
            BeginSeekAnimation(-SeekSeconds);
        }

        await _media.PreviousOrSeekBackAsync(_primarySessionKey);
    }

    private async Task GoNextAsync()
    {
        if (!CanSkipNext && CanSeek)
        {
            BeginSeekAnimation(SeekSeconds);
        }

        await _media.NextOrSeekForwardAsync(_primarySessionKey);
    }

    private void BeginSeekAnimation(double deltaSeconds)
    {
        var from = EstimatePosition();
        var to = ClampPosition(from + TimeSpan.FromSeconds(deltaSeconds));
        _seekFrom = from;
        _seekTo = to;
        _seekAnimStart = _clock.Elapsed.TotalSeconds;
        _seekAnimating = true;
        _seekGuardUntil = _seekAnimStart + 1.6;
        PublishTimeline(from);
    }

    private TimeSpan ClampPosition(TimeSpan position)
    {
        if (position < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        if (_duration > TimeSpan.Zero && position > _duration)
        {
            return _duration;
        }

        return position;
    }

    /// <summary>
    /// SMTC often re-reports a stale Position. Never jump backwards while playing unless it looks like a seek.
    /// </summary>
    private void SyncTimeline(TimeSpan reported, TimeSpan duration, bool playing)
    {
        _duration = duration;
        var now = _clock.Elapsed.TotalSeconds;

        // Right after local ±N seek: ignore stale SMTC that would teleport the bar back.
        if (now < _seekGuardUntil)
        {
            var estimated = EstimatePosition();
            if (Math.Abs((reported - estimated).TotalSeconds) <= 2.0)
            {
                _seekAnimating = false;
                SetTimelineAnchor(reported, playing);
                PublishTimeline(reported);
            }

            return;
        }

        if (!playing)
        {
            _seekAnimating = false;
            SetTimelineAnchor(reported, playing: false);
            PublishTimeline(reported);
            return;
        }

        var current = EstimatePosition();
        var deltaSeconds = (reported - current).TotalSeconds;

        // Large jump either way = external seek / track change.
        if (Math.Abs(deltaSeconds) >= 1.5 || _playbackRate <= 0)
        {
            _seekAnimating = false;
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
        if (_seekAnimating)
        {
            var t = (_clock.Elapsed.TotalSeconds - _seekAnimStart) / SeekAnimSeconds;
            if (t >= 1)
            {
                _seekAnimating = false;
                SetTimelineAnchor(_seekTo, IsPlaying);
                return _seekTo;
            }

            var eased = 1 - Math.Pow(1 - Math.Clamp(t, 0, 1), 3);
            var delta = (_seekTo - _seekFrom).TotalSeconds * eased;
            return ClampPosition(_seekFrom + TimeSpan.FromSeconds(delta));
        }

        if (_playbackRate <= 0)
        {
            return _anchorPosition;
        }

        var elapsed = _clock.Elapsed.TotalSeconds - _anchorSeconds;
        var estimated = _anchorPosition + TimeSpan.FromSeconds(elapsed * _playbackRate);
        return ClampPosition(estimated);
    }

    private void PublishTimeline(TimeSpan position)
    {
        var nextText = $"{Format(position)} / {Format(_duration)}";
        var nextProgress = _duration.TotalSeconds <= 0
            ? 0
            : Math.Clamp(position.TotalSeconds / _duration.TotalSeconds, 0, 1);

        // Skip tiny progress repaints that force layered-window redraws.
        var minDelta = _seekAnimating ? 0.0004 : 0.0025;
        if (nextText == TimeText && Math.Abs(nextProgress - Progress) < minDelta)
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
