using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SoundTaskbar.Controls;
using SoundTaskbar.Helpers;
using SoundTaskbar.Services;

namespace SoundTaskbar.ViewModels;

public sealed class SideSessionViewModel : INotifyPropertyChanged
{
    public const double MeterHeight = 22;

    private readonly AppVolumeService _volume;
    private readonly Action _onVolumeEditorOpened;
    private readonly Action? _onChipLayoutChanged;
    private readonly DispatcherTimer _volumeHideTimer;
    private BitmapImage? _artwork;
    private string _title = string.Empty;
    private string _artist = string.Empty;
    private string _sourceName = string.Empty;
    private string _durationText = string.Empty;
    private string _timeText = "0:00 / 0:00";
    private double _progress;
    private bool _isPlaying;
    private bool _isVideo;
    private bool _showExpandedMeta;
    private double _metaRailWidth;
    private double _targetMetaRailWidth;
    private DualCardStyle _dualCardStyle = DualCardStyle.Compact;
    private bool _canToggle = true;
    private bool _usesSkipIcons;
    private bool _canSkipPrevious;
    private bool _canSkipNext;
    private bool _canSeek;
    private bool _transportLocked;
    private bool _showDivider;
    private string _appId = string.Empty;
    private float _volumeLevel = 1f;
    private bool _isMuted;
    private bool _canControlVolume;
    private bool _isVolumeEditorOpen;
    private double _peakLevel;
    private bool _suppressVolumeWrite;
    private long _volumeWriteTicks;
    private TimeSpan _anchorPosition;
    private TimeSpan _durationSpan;
    private long _anchorTickMs;
    private string _lastPublishedSecond = string.Empty;

    public SideSessionViewModel(
        string sessionKey,
        AppVolumeService volume,
        Action onVolumeEditorOpened,
        Action? onChipLayoutChanged,
        Func<string, Task> toggleAsync,
        Func<string, Task> previousAsync,
        Func<string, Task> nextAsync)
    {
        SessionKey = sessionKey;
        _volume = volume;
        _onVolumeEditorOpened = onVolumeEditorOpened;
        _onChipLayoutChanged = onChipLayoutChanged;

        ToggleCommand = new RelayCommand(
            async _ => await toggleAsync(SessionKey),
            _ => _canToggle);
        PreviousCommand = new RelayCommand(
            async _ => await previousAsync(SessionKey),
            _ => CanGoPrevious);
        NextCommand = new RelayCommand(
            async _ => await nextAsync(SessionKey),
            _ => CanGoNext);
        ToggleVolumeCommand = new RelayCommand(
            _ => ToggleVolumeEditor(),
            _ => CanControlVolume);
        ToggleMuteCommand = new RelayCommand(
            _ => ToggleMute(),
            _ => CanControlVolume);

        _volumeHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _volumeHideTimer.Tick += (_, _) =>
        {
            _volumeHideTimer.Stop();
            IsVolumeEditorOpen = false;
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string SessionKey { get; }

    public string AppId
    {
        get => _appId;
        set => SetField(ref _appId, value);
    }

    public int SeekSeconds => MediaSessionService.SeekStepSeconds;

    public RelayCommand ToggleCommand { get; }
    public RelayCommand PreviousCommand { get; }
    public RelayCommand NextCommand { get; }
    public RelayCommand ToggleVolumeCommand { get; }
    public RelayCommand ToggleMuteCommand { get; }

    public BitmapImage? Artwork
    {
        get => _artwork;
        set => SetField(ref _artwork, value);
    }

    public string Title
    {
        get => _title;
        set
        {
            if (SetField(ref _title, value))
            {
                RecalculateMetaWidth();
            }
        }
    }

    public string Artist
    {
        get => _artist;
        set
        {
            if (SetField(ref _artist, value))
            {
                RecalculateMetaWidth();
            }
        }
    }

    public string SourceName
    {
        get => _sourceName;
        set => SetField(ref _sourceName, value);
    }

    public string DurationText
    {
        get => _durationText;
        set => SetField(ref _durationText, value);
    }

    public string TimeText
    {
        get => _timeText;
        set => SetField(ref _timeText, value);
    }

    public double Progress
    {
        get => _progress;
        set => SetField(ref _progress, value);
    }

    public bool IsVideo
    {
        get => _isVideo;
        set => SetField(ref _isVideo, value);
    }

    public bool ShowExpandedMeta
    {
        get => _showExpandedMeta;
        set
        {
            if (SetField(ref _showExpandedMeta, value))
            {
                OnPropertyChanged(nameof(ExpandedChromeVisibility));
                OnPropertyChanged(nameof(CompactOverlayVisibility));
                RecalculateMetaWidth();
                _onChipLayoutChanged?.Invoke();
            }
        }
    }

    /// <summary>Play/pause button + meta to the right of art (pair playing).</summary>
    public System.Windows.Visibility ExpandedChromeVisibility =>
        ShowExpandedMeta ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    /// <summary>▶/❚❚ overlay on artwork (paused chip only).</summary>
    public System.Windows.Visibility CompactOverlayVisibility =>
        ShowExpandedMeta ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    /// <summary>
    /// Animated width of title/artist/duration rail. Driven by MainWindow so the
    /// parked host can grow in place — art stays put, only vol/prev slide left.
    /// </summary>
    public double MetaRailWidth
    {
        get => _metaRailWidth;
        set => SetField(ref _metaRailWidth, value);
    }

    /// <summary>Target chip meta width from max(title, artist) + padding.</summary>
    public double TargetMetaRailWidth =>
        ShowExpandedMeta && !IsVolumeEditorOpen ? _targetMetaRailWidth : 0;

    public double MetaContentWidth => Math.Max(0, _targetMetaRailWidth - 5);

    public DualCardStyle DualCardStyle
    {
        get => _dualCardStyle;
        set
        {
            if (SetField(ref _dualCardStyle, value))
            {
                OnPropertyChanged(nameof(IsCompactCard));
                OnPropertyChanged(nameof(IsTwinVideoCard));
                OnPropertyChanged(nameof(IsMirrored));
                OnPropertyChanged(nameof(CompactVisibility));
                OnPropertyChanged(nameof(TwinVideoVisibility));
                OnPropertyChanged(nameof(ArtColumn));
                OnPropertyChanged(nameof(MetaColumn));
                OnPropertyChanged(nameof(TransportColumn));
            }
        }
    }

    public bool IsCompactCard => DualCardStyle == DualCardStyle.Compact;

    public bool IsTwinVideoCard =>
        DualCardStyle is DualCardStyle.TwinVideo or DualCardStyle.TwinVideoMirrored;

    public bool IsMirrored => DualCardStyle == DualCardStyle.TwinVideoMirrored;

    public System.Windows.Visibility CompactVisibility =>
        IsTwinVideoCard ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    public System.Windows.Visibility TwinVideoVisibility =>
        IsTwinVideoCard ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    // Normal: art | meta | transport — mirrored: transport | meta | art
    public int ArtColumn => IsMirrored ? 2 : 0;

    public int MetaColumn => 1;

    public int TransportColumn => IsMirrored ? 0 : 2;

    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            if (SetField(ref _isPlaying, value))
            {
                OnPropertyChanged(nameof(OverlayGlyph));
                OnPropertyChanged(nameof(PlayPauseLabel));
                if (IsVolumeEditorOpen)
                {
                    IsVolumeEditorOpen = false;
                }
            }
        }
    }

