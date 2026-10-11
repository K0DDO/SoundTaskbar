using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace SoundTaskbar.Controls;

public partial class MarqueeText : UserControl
{
    private const double FadeStartOffset = 0.62;

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

    private readonly GradientStop _fadeBegin = new(Colors.Black, 1);
    private readonly GradientStop _fadeOut = new(Colors.Black, 1);
    private readonly LinearGradientBrush _fadeBrush;
    private bool _restartQueued;
    private bool _isHovering;
    private bool _overflows;

    public MarqueeText()
    {
        InitializeComponent();

        // Build mask in code so the brush stays unfrozen and animatable.
        _fadeBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0),
            GradientStops =
            {
                new GradientStop(Colors.Black, 0),
                _fadeBegin,
                _fadeOut
            }
        };
        FadeHost.OpacityMask = _fadeBrush;
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
        Slide.BeginAnimation(TranslateTransform.XProperty, null);
        _fadeBegin.BeginAnimation(GradientStop.OffsetProperty, null);
        _fadeOut.BeginAnimation(GradientStop.OffsetProperty, null);
        Slide.X = 0;
        UpdateOverflowFlag();
        ApplyFade(_overflows);
    }

    private void ApplyFade(bool show)
    {
        if (show)
        {
            _fadeBegin.Offset = FadeStartOffset;
            _fadeBegin.Color = Colors.Black;
            _fadeOut.Offset = 1;
            _fadeOut.Color = Colors.Transparent;
        }
        else
        {
            _fadeBegin.Offset = 1;
            _fadeBegin.Color = Colors.Black;
            _fadeOut.Offset = 1;
            _fadeOut.Color = Colors.Black;
        }
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

        if (!_overflows)
        {
            return;
        }

        var viewWidth = Host.ActualWidth;
        var textWidth = RunText.ActualWidth;
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

        // Keep right-edge fade until scroll reaches the end; AutoReverse restores it.
        var fadeBeginAnim = new DoubleAnimationUsingKeyFrames
        {
            Duration = duration,
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            BeginTime = begin
        };
        fadeBeginAnim.KeyFrames.Add(new DiscreteDoubleKeyFrame(FadeStartOffset, KeyTime.FromPercent(0)));
        fadeBeginAnim.KeyFrames.Add(new DiscreteDoubleKeyFrame(FadeStartOffset, KeyTime.FromPercent(0.88)));
        fadeBeginAnim.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(1)));

        ApplyFade(true);
        Slide.BeginAnimation(TranslateTransform.XProperty, slide);
        _fadeBegin.BeginAnimation(GradientStop.OffsetProperty, fadeBeginAnim);
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
