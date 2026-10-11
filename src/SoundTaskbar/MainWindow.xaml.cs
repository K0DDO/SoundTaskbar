using System.Windows;
using SoundTaskbar.Services;
using SoundTaskbar.ViewModels;

namespace SoundTaskbar;

public partial class MainWindow : Window
{
    private readonly MediaViewModel _viewModel = new();
    private TaskbarHostService? _taskbarHost;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _taskbarHost = new TaskbarHostService(this);
        _taskbarHost.Start();
        await _viewModel.InitializeAsync();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _taskbarHost?.Dispose();
        _viewModel.Dispose();
    }

    private void OnExitClick(object sender, RoutedEventArgs e) =>
        Application.Current.Shutdown();
}
