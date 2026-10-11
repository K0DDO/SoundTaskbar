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
            new PropertyMetadata(TransportIconKind.SkipNext, OnKindChanged));

    private TransportIconKind _renderedKind = TransportIconKind.SkipNext;
    private bool _loaded;
    private int _animationGeneration;

    public TransportIcon()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _loaded = true;
            ApplyGeometry(Kind, animate: false);
        };
        Foreground = Brushes.White;
    }

    public TransportIconKind Kind
    {
        get => (TransportIconKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    private static void OnKindChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TransportIcon icon)
        {
            icon.ApplyGeometry((TransportIconKind)e.NewValue, animate: icon._loaded);
        }
    }

    private void ApplyGeometry(TransportIconKind kind, bool animate)
    {
        var generation = ++_animationGeneration;

        if (!animate || kind == _renderedKind)
        {
            IconPath.BeginAnimation(UIElement.OpacityProperty, null);
            IconPath.Data = GeometryFor(kind);
            IconPath.Opacity = 1;
            _renderedKind = kind;
            return;
        }

        var fadeOut = new DoubleAnimation(IconPath.Opacity, 0, TimeSpan.FromMilliseconds(100))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };

        fadeOut.Completed += (_, _) =>
        {
            if (generation != _animationGeneration)
            {
                return;
            }

            IconPath.Data = GeometryFor(kind);
            _renderedKind = kind;
            IconPath.BeginAnimation(
                UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                });
        };

        IconPath.BeginAnimation(UIElement.OpacityProperty, fadeOut);
    }

    private static Geometry GeometryFor(TransportIconKind kind) =>
        kind switch
        {
            // Skip previous: bar + triangle  |◀
            TransportIconKind.SkipPrevious => Geometry.Parse("M1,2 L1,12 L2.2,12 L2.2,2 Z M12.5,2 L12.5,12 L4.5,7 Z"),
            // Skip next: triangle + bar  ▶|
            TransportIconKind.SkipNext => Geometry.Parse("M1.5,2 L9.5,7 L1.5,12 Z M11.3,2 L12.5,2 L12.5,12 L11.3,12 Z"),
            // Seek back: double triangle  ◀◀
            TransportIconKind.SeekBack => Geometry.Parse("M7.2,2 L7.2,12 L1.2,7 Z M13.2,2 L13.2,12 L7.2,7 Z"),
            // Seek forward: double triangle  ▶▶
            _ => Geometry.Parse("M1,2 L1,12 L7,7 Z M7,2 L7,12 L13,7 Z")
        };
}
