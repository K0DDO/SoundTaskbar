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
    private readonly DispatcherTimer _parkedWatchdog;
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
    private bool _showParkedRail;
    private bool _parkedDismissPending;
    private double _desiredWidgetWidth = 380;
    private string? _pairPrimaryKey;
    private string? _pairSideKey;
    private DateTimeOffset? _pairExitAfter;
    private const int PairExitGraceMs = 1000;
    private BitmapImage? _appIcon;
    private readonly Dictionary<string, BitmapImage?> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private string? _iconLoadAppId;
    private readonly Dictionary<string, BitmapImage?> _artworkCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Title, string Artist)> _metaCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _kindCache = new(StringComparer.OrdinalIgnoreCase);

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
            async _ =>
            {
                if (IsVolumeEditorOpen)
                {
                    IsVolumeEditorOpen = false;
                }

                CloseSideVolumeEditors();
                await _media.TogglePlayPauseAsync(_primarySessionKey);
            },
            _ => CanTogglePlayback);
        NextCommand = new RelayCommand(
            async _ => await GoNextAsync(),
            _ => CanGoNext);
        ToggleMuteCommand = new RelayCommand(
            _ =>
            {
                CloseSideVolumeEditors();
                ToggleMute();
            },
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

        // SMTC can keep a zombie paused session after quit — poll + process check.
        _parkedWatchdog = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _parkedWatchdog.Tick += (_, _) => OnParkedWatchdog();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? ParkedLayoutChanged;

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

    public double DesiredWidgetWidth
    {
        get => _desiredWidgetWidth;
        private set => SetField(ref _desiredWidgetWidth, value);
    }

    /// <summary>
    /// Drives parked-rail slide. Stays true until UI finishes shrink animation, then clears chips.
    /// </summary>
    public bool ShowParkedRail
    {
        get => _showParkedRail;
        private set => SetField(ref _showParkedRail, value);
    }

    public void CompleteParkedDismiss()
    {
        if (!_parkedDismissPending && SideSessions.Count == 0)
        {
            return;
        }

        _parkedDismissPending = false;
        SideSessions.Clear();
        UpdateParkedWatchdog();
    }

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
                if (IsVolumeEditorOpen)
                {
                    IsVolumeEditorOpen = false;
                }

                CloseSideVolumeEditors();
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
                // Both icons share one family — refresh the pair together.
                OnPropertyChanged(nameof(NextIconKind));
                OnPropertyChanged(nameof(PreviousIconKind));
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
                OnPropertyChanged(nameof(NextIconKind));
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
        CanSkipNext || CanSkipPrevious
            ? TransportIconKind.SkipNext
            : TransportIconKind.SeekForward;

    public TransportIconKind PreviousIconKind =>
        CanSkipPrevious || CanSkipNext
            ? TransportIconKind.SkipPrevious
            : TransportIconKind.SeekBack;

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

    public BitmapImage? AppIcon
    {
        get => _appIcon;
        private set => SetField(ref _appIcon, value);
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
                CloseSideVolumeEditors();
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

        foreach (var side in SideSessions)
        {
            side.RefreshVolume();
        }
    }

    private void CloseSideVolumeEditors()
    {
        foreach (var side in SideSessions)
        {
            side.CloseVolumeEditor();
        }
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
        _parkedWatchdog.Stop();
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
        var primary = PickPrimary(enriched, playing, focusedKey);

        if (playing.Count >= 2)
        {
            _pairExitAfter = null;
            // If pause+play chip is already on screen, pass its key so we morph in place
            // and never flash the old DualHost compact UI.
            var parkedSideKey = !IsDualPlaying && ShowParkedRail && SideSessions.Count > 0
                ? SideSessions[0].SessionKey
                : null;
            primary = ApplyPairPlan(
                PairLayoutPlanner.Plan(playing, _primarySessionKey, parkedSideKey));
        }
        else if (TryHoldPairAfterVideoStop(enriched, out var heldPrimary))
        {
            primary = heldPrimary;
        }
        else
        {
            ClearPairHold();
            IsDualPlaying = false;
            DualSessions.Clear();
            DesiredWidgetWidth = 380;

            var parked = enriched
                .Where(s => s.SessionKey != primary.SessionKey && s.IsParkable && _volume.IsAppRunning(s.AppId))
                .OrderByDescending(s => s.Artwork is not null)
                .Take(2)
                .ToList();

            if (parked.Count > 0)
            {
                _parkedDismissPending = false;
                SyncCollection(SideSessions, parked, freezeTransportIcons: true, expandedMeta: false);
                ShowParkedRail = true;
                UpdateParkedWatchdog();
            }
            else
            {
                RequestParkedDismiss();
            }
        }

        _primarySessionKey = primary.SessionKey;
        _media.Focus(primary.SessionKey);
        ApplyPrimary(primary, keepArtwork);
    }

    /// <summary>
    /// After video stops while audio still plays, keep A/V pair UI for 5s before collapsing.
    /// </summary>
    private bool TryHoldPairAfterVideoStop(
        List<MediaSessionSnapshot> enriched,
        out MediaSessionSnapshot primary)
    {
        primary = default!;
        if (string.IsNullOrEmpty(_pairPrimaryKey) || string.IsNullOrEmpty(_pairSideKey))
        {
            return false;
        }

        var video = enriched.FirstOrDefault(s => s.SessionKey == _pairPrimaryKey);
        var audio = enriched.FirstOrDefault(s => s.SessionKey == _pairSideKey);
        if (video is null || audio is null || !video.IsParkable || !audio.IsParkable)
        {
            return false;
        }

        // Only hold when the video side stopped/paused and audio is still the active one.
        if (video.IsPlaying || !audio.IsPlaying)
        {
            return false;
        }

        _pairExitAfter ??= DateTimeOffset.UtcNow.AddMilliseconds(PairExitGraceMs);
        if (DateTimeOffset.UtcNow >= _pairExitAfter.Value)
        {
            return false;
        }

        IsDualPlaying = false;
        DualSessions.Clear();
        DesiredWidgetWidth = 520;
        _parkedDismissPending = false;
        SyncCollection(
            SideSessions,
            new List<MediaSessionSnapshot> { audio },
            freezeTransportIcons: true,
            expandedMeta: true);
        ShowParkedRail = true;
        UpdateParkedWatchdog();
        primary = video;
        return true;
    }

    private void ClearPairHold()
    {
        _pairPrimaryKey = null;
        _pairSideKey = null;
        _pairExitAfter = null;
    }

    private MediaSessionSnapshot ApplyPairPlan(PairPlan plan)
    {
        DesiredWidgetWidth = plan.WidgetWidth;

        switch (plan.Kind)
        {
            case PairKind.PrimaryWithExpandedSide:
                // Stay on SingleHost — only expand the side chip (no DualHost).
                IsDualPlaying = false;
                DualSessions.Clear();
                _parkedDismissPending = false;
                _pairPrimaryKey = plan.Primary.SessionKey;
                _pairSideKey = plan.SideSessions.Count > 0 ? plan.SideSessions[0].SessionKey : null;
                _pairExitAfter = null;
                SyncCollection(
                    SideSessions,
                    plan.SideSessions.ToList(),
                    freezeTransportIcons: true,
                    expandedMeta: true);
                ShowParkedRail = true;
                UpdateParkedWatchdog();
                return plan.Primary;

            case PairKind.TwinVideo:
                ClearPairHold();
                IsDualPlaying = true;
                RequestParkedDismiss();
                SyncCollection(
                    DualSessions,
                    plan.DualSessions.ToList(),
                    freezeTransportIcons: false,
                    expandedMeta: false,
                    twinVideo: true);
                return plan.Primary;

            default:
                ClearPairHold();
                IsDualPlaying = false;
                DualSessions.Clear();
                DesiredWidgetWidth = 380;
                RequestParkedDismiss();
                return plan.Primary;
        }
    }

    /// <summary>
    /// Prefer a playing session; if nothing plays, keep the previous primary so
    /// paused video/music do not swap places when the other also pauses.
    /// </summary>
    private MediaSessionSnapshot PickPrimary(
        List<MediaSessionSnapshot> enriched,
        List<MediaSessionSnapshot> playing,
        string? focusedKey)
    {
        if (playing.Count > 0)
        {
            // Explicit focus (e.g. resume from parked chip) wins among playing.
            return playing.FirstOrDefault(s => s.SessionKey == focusedKey)
                   ?? playing.FirstOrDefault(s => s.SessionKey == _primarySessionKey)
                   ?? playing[0];
        }

        if (!string.IsNullOrEmpty(_primarySessionKey))
        {
            var sticky = enriched.FirstOrDefault(s =>
                s.SessionKey == _primarySessionKey &&
                s.IsParkable &&
                _volume.IsAppRunning(s.AppId));
            if (sticky is not null)
            {
                return sticky;
            }
        }

        return enriched.FirstOrDefault(s => s.SessionKey == focusedKey)
               ?? enriched[0];
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

        // Sticky kind: known apps win; once video, stay video unless known music.
        bool isVideo;
        if (MediaKindHelper.IsKnownMusicAppId(snap.AppId))
        {
            isVideo = false;
        }
        else if (MediaKindHelper.IsKnownVideoAppId(snap.AppId) || snap.IsVideo)
        {
            isVideo = true;
        }
        else if (_kindCache.TryGetValue(snap.SessionKey, out var cached))
        {
            isVideo = cached;
        }
        else
        {
            isVideo = false;
        }

        _kindCache[snap.SessionKey] = isVideo;

        return new MediaSessionSnapshot
        {
            HasSession = snap.HasSession,
            SessionKey = snap.SessionKey,
            Title = title ?? string.Empty,
            Artist = artist ?? string.Empty,
            AppId = snap.AppId,
            IsPlaying = snap.IsPlaying,
            IsParkable = snap.IsParkable,
            CanPlay = snap.CanPlay,
            CanPause = snap.CanPause,
            CanSkipNext = snap.CanSkipNext,
            CanSkipPrevious = snap.CanSkipPrevious,
            CanSeek = snap.CanSeek,
            IsVideo = isVideo,
            Position = snap.Position,
            Duration = snap.Duration,
            Artwork = snap.Artwork ?? art
        };
    }

    private void SyncCollection(
        ObservableCollection<SideSessionViewModel> target,
        List<MediaSessionSnapshot> source,
        bool freezeTransportIcons,
        bool expandedMeta = false,
        bool twinVideo = false)
    {
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (source.All(s => s.SessionKey != target[i].SessionKey))
            {
                target.RemoveAt(i);
            }
        }

        for (var index = 0; index < source.Count; index++)
        {
            var snap = source[index];
            var existing = target.FirstOrDefault(s => s.SessionKey == snap.SessionKey);
            if (existing is null)
            {
                existing = new SideSessionViewModel(
                    snap.SessionKey,
                    _volume,
                    onVolumeEditorOpened: () =>
                    {
                        if (IsVolumeEditorOpen)
                        {
                            IsVolumeEditorOpen = false;
                        }
                    },
                    onChipLayoutChanged: () => ParkedLayoutChanged?.Invoke(this, EventArgs.Empty),
                    ToggleSideSessionAsync,
                    PreviousSideSessionAsync,
                    NextSideSessionAsync);
                target.Add(existing);
                existing.SetTransportMode(
                    snap.CanSkipPrevious,
                    snap.CanSkipNext,
                    snap.CanSeek,
                    lockMode: freezeTransportIcons);
            }
            else if (!freezeTransportIcons)
            {
                existing.SetTransportMode(
                    snap.CanSkipPrevious,
                    snap.CanSkipNext,
                    snap.CanSeek,
                    lockMode: false);
            }

            existing.AppId = snap.AppId;
            existing.Artwork = snap.Artwork ?? existing.Artwork;
            existing.Title = string.IsNullOrWhiteSpace(snap.Title) ? existing.Title : snap.Title;
            if (string.IsNullOrWhiteSpace(existing.Title))
            {
                existing.Title = "Без названия";
            }

            if (!string.IsNullOrWhiteSpace(snap.Artist) || !expandedMeta)
            {
                existing.Artist = snap.Artist?.Trim() ?? string.Empty;
            }

            existing.SourceName = MediaSourceFormatter.Format(snap.AppId);
            existing.IsPlaying = snap.IsPlaying;
            existing.IsVideo = snap.IsVideo;
            existing.CanToggle = snap.IsPlaying ? snap.CanPause : snap.CanPlay;
            existing.ShowExpandedMeta = expandedMeta;
            existing.DualCardStyle = twinVideo
                ? (index == 0 ? DualCardStyle.TwinVideoMirrored : DualCardStyle.TwinVideo)
                : DualCardStyle.Compact;
            existing.ApplyTimeline(snap.Position, snap.Duration);
            existing.RefreshVolume();
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

        for (var i = 0; i < target.Count; i++)
        {
            target[i].ShowDivider = i < target.Count - 1;
            if (twinVideo)
            {
                target[i].DualCardStyle = i == 0
                    ? DualCardStyle.TwinVideoMirrored
                    : DualCardStyle.TwinVideo;
            }
        }
    }

    private async Task ToggleSideSessionAsync(string sessionKey)
    {
        if (IsVolumeEditorOpen)
        {
            IsVolumeEditorOpen = false;
        }

        _primarySessionKey = sessionKey;
        _media.Focus(sessionKey);
        await _media.TogglePlayPauseAsync(sessionKey);
        await RefreshAsync(full: true);
    }

    private async Task PreviousSideSessionAsync(string sessionKey)
    {
        _media.Focus(sessionKey);
        await _media.PreviousOrSeekBackAsync(sessionKey);
        await RefreshAsync(full: false);
    }

    private async Task NextSideSessionAsync(string sessionKey)
    {
        _media.Focus(sessionKey);
        await _media.NextOrSeekForwardAsync(sessionKey);
        await RefreshAsync(full: false);
    }

    private void RequestParkedDismiss()
    {
        if (ShowParkedRail || SideSessions.Count > 0)
        {
            _parkedDismissPending = true;
            ShowParkedRail = false;
            // Keep SideSessions until CompleteParkedDismiss after slide-out.
        }

        UpdateParkedWatchdog();
    }

    private void UpdateParkedWatchdog()
    {
        if (ShowParkedRail && SideSessions.Count > 0)
        {
            if (!_parkedWatchdog.IsEnabled)
            {
                _parkedWatchdog.Start();
            }
        }
        else if (_parkedWatchdog.IsEnabled)
        {
            _parkedWatchdog.Stop();
        }
    }

    private void OnParkedWatchdog()
    {
        if (_disposed || !ShowParkedRail || SideSessions.Count == 0)
        {
            _parkedWatchdog.Stop();
            return;
        }

        // Process quit → slide rail away (chips stay visible during animation).
        var dead = SideSessions.Any(s => !_volume.IsAppRunning(s.AppId));
        if (dead)
        {
            RequestParkedDismiss();
            return;
        }

        if (_media.PollSessions())
        {
            _ = RefreshAsync(full: false);
        }
    }

    private void ClearAllSessions()
    {
        IsDualPlaying = false;
        DesiredWidgetWidth = 380;
        _parkedDismissPending = false;
        ShowParkedRail = false;
        SideSessions.Clear();
        DualSessions.Clear();
        _kindCache.Clear();
        ClearPairHold();
        UpdateParkedWatchdog();
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
        AppIcon = null;
        _iconLoadAppId = null;
        HasSession = false;
    }

    private async Task LoadAppIconAsync(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            AppIcon = null;
            return;
        }

        if (_iconCache.TryGetValue(appId, out var cached))
        {
            AppIcon = cached;
            return;
        }

        _iconLoadAppId = appId;
        var icon = await AppIconLoader.TryLoadAsync(appId);
        if (_disposed || !string.Equals(_iconLoadAppId, appId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _iconCache[appId] = icon;
        AppIcon = icon;
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

        if (appChanged && IsVolumeEditorOpen)
        {
            IsVolumeEditorOpen = false;
        }

        if (appChanged || !CanControlVolume)
        {
            RefreshVolume();
        }

        if (appChanged || AppIcon is null)
        {
            _ = LoadAppIconAsync(snapshot.AppId);
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
        if (_pairExitAfter is not null && DateTimeOffset.UtcNow >= _pairExitAfter.Value)
        {
            _ = RefreshAsync(full: false);
        }

        foreach (var side in SideSessions)
        {
            side.TickTimelineClock();
        }

        foreach (var side in DualSessions)
        {
            side.TickTimelineClock();
        }

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
