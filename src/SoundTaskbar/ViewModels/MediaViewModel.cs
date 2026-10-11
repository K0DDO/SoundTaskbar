using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SoundTaskbar.Helpers;
using SoundTaskbar.Services;

namespace SoundTaskbar.ViewModels;

public sealed class MediaViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly MediaSessionService _media;
    private readonly DispatcherTimer _ticker;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
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
    private bool _canTogglePlayback;
    private BitmapImage? _artwork;

    public MediaViewModel()
    {
        _media = new MediaSessionService();
        _media.Changed += OnMediaChanged;

        PreviousCommand = new RelayCommand(async _ => await _media.SkipPreviousAsync(), _ => CanSkipPrevious);
        PlayPauseCommand = new RelayCommand(async _ => await _media.TogglePlayPauseAsync(), _ => CanTogglePlayback);
        NextCommand = new RelayCommand(async _ => await _media.SkipNextAsync(), _ => CanSkipNext);

        _ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _ticker.Tick += async (_, _) => await RefreshAsync(full: false);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public RelayCommand PreviousCommand { get; }
    public RelayCommand PlayPauseCommand { get; }
    public RelayCommand NextCommand { get; }

    public string Title
    {
        get => _title;
        private set
        {
            if (SetField(ref _title, value))
            {
                OnPropertyChanged(nameof(Headline));
            }
        }
    }

    public string Artist
    {
        get => _artist;
        private set
        {
            if (SetField(ref _artist, value))
            {
                OnPropertyChanged(nameof(Headline));
            }
        }
    }

    public string Headline =>
        string.IsNullOrWhiteSpace(Artist) ? Title : $"{Title}  ·  {Artist}";

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
        private set => SetField(ref _canSkipNext, value);
    }

    public bool CanSkipPrevious
    {
        get => _canSkipPrevious;
        private set => SetField(ref _canSkipPrevious, value);
    }

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

    public async Task InitializeAsync()
    {
        await _media.StartAsync();
        await RefreshAsync(full: true);
        _ticker.Start();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ticker.Stop();
        _media.Changed -= OnMediaChanged;
        _media.Dispose();
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

    private async Task RefreshAsync(bool full)
    {
        if (_disposed)
        {
            return;
        }

        await _refreshLock.WaitAsync();
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
            Artist = "Запусти Spotify, Яндекс Музыку или видео";
            AppId = string.Empty;
            TimeText = "0:00 / 0:00";
            Progress = 0;
            IsPlaying = false;
            CanSkipNext = false;
            CanSkipPrevious = false;
            CanTogglePlayback = false;
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

        AppId = snapshot.AppId;
        IsPlaying = snapshot.IsPlaying;
        CanSkipNext = snapshot.CanSkipNext;
        CanSkipPrevious = snapshot.CanSkipPrevious;
        CanTogglePlayback = snapshot.IsPlaying ? snapshot.CanPause : snapshot.CanPlay;

        var duration = snapshot.Duration;
        var position = snapshot.Position;
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        if (position < TimeSpan.Zero)
        {
            position = TimeSpan.Zero;
        }

        if (duration > TimeSpan.Zero && position > duration)
        {
            position = duration;
        }

        TimeText = $"{Format(position)} / {Format(duration)}";
        Progress = duration.TotalSeconds <= 0 ? 0 : Math.Clamp(position.TotalSeconds / duration.TotalSeconds, 0, 1);

        if (!keepArtwork)
        {
            Artwork = snapshot.Artwork;
        }

        CommandManager.InvalidateRequerySuggested();
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