    public string PlayPauseLabel => IsPlaying ? "❚❚" : "▶";

    public bool CanToggle
    {
        get => _canToggle;
        set
        {
            if (SetField(ref _canToggle, value))
            {
                CommandManager.InvalidateRequerySuggested();
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
            OnPropertyChanged(nameof(VolumeGlyph));
            OnPropertyChanged(nameof(MuteGlyph));
            OnPropertyChanged(nameof(IsEffectivelyMuted));
            if (!_suppressVolumeWrite && IsVolumeEditorOpen)
            {
                BounceVolumeHideTimer();
            }
        }
    }

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
                _onVolumeEditorOpened();
                BounceVolumeHideTimer();
            }
            else
            {
                _volumeHideTimer.Stop();
            }

            _onChipLayoutChanged?.Invoke();
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

    public string MuteGlyph => IsEffectivelyMuted ? "\uE74F" : "\uE767";

    public bool CanGoPrevious => _canSkipPrevious || _canSeek;

    public bool CanGoNext => _canSkipNext || _canSeek;

    // Both sides always share one mode — never SeekBack + SkipNext.
    public TransportIconKind PreviousIconKind =>
        _usesSkipIcons ? TransportIconKind.SkipPrevious : TransportIconKind.SeekBack;

    public TransportIconKind NextIconKind =>
        _usesSkipIcons ? TransportIconKind.SkipNext : TransportIconKind.SeekForward;

    public void ApplyTimeline(TimeSpan position, TimeSpan duration)
    {
        var safeDuration = duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
        var safePosition = position < TimeSpan.Zero ? TimeSpan.Zero : position;
        if (safeDuration > TimeSpan.Zero && safePosition > safeDuration)
        {
            safePosition = safeDuration;
        }

        _durationSpan = safeDuration;
        _anchorPosition = safePosition;
        _anchorTickMs = Environment.TickCount64;
        DurationText = FormatTime(safeDuration);
        PublishTime(safePosition);
    }

