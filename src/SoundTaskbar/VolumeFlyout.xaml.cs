using System.Windows;
using System.Windows.Controls.Primitives;
using SoundTaskbar.ViewModels;

namespace SoundTaskbar;

public partial class VolumeFlyout : Window
{
    private readonly MediaViewModel _viewModel;
    private bool _syncing;

    public VolumeFlyout(MediaViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        SyncFromViewModel();
    }

    public void SyncFromViewModel()
    {
        _syncing = true;
        try
        {
            VolumeSlider.Value = _viewModel.Volume;
            PercentText.Text = $"{_viewModel.VolumePercent}%";
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing)
        {
            return;
        }

        _viewModel.SetVolume((float)e.NewValue);
        PercentText.Text = $"{_viewModel.VolumePercent}%";
    }

    private void OnMuteClick(object sender, RoutedEventArgs e)
    {
        _viewModel.ToggleMute();
        SyncFromViewModel();
    }

    private void OnDeactivated(object? sender, EventArgs e) => Hide();
}
