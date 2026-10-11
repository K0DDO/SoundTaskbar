using System.Windows;
using System.Windows.Media;
using SoundTaskbar.Services;
using SoundTaskbar.ViewModels;

namespace SoundTaskbar;

public partial class MainWindow : Window
{
    private readonly MediaViewModel _viewModel = new();
    private readonly EventHandler<TaskbarAppearance> _appearanceHandler;
    private TaskbarHostService? _taskbarHost;
    private TaskbarAppearanceService? _appearance;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _appearanceHandler = (_, appearance) => ApplyAppearance(appearance);
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
        // When blur/transparency is on, keep the brush light so acrylic shows through.
        RootBorder.Background = appearance.UseBlur
            ? Brushes.Transparent
            : new SolidColorBrush(appearance.PanelColor);

        HeadlineText.Foreground = new SolidColorBrush(appearance.Foreground);
        TimeText.Foreground = new SolidColorBrush(appearance.MutedForeground);
        ArtworkPlaceholder.Foreground = new SolidColorBrush(appearance.MutedForeground);
        ProgressBar.Background = new SolidColorBrush(appearance.ProgressTrack);
        ProgressBar.Foreground = new SolidColorBrush(
            appearance.IsLight ? Color.FromRgb(0x00, 0x6C, 0xBE) : Color.FromRgb(0x9E, 0xC5, 0xFF));
    }

    private void OnExitClick(object sender, RoutedEventArgs e) =>
        Application.Current.Shutdown();
}
