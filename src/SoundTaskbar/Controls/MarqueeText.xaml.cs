using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace SoundTaskbar.Controls;

public partial class MarqueeText : UserControl
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(
            nameof(Text),
            typeof(string),
            typeof(MarqueeText),
            new PropertyMetadata(string.Empty));

    private bool _restartQueued;
    private bool _isHovering;

    public MarqueeText()
    {
        InitializeComponent();
        // Hover is driven by a larger parent hit-zone via SetHovering
        // (artwork + title + artist), not by this control alone.
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public void SetHovering(bool hovering)
    {
        if (_isHovering == hovering)
        {
            return;
        }

        _isHovering = hovering;
        if (hovering)
        {
            Restart();
        }
        else
        {
            Stop();
        }
    }

    private void OnHostSizeChanged(object sender, SizeChangedEventArgs e) => QueueRestart();

    private void OnTextSizeChanged(object sender, SizeChangedEventArgs e) => QueueRestart();

    private void QueueRestart()
    {
        if (_restartQueued)
        {
            return;
        }

        _restartQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _restartQueued = false;
            if (_isHovering)
            {
                Restart();
            }
            else
            {
                Stop();
            }
        }, DispatcherPriority.Background);
    }

    private void Stop()
    {
        Slide.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
        Slide.X = 0;
    }

    private void Restart()
    {
        if (!IsLoaded || !_isHovering)
        {
            Stop();
            return;
        }

        Stop();

        var viewWidth = Host.ActualWidth;
        var textWidth = RunText.ActualWidth;
        if (viewWidth <= 1 || textWidth <= viewWidth + 2)
        {
            return;
        }

        var distance = textWidth - viewWidth + 12;
        var seconds = Math.Max(3.5, distance / 32);

        var animation = new DoubleAnimation
        {
            From = 0,
            To = -distance,
            Duration = TimeSpan.FromSeconds(seconds),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            BeginTime = TimeSpan.FromMilliseconds(250)
        };

        Slide.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, animation);
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == TextProperty || e.Property == FontSizeProperty || e.Property == FontWeightProperty)
        {
            QueueRestart();
        }
    }
}
