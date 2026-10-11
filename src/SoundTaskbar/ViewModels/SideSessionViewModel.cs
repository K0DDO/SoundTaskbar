using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SoundTaskbar.Controls;
using SoundTaskbar.Helpers;
using SoundTaskbar.Services;

namespace SoundTaskbar.ViewModels;

public sealed class SideSessionViewModel : INotifyPropertyChanged
{
    public const double MeterHeight = 18;

    private readonly AppVolumeService _volume;
    private readonly Action _onVolumeEditorOpened;
    private readonly Action? _onChipLayoutChanged;
    private readonly DispatcherTimer _volumeHideTimer;
    private BitmapImage? _artwork;
    private string _title = string.Empty;
    private string _sourceName = string.Empty;
    private bool _isPlaying;
    private bool _canToggle = true;
    private bool _usesSkipIcons;
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
        set => SetField(ref _title, value);
    }

    public string SourceName
    {
        get => _sourceName;
        set => SetField(ref _sourceName, value);
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            if (SetField(ref _isPlaying, value))
            {
                OnPropertyChanged(nameof(OverlayGlyph));
                if (IsVolumeEditorOpen)
                {
                    IsVolumeEditorOpen = false;
                }
            }
        }
    }

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

    public bool CanGoPrevious => _usesSkipIcons || _canSeek;

    public bool CanGoNext => _usesSkipIcons || _canSeek;

    public TransportIconKind PreviousIconKind =>
        _usesSkipIcons ? TransportIconKind.SkipPrevious : TransportIconKind.SeekBack;

    public TransportIconKind NextIconKind =>
        _usesSkipIcons ? TransportIconKind.SkipNext : TransportIconKind.SeekForward;

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

        var useSkip = canSkipPrevious || canSkipNext;
        _usesSkipIcons = useSkip;
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

    public void RefreshVolume()
    {
        if (string.IsNullOrWhiteSpace(AppId))
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
