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

    /// <summary>Match scrim to taskbar luminance so the edge fade reads as text disappearing.</summary>
    public void SetEdgeScrim(bool isLight)
    {
        if (isLight)
        {
            ScrimMid.Color = Color.FromArgb(0x99, 0xE8, 0xE8, 0xE8);
            ScrimEnd.Color = Color.FromArgb(0xF0, 0xE8, 0xE8, 0xE8);
        }
        else
        {
            ScrimMid.Color = Color.FromArgb(0x99, 0x18, 0x18, 0x18);
            ScrimEnd.Color = Color.FromArgb(0xF0, 0x12, 0x12, 0x12);
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
        UpdateOverflowFlag();
        // Scrim marks the block edge whenever title is clipped.
        EdgeScrim.Opacity = _overflows ? 1 : 0;
    }

    private void UpdateOverflowFlag()
    {
        var viewWidth = Root.ActualWidth;
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

        var viewWidth = Root.ActualWidth;
        var textWidth = RunText.ActualWidth;
        var distance = textWidth - viewWidth + 8;
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

        // Hide edge scrim only when scroll reaches the real end of the title.
        var scrimAnim = new DoubleAnimationUsingKeyFrames
        {
            Duration = duration,
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            BeginTime = begin
        };
        scrimAnim.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromPercent(0)));
        scrimAnim.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromPercent(0.9)));
        scrimAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));

        EdgeScrim.Opacity = 1;
        Slide.BeginAnimation(TranslateTransform.XProperty, slide);
        ShadowSlide.BeginAnimation(TranslateTransform.XProperty, slide.Clone());
        EdgeScrim.BeginAnimation(OpacityProperty, scrimAnim);
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
