using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SoundTaskbar.Controls;

public enum TransportIconKind
{
    SkipPrevious,
    SkipNext,
    SeekBack,
    SeekForward
}

public partial class TransportIcon : UserControl
{
    public static readonly DependencyProperty KindProperty =
        DependencyProperty.Register(
            nameof(Kind),
            typeof(TransportIconKind),
            typeof(TransportIcon),
            new PropertyMetadata(TransportIconKind.SkipNext, OnVisualChanged));

    public static readonly DependencyProperty SecondsProperty =
        DependencyProperty.Register(
            nameof(Seconds),
            typeof(int),
            typeof(TransportIcon),
            new PropertyMetadata(10, OnSecondsChanged));

    private TransportIconKind _renderedKind = TransportIconKind.SkipNext;
    private bool _loaded;
    private int _animationGeneration;

    public TransportIcon()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _loaded = true;
            ApplyVisual(Kind, animate: false);
        };
        Foreground = Brushes.White;
    }

    public TransportIconKind Kind
    {
        get => (TransportIconKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public int Seconds
    {
        get => (int)GetValue(SecondsProperty);
        set => SetValue(SecondsProperty, value);
    }

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TransportIcon icon)
        {
            icon.ApplyVisual((TransportIconKind)e.NewValue, animate: icon._loaded);
        }
    }

    private static void OnSecondsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TransportIcon icon)
        {
            icon.SeekLabel.Text = e.NewValue?.ToString() ?? "10";
        }
    }

    private void ApplyVisual(TransportIconKind kind, bool animate)
    {
        var generation = ++_animationGeneration;
        SeekLabel.Text = Seconds.ToString();

        if (!animate || kind == _renderedKind)
        {
            SkipPath.BeginAnimation(UIElement.OpacityProperty, null);
            SeekRoot.BeginAnimation(UIElement.OpacityProperty, null);
            ShowKind(kind);
            _renderedKind = kind;
            return;
        }

        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(100))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };

        fadeOut.Completed += (_, _) =>
        {
            if (generation != _animationGeneration)
            {
                return;
            }

            ShowKind(kind);
            _renderedKind = kind;

            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };

            if (IsSeek(kind))
            {
                SeekRoot.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            }
            else
            {
                SkipPath.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            }
        };

        if (IsSeek(_renderedKind))
        {
            SeekRoot.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            SkipPath.Opacity = 0;
        }
        else
        {
            SkipPath.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            SeekRoot.Opacity = 0;
        }
    }

    private void ShowKind(TransportIconKind kind)
    {
        if (IsSeek(kind))
        {
            SkipPath.Opacity = 0;
            SeekRoot.Opacity = 1;
            var flip = kind == TransportIconKind.SeekBack ? -1 : 1;
            SeekFlip.ScaleX = flip;
            TipFlip.ScaleX = flip;
            // Tip path is drawn for forward (top-right); flip mirrors for back.
            SeekTip.Margin = flip < 0 ? new Thickness(0.2, 0, 0, 0) : new Thickness(0);
        }
        else
        {
            SeekRoot.Opacity = 0;
            SkipPath.Opacity = 1;
            SkipPath.Data = GeometryForSkip(kind);
        }
    }

    private static bool IsSeek(TransportIconKind kind) =>
        kind is TransportIconKind.SeekBack or TransportIconKind.SeekForward;

    private static Geometry GeometryForSkip(TransportIconKind kind) =>
        kind switch
        {
            TransportIconKind.SkipPrevious => Geometry.Parse("M1,2 L1,12 L2.2,12 L2.2,2 Z M12.5,2 L12.5,12 L4.5,7 Z"),
            _ => Geometry.Parse("M1.5,2 L9.5,7 L1.5,12 Z M11.3,2 L12.5,2 L12.5,12 L11.3,12 Z")
        };
}
