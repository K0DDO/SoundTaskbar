using System.Windows;
using System.Windows.Media;
using SoundTaskbar.Services;
using SoundTaskbar.ViewModels;

namespace SoundTaskbar;

public partial class MainWindow : Window
{
    private readonly MediaViewModel _viewModel = new();
    private readonly EventHandler<TaskbarAppearance> _appearanceHandler;
    private readonly SolidColorBrush _panelBrush = new(Color.FromArgb(0x55, 0x20, 0x20, 0x20));
    private readonly SolidColorBrush _headlineBrush = new(Colors.White);
    private readonly SolidColorBrush _mutedBrush = new(Color.FromRgb(0xC0, 0xC6, 0xD0));
    private readonly SolidColorBrush _progressTrackBrush = new(Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF));
    private readonly SolidColorBrush _progressFillBrush = new(Color.FromRgb(0x9E, 0xC5, 0xFF));
    private TaskbarHostService? _taskbarHost;
    private TaskbarAppearanceService? _appearance;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _appearanceHandler = (_, appearance) => ApplyAppearance(appearance);

        RootBorder.Background = _panelBrush;
        HeadlineText.Foreground = _headlineBrush;
        TimeText.Foreground = _mutedBrush;
        ArtworkPlaceholder.Foreground = _mutedBrush;
        ProgressBar.Background = _progressTrackBrush;
        ProgressBar.Foreground = _progressFillBrush;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _appearance = new TaskbarAppearanceService(this);
        _appearance.Changed += _appearanceHandler;
        _appearance.Start();
        ApplyAppearance(_appearance.Current);

        _taskbarHost = new TaskbarHostService(this);
        _taskbarHost.Start();
        await _viewModel.InitializeAsync();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_appearance is not null)
        {
            _appearance.Changed -= _appearanceHandler;
            _appearance.Dispose();
        }

        _taskbarHost?.Dispose();
        _viewModel.Dispose();
    }

    private void ApplyAppearance(TaskbarAppearance appearance)
    {
        // Mutate brushes in place — replacing brushes / re-applying acrylic was flashing the overlay.
        _panelBrush.Color = appearance.PanelColor;
        _headlineBrush.Color = appearance.Foreground;
        _mutedBrush.Color = appearance.MutedForeground;
        _progressTrackBrush.Color = appearance.ProgressTrack;
        _progressFillBrush.Color = appearance.IsLight
            ? Color.FromRgb(0x00, 0x6C, 0xBE)
            : Color.FromRgb(0x9E, 0xC5, 0xFF);
    }

    private void OnExitClick(object sender, RoutedEventArgs e) =>
        Application.Current.Shutdown();
}
