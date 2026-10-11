using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SoundTaskbar.Services;
using SoundTaskbar.ViewModels;

namespace SoundTaskbar;

public partial class MainWindow : Window
{
    private readonly MediaViewModel _viewModel = new();
    private readonly EventHandler<TaskbarAppearance> _appearanceHandler;
    private readonly EventHandler<TaskbarLayout> _layoutHandler;
    private readonly SolidColorBrush _headlineBrush = new(Color.FromRgb(0xF5, 0xF5, 0xF5));
    private readonly SolidColorBrush _mutedBrush = new(Color.FromRgb(0xC8, 0xCD, 0xD6));
    private readonly SolidColorBrush _progressTrackBrush = new(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
    private readonly SolidColorBrush _progressFillBrush = new(Color.FromRgb(0x9E, 0xC5, 0xFF));
    private TaskbarHostService? _taskbarHost;
    private TaskbarAppearanceService? _appearance;
    private VolumeFlyout? _volumeFlyout;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _appearanceHandler = (_, appearance) => ApplyAppearance(appearance);
        _layoutHandler = (_, layout) => ApplyLayout(layout);

        HeadlineText.Foreground = _headlineBrush;
        TimeText.Foreground = _mutedBrush;
        ArtworkPlaceholder.Foreground = _mutedBrush;
        ProgressBar.Background = _progressTrackBrush;
        ProgressBar.Foreground = _progressFillBrush;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _taskbarHost = new TaskbarHostService(this);
        _taskbarHost.LayoutChanged += _layoutHandler;
        _taskbarHost.Start();

        _appearance = new TaskbarAppearanceService(this);
        _appearance.Changed += _appearanceHandler;
        _appearance.Start();
        ApplyAppearance(_appearance.Current);

        await _viewModel.InitializeAsync();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _volumeFlyout?.Close();
        _volumeFlyout = null;

        if (_taskbarHost is not null)
        {
            _taskbarHost.LayoutChanged -= _layoutHandler;
            _taskbarHost.Dispose();
        }

        if (_appearance is not null)
        {
            _appearance.Changed -= _appearanceHandler;
            _appearance.Dispose();
        }

        _viewModel.Dispose();
    }

    private void OnVolumeClick(object sender, RoutedEventArgs e)
    {
        _viewModel.RefreshVolume();
        if (!_viewModel.CanControlVolume)
        {
            return;
        }

        _volumeFlyout ??= new VolumeFlyout(_viewModel);
        if (_volumeFlyout.IsVisible)
        {
            _volumeFlyout.Hide();
            return;
        }

        _volumeFlyout.SyncFromViewModel();

        // PointToScreen works for taskbar-hosted child windows.
        var screen = VolumeButton.PointToScreen(new Point(VolumeButton.ActualWidth / 2, 0));
        _volumeFlyout.Left = screen.X - (_volumeFlyout.Width / 2);
        _volumeFlyout.Top = screen.Y - _volumeFlyout.Height - 10;
        _volumeFlyout.Show();
        _volumeFlyout.Activate();
    }

    private void ApplyLayout(TaskbarLayout layout)
    {
        WidgetRoot.Width = layout.WidgetWidth;
        WidgetRoot.Height = layout.WindowHeight;
        Canvas.SetLeft(WidgetRoot, layout.WidgetLeft);
        Canvas.SetTop(WidgetRoot, 0);
    }

    private void ApplyAppearance(TaskbarAppearance appearance)
    {
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
