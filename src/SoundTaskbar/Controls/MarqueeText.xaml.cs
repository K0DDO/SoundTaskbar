using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
            new PropertyMetadata(false));

    private bool _restartQueued;
    private bool _isHovering;
    private bool _overflows;
    private bool _isRunning;
    private double _lastViewWidth;
    private double _lastTextWidth;

    public MarqueeText()
    {
        InitializeComponent();
        Loaded += (_, _) => QueueRestart();
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public bool ScrollWhenOverflow
    {
        get => (bool)GetValue(ScrollWhenOverflowProperty);
        set => SetValue(ScrollWhenOverflowProperty, value);
    }

    public void SetEdgeScrim(bool isLight)
    {
        if (isLight)
        {
            ScrimMid.Color = Color.FromArgb(0x66, 0xE8, 0xE8, 0xE8);
            ScrimEnd.Color = Color.FromArgb(0xCC, 0xE8, 0xE8, 0xE8);
        }
        else
        {
            ScrimMid.Color = Color.FromArgb(0x66, 0x14, 0x14, 0x14);
            ScrimEnd.Color = Color.FromArgb(0xCC, 0x10, 0x10, 0x10);
        }
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
        }, DispatcherPriority.Render);
    }

    private void Stop()
    {
        Slide.BeginAnimation(TranslateTransform.XProperty, null);
        ShadowSlide.BeginAnimation(TranslateTransform.XProperty, null);
        EdgeScrim.BeginAnimation(OpacityProperty, null);
        Slide.X = 0;
        ShadowSlide.X = 0;
        _isRunning = false;
        UpdateOverflowFlag();
        EdgeScrim.Opacity = _overflows ? 1 : 0;
    }

    private void UpdateOverflowFlag()
    {
        var viewWidth = Root.ActualWidth;
        // Prefer DesiredSize: Canvas children keep natural text width.
        var textWidth = Math.Max(RunText.ActualWidth, RunText.DesiredSize.Width);
        if (textWidth < 1)
        {
            RunText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            textWidth = RunText.DesiredSize.Width;
        }

        _overflows = viewWidth > 1 && textWidth > viewWidth + 2;
        _lastViewWidth = viewWidth;
        _lastTextWidth = textWidth;
    }

    private void Restart()
    {
        if (!IsLoaded || (!_isHovering && !ScrollWhenOverflow))
        {
            Stop();
            return;
        }

        var prevView = _lastViewWidth;
        var prevText = _lastTextWidth;
        UpdateOverflowFlag();

        if (!_overflows)
        {
            Stop();
            return;
        }

        var viewWidth = _lastViewWidth;
        var textWidth = _lastTextWidth;

        // Ignore tiny viewport changes (pause chip shell resize) — restarting looks like a jerk.
        if (_isRunning &&
            Math.Abs(prevView - viewWidth) < 14 &&
            Math.Abs(prevText - textWidth) < 0.5)
        {
            _lastViewWidth = viewWidth;
            return;
        }

        Slide.BeginAnimation(TranslateTransform.XProperty, null);
        ShadowSlide.BeginAnimation(TranslateTransform.XProperty, null);
        EdgeScrim.BeginAnimation(OpacityProperty, null);
        Slide.X = 0;
        ShadowSlide.X = 0;

        // Scroll far enough that the last glyph clears the scrim and sits fully in view.
        var scrimPad = EdgeScrim.Width;
        var distance = textWidth - viewWidth + scrimPad;
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

        var scrimAnim = new DoubleAnimationUsingKeyFrames
        {
            Duration = duration,
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            BeginTime = begin
        };
        scrimAnim.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromPercent(0)));
        scrimAnim.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromPercent(0.88)));
        scrimAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));

        EdgeScrim.Opacity = 1;
        Slide.BeginAnimation(TranslateTransform.XProperty, slide);
        ShadowSlide.BeginAnimation(TranslateTransform.XProperty, slide.Clone());
        EdgeScrim.BeginAnimation(OpacityProperty, scrimAnim);
        _isRunning = true;
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == TextProperty ||
            e.Property == FontSizeProperty ||
            e.Property == FontWeightProperty ||
            e.Property == ScrollWhenOverflowProperty)
        {
            _isRunning = false;
            QueueRestart();
        }
    }
}
