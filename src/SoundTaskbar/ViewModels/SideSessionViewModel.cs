using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using SoundTaskbar.Controls;
using SoundTaskbar.Helpers;
using SoundTaskbar.Services;

namespace SoundTaskbar.ViewModels;

public sealed class SideSessionViewModel : INotifyPropertyChanged
{
    private BitmapImage? _artwork;
    private string _title = string.Empty;
    private string _sourceName = string.Empty;
    private bool _isPlaying;
    private bool _canToggle = true;
    private bool _canSkipPrevious;
    private bool _canSkipNext;
    private bool _canSeek;
    private bool _showDivider;

    public SideSessionViewModel(
        string sessionKey,
        Func<string, Task> toggleAsync,
        Func<string, Task> previousAsync,
        Func<string, Task> nextAsync)
    {
        SessionKey = sessionKey;
        ToggleCommand = new RelayCommand(
            async _ => await toggleAsync(SessionKey),
            _ => _canToggle);
        PreviousCommand = new RelayCommand(
            async _ => await previousAsync(SessionKey),
            _ => CanGoPrevious);
        NextCommand = new RelayCommand(
            async _ => await nextAsync(SessionKey),
            _ => CanGoNext);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string SessionKey { get; }

    public int SeekSeconds => MediaSessionService.SeekStepSeconds;

    public RelayCommand ToggleCommand { get; }
    public RelayCommand PreviousCommand { get; }
    public RelayCommand NextCommand { get; }

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

    public bool CanSkipPrevious
    {
        get => _canSkipPrevious;
        set
        {
            if (SetField(ref _canSkipPrevious, value))
            {
                OnPropertyChanged(nameof(CanGoPrevious));
                OnPropertyChanged(nameof(PreviousIconKind));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool CanSkipNext
    {
        get => _canSkipNext;
        set
        {
            if (SetField(ref _canSkipNext, value))
            {
                OnPropertyChanged(nameof(CanGoNext));
                OnPropertyChanged(nameof(NextIconKind));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool CanSeek
    {
        get => _canSeek;
        set
        {
            if (SetField(ref _canSeek, value))
            {
                OnPropertyChanged(nameof(CanGoPrevious));
                OnPropertyChanged(nameof(CanGoNext));
                OnPropertyChanged(nameof(PreviousIconKind));
                OnPropertyChanged(nameof(NextIconKind));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool CanGoPrevious => CanSkipPrevious || CanSeek;

    public bool CanGoNext => CanSkipNext || CanSeek;

    public TransportIconKind PreviousIconKind =>
        CanSkipPrevious ? TransportIconKind.SkipPrevious : TransportIconKind.SeekBack;

    public TransportIconKind NextIconKind =>
        CanSkipNext ? TransportIconKind.SkipNext : TransportIconKind.SeekForward;

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
