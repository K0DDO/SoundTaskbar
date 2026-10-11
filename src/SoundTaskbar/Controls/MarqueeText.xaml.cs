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

    public static readonly DependencyProperty ScrollWhenOverflowProperty =
        DependencyProperty.Register(
            nameof(ScrollWhenOverflow),
            typeof(bool),
            typeof(MarqueeText),
            new PropertyMetadata(false, (_, __) => { /* restart via QueueRestart on size */ }));

    private bool _restartQueued;
    private bool _isHovering;
    private bool _overflows;

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

    /// <summary>When true, marquee runs whenever text overflows (no hover needed).</summary>
    public bool ScrollWhenOverflow
    {
        get => (bool)GetValue(ScrollWhenOverflowProperty);
        set => SetValue(ScrollWhenOverflowProperty, value);
    }

    public void SetHovering(bool hovering)
    {
        if (_isHovering == hovering)
        {
            return;
        }

        _isHovering = hovering;
        if (hovering || ScrollWhenOverflow)
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
            if (_isHovering || ScrollWhenOverflow)
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
        EllipsisMark.BeginAnimation(OpacityProperty, null);
        Slide.X = 0;
        UpdateOverflowFlag();
        // At rest: keep "..." while the title still overflows.
        EllipsisMark.Opacity = _overflows ? 1 : 0;
    }

    private void UpdateOverflowFlag()
    {
        var viewWidth = Host.ActualWidth;
        var textWidth = RunText.ActualWidth;
        _overflows = viewWidth > 1 && textWidth > viewWidth + 2;
    }

    private void Restart()
    {
        if (!IsLoaded || (!_isHovering && !ScrollWhenOverflow))
        {
            Stop();
            return;
        }

        Stop();

        var viewWidth = Host.ActualWidth;
        var textWidth = RunText.ActualWidth;
        if (!_overflows)
        {
            return;
        }

        var distance = textWidth - viewWidth + 12;
        var seconds = Math.Max(3.5, distance / 32);
        var duration = TimeSpan.FromSeconds(seconds);
        var begin = TimeSpan.FromMilliseconds(250);
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };

        var slide = new DoubleAnimation
        {
            From = 0,
            To = -distance,
            Duration = duration,
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = ease,
            BeginTime = begin
        };

        // Keep "..." until the scroll reaches the end; AutoReverse brings them back.
        var ellipsis = new DoubleAnimationUsingKeyFrames
        {
            Duration = duration,
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            BeginTime = begin
        };
        ellipsis.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromPercent(0)));
        ellipsis.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromPercent(0.92)));
        ellipsis.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));

        Slide.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, slide);
        EllipsisMark.BeginAnimation(OpacityProperty, ellipsis);
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == TextProperty ||
            e.Property == FontSizeProperty ||
            e.Property == FontWeightProperty ||
            e.Property == ScrollWhenOverflowProperty)
        {
            QueueRestart();
        }
    }
}
