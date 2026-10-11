using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SoundTaskbar.Services;
using SoundTaskbar.ViewModels;

namespace SoundTaskbar;

public partial class MainWindow : Window
{
    private const double VolumeClosedWidth = 22;
    private const double VolumeOpenWidth = 80;

    private readonly MediaViewModel _viewModel = new();
    private readonly EventHandler<TaskbarAppearance> _appearanceHandler;
    private readonly EventHandler<TaskbarLayout> _layoutHandler;
    private readonly SolidColorBrush _titleBrush = new(Color.FromRgb(0xF5, 0xF5, 0xF5));
    private readonly SolidColorBrush _mutedBrush = new(Color.FromRgb(0xB8, 0xC0, 0xCC));
    private readonly SolidColorBrush _progressTrackBrush = new(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
    private readonly SolidColorBrush _progressFillBrush = new(Color.FromRgb(0x9E, 0xC5, 0xFF));
    private TaskbarHostService? _taskbarHost;
    private TaskbarAppearanceService? _appearance;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _appearanceHandler = (_, appearance) => ApplyAppearance(appearance);
        _layoutHandler = (_, layout) => ApplyLayout(layout);
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        TitleMarquee.Foreground = _titleBrush;
        ArtistText.Foreground = _mutedBrush;
        TimeText.Foreground = _mutedBrush;
        ArtworkPlaceholder.Foreground = _mutedBrush;
        ProgressBar.Background = _progressTrackBrush;
        ProgressBar.Foreground = _progressFillBrush;

        VolumeHost.Width = VolumeClosedWidth;
        VolumeSlider.Opacity = 0;
        VolumeSlider.IsHitTestVisible = false;
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
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

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

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MediaViewModel.IsVolumeEditorOpen))
        {
            AnimateVolumeEditor(_viewModel.IsVolumeEditorOpen);
        }
    }

    private void OnVolumeClick(object sender, RoutedEventArgs e)
    {
        _viewModel.RefreshVolume();
        if (!_viewModel.CanControlVolume)
        {
            return;
        }

        _viewModel.IsVolumeEditorOpen = !_viewModel.IsVolumeEditorOpen;
    }

    private void AnimateVolumeEditor(bool open)
    {
        var duration = TimeSpan.FromMilliseconds(260);
        var ease = new CubicEase { EasingMode = open ? EasingMode.EaseOut : EasingMode.EaseIn };

        VolumeHost.BeginAnimation(
            WidthProperty,
            new DoubleAnimation
            {
                To = open ? VolumeOpenWidth : VolumeClosedWidth,
                Duration = duration,
                EasingFunction = ease
            });

        VolumeSlider.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation
            {
                To = open ? 1 : 0,
                Duration = TimeSpan.FromMilliseconds(open ? 200 : 140),
                BeginTime = open ? TimeSpan.FromMilliseconds(40) : TimeSpan.Zero,
                EasingFunction = ease
            });

        VolumeButton.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation
            {
                To = open ? 0 : 1,
                Duration = TimeSpan.FromMilliseconds(open ? 140 : 200),
                BeginTime = open ? TimeSpan.Zero : TimeSpan.FromMilliseconds(40),
                EasingFunction = ease
            });

        VolumeSlider.IsHitTestVisible = open;
        VolumeButton.IsHitTestVisible = !open;
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
        _titleBrush.Color = appearance.Foreground;
        _mutedBrush.Color = appearance.MutedForeground;
        _progressTrackBrush.Color = appearance.ProgressTrack;
        _progressFillBrush.Color = appearance.IsLight
            ? Color.FromRgb(0x00, 0x6C, 0xBE)
            : Color.FromRgb(0x9E, 0xC5, 0xFF);
        TitleMarquee.Foreground = _titleBrush;

        var iconBrush = new SolidColorBrush(appearance.Foreground);
        PreviousIcon.Foreground = iconBrush;
        NextIcon.Foreground = iconBrush;
    }

    private void OnExitClick(object sender, RoutedEventArgs e) =>
        Application.Current.Shutdown();
}