    /// <summary>Local clock so TimeText advances every second while SMTC is sparse.</summary>
    public void TickTimelineClock()
    {
        if (!IsPlaying || _durationSpan <= TimeSpan.Zero)
        {
            return;
        }

        var elapsed = (Environment.TickCount64 - _anchorTickMs) / 1000.0;
        if (elapsed < 0)
        {
            elapsed = 0;
        }

        var pos = _anchorPosition + TimeSpan.FromSeconds(elapsed);
        if (pos > _durationSpan)
        {
            pos = _durationSpan;
        }

        PublishTime(pos);
    }

    private void PublishTime(TimeSpan position)
    {
        var text = $"{FormatTime(position)} / {FormatTime(_durationSpan)}";
        var secondKey = FormatTime(position);
        if (secondKey != _lastPublishedSecond || text != TimeText)
        {
            _lastPublishedSecond = secondKey;
            TimeText = text;
        }

        Progress = _durationSpan.TotalSeconds <= 0
            ? 0
            : Math.Clamp(position.TotalSeconds / _durationSpan.TotalSeconds, 0, 1);
    }

    private void RecalculateMetaWidth()
    {
        var titleW = TextMeasure.Width(Title, fontSize: 10, FontWeights.SemiBold);
        var artistW = TextMeasure.Width(Artist, fontSize: 8, FontWeights.Normal);
        // Floor by typical time string so "1:23 / 3:45" is never clipped.
        var timeW = TextMeasure.Width("0:00 / 0:00", fontSize: 8, FontWeights.Normal);
        var content = Math.Max(titleW, Math.Max(artistW, timeW));
        // +8 pad (incl. right margin); clamp long titles.
        var next = Math.Clamp(Math.Ceiling(content) + 8, 44, 120);
        if (Math.Abs(next - _targetMetaRailWidth) < 0.5)
        {
            return;
        }

        _targetMetaRailWidth = next;
        OnPropertyChanged(nameof(TargetMetaRailWidth));
        OnPropertyChanged(nameof(MetaContentWidth));
        if (ShowExpandedMeta && !IsVolumeEditorOpen)
        {
            _onChipLayoutChanged?.Invoke();
        }
    }

    public bool ShowDivider
    {
        get => _showDivider;
        set
        {
            if (SetField(ref _showDivider, value))
            {
                OnPropertyChanged(nameof(DividerVisibility));
            }
        }
    }

    public System.Windows.Visibility DividerVisibility =>
        ShowDivider ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public string OverlayGlyph => IsPlaying ? "❚❚" : "▶";

    public void SetTransportMode(bool canSkipPrevious, bool canSkipNext, bool canSeek, bool lockMode)
    {
        if (_transportLocked)
        {
            return;
        }

        // Same glyph family on both buttons whenever either skip exists.
        _usesSkipIcons = canSkipPrevious || canSkipNext;
        _canSkipPrevious = canSkipPrevious;
        _canSkipNext = canSkipNext;
        _canSeek = canSeek;
        if (lockMode)
        {
            _transportLocked = true;
        }

        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(PreviousIconKind));
        OnPropertyChanged(nameof(NextIconKind));
        CommandManager.InvalidateRequerySuggested();
    }

    private static string FormatTime(TimeSpan value)
    {
        if (value.TotalHours >= 1)
        {
            return value.ToString(@"h\:mm\:ss");
        }

        return value.ToString(@"m\:ss");
    }

    public void RefreshVolume()
    {
        if (string.IsNullOrWhiteSpace(AppId))
        {
            CanControlVolume = false;
            PeakLevel = 0;
            return;
        }

        var state = _volume.GetState(AppId, forceRefresh: true);
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

        var recentlyWrote = Environment.TickCount64 - _volumeWriteTicks < 280;
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
        PeakLevel = state.IsMuted
            ? 0
            : Math.Clamp(state.Peak * (0.2f + 0.8f * state.Volume), 0f, 1f);
    }

    public void CloseVolumeEditor()
    {
        if (IsVolumeEditorOpen)
        {
            IsVolumeEditorOpen = false;
        }
    }

    private void ToggleVolumeEditor()
    {
        RefreshVolume();
        if (!CanControlVolume)
        {
            return;
        }

        IsVolumeEditorOpen = !IsVolumeEditorOpen;
    }

    private void ToggleMute()
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

    private void BounceVolumeHideTimer()
    {
        _volumeHideTimer.Stop();
        _volumeHideTimer.Start();
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
