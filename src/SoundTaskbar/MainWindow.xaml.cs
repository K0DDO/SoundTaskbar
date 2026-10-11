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

    /// <summary>0 = hidden, 1 = fully open. Drives chip + divider + shell together.</summary>
    private static readonly DependencyProperty ParkedRevealProperty =
        DependencyProperty.Register(
            nameof(ParkedReveal),
            typeof(double),
            typeof(MainWindow),
            new PropertyMetadata(0.0, OnParkedRevealChanged));

    private readonly MediaViewModel _viewModel = new();
    private readonly EventHandler<TaskbarAppearance> _appearanceHandler;
    private readonly EventHandler<TaskbarLayout> _layoutHandler;
    private readonly SolidColorBrush _titleBrush = new(Color.FromRgb(0xF5, 0xF5, 0xF5));
    private readonly SolidColorBrush _mutedBrush = new(Color.FromRgb(0xB8, 0xC0, 0xCC));
    private readonly SolidColorBrush _progressTrackBrush = new(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
    private readonly SolidColorBrush _progressFillBrush = new(Color.FromRgb(0x9E, 0xC5, 0xFF));
    private TaskbarHostService? _taskbarHost;
    private TaskbarAppearanceService? _appearance;
    private bool _parkedOpen;
    private bool _playPlayOpen;
    private bool _layoutReady;
    private double _parkedChipTarget;
    private CancellationTokenSource? _parkedRemeasureCts;
    private AnimationClockHolder? _playPlayClock;

    private const double ChipVolumeClosedWidth = 14;
    private const double ChipVolumeOpenWidth = 64;
    private const int PlayPlayMorphMs = 340;

    private double ParkedReveal
    {
        get => (double)GetValue(ParkedRevealProperty);
        set => SetValue(ParkedRevealProperty, value);
    }

    private static void OnParkedRevealChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((MainWindow)d).ApplyParkedReveal((double)e.NewValue);

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

        SingleHost.Opacity = 1;
        SingleHost.IsHitTestVisible = true;
        ParkedHost.Width = 0;
        ParkedHost.Opacity = 0;
        ParkedDivider.Width = 0;
        ParkedDivider.Opacity = 0;
        ParkedDivider.Margin = new Thickness(0);
        _parkedChipTarget = 0;
        ParkedReveal = 0;
        _playPlayOpen = false;
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
        AnimateParkedHost(_viewModel.ShowParkedRail, animate: false);
        MorphPlayPlay(_viewModel.IsPlayPlay, animate: false);
        AnimateSourceNameForPair(_viewModel.ShowPairAppIcon, animate: false);
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
        else if (e.PropertyName == nameof(MediaViewModel.ShowParkedRail))
        {
            AnimateParkedHost(_viewModel.ShowParkedRail, animate: _layoutReady);
        }
        else if (e.PropertyName == nameof(MediaViewModel.ShowPairAppIcon))
        {
            AnimateSourceNameForPair(_viewModel.ShowPairAppIcon, animate: _layoutReady);
        }
        else if (e.PropertyName == nameof(MediaViewModel.DesiredWidgetWidth))
        {
            if (_taskbarHost is not null && _playPlayClock is null)
            {
                _taskbarHost.DesiredWidgetWidthDip = _viewModel.DesiredWidgetWidth;
            }
        }
    }

    private void OnSideSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_viewModel.ShowParkedRail || !_layoutReady)
        {
            return;
        }

        if (_parkedOpen && ParkedHost.ActualWidth > 1)
        {
            MorphPlayPlay(_viewModel.IsPlayPlay, animate: true);
        }
        else
        {
            AnimateParkedHost(open: true, animate: true);
        }
    }

    private void OnParkedLayoutChanged(object? sender, EventArgs e)
    {
        if (!_viewModel.ShowParkedRail || !_layoutReady)
        {
            return;
        }

        // Chip on screen: morph pause↔play+play in place (never restart from width 0).
        if (_parkedOpen && ParkedHost.ActualWidth > 1)
        {
            MorphPlayPlay(_viewModel.IsPlayPlay, animate: true);
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
                        MorphPlayPlay(_viewModel.IsPlayPlay, animate: true);
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
    /// Morph pause chip ↔ play+play: ExpandProgress crossfade + meta + host + shell one clock.
    /// Does not touch frozen pause+play appear/dismiss (ParkedReveal).
    /// </summary>
    private void MorphPlayPlay(bool expand, bool animate)
    {
        if (!_viewModel.ShowParkedRail || ParkedList.Items.Count == 0)
        {
            _playPlayOpen = false;
            return;
        }

        var parkedFrom = ParkedHost.ActualWidth > 1 ? ParkedHost.ActualWidth : _parkedChipTarget;
        if (parkedFrom < 1)
        {
            SnapChipVolumeHosts();
            SyncChipMetaWidths(animate: false);
            AnimateParkedHost(open: true, animate: animate);
            return;
        }

        var progressFrom = 0.0;
        foreach (var item in ParkedList.Items)
        {
            if (item is SideSessionViewModel s)
            {
                progressFrom = s.ExpandProgress;
                break;
            }
        }

        var progressTo = expand ? 1.0 : 0.0;
        var shellFrom = _taskbarHost?.DesiredWidgetWidthDip
                        ?? _viewModel.ComputeParkedCompositeWidth(parkedFrom);

        // Measure compact width (progress 0, meta 0).
        foreach (var item in ParkedList.Items)
        {
            if (item is SideSessionViewModel side)
            {
                side.ExpandProgress = 0;
                side.MetaRailWidth = 0;
            }
        }

        PushChipMetaHostWidths();
        ParkedHost.BeginAnimation(WidthProperty, null);
        ParkedHost.Width = double.NaN;
        ParkedHost.UpdateLayout();
        ParkedList.UpdateLayout();
        var compactWidth = Math.Max(1, Math.Ceiling(MeasureParkedListWidth()));

        // Measure expanded width (progress 1, meta target). Use MeasuredMetaRailWidth so
        // collapse still knows the expanded size after ShowExpandedMeta flips false.
        foreach (var item in ParkedList.Items)
        {
            if (item is SideSessionViewModel side)
            {
                side.ExpandProgress = 1;
                side.MetaRailWidth = side.MeasuredMetaRailWidth;
            }
        }

        PushChipMetaHostWidths();
        ParkedHost.Width = double.NaN;
        ParkedHost.UpdateLayout();
        ParkedList.UpdateLayout();
        var expandedWidth = Math.Max(compactWidth, Math.Ceiling(MeasureParkedListWidth()));

        var chipTo = expand ? expandedWidth : compactWidth;
        var shellTo = expand
            ? _viewModel.ComputePlayPlayCompositeWidth(expandedWidth)
            : _viewModel.ComputeParkedCompositeWidth(compactWidth);

        // Restore start frame before tween.
        foreach (var item in ParkedList.Items)
        {
            if (item is SideSessionViewModel side)
            {
                side.ExpandProgress = progressFrom;
                side.MetaRailWidth = progressFrom * side.MeasuredMetaRailWidth;
            }
        }

        PushChipMetaHostWidths();
        ParkedHost.Width = parkedFrom;
        ParkedHost.Opacity = 1;
        _parkedChipTarget = chipTo;
        _playPlayOpen = expand;

        if (!animate || (Math.Abs(progressTo - progressFrom) < 0.01 && Math.Abs(chipTo - parkedFrom) < 0.5))
        {
            ApplyPlayPlayFrame(progressTo, chipTo, shellTo);
            if (expand)
            {
                _viewModel.ApplyPlayPlayCompositeWidth(expandedWidth);
            }
            else
            {
                _viewModel.ApplyParkedCompositeWidth(compactWidth);
            }

            _playPlayClock = null;
            return;
        }

        var ease = new CubicEase { EasingMode = expand ? EasingMode.EaseOut : EasingMode.EaseIn };
        var duration = TimeSpan.FromMilliseconds(PlayPlayMorphMs);
        var chipFrom = parkedFrom;
        var p0 = progressFrom;
        var p1 = progressTo;
        var metaTargets = new List<double>();
        foreach (var item in ParkedList.Items)
        {
            metaTargets.Add(item is SideSessionViewModel side ? side.MeasuredMetaRailWidth : 0);
        }

        _playPlayClock = new AnimationClockHolder();
        _playPlayClock.ProgressChanged += t =>
        {
            var p = p0 + (p1 - p0) * t;
            var chip = chipFrom + (chipTo - chipFrom) * t;
            var shell = shellFrom + (shellTo - shellFrom) * t;
            ApplyPlayPlayFrame(p, chip, shell, metaTargets);
        };
        _playPlayClock.Start(
            0,
            1,
            duration,
            ease,
            () =>
            {
                ApplyPlayPlayFrame(progressTo, chipTo, shellTo);
                if (expand)
                {
                    _viewModel.ApplyPlayPlayCompositeWidth(expandedWidth);
                }
                else
                {
                    _viewModel.ApplyParkedCompositeWidth(compactWidth);
                }

                _playPlayClock = null;
            });

        AnimateSourceNameForPair(expand, animate: true);
    }

    private void ApplyPlayPlayFrame(
        double progress,
        double chipWidth,
        double shellWidth,
        IReadOnlyList<double>? metaTargets = null)
    {
        for (var i = 0; i < ParkedList.Items.Count; i++)
        {
            if (ParkedList.Items[i] is not SideSessionViewModel side)
            {
                continue;
            }

            side.ExpandProgress = progress;
            var metaTo = metaTargets is not null && i < metaTargets.Count
                ? metaTargets[i]
                : side.MeasuredMetaRailWidth;
            side.MetaRailWidth = metaTo * progress;
            if (ParkedList.ItemContainerGenerator.ContainerFromIndex(i) is DependencyObject container &&
                FindDescendantByName(container, "ChipMetaHost") is FrameworkElement metaHost)
            {
                metaHost.BeginAnimation(WidthProperty, null);
                metaHost.Width = side.MetaRailWidth;
            }
        }

        ParkedHost.BeginAnimation(WidthProperty, null);
        ParkedHost.Width = chipWidth;
        ParkedHost.Opacity = 1;
        ParkedDivider.Width = ParkedDividerWidth;
        ParkedDivider.Margin = new Thickness(8, 0, 6, 0);
        ParkedDivider.Opacity = 1;

        if (_taskbarHost is not null)
        {
            _taskbarHost.DesiredWidgetWidthDip = shellWidth;
        }
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

    /// <summary>Push current MetaRailWidth to ChipMetaHost without reading TargetMetaRailWidth.</summary>
    private void PushChipMetaHostWidths()
    {
        for (var i = 0; i < ParkedList.Items.Count; i++)
        {
            if (ParkedList.Items[i] is not SideSessionViewModel side)
            {
                continue;
            }

            if (ParkedList.ItemContainerGenerator.ContainerFromIndex(i) is DependencyObject container &&
                FindDescendantByName(container, "ChipMetaHost") is FrameworkElement metaHost)
            {
                metaHost.BeginAnimation(WidthProperty, null);
                metaHost.Width = side.MetaRailWidth;
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

    private void AnimateParkedHost(bool open, bool animate)
    {
        if (!open && !_parkedOpen)
        {
            _viewModel.CompleteParkedDismiss();
            _viewModel.ApplyParkedCompositeWidth(null);
            SealParkedReveal(0);
            return;
        }

        ParkedHost.IsHitTestVisible = open;

        if (open)
        {
            var currentWidth = ParkedHost.ActualWidth;
            var alreadyVisible = _parkedOpen && currentWidth > 1;

            SnapChipVolumeHosts();
            SyncChipMetaWidths(animate: false);

            if (alreadyVisible && animate)
            {
                ParkedList.UpdateLayout();
                var targetWidth = Math.Ceiling(MeasureParkedListWidth());
                if (targetWidth < 1)
                {
                    targetWidth = currentWidth;
                }

                // Soft resize at full reveal: lerp chip+shell without disturbing reveal clock.
                BeginAnimation(ParkedRevealProperty, null);
                ParkedReveal = 1;
                var chipFrom = currentWidth;
                var chipTo = targetWidth;
                var holder = new AnimationClockHolder();
                holder.ProgressChanged += t =>
                {
                    var chip = chipFrom + (chipTo - chipFrom) * t;
                    ParkedHost.Width = chip;
                    ParkedDivider.Width = ParkedDividerWidth;
                    ParkedDivider.Margin = new Thickness(8, 0, 6, 0);
                    ParkedHost.Opacity = 1;
                    ParkedDivider.Opacity = 1;
                    if (_taskbarHost is not null)
                    {
                        _taskbarHost.DesiredWidgetWidthDip = _playPlayOpen
                            ? _viewModel.ComputePlayPlayCompositeWidth(chip)
                            : _viewModel.ComputeParkedCompositeWidth(chip);
                    }
                };
                holder.Start(
                    0,
                    1,
                    TimeSpan.FromMilliseconds(280),
                    new CubicEase { EasingMode = EasingMode.EaseOut },
                    () =>
                    {
                        _parkedChipTarget = chipTo;
                        ApplyParkedReveal(1);
                        if (_playPlayOpen)
                        {
                            _viewModel.ApplyPlayPlayCompositeWidth(chipTo);
                        }
                        else
                        {
                            _viewModel.ApplyParkedCompositeWidth(chipTo);
                        }
                    });
                return;
            }

            ParkedHost.Width = double.NaN;
            ParkedHost.UpdateLayout();
            ParkedList.UpdateLayout();
            _parkedChipTarget = Math.Ceiling(MeasureParkedListWidth());
            if (_parkedChipTarget < 1)
            {
                _parkedChipTarget = 72;
            }
        }
        else if (_parkedChipTarget < 1)
        {
            _parkedChipTarget = Math.Max(ParkedHost.ActualWidth, 72);
        }

        var wasOpen = _parkedOpen;
        _parkedOpen = open;

        if (!animate)
        {
            SealParkedReveal(open ? 1 : 0);
            if (!open)
            {
                _viewModel.CompleteParkedDismiss();
                _viewModel.ApplyParkedCompositeWidth(null);
            }
            else
            {
                _viewModel.ApplyParkedCompositeWidth(_parkedChipTarget);
            }

            return;
        }

        if (wasOpen == open)
        {
            return;
        }

        // Appear 0→1 EaseOut; dismiss = exact reverse (1→0 EaseIn), same duration / curve family.
        var from = open ? 0 : 1;
        var to = open ? 1 : 0;
        SealParkedReveal(from);

        var duration = TimeSpan.FromMilliseconds(340);
        var ease = new CubicEase { EasingMode = open ? EasingMode.EaseOut : EasingMode.EaseIn };
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
            SealParkedReveal(to);
            if (open)
            {
                _viewModel.ApplyParkedCompositeWidth(_parkedChipTarget);
            }
            else
            {
                _viewModel.CompleteParkedDismiss();
                _viewModel.ApplyParkedCompositeWidth(null);
            }
        };
        BeginAnimation(ParkedRevealProperty, anim);
    }

    /// <summary>
    /// Write the visual value into the local DP before clearing clocks — otherwise
    /// BeginAnimation(null) snaps to the old base (often 0) and the chip blinks.
    /// </summary>
    private void SealParkedReveal(double value)
    {
        value = Math.Clamp(value, 0, 1);
        SetValue(ParkedRevealProperty, value);
        BeginAnimation(ParkedRevealProperty, null);
        SetValue(ParkedRevealProperty, value);
        ApplyParkedReveal(value);
    }

    /// <summary>Apply chip / divider / shell from a single reveal factor (0..1).</summary>
    private void ApplyParkedReveal(double p)
    {
        p = Math.Clamp(p, 0, 1);
        var chip = _parkedChipTarget * p;
        ParkedHost.Width = chip;
        ParkedDivider.Width = ParkedDividerWidth * p;
        ParkedDivider.Margin = new Thickness(8 * p, 0, 6 * p, 0);
        // Same opacity mapping both ways so dismiss is a true reverse of appear.
        var fade = Math.Clamp(p * 2.0, 0, 1);
        ParkedHost.Opacity = fade;
        ParkedDivider.Opacity = fade;

        var shell = MediaViewModel.SoloWidgetWidth +
                    p * (_parkedChipTarget + MediaViewModel.PausePlayDividerChrome - MediaViewModel.PausePlayTrim);
        if (_taskbarHost is not null)
        {
            _taskbarHost.DesiredWidgetWidthDip = shell;
        }
    }

    /// <summary>Tiny helper to tween a double without fighting ParkedReveal.</summary>
    private sealed class AnimationClockHolder : Animatable
    {
        public static readonly DependencyProperty ValueProperty =
            DependencyProperty.Register(
                nameof(Value),
                typeof(double),
                typeof(AnimationClockHolder),
                new PropertyMetadata(0.0, static (d, e) =>
                    ((AnimationClockHolder)d).ProgressChanged?.Invoke((double)e.NewValue)));

        public double Value
        {
            get => (double)GetValue(ValueProperty);
            set => SetValue(ValueProperty, value);
        }

        public Action<double>? ProgressChanged;

        protected override Freezable CreateInstanceCore() => new AnimationClockHolder();

        public void Start(double from, double to, Duration duration, IEasingFunction ease, Action? onDone)
        {
            BeginAnimation(ValueProperty, null);
            Value = from;
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
                BeginAnimation(ValueProperty, null);
                Value = to;
                onDone?.Invoke();
            };
            BeginAnimation(ValueProperty, anim);
        }
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
        // play+play already keeps the name collapsed into the icon.
        if (_viewModel.ShowPairAppIcon)
        {
            return;
        }

        AnimateSourceNameWidth(collapse: volumeOpen, animate: true, duration, ease);
    }

    /// <summary>play+play: collapse app name into icon; reverse when leaving pair.</summary>
    private void AnimateSourceNameForPair(bool pair, bool animate)
    {
        if (_viewModel.IsVolumeEditorOpen && !pair)
        {
            // Volume editor owns the collapsed name while open.
            return;
        }

        var duration = TimeSpan.FromMilliseconds(PlayPlayMorphMs);
        var ease = new CubicEase { EasingMode = pair ? EasingMode.EaseOut : EasingMode.EaseIn };
        AnimateSourceNameWidth(collapse: pair, animate, duration, ease);
    }

    private void AnimateSourceNameWidth(
        bool collapse,
        bool animate,
        Duration duration,
        IEasingFunction ease)
    {
        SourceText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var full = Math.Max(1, Math.Ceiling(SourceText.DesiredSize.Width + SourceText.Margin.Left));
        var from = SourceNameHost.ActualWidth > 0.5
            ? SourceNameHost.ActualWidth
            : (collapse ? full : 0);
        var to = collapse ? 0 : full;

        if (!animate || Math.Abs(to - from) < 0.5)
        {
            SourceNameHost.BeginAnimation(WidthProperty, null);
            SourceNameHost.Width = collapse ? 0 : double.NaN;
            SourceText.Opacity = 1;
            return;
        }

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
            SourceNameHost.Width = collapse ? 0 : double.NaN;
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
