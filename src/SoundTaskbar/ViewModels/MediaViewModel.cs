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

/// <summary>Old primary becomes a side chip, the side chip <see cref="ToKey"/> becomes primary.</summary>
public sealed class PrimarySwapEventArgs(string fromKey, string toKey) : EventArgs
{
    public string FromKey { get; } = fromKey;
    public string ToKey { get; } = toKey;
}

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
    private bool _showParkedRail;
    private bool _parkedDismissPending;
    /// <summary>Frozen solo baseline width — do not change for pause+play composition.</summary>
    public const double SoloWidgetWidth = 310;

    /// <summary>Play zone is slightly narrower than solo when a pause chip sits beside it.</summary>
    public const double PausePlayTrim = 28;

    /// <summary>Extra primary trim in play+play (name→icon + tighter meta). Frozen pause+play keeps PausePlayTrim only.</summary>
    public const double PlayPlayExtraTrim = 32;

    /// <summary>Fixed left meta width in play+play (matches typical primary meta; no live reflow).</summary>
    public const double PlayPlayMetaWidth = 96;

    /// <summary>ParkedDivider width 2.5 + margins 8+6.</summary>
    public const double PausePlayDividerChrome = 16.5;

    private double _desiredWidgetWidth = SoloWidgetWidth;
    private string? _pairPrimaryKey;
    private string? _pairSideKey;
    private DateTimeOffset? _pairExitAfter;
    private DateTimeOffset? _emptySessionsGraceUntil;
    private long _lastPrimaryAliveCheckMs;
    /// <summary>Hold pair UI across Win+D / minimize-all SMTC blips.</summary>
    private const int PairExitGraceMs = 2500;
    /// <summary>Brief hold for Win+D SMTC blips; tab/app close still collapses after this.</summary>
    private const int EmptySessionsGraceMs = 900;
    /// <summary>Our own play/pause click is not an SMTC blip — skip the pair hold.</summary>
    private const int UserToggleGraceSkipMs = 1500;
    /// <summary>Side rail cap: expanded play chip + small paused chips, separated by dividers.</summary>
    private const int MaxSideChips = 3;
    private DateTimeOffset _skipPairGraceUntil;
    private string _displayedPrimaryKey = string.Empty;
    private bool _staleVisuals;
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
                _skipPairGraceUntil = DateTimeOffset.UtcNow.AddMilliseconds(UserToggleGraceSkipMs);
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
        // Snappy: dismiss starts as soon as the paused app is gone (was 350ms).
        _parkedWatchdog = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _parkedWatchdog.Tick += (_, _) => OnParkedWatchdog();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? ParkedLayoutChanged;

    /// <summary>
    /// Raised right before a side session takes over the primary slot (roles swap).
    /// UI snapshots the old frame here, before any bound property changes.
    /// </summary>
    public event EventHandler<PrimarySwapEventArgs>? PrimarySwapping;

    public RelayCommand PreviousCommand { get; }
    public RelayCommand PlayPauseCommand { get; }
    public RelayCommand NextCommand { get; }
    public RelayCommand ToggleMuteCommand { get; }

    public ObservableCollection<SideSessionViewModel> SideSessions { get; } = new();

    public int SeekSeconds => MediaSessionService.SeekStepSeconds;

    /// <summary>Two playing sources: side chip expanded (play+play).</summary>
    public bool IsPlayPlay =>
        ShowParkedRail && SideSessions.Any(s => s.ShowExpandedMeta);

    /// <summary>play+play → icon only (name width animated in UI); solo/pause+play → icon + name.</summary>
    public bool ShowPairAppIcon => IsPlayPlay;

    public Visibility AppIconVisibility =>
        AppIcon is not null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Name stays in tree; MainWindow collapses width for play+play / volume.</summary>
    public Visibility SourceLabelVisibility => Visibility.Visible;

    public double DesiredWidgetWidth
    {
        get => _desiredWidgetWidth;
        private set => SetField(ref _desiredWidgetWidth, value);
    }

    /// <summary>pause+play shell width, or solo when parked is absent.</summary>
    public double ComputeParkedCompositeWidth(double? parkedMeasuredWidth)
    {
        if (parkedMeasuredWidth is null || parkedMeasuredWidth < 1)
        {
            return SoloWidgetWidth;
        }

        return SoloWidgetWidth - PausePlayTrim + parkedMeasuredWidth.Value + PausePlayDividerChrome;
    }

    /// <summary>play+play: tighter primary + measured expanded chip + divider.</summary>
    public double ComputePlayPlayCompositeWidth(double parkedMeasuredWidth) =>
        SoloWidgetWidth - PausePlayTrim - PlayPlayExtraTrim + parkedMeasuredWidth + PausePlayDividerChrome;

    /// <summary>
    /// pause+play only. play+play width is driven by MainWindow morph (measured).
    /// </summary>
    public void ApplyParkedCompositeWidth(double? parkedMeasuredWidth)
    {
        if (IsPlayPlay)
        {
            return;
        }

        if (parkedMeasuredWidth is null || parkedMeasuredWidth < 1 || !ShowParkedRail)
        {
            DesiredWidgetWidth = SoloWidgetWidth;
            return;
        }

        DesiredWidgetWidth = ComputeParkedCompositeWidth(parkedMeasuredWidth);
    }

    public void ApplyPlayPlayCompositeWidth(double parkedMeasuredWidth)
    {
        if (parkedMeasuredWidth < 1)
        {
            return;
        }

        DesiredWidgetWidth = ComputePlayPlayCompositeWidth(parkedMeasuredWidth);
    }

    /// <summary>
    /// Drives parked-rail slide. Stays true until UI finishes shrink animation, then clears chips.
    /// </summary>
    public bool ShowParkedRail
    {
        get => _showParkedRail;
        private set
        {
            if (SetField(ref _showParkedRail, value))
            {
                NotifySourceSlotChanged();
            }
        }
    }

    private void NotifySourceSlotChanged()
    {
        OnPropertyChanged(nameof(ShowPairAppIcon));
        OnPropertyChanged(nameof(AppIconVisibility));
        OnPropertyChanged(nameof(SourceLabelVisibility));
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
        private set
        {
            if (SetField(ref _appIcon, value))
            {
                NotifySourceSlotChanged();
            }
        }
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
            // Win+D / minimize-all can briefly empty SMTC — do not nuke UI.
            if (ShouldHoldEmptySessions())
            {
                return;
            }

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

        // Stopped/Closed SMTC leftovers (tab closed) are not parkable — treat as gone.
        var enriched = snapshots
            .Select(Enrich)
            .Where(s => s.HasSession && s.IsParkable)
            .ToList();

        if (enriched.Count == 0)
        {
            if (ShouldHoldEmptySessions())
            {
                return;
            }

            ClearAllSessions();
            return;
        }

        _emptySessionsGraceUntil = null;

        var playing = enriched.Where(s => s.IsPlaying).ToList();
        var focusedKey = _media.FocusedKey;
        var primary = PickPrimary(enriched, playing, focusedKey);
        if (primary is null)
        {
            if (ShouldHoldEmptySessions())
            {
                return;
            }

            ClearAllSessions();
            return;
        }

        if (playing.Count >= 2)
        {
            _pairExitAfter = null;
            // Keep the chip that started playing on the side so pause+play → play+play morphs in place.
            var parkedSideKey = ShowParkedRail ? PickParkedSideKey(playing) : null;
            var plan = PairLayoutPlanner.Plan(playing, _primarySessionKey, parkedSideKey);
            RaiseSwapIfNeeded(plan.Primary.SessionKey);
            primary = ApplyPairPlan(plan, enriched);
        }
        else if (TryHoldPairGrace(enriched, playing, out var heldPrimary))
        {
            primary = heldPrimary;
        }
        else
        {
            ClearPairHold();
            RaiseSwapIfNeeded(primary.SessionKey);

            var parked = BuildSideList(enriched, primary.SessionKey, expanded: null);

            if (parked.Count > 0)
            {
                _parkedDismissPending = false;
                SyncCollection(SideSessions, parked, freezeTransportIcons: true, expandedKey: null);
                ShowParkedRail = true;
                UpdateParkedWatchdog();
                // Widget width: MainWindow ApplyParkedCompositeWidth after chip measure (not Solo clamp).
            }
            else
            {
                DesiredWidgetWidth = SoloWidgetWidth;
                RequestParkedDismiss();
            }
        }

        _primarySessionKey = primary.SessionKey;
        _media.Focus(primary.SessionKey);
        ApplyPrimary(primary, keepArtwork);
    }

    private bool ShouldHoldEmptySessions()
    {
        if (!HasSession && SideSessions.Count == 0)
        {
            return false;
        }

        _emptySessionsGraceUntil ??= DateTimeOffset.UtcNow.AddMilliseconds(EmptySessionsGraceMs);
        return DateTimeOffset.UtcNow < _emptySessionsGraceUntil.Value;
    }

    /// <summary>
    /// Keep play+play / pair UI briefly when SMTC flickers (Win+D, minimize-all, pause blip).
    /// </summary>
    private bool TryHoldPairGrace(
        List<MediaSessionSnapshot> enriched,
        List<MediaSessionSnapshot> playing,
        out MediaSessionSnapshot primary)
    {
        primary = default!;
        if (string.IsNullOrEmpty(_pairPrimaryKey) || string.IsNullOrEmpty(_pairSideKey))
        {
            return false;
        }

        var left = enriched.FirstOrDefault(s => s.SessionKey == _pairSideKey);
        var right = enriched.FirstOrDefault(s => s.SessionKey == _pairPrimaryKey);
        if (left is null || right is null || !left.IsParkable || !right.IsParkable)
        {
            return false;
        }

        // Still two playing — planner should have handled; do not hold.
        if (playing.Count >= 2 || DateTimeOffset.UtcNow < _skipPairGraceUntil)
        {
            return false;
        }

        _pairExitAfter ??= DateTimeOffset.UtcNow.AddMilliseconds(PairExitGraceMs);
        if (DateTimeOffset.UtcNow >= _pairExitAfter.Value)
        {
            return false;
        }

        // Hold play+play chrome through the blip (do not morph down then up).
        _parkedDismissPending = false;
        SyncCollection(
            SideSessions,
            BuildSideList(enriched, right.SessionKey, left),
            freezeTransportIcons: true,
            expandedKey: left.SessionKey);
        ShowParkedRail = true;
        UpdateParkedWatchdog();
        primary = right;
        return true;
    }

    /// <summary>Extra sources as small chips (left), the expanded play chip last (next to primary).</summary>
    private List<MediaSessionSnapshot> BuildSideList(
        List<MediaSessionSnapshot> enriched,
        string primaryKey,
        MediaSessionSnapshot? expanded)
    {
        var slots = MaxSideChips - (expanded is null ? 0 : 1);
        var list = enriched
            .Where(s => s.SessionKey != primaryKey &&
                        s.SessionKey != expanded?.SessionKey &&
                        s.IsParkable &&
                        (s.IsPlaying || _volume.IsAppRunning(s.AppId)))
            .OrderByDescending(s => s.Artwork is not null)
            .Take(slots)
            .ToList();
        if (expanded is not null)
        {
            list.Add(expanded);
        }

        return list;
    }

    private string? PickParkedSideKey(List<MediaSessionSnapshot> playing)
    {
        var expanded = SideSessions.FirstOrDefault(s => s.ShowExpandedMeta);
        if (expanded is not null)
        {
            return expanded.SessionKey;
        }

        return SideSessions.FirstOrDefault(s => playing.Any(p => p.SessionKey == s.SessionKey))?.SessionKey;
    }

    private void RaiseSwapIfNeeded(string nextPrimaryKey)
    {
        if (!HasSession ||
            string.IsNullOrEmpty(_displayedPrimaryKey) ||
            string.Equals(nextPrimaryKey, _displayedPrimaryKey, StringComparison.Ordinal) ||
            SideSessions.All(s => s.SessionKey != nextPrimaryKey))
        {
            return;
        }

        PrimarySwapping?.Invoke(this, new PrimarySwapEventArgs(_displayedPrimaryKey, nextPrimaryKey));
    }

    private void ClearPairHold()
    {
        _pairPrimaryKey = null;
        _pairSideKey = null;
        _pairExitAfter = null;
    }

    private MediaSessionSnapshot ApplyPairPlan(PairPlan plan, List<MediaSessionSnapshot> enriched)
    {
        switch (plan.Kind)
        {
            case PairKind.PlayPlay:
                // SingleHost + expanded side chip; shell width from MainWindow measure.
                _parkedDismissPending = false;
                _pairPrimaryKey = plan.Primary.SessionKey;
                var expanded = plan.SideSessions.Count > 0 ? plan.SideSessions[0] : null;
                _pairSideKey = expanded?.SessionKey;
                _pairExitAfter = null;
                SyncCollection(
                    SideSessions,
                    BuildSideList(enriched, plan.Primary.SessionKey, expanded),
                    freezeTransportIcons: true,
                    expandedKey: _pairSideKey);
                ShowParkedRail = true;
                UpdateParkedWatchdog();
                return plan.Primary;

            default:
                ClearPairHold();
                DesiredWidgetWidth = SoloWidgetWidth;
                RequestParkedDismiss();
                return plan.Primary;
        }
    }

    /// <summary>
    /// Prefer a playing session; if nothing plays, keep the previous primary so
    /// paused video/music do not swap places when the other also pauses.
    /// Returns null when nothing alive remains (tab/app closed).
    /// </summary>
    private MediaSessionSnapshot? PickPrimary(
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
                _volume.IsAppRunning(s.AppId));
            if (sticky is not null)
            {
                return sticky;
            }
        }

        // Only parkable + running apps — never keep a dead Stopped SMTC zombie as primary.
        return enriched.FirstOrDefault(s =>
                   s.SessionKey == focusedKey && _volume.IsAppRunning(s.AppId))
               ?? enriched.FirstOrDefault(s => _volume.IsAppRunning(s.AppId));
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
        string? expandedKey = null)
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

            var expandedMeta = string.Equals(snap.SessionKey, expandedKey, StringComparison.Ordinal);
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
            existing.ApplyTimeline(snap.Position, snap.Duration);
            existing.RefreshVolume();
            if (_iconCache.TryGetValue(snap.AppId, out var cachedIcon))
            {
                existing.AppIcon = cachedIcon;
            }
            else if (existing.AppIcon is null && !string.IsNullOrWhiteSpace(snap.AppId))
            {
                _ = LoadSideAppIconAsync(existing, snap.AppId);
            }
        }

        NotifySourceSlotChanged();

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
        }
    }

    private async Task ToggleSideSessionAsync(string sessionKey)
    {
        if (IsVolumeEditorOpen)
        {
            IsVolumeEditorOpen = false;
        }

        _primarySessionKey = sessionKey;
        _skipPairGraceUntil = DateTimeOffset.UtcNow.AddMilliseconds(UserToggleGraceSkipMs);
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
        var deadCount = SideSessions.Count(s => !_volume.IsAppRunning(s.AppId));
        if (deadCount == SideSessions.Count)
        {
            RequestParkedDismiss();
            return;
        }

        if (deadCount > 0)
        {
            _ = RefreshAsync(full: false);
            return;
        }

        if (_media.PollSessions())
        {
            _ = RefreshAsync(full: false);
        }
    }

    /// <summary>
    /// No source left: UI collapses and hides the widget. Last title/art stay on screen
    /// during the collapse and are replaced on the next session (<see cref="_staleVisuals"/>).
    /// </summary>
    private void ClearAllSessions()
    {
        if (!HasSession && SideSessions.Count == 0)
        {
            return;
        }

        DesiredWidgetWidth = SoloWidgetWidth;
        RequestParkedDismiss();
        _kindCache.Clear();
        ClearPairHold();
        _emptySessionsGraceUntil = null;
        _primarySessionKey = string.Empty;
        _displayedPrimaryKey = string.Empty;
        _staleVisuals = true;
        SetTimelineAnchor(_anchorPosition, playing: false);
        IsPlaying = false;
        CanSkipNext = false;
        CanSkipPrevious = false;
        CanSeek = false;
        CanTogglePlayback = false;
        IsVolumeEditorOpen = false;
        PeakLevel = 0;
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

        foreach (var side in SideSessions)
        {
            if (string.Equals(side.AppId, appId, StringComparison.OrdinalIgnoreCase))
            {
                side.AppIcon = icon;
            }
        }
    }

    private async Task LoadSideAppIconAsync(SideSessionViewModel side, string appId)
    {
        if (_iconCache.TryGetValue(appId, out var cached))
        {
            side.AppIcon = cached;
            return;
        }

        var icon = await AppIconLoader.TryLoadAsync(appId);
        if (_disposed)
        {
            return;
        }

        _iconCache[appId] = icon;
        if (string.Equals(side.AppId, appId, StringComparison.OrdinalIgnoreCase))
        {
            side.AppIcon = icon;
        }
    }

    private void ApplyPrimary(MediaSessionSnapshot snapshot, bool keepArtwork)
    {
        var fresh = _staleVisuals;
        _staleVisuals = false;
        _primarySessionKey = snapshot.SessionKey;
        _displayedPrimaryKey = snapshot.SessionKey;

        if (!keepArtwork || fresh || !string.IsNullOrWhiteSpace(snapshot.Title))
        {
            Title = string.IsNullOrWhiteSpace(snapshot.Title) ? "Без названия" : snapshot.Title;
        }

        if (!keepArtwork || fresh || !string.IsNullOrWhiteSpace(snapshot.Artist))
        {
            Artist = snapshot.Artist?.Trim() ?? string.Empty;
        }

        if (fresh)
        {
            Artwork = snapshot.Artwork;
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

        // Last, so the reveal starts with the new frame already bound.
        HasSession = true;
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

        // Empty-session grace used to expire with no follow-up refresh → widget stuck forever.
        if (_emptySessionsGraceUntil is not null &&
            DateTimeOffset.UtcNow >= _emptySessionsGraceUntil.Value)
        {
            _ = RefreshAsync(full: true);
        }

        foreach (var side in SideSessions)
        {
            side.TickTimelineClock();
        }

        if (!HasSession)
        {
            return;
        }

        // Primary app quit while SMTC still holds a zombie — collapse.
        var nowMs = Environment.TickCount64;
        if (nowMs - _lastPrimaryAliveCheckMs > 500)
        {
            _lastPrimaryAliveCheckMs = nowMs;
            if (!string.IsNullOrWhiteSpace(AppId) && !_volume.IsAppRunning(AppId))
            {
                _ = RefreshAsync(full: true);
                return;
            }
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
