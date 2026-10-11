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
            new PropertyMetadata(string.Empty, (_, _) => { /* bound to TextBlock */ }));

    private DoubleAnimation? _animation;
    private bool _restartQueued;

    public MarqueeText()
    {
        InitializeComponent();
        Loaded += (_, _) => Restart();
        IsVisibleChanged += (_, _) => Restart();
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
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
            Restart();
        }, DispatcherPriority.Background);
    }

    private void Restart()
    {
        if (!IsLoaded)
        {
            return;
        }

        Slide.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
        Slide.X = 0;

        var viewWidth = Host.ActualWidth;
        var textWidth = RunText.ActualWidth;
        if (viewWidth <= 1 || textWidth <= viewWidth + 2)
        {
            return;
        }

        var distance = textWidth - viewWidth + 12;
        var seconds = Math.Max(4, distance / 28);

        _animation = new DoubleAnimation
        {
            From = 0,
            To = -distance,
            Duration = TimeSpan.FromSeconds(seconds),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };

        // Pause briefly at each end.
        _animation.BeginTime = TimeSpan.FromSeconds(1.2);
        Slide.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, _animation);
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
