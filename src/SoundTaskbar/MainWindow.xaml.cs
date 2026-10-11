using System.Collections.Specialized;
using System.ComponentModel;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SoundTaskbar.Services;
using SoundTaskbar.ViewModels;

namespace SoundTaskbar;

public partial class MainWindow : Window
{
    private const double VolumeClosedWidth = 16;
    private const double VolumeOpenWidth = 74;
    private const double VolumeButtonWidth = 16;
    private const double ParkedDividerWidth = 2.5;

    private static readonly DependencyProperty ShellWidthAnimProperty =
        DependencyProperty.Register(
            nameof(ShellWidthAnim),
            typeof(double),
            typeof(MainWindow),
            new PropertyMetadata(MediaViewModel.SoloWidgetWidth, OnShellWidthAnimChanged));

    private readonly MediaViewModel _viewModel = new();
    private readonly EventHandler<TaskbarAppearance> _appearanceHandler;
    private readonly EventHandler<TaskbarLayout> _layoutHandler;
    private readonly SolidColorBrush _titleBrush = new(Color.FromRgb(0xF5, 0xF5, 0xF5));
    private readonly SolidColorBrush _mutedBrush = new(Color.FromRgb(0xB8, 0xC0, 0xCC));
    private readonly SolidColorBrush _progressTrackBrush = new(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
    private readonly SolidColorBrush _progressFillBrush = new(Color.FromRgb(0x9E, 0xC5, 0xFF));
    private TaskbarHostService? _taskbarHost;
    private TaskbarAppearanceService? _appearance;
    private bool _dualVisual;
    private bool _parkedOpen;
    private bool _layoutReady;
    private CancellationTokenSource? _parkedRemeasureCts;

    private const double ChipVolumeClosedWidth = 14;
    private const double ChipVolumeOpenWidth = 64;

    private double ShellWidthAnim
    {
        get => (double)GetValue(ShellWidthAnimProperty);
        set => SetValue(ShellWidthAnimProperty, value);
    }

    private static void OnShellWidthAnimChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var window = (MainWindow)d;
        if (window._taskbarHost is not null)
        {
            window._taskbarHost.DesiredWidgetWidthDip = (double)e.NewValue;
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _appearanceHandler = (_, appearance) => ApplyAppearance(appearance);
        _layoutHandler = (_, layout) => ApplyLayout(layout);
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.SideSessions.CollectionChanged += OnSideSessionsChanged;
        _viewModel.ParkedLayoutChanged += OnParkedLayoutChanged;

        TitleMarquee.Foreground = _titleBrush;
        ArtistText.Foreground = _mutedBrush;
        TimeText.Foreground = _mutedBrush;
        ArtworkPlaceholder.Foreground = _mutedBrush;
        ProgressBar.Background = _progressTrackBrush;
        ProgressBar.Foreground = _progressFillBrush;

        VolumeHost.Width = VolumeClosedWidth;
        VolumeSlider.Opacity = 0;
        VolumeSlider.IsHitTestVisible = false;
        MuteButton.Opacity = 0;
        MuteButton.IsHitTestVisible = false;

        DualHost.Opacity = 0;
        DualHost.IsHitTestVisible = false;
        DualSlide.X = 28;
        SingleHost.Opacity = 1;
        SingleHost.IsHitTestVisible = true;
        SingleSlide.X = 0;
        ParkedHost.Width = 0;
        ParkedHost.Opacity = 0;
        ParkedDivider.Width = 0;
        ParkedDivider.Opacity = 0;
        ParkedDivider.Margin = new Thickness(0);
        ShellWidthAnim = MediaViewModel.SoloWidgetWidth;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _layoutReady = true;
        _taskbarHost = new TaskbarHostService(this);
        _taskbarHost.LayoutChanged += _layoutHandler;
        _taskbarHost.Start();

        _appearance = new TaskbarAppearanceService(this);
        _appearance.Changed += _appearanceHandler;
        _appearance.Start();
        ApplyAppearance(_appearance.Current);

        await _viewModel.InitializeAsync();
        _taskbarHost.DesiredWidgetWidthDip = _viewModel.DesiredWidgetWidth;
        ApplyModeVisual(_viewModel.IsDualPlaying, animate: false);
        AnimateParkedHost(_viewModel.ShowParkedRail, animate: false);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _parkedRemeasureCts?.Cancel();
        _parkedRemeasureCts?.Dispose();
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.SideSessions.CollectionChanged -= OnSideSessionsChanged;
        _viewModel.ParkedLayoutChanged -= OnParkedLayoutChanged;

        if (_taskbarHost is not null)
        {
            _taskbarHost.LayoutChanged -= _layoutHandler;
            _taskbarHost.Dispose();
        }

        if (_appearance is not null)
        {
            _appearance.Changed -= _appearanceHandler;
            _appearance.Dispose();
        }

        _viewModel.Dispose();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MediaViewModel.IsVolumeEditorOpen))
        {
            AnimateVolumeEditor(_viewModel.IsVolumeEditorOpen);
        }
        else if (e.PropertyName == nameof(MediaViewModel.IsDualPlaying))
        {
            ApplyModeVisual(_viewModel.IsDualPlaying, animate: _layoutReady);
        }
        else if (e.PropertyName == nameof(MediaViewModel.ShowParkedRail))
        {
            AnimateParkedHost(_viewModel.ShowParkedRail, animate: _layoutReady && !_viewModel.IsDualPlaying);
        }
        else if (e.PropertyName == nameof(MediaViewModel.DesiredWidgetWidth))
        {
            if (_taskbarHost is not null)
            {
                _taskbarHost.DesiredWidgetWidthDip = _viewModel.DesiredWidgetWidth;
            }
        }
    }

    private void OnSideSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_viewModel.ShowParkedRail || !_layoutReady || _viewModel.IsDualPlaying)
        {
            return;
        }

        if (_parkedOpen && ParkedHost.ActualWidth > 1)
        {
            ExpandChipMetaInPlace();
        }
        else
        {
            AnimateParkedHost(open: true, animate: true);
        }
    }

    private void OnParkedLayoutChanged(object? sender, EventArgs e)
    {
        if (!_viewModel.ShowParkedRail || !_layoutReady || _viewModel.IsDualPlaying)
        {
            return;
        }

        // Chip already on screen: grow meta in place (only vol/prev slide).
        // Never restart ParkedHost from width 0 — that looks like dismiss + slide-in.
        if (_parkedOpen && ParkedHost.ActualWidth > 1)
        {
            ExpandChipMetaInPlace();
            return;
        }

        _parkedRemeasureCts?.Cancel();
        _parkedRemeasureCts = new CancellationTokenSource();
        var token = _parkedRemeasureCts.Token;
        _ = RemeasureParkedAfterSettleAsync(token);
    }

    private async Task RemeasureParkedAfterSettleAsync(CancellationToken token)
    {
        try
        {
            await Dispatcher.InvokeAsync(
                () =>
                {
                    SnapChipVolumeHosts();
                    SyncChipMetaWidths(animate: false);
                    AnimateParkedHost(open: true, animate: true);
                },
                DispatcherPriority.Loaded);

            await Task.Delay(280, token);
            if (token.IsCancellationRequested || !_viewModel.ShowParkedRail)
            {
                return;
            }

            await Dispatcher.InvokeAsync(
                () =>
                {
                    if (_parkedOpen && ParkedHost.ActualWidth > 1)
                    {
                        ExpandChipMetaInPlace();
                    }
                    else
                    {
                        SnapChipVolumeHosts();
                        SyncChipMetaWidths(animate: false);
                        AnimateParkedHost(open: true, animate: true);
                    }
                },
                DispatcherPriority.Loaded);
        }
        catch (TaskCanceledException)
        {
            // newer layout change superseded this remasure
        }
    }

    /// <summary>
    /// Animate meta rail and resize ParkedHost to the full measured chip width.
    /// Must remasure (not just +delta): right-aligned clip otherwise eats volume + VU.
    /// </summary>
    private void ExpandChipMetaInPlace()
    {
        var parkedFrom = ParkedHost.ActualWidth;
        if (parkedFrom < 1)
        {
            SyncChipMetaWidths(animate: false);
            AnimateParkedHost(open: true, animate: true);
            return;
        }

        // Remember per-chip meta from/to, then snap meta to target for an accurate measure.
        var metaAnims = new List<(FrameworkElement Host, double From, double To, SideSessionViewModel Side)>();
        for (var i = 0; i < ParkedList.Items.Count; i++)
        {
            if (ParkedList.Items[i] is not SideSessionViewModel side)
            {
                continue;
            }

            var target = side.TargetMetaRailWidth;
            var from = side.MetaRailWidth;
            if (ParkedList.ItemContainerGenerator.ContainerFromIndex(i) is DependencyObject container &&
                FindDescendantByName(container, "ChipMetaHost") is FrameworkElement metaHost)
            {
                metaAnims.Add((metaHost, from, target, side));
            }

            side.MetaRailWidth = target;
        }

        SyncChipMetaWidths(animate: false);

        // Unconstrain host so DesiredSize includes volume + VU + meta + art.
        ParkedHost.BeginAnimation(WidthProperty, null);
        ParkedHost.Width = double.NaN;
        ParkedHost.UpdateLayout();
        ParkedList.UpdateLayout();
        var parkedTo = Math.Ceiling(MeasureParkedListWidth());
        ParkedHost.Width = parkedFrom;
        if (parkedTo < 1)
        {
            parkedTo = parkedFrom;
        }

        // Animate meta  from→to (volume/VU stay visible; host grows to full measure).
        foreach (var (metaHost, from, to, side) in metaAnims)
        {
            if (Math.Abs(to - from) < 0.5)
            {
                continue;
            }

            metaHost.BeginAnimation(WidthProperty, null);
            metaHost.Width = from;
            var anim = new DoubleAnimation
            {
                From = from,
                To = to,
                Duration = TimeSpan.FromMilliseconds(260),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            var capturedHost = metaHost;
            var capturedTo = to;
            var capturedSide = side;
            anim.Completed += (_, _) =>
            {
                capturedSide.MetaRailWidth = capturedTo;
                capturedHost.BeginAnimation(WidthProperty, null);
                capturedHost.Width = capturedTo;
            };
            metaHost.BeginAnimation(WidthProperty, anim);
        }

        if (Math.Abs(parkedTo - parkedFrom) < 0.5)
        {
            return;
        }

        ParkedHost.BeginAnimation(WidthProperty, null);
        ParkedHost.Width = parkedFrom;
        ParkedHost.Opacity = 1;
        ParkedHost.BeginAnimation(
            WidthProperty,
            new DoubleAnimation
            {
                From = parkedFrom,
                To = parkedTo,
                Duration = TimeSpan.FromMilliseconds(260),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }

    private void SyncChipMetaWidths(bool animate)
    {
        for (var i = 0; i < ParkedList.Items.Count; i++)
        {
            if (ParkedList.Items[i] is not SideSessionViewModel side)
            {
                continue;
            }

            var target = side.TargetMetaRailWidth;
            if (!animate)
            {
                side.MetaRailWidth = target;
                if (ParkedList.ItemContainerGenerator.ContainerFromIndex(i) is DependencyObject container &&
                    FindDescendantByName(container, "ChipMetaHost") is FrameworkElement metaHost)
                {
                    metaHost.BeginAnimation(WidthProperty, null);
                    metaHost.Width = target;
                }
            }
        }
    }

    private void OnMarqueeZoneEnter(object sender, System.Windows.Input.MouseEventArgs e) =>
        TitleMarquee.SetHovering(true);

    private void OnMarqueeZoneLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // RenderTransform moves glyphs out from under the cursor and can false-fire Leave.
        // Re-check after layout/input so we only stop when the pointer really left the zone.
        Dispatcher.BeginInvoke(() =>
        {
            if (!SingleHost.IsMouseOver)
            {
                TitleMarquee.SetHovering(false);
            }
        }, DispatcherPriority.Input);
    }

    private void OnVolumeClick(object sender, RoutedEventArgs e)
    {
        _viewModel.RefreshVolume();
        if (!_viewModel.CanControlVolume)
        {
            return;
        }

        _viewModel.IsVolumeEditorOpen = !_viewModel.IsVolumeEditorOpen;
    }

    private void ApplyModeVisual(bool dual, bool animate)
    {
        if (_dualVisual == dual && animate)
        {
            return;
        }

        _dualVisual = dual;

        if (!animate)
        {
            DualHost.BeginAnimation(OpacityProperty, null);
            SingleHost.BeginAnimation(OpacityProperty, null);
            DualSlide.BeginAnimation(TranslateTransform.XProperty, null);
            SingleSlide.BeginAnimation(TranslateTransform.XProperty, null);
            VuHost.BeginAnimation(OpacityProperty, null);
            TransportHost.BeginAnimation(OpacityProperty, null);

            DualHost.Opacity = dual ? 1 : 0;
            DualHost.IsHitTestVisible = dual;
            DualSlide.X = dual ? 0 : 28;

            SingleHost.Opacity = dual ? 0 : 1;
            SingleHost.IsHitTestVisible = !dual;
            SingleSlide.X = dual ? -28 : 0;

            VuHost.Opacity = dual ? 0 : 1;
            VuHost.IsHitTestVisible = !dual;
            TransportHost.Opacity = dual ? 0 : 1;
            TransportHost.IsHitTestVisible = !dual;

            if (dual)
            {
                AnimateParkedHost(open: false, animate: false);
            }

            return;
        }

        var duration = TimeSpan.FromMilliseconds(260);
        var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };
        var easeIn = new CubicEase { EasingMode = EasingMode.EaseIn };

        if (dual)
        {
            // Single rolls left out, dual rolls in from the right.
            AnimateOpacity(SingleHost, 0, 160, easeIn);
            AnimateTranslate(SingleSlide, -28, duration, easeIn);
            SingleHost.IsHitTestVisible = false;

            DualHost.IsHitTestVisible = true;
            DualSlide.X = 28;
            AnimateOpacity(DualHost, 1, 220, easeOut, beginMs: 40);
            AnimateTranslate(DualSlide, 0, duration, easeOut, beginMs: 40);

            AnimateOpacity(VuHost, 0, 140, easeIn);
            AnimateOpacity(TransportHost, 0, 140, easeIn);
            VuHost.IsHitTestVisible = false;
            TransportHost.IsHitTestVisible = false;
            AnimateParkedHost(open: false, animate: true);
        }
        else
        {
            AnimateOpacity(DualHost, 0, 160, easeIn);
            AnimateTranslate(DualSlide, 28, duration, easeIn);
            DualHost.IsHitTestVisible = false;

            SingleHost.IsHitTestVisible = true;
            SingleSlide.X = -28;
            AnimateOpacity(SingleHost, 1, 220, easeOut, beginMs: 40);
            AnimateTranslate(SingleSlide, 0, duration, easeOut, beginMs: 40);

            VuHost.IsHitTestVisible = true;
            TransportHost.IsHitTestVisible = true;
            AnimateOpacity(VuHost, 1, 200, easeOut, beginMs: 40);
            AnimateOpacity(TransportHost, 1, 200, easeOut, beginMs: 40);
            AnimateParkedHost(_viewModel.SideSessions.Count > 0, animate: true);
        }
    }

    private void AnimateParkedHost(bool open, bool animate)
    {
        if (!open && !_parkedOpen)
        {
            _viewModel.CompleteParkedDismiss();
            _viewModel.ApplyParkedCompositeWidth(null);
            BeginAnimation(ShellWidthAnimProperty, null);
            ShellWidthAnim = MediaViewModel.SoloWidgetWidth;
            return;
        }

        ParkedHost.IsHitTestVisible = open;

        double targetWidth = 0;
        if (open)
        {
            var currentWidth = ParkedHost.ActualWidth;
            var alreadyVisible = _parkedOpen && currentWidth > 1;

            ParkedHost.BeginAnimation(WidthProperty, null);
            SnapChipVolumeHosts();
            SyncChipMetaWidths(animate: false);

            // Soft resize while visible: keep shell + chip in lockstep (no play jerk).
            if (alreadyVisible && animate)
            {
                ParkedList.UpdateLayout();
                targetWidth = Math.Ceiling(MeasureParkedListWidth());
                if (targetWidth < 1)
                {
                    targetWidth = currentWidth;
                }

                var resizeDuration = TimeSpan.FromMilliseconds(280);
                var resizeEase = new CubicEase { EasingMode = EasingMode.EaseOut };
                ParkedHost.Width = currentWidth;
                ParkedHost.Opacity = 1;
                ParkedHost.BeginAnimation(
                    WidthProperty,
                    new DoubleAnimation
                    {
                        From = currentWidth,
                        To = targetWidth,
                        Duration = resizeDuration,
                        EasingFunction = resizeEase
                    });
                AnimateShellWidth(
                    _viewModel.ComputeParkedCompositeWidth(targetWidth),
                    resizeDuration,
                    resizeEase,
                    commitParkedWidth: targetWidth);
                return;
            }

            ParkedHost.Width = double.NaN;
            ParkedHost.UpdateLayout();
            ParkedList.UpdateLayout();
            targetWidth = Math.Ceiling(MeasureParkedListWidth());
            if (targetWidth < 1)
            {
                targetWidth = 72;
            }

            if (animate && !_parkedOpen)
            {
                ParkedHost.Width = 0;
            }
            else if (!animate)
            {
                ParkedHost.Width = targetWidth;
            }
            else
            {
                ParkedHost.Width = currentWidth;
            }
        }

        var wasOpen = _parkedOpen;
        _parkedOpen = open;

        var dividerMargin = open ? new Thickness(8, 0, 6, 0) : new Thickness(0);

        if (!animate)
        {
            ParkedHost.BeginAnimation(WidthProperty, null);
            ParkedHost.BeginAnimation(OpacityProperty, null);
            ParkedDivider.BeginAnimation(WidthProperty, null);
            ParkedDivider.BeginAnimation(OpacityProperty, null);
            ParkedDivider.BeginAnimation(MarginProperty, null);
            BeginAnimation(ShellWidthAnimProperty, null);
            ParkedHost.Width = open ? targetWidth : 0;
            ParkedHost.Opacity = open ? 1 : 0;
            ParkedDivider.Width = open ? ParkedDividerWidth : 0;
            ParkedDivider.Opacity = open ? 1 : 0;
            ParkedDivider.Margin = dividerMargin;
            if (!open)
            {
                _viewModel.CompleteParkedDismiss();
                _viewModel.ApplyParkedCompositeWidth(null);
                ShellWidthAnim = MediaViewModel.SoloWidgetWidth;
            }
            else
            {
                _viewModel.ApplyParkedCompositeWidth(targetWidth);
                ShellWidthAnim = _viewModel.DesiredWidgetWidth;
            }

            return;
        }

        // Chip + divider + shell share one clock so play stays put (grows only by trim).
        var durationMs = open ? 340 : 300;
        var duration = TimeSpan.FromMilliseconds(durationMs);
        var easeAnim = new CubicEase { EasingMode = open ? EasingMode.EaseOut : EasingMode.EaseIn };
        var shellTo = open
            ? _viewModel.ComputeParkedCompositeWidth(targetWidth)
            : MediaViewModel.SoloWidgetWidth;

        if (wasOpen != open)
        {
            ParkedHost.BeginAnimation(OpacityProperty, null);
            ParkedDivider.BeginAnimation(OpacityProperty, null);
            ParkedDivider.BeginAnimation(MarginProperty, null);

            if (open)
            {
                ParkedHost.Opacity = 0;
                ParkedDivider.Opacity = 0;
                ParkedDivider.Margin = new Thickness(0);
            }

            // Fade in with the wipe from the divider; fade out near the end on dismiss.
            ParkedHost.BeginAnimation(
                OpacityProperty,
                new DoubleAnimation
                {
                    To = open ? 1 : 0,
                    Duration = TimeSpan.FromMilliseconds(open ? 160 : 140),
                    BeginTime = open ? TimeSpan.Zero : TimeSpan.FromMilliseconds(140),
                    EasingFunction = easeAnim
                });

            ParkedDivider.BeginAnimation(
                OpacityProperty,
                new DoubleAnimation
                {
                    To = open ? 1 : 0,
                    Duration = TimeSpan.FromMilliseconds(open ? 200 : 160),
                    BeginTime = open ? TimeSpan.Zero : TimeSpan.FromMilliseconds(100),
                    EasingFunction = easeAnim
                });

            ParkedDivider.BeginAnimation(
                MarginProperty,
                new ThicknessAnimation
                {
                    From = open ? new Thickness(0) : ParkedDivider.Margin,
                    To = dividerMargin,
                    Duration = duration,
                    EasingFunction = easeAnim
                });
        }

        var widthAnim = new DoubleAnimation
        {
            From = open ? 0 : ParkedHost.ActualWidth,
            To = open ? targetWidth : 0,
            Duration = duration,
            EasingFunction = easeAnim
        };
        if (!open)
        {
            widthAnim.Completed += (_, _) =>
            {
                _viewModel.CompleteParkedDismiss();
                _viewModel.ApplyParkedCompositeWidth(null);
                BeginAnimation(ShellWidthAnimProperty, null);
                ShellWidthAnim = MediaViewModel.SoloWidgetWidth;
            };
        }

        ParkedHost.BeginAnimation(WidthProperty, widthAnim);

        ParkedDivider.BeginAnimation(
            WidthProperty,
            new DoubleAnimation
            {
                From = open ? 0 : ParkedDivider.ActualWidth,
                To = open ? ParkedDividerWidth : 0,
                Duration = duration,
                EasingFunction = easeAnim
            });

        AnimateShellWidth(
            shellTo,
            duration,
            easeAnim,
            commitParkedWidth: open ? targetWidth : null);
    }

    /// <summary>Grow/shrink taskbar shell in sync with chip so the play module does not jump.</summary>
    private void AnimateShellWidth(
        double to,
        Duration duration,
        IEasingFunction ease,
        double? commitParkedWidth)
    {
        BeginAnimation(ShellWidthAnimProperty, null);
        var from = ShellWidthAnim;
        if (from < 1)
        {
            from = _viewModel.DesiredWidgetWidth;
            ShellWidthAnim = from;
        }

        var anim = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = duration,
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd
        };
        anim.Completed += (_, _) =>
        {
            BeginAnimation(ShellWidthAnimProperty, null);
            ShellWidthAnim = to;
            if (commitParkedWidth is not null)
            {
                _viewModel.ApplyParkedCompositeWidth(commitParkedWidth);
            }
            else if (to <= MediaViewModel.SoloWidgetWidth + 0.5)
            {
                _viewModel.ApplyParkedCompositeWidth(null);
            }
        };
        BeginAnimation(ShellWidthAnimProperty, anim);
    }

    private void SnapChipVolumeHosts()
    {
        for (var i = 0; i < ParkedList.Items.Count; i++)
        {
            if (ParkedList.ItemContainerGenerator.ContainerFromIndex(i) is not DependencyObject container)
            {
                continue;
            }

            if (ParkedList.Items[i] is not SideSessionViewModel side)
            {
                continue;
            }

            foreach (var hostName in new[] { "ChipVolumeHost", "ChipVolumeHostExpanded" })
            {
                if (FindDescendantByName(container, hostName) is not FrameworkElement volumeHost)
                {
                    continue;
                }

                volumeHost.BeginAnimation(WidthProperty, null);
                volumeHost.Width = side.IsVolumeEditorOpen
                    ? ChipVolumeOpenWidth
                    : ChipVolumeClosedWidth;
            }
        }
    }

    private double MeasureParkedListWidth()
    {
        ParkedList.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = ParkedList.DesiredSize.Width;
        if (width < 1)
        {
            width = ParkedList.ActualWidth;
        }

        return width;
    }

    private static DependencyObject? FindDescendantByName(DependencyObject root, string name)
    {
        if (root is FrameworkElement fe && fe.Name == name)
        {
            return root;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            var match = FindDescendantByName(child, name);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private static void AnimateOpacity(
        UIElement element,
        double to,
        int ms,
        IEasingFunction ease,
        int beginMs = 0)
    {
        element.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation
            {
                To = to,
                Duration = TimeSpan.FromMilliseconds(ms),
                BeginTime = TimeSpan.FromMilliseconds(beginMs),
                EasingFunction = ease
            });
    }

    private static void AnimateTranslate(
        TranslateTransform transform,
        double to,
        TimeSpan duration,
        IEasingFunction ease,
        int beginMs = 0)
    {
        transform.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation
            {
                To = to,
                Duration = duration,
                BeginTime = TimeSpan.FromMilliseconds(beginMs),
                EasingFunction = ease
            });
    }

    private void AnimateVolumeEditor(bool open)
    {
        // One shared clock so volume grow and source-name collapse stay in lockstep.
        var duration = TimeSpan.FromMilliseconds(260);
        var ease = new CubicEase { EasingMode = open ? EasingMode.EaseOut : EasingMode.EaseIn };

        VolumeHost.BeginAnimation(
            WidthProperty,
            new DoubleAnimation
            {
                To = open ? VolumeOpenWidth : VolumeClosedWidth,
                Duration = duration,
                EasingFunction = ease
            });

        VolumeSlider.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation
            {
                To = open ? 1 : 0,
                Duration = duration,
                EasingFunction = ease
            });

        VolumeButton.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation
            {
                To = open ? 0 : 1,
                Duration = duration,
                EasingFunction = ease
            });

        VolumeButton.BeginAnimation(
            WidthProperty,
            new DoubleAnimation
            {
                To = open ? 0 : VolumeButtonWidth,
                Duration = duration,
                EasingFunction = ease
            });

        MuteButton.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation
            {
                To = open ? 1 : 0,
                Duration = duration,
                EasingFunction = ease
            });

        VolumeSlider.IsHitTestVisible = open;
        MuteButton.IsHitTestVisible = open;
        VolumeButton.IsHitTestVisible = !open;

        AnimateSourceNameForVolume(open, duration, ease);
    }

    /// <summary>Solo: collapse app name into the mono icon in sync with the volume host width.</summary>
    private void AnimateSourceNameForVolume(bool volumeOpen, Duration duration, IEasingFunction ease)
    {
        if (_viewModel.ShowPairAppIcon || SourceText.Visibility != Visibility.Visible)
        {
            SourceNameHost.BeginAnimation(WidthProperty, null);
            SourceNameHost.Width = double.NaN;
            SourceText.Opacity = 1;
            return;
        }

        SourceText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var full = Math.Max(1, Math.Ceiling(SourceText.DesiredSize.Width));
        var from = SourceNameHost.ActualWidth > 0.5
            ? SourceNameHost.ActualWidth
            : (volumeOpen ? full : 0);
        var to = volumeOpen ? 0 : full;

        SourceNameHost.BeginAnimation(WidthProperty, null);
        SourceNameHost.Width = from;

        var widthAnim = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = duration,
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop
        };
        widthAnim.Completed += (_, _) =>
        {
            SourceNameHost.BeginAnimation(WidthProperty, null);
            SourceNameHost.Width = volumeOpen ? 0 : double.NaN;
        };

        SourceNameHost.BeginAnimation(WidthProperty, widthAnim);
    }

    private void ApplyLayout(TaskbarLayout layout)
    {
        WidgetRoot.Width = layout.WidgetWidth;
        WidgetRoot.Height = layout.WindowHeight;
        Canvas.SetLeft(WidgetRoot, layout.WidgetLeft);
        Canvas.SetTop(WidgetRoot, 0);
    }

    private void ApplyAppearance(TaskbarAppearance appearance)
    {
        _titleBrush.Color = appearance.Foreground;
        _mutedBrush.Color = appearance.MutedForeground;
        _progressTrackBrush.Color = appearance.ProgressTrack;
        _progressFillBrush.Color = appearance.ProgressFill;
        TitleMarquee.Foreground = _titleBrush;
        TitleMarquee.SetEdgeScrim(appearance.IsLight);
        ArtistText.Foreground = _mutedBrush;
        SourceText.Foreground = _mutedBrush;

        if (Resources["ProgressAccentBrush"] is SolidColorBrush accentBrush)
        {
            accentBrush.Color = appearance.ProgressFill;
        }

        if (Resources["ProgressTrackBrush"] is SolidColorBrush trackBrush)
        {
            trackBrush.Color = appearance.ProgressTrack;
        }

        ProgressBar.Background = _progressTrackBrush;
        ProgressBar.Foreground = _progressFillBrush;

        var iconBrush = new SolidColorBrush(appearance.Foreground);
        PreviousIcon.Foreground = iconBrush;
        NextIcon.Foreground = iconBrush;
    }

    private void OnExitClick(object sender, RoutedEventArgs e) =>
        Application.Current.Shutdown();
}
