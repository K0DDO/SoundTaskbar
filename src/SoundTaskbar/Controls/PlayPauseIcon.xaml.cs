using System.Windows;
using System.Windows.Controls;

namespace SoundTaskbar.Controls;

public partial class PlayPauseIcon : UserControl
{
    public static readonly DependencyProperty IsPlayingProperty =
        DependencyProperty.Register(
            nameof(IsPlaying),
            typeof(bool),
            typeof(PlayPauseIcon),
            new PropertyMetadata(false, OnPlayingChanged));

    public PlayPauseIcon()
    {
        InitializeComponent();
        ApplyPlaying(IsPlaying);
    }

    public bool IsPlaying
    {
        get => (bool)GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    private static void OnPlayingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PlayPauseIcon icon)
        {
            icon.ApplyPlaying((bool)e.NewValue);
        }
    }

    private void ApplyPlaying(bool playing)
    {
        PlayPath.Visibility = playing ? Visibility.Collapsed : Visibility.Visible;
        PauseRoot.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
    }
}
