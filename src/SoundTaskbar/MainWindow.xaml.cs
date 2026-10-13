using System.Collections.Specialized;
using System.ComponentModel;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SoundTaskbar.Controls;
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
    private bool _pairNameCollapsed;
    private bool _layoutReady;
    private double _parkedChipTarget;
    private CancellationTokenSource? _parkedRemeasureCts;
    private AnimationClockHolder? _playPlayClock;
    private AnimationClockHolder? _visibilityClock;
    private AnimationClockHolder? _swapClock;
    private FrameworkElement? _swapChip;
    private bool _widgetShown;
    private bool _revealAnimating;
    private bool _morphAfterReveal;
    private bool _morphScheduled;
    private bool _appearanceIsLight;
    private Color? _traySample;
    private readonly TranslateTransform _widgetShift = new();
    private readonly TranslateTransform _primaryArtShift = new();
    private readonly TranslateTransform _primaryMetaShift = new();

    private const double ChipVolumeClosedWidth = 14;
    private const double ChipVolumeOpenWidth = 64;
    private const int PlayPlayMorphMs = 340;
    private const int WidgetRevealMs = 480;
    /// <summary>Hide/show slides the content toward the tray by this much.</summary>
    private const double WidgetRevealShift = 10;
    private const int PrimarySwapMs = 420;

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
        _viewModel.PrimarySwapping += OnPrimarySwapping;
        foreach (var side in _viewModel.SideSessions)
        {
            side.PropertyChanged += OnSideSessionPropertyChanged;
        }

        WidgetRoot.RenderTransform = _widgetShift;
        WidgetRoot.Opacity = 0;
        PrimaryArtHost.RenderTransform = _primaryArtShift;
        PrimaryMetaHost.RenderTransform = _primaryMetaShift;

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
        // Hidden until a source shows up.
        _taskbarHost.RevealFraction = 0;
        _taskbarHost.Start();

        _appearance = new TaskbarAppearanceService(this);
        _appearance.Changed += _appearanceHandler;
        _appearance.Start();
        ApplyAppearance(_appearance.Current);

        await _viewModel.InitializeAsync();
        _taskbarHost.DesiredWidgetWidthDip = _viewModel.DesiredWidgetWidth;
        AnimateParkedHost(_viewModel.ShowParkedRail, animate: false);
        MorphSideChips(animate: false);
        AnimateSourceNameForPair(_viewModel.ShowPairAppIcon, animate: false);
        AnimateWidgetVisibility(_viewModel.HasSession);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _parkedRemeasureCts?.Cancel();
        _parkedRemeasureCts?.Dispose();
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.SideSessions.CollectionChanged -= OnSideSessionsChanged;
        _viewModel.ParkedLayoutChanged -= OnParkedLayoutChanged;
        _viewModel.PrimarySwapping -= OnPrimarySwapping;
        foreach (var side in _viewModel.SideSessions)
        {
            side.PropertyChanged -= OnSideSessionPropertyChanged;
        }

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
            if (_viewModel.ShowPairAppIcon != _pairNameCollapsed)
            {
                AnimateSourceNameForPair(_viewModel.ShowPairAppIcon, animate: _layoutReady);
            }
        }
        else if (e.PropertyName == nameof(MediaViewModel.HasSession))
        {
            if (_layoutReady && _taskbarHost is not null)
            {
                AnimateWidgetVisibility(_viewModel.HasSession);
            }
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
        if (e.OldItems is not null)
        {
            foreach (SideSessionViewModel side in e.OldItems)
            {
                side.PropertyChanged -= OnSideSessionPropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (SideSessionViewModel side in e.NewItems)
            {
                side.PropertyChanged += OnSideSessionPropertyChanged;
            }
        }

        ApplyChipMarqueeScrims();

        if (!_viewModel.ShowParkedRail || !_layoutReady)
        {
            return;
        }

        if (_parkedOpen && ParkedHost.ActualWidth > 1)
        {
            ScheduleSideMorph();
        }
        else
        {
            AnimateParkedHost(open: true, animate: true);
        }
    }

    private void OnSideSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not SideSessionViewModel side)
        {
            return;
        }

        if (e.PropertyName == nameof(SideSessionViewModel.IsVolumeEditorOpen) &&
            side.ShowExpandedMeta &&
            side.ExpandProgress > 0.5)
        {
            AnimatePlayPlaySideVolume(side);
        }
    }

    private void OnParkedLayoutChanged(object? sender, EventArgs e)
    {
        if (!_viewModel.ShowParkedRail || !_layoutReady)
        {
            return;
        }

        // Chip on screen: morph pause↔play+play; otherwise soft-resize (volume on pause chip).
        if (_parkedOpen && ParkedHost.ActualWidth > 1)
        {
            if (_playPlayOpen || _viewModel.IsPlayPlay)
            {
                ScheduleSideMorph();
            }
            else
            {
                SoftResizeParkedChip();
            }

            return;
        }

        _parkedRemeasureCts?.Cancel();
        _parkedRemeasureCts = new CancellationTokenSource();
        var token = _parkedRemeasureCts.Token;
        _ = RemeasureParkedAfterSettleAsync(token);
    }

    /// <summary>Coalesce side-rail changes (SyncCollection fires many) into one morph after layout.</summary>
    private void ScheduleSideMorph()
    {
        if (_revealAnimating)
        {
            _morphAfterReveal = true;
            return;
        }

        if (_morphScheduled)
        {
            return;
        }

        _morphScheduled = true;
        Dispatcher.BeginInvoke(
            () =>
            {
                _morphScheduled = false;
                if (_viewModel.ShowParkedRail && _parkedOpen && ParkedHost.ActualWidth > 1)
                {
                    MorphSideChips(animate: true);
                }
            },
            DispatcherPriority.Loaded);
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
                        ScheduleSideMorph();
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
    /// Morph every side chip to its own target: the play+play chip expands (mirrored chrome + meta),
    /// extra sources stay small paused chips. Chip host + shell ride one clock.
    /// Does not touch frozen pause+play appear/dismiss (ParkedReveal).
    /// </summary>
    private void MorphSideChips(bool animate)
    {
        if (!_viewModel.ShowParkedRail || ParkedList.Items.Count == 0)
        {
            return;
        }

        var parkedFrom = !double.IsNaN(ParkedHost.Width) && ParkedHost.Width > 1
            ? ParkedHost.Width
            : ParkedHost.ActualWidth > 1 ? ParkedHost.ActualWidth : _parkedChipTarget;
        if (parkedFrom < 1)
        {
            SnapChipVolumeHosts();
            SyncChipMetaWidths(animate: false);
            AnimateParkedHost(open: true, animate: animate);
            return;
        }

        _playPlayClock?.Cancel();
        _playPlayClock = null;

        var sides = ParkedList.Items.OfType<SideSessionViewModel>().ToList();
        var progressFrom = sides.Select(s => s.ExpandProgress).ToArray();
        var progressTo = sides.Select(s => s.ShowExpandedMeta ? 1.0 : 0.0).ToArray();
        // MeasuredMetaRailWidth so collapse still knows the expanded size after ShowExpandedMeta flips.
        var metaTargets = sides.Select(s => s.MeasuredMetaRailWidth).ToArray();
        var expand = progressTo.Any(p => p > 0.5);
        var shellFrom = _taskbarHost?.DesiredWidgetWidthDip
                        ?? _viewModel.ComputeParkedCompositeWidth(parkedFrom);

        // Containers for freshly added chips must exist before ChipMetaHost widths are pushed.
        ParkedList.UpdateLayout();
        SnapChipVolumeHosts();
        SetChipProgress(sides, progressFrom, progressTo, metaTargets, 1);
        ParkedHost.BeginAnimation(WidthProperty, null);
        ParkedHost.Width = double.NaN;
        ParkedHost.UpdateLayout();
        ParkedList.UpdateLayout();
        var chipTo = Math.Max(1, Math.Ceiling(MeasureParkedListWidth()));
        var shellTo = expand
            ? _viewModel.ComputePlayPlayCompositeWidth(chipTo)
            : _viewModel.ComputeParkedCompositeWidth(chipTo);

        SetChipProgress(sides, progressFrom, progressTo, metaTargets, 0);
        ParkedHost.Width = parkedFrom;
        ParkedHost.Opacity = 1;
        _parkedChipTarget = chipTo;
        var wasExpanded = _playPlayOpen;
        _playPlayOpen = expand;

        void Finish()
        {
            ApplySideFrame(sides, progressFrom, progressTo, metaTargets, 1, chipTo, shellTo);
            if (expand)
            {
                _viewModel.ApplyPlayPlayCompositeWidth(chipTo);
            }
            else
            {
                _viewModel.ApplyParkedCompositeWidth(chipTo);
            }

            _taskbarHost?.FlushRegion();
            ApplyChipMarqueeScrims();
            _playPlayClock = null;
        }

        var progressSettled = progressFrom.Zip(progressTo).All(p => Math.Abs(p.First - p.Second) < 0.01);
        if (!animate ||
            (progressSettled && Math.Abs(chipTo - parkedFrom) < 0.5 && Math.Abs(shellTo - shellFrom) < 0.5))
        {
            Finish();
            if (expand != wasExpanded)
            {
                AnimateSourceNameForPair(expand, animate: false);
            }

            return;
        }

        var clock = new AnimationClockHolder();
        clock.ProgressChanged += t =>
        {
            var chip = parkedFrom + (chipTo - parkedFrom) * t;
            var shell = shellFrom + (shellTo - shellFrom) * t;
            ApplySideFrame(sides, progressFrom, progressTo, metaTargets, t, chip, shell);
        };
        _playPlayClock = clock;
        clock.Start(
            0,
            1,
            TimeSpan.FromMilliseconds(PlayPlayMorphMs),
            new CubicEase { EasingMode = EasingMode.EaseInOut },
            Finish);

        if (expand != wasExpanded)
        {
            AnimateSourceNameForPair(expand, animate: true);
        }
    }

    /// <summary>Per-chip ExpandProgress / meta lerp at t (0 = from, 1 = to) without touching hosts.</summary>
    private void SetChipProgress(
        IReadOnlyList<SideSessionViewModel> sides,
        double[] from,
        double[] to,
        double[] metaTargets,
        double t)
    {
        for (var i = 0; i < sides.Count; i++)
        {
            var side = sides[i];
            var p = from[i] + (to[i] - from[i]) * t;
            side.ExpandProgress = p;
            side.MetaRailWidth = metaTargets[i] * p;
            if (ParkedList.ItemContainerGenerator.ContainerFromItem(side) is DependencyObject container &&
                FindDescendantByName(container, "ChipMetaHost") is FrameworkElement metaHost)
            {
                metaHost.BeginAnimation(WidthProperty, null);
                metaHost.Width = side.EffectiveMetaRailWidth;
            }
        }
    }

    private void ApplySideFrame(
        IReadOnlyList<SideSessionViewModel> sides,
        double[] from,
        double[] to,
        double[] metaTargets,
        double t,
        double chipWidth,
        double shellWidth)
    {
        SetChipProgress(sides, from, to, metaTargets, t);

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

    /// <summary>Push current EffectiveMetaRailWidth to ChipMetaHost without reading TargetMetaRailWidth.</summary>
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
                metaHost.Width = side.EffectiveMetaRailWidth;
            }
        }
    }

    /// <summary>pause+play: grow/shrink ParkedHost to measured chip (e.g. volume open) with animation.</summary>
    private void SoftResizeParkedChip()
    {
        if (_playPlayOpen || !_parkedOpen)
        {
            return;
        }

        var from = ParkedHost.ActualWidth > 1 ? ParkedHost.ActualWidth : _parkedChipTarget;
        SnapChipVolumeHosts();
        ParkedHost.BeginAnimation(WidthProperty, null);
        ParkedHost.Width = double.NaN;
        ParkedHost.UpdateLayout();
        ParkedList.UpdateLayout();
        var to = Math.Max(1, Math.Ceiling(MeasureParkedListWidth()));
        ParkedHost.Width = from;
        if (Math.Abs(to - from) < 0.5)
        {
            _viewModel.ApplyParkedCompositeWidth(to);
            return;
        }

        _parkedChipTarget = to;
        var shellFrom = _taskbarHost?.DesiredWidgetWidthDip ?? _viewModel.ComputeParkedCompositeWidth(from);
        var shellTo = _viewModel.ComputeParkedCompositeWidth(to);
        var ease = new CubicEase { EasingMode = to > from ? EasingMode.EaseOut : EasingMode.EaseIn };
        var holder = new AnimationClockHolder();
        holder.ProgressChanged += t =>
        {
            var chip = from + (to - from) * t;
            ParkedHost.Width = chip;
            if (_taskbarHost is not null)
            {
                _taskbarHost.DesiredWidgetWidthDip = shellFrom + (shellTo - shellFrom) * t;
            }
        };
        holder.Start(
            0,
            1,
            TimeSpan.FromMilliseconds(260),
            ease,
            () =>
            {
                ParkedHost.Width = to;
                _viewModel.ApplyParkedCompositeWidth(to);
                _taskbarHost?.FlushRegion();
            });
    }

    /// <summary>play+play: volume grows by stealing meta width — art shifts, chip/shell stay fixed.</summary>
    private void AnimatePlayPlaySideVolume(SideSessionViewModel side)
    {
        for (var i = 0; i < ParkedList.Items.Count; i++)
        {
            if (!ReferenceEquals(ParkedList.Items[i], side))
            {
                continue;
            }

            if (ParkedList.ItemContainerGenerator.ContainerFromIndex(i) is not DependencyObject container ||
                FindDescendantByName(container, "ChipMetaHost") is not FrameworkElement metaHost)
            {
                return;
            }

            var to = side.EffectiveMetaRailWidth;
            var from = metaHost.ActualWidth > 0.5 ? metaHost.ActualWidth : metaHost.Width;
            if (double.IsNaN(from) || from < 0)
            {
                from = to;
            }

            metaHost.BeginAnimation(WidthProperty, null);
            metaHost.Width = from;
            var ease = new CubicEase
            {
                EasingMode = side.IsVolumeEditorOpen ? EasingMode.EaseOut : EasingMode.EaseIn
            };
            metaHost.BeginAnimation(
                WidthProperty,
                new DoubleAnimation
                {
                    From = from,
                    To = to,
                    Duration = TimeSpan.FromMilliseconds(260),
                    EasingFunction = ease,
                    FillBehavior = FillBehavior.HoldEnd
                });
            return;
        }
    }

    private void ApplyChipMarqueeScrims()
    {
        for (var i = 0; i < ParkedList.Items.Count; i++)
        {
            if (ParkedList.ItemContainerGenerator.ContainerFromIndex(i) is not DependencyObject container)
            {
                continue;
            }

            ApplyMarqueeScrims(container);
        }
    }

    private void ApplyMarqueeScrims(DependencyObject root)
    {
        if (root is MarqueeText marquee)
        {
            marquee.SetEdgeScrim(_appearanceIsLight, _traySample);
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            ApplyMarqueeScrims(VisualTreeHelper.GetChild(root, i));
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
            _playPlayOpen = false;
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
                _playPlayOpen = false;
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
            _revealAnimating = false;
            SealParkedReveal(to);
            if (open)
            {
                _viewModel.ApplyParkedCompositeWidth(_parkedChipTarget);
            }
            else
            {
                _viewModel.CompleteParkedDismiss();
                _playPlayOpen = false;
                _viewModel.ApplyParkedCompositeWidth(null);
            }

            _taskbarHost?.FlushRegion();
            if (_morphAfterReveal)
            {
                _morphAfterReveal = false;
                if (open)
                {
                    ScheduleSideMorph();
                }
            }
        };
        _playPlayClock?.Cancel();
        _playPlayClock = null;
        _revealAnimating = true;
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

        // play+play dismiss starts from the tighter play+play shell (no 32px jump); pause+play unchanged.
        var trim = MediaViewModel.PausePlayTrim + (_playPlayOpen ? MediaViewModel.PlayPlayExtraTrim : 0);
        var shell = MediaViewModel.SoloWidgetWidth +
                    p * (_parkedChipTarget + MediaViewModel.PausePlayDividerChrome - trim);
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
        private bool _cancelled;

        protected override Freezable CreateInstanceCore() => new AnimationClockHolder();

        /// <summary>Stop ticking and drop the completion callback (a newer tween took over).</summary>
        public void Cancel()
        {
            _cancelled = true;
            ProgressChanged = null;
            BeginAnimation(ValueProperty, null);
        }

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
                if (_cancelled)
                {
                    return;
                }

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
        AnimateTimeTextForVolume(open, duration, ease);
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

    /// <summary>
    /// Solo: dissolve elapsed/duration into the progress bar when volume opens —
    /// width collapses left into the timeline while opacity fades.
    /// </summary>
    private void AnimateTimeTextForVolume(bool volumeOpen, Duration duration, IEasingFunction ease)
    {
        // play+play: keep time visible (volume already steals left meta, not primary time).
        if (_viewModel.ShowPairAppIcon)
        {
            TimeTextHost.BeginAnimation(WidthProperty, null);
            TimeTextHost.Width = double.NaN;
            TimeText.BeginAnimation(OpacityProperty, null);
            TimeText.Opacity = 1;
            return;
        }

        TimeText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var full = Math.Max(
            1,
            Math.Ceiling(TimeText.DesiredSize.Width + TimeText.Margin.Left + TimeText.Margin.Right));

        var current = TimeTextHost.Width;
        if (volumeOpen && !double.IsNaN(current) && current < 0.5 && TimeTextHost.ActualWidth < 0.5)
        {
            TimeTextHost.BeginAnimation(WidthProperty, null);
            TimeTextHost.Width = 0;
            TimeText.BeginAnimation(OpacityProperty, null);
            TimeText.Opacity = 0;
            return;
        }

        if (!volumeOpen && double.IsNaN(current) && TimeTextHost.ActualWidth > 0.5 && TimeText.Opacity > 0.95)
        {
            return;
        }

        var from = TimeTextHost.ActualWidth > 0.5
            ? TimeTextHost.ActualWidth
            : (!double.IsNaN(current) && current >= 0 ? current : (volumeOpen ? full : 0));
        var to = volumeOpen ? 0 : full;
        var opacityFrom = TimeText.Opacity;
        if (double.IsNaN(opacityFrom))
        {
            opacityFrom = volumeOpen ? 1 : 0;
        }

        var opacityTo = volumeOpen ? 0 : 1;

        if (Math.Abs(to - from) < 0.5 && Math.Abs(opacityTo - opacityFrom) < 0.02)
        {
            TimeTextHost.BeginAnimation(WidthProperty, null);
            TimeTextHost.Width = volumeOpen ? 0 : double.NaN;
            TimeText.BeginAnimation(OpacityProperty, null);
            TimeText.Opacity = opacityTo;
            return;
        }

        TimeTextHost.BeginAnimation(WidthProperty, null);
        TimeTextHost.Width = from;
        TimeText.BeginAnimation(OpacityProperty, null);
        TimeText.Opacity = opacityFrom;

        var widthAnim = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = duration,
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd
        };
        widthAnim.Completed += (_, _) =>
        {
            TimeTextHost.BeginAnimation(WidthProperty, null);
            TimeTextHost.Width = volumeOpen ? 0 : double.NaN;
            TimeText.BeginAnimation(OpacityProperty, null);
            TimeText.Opacity = opacityTo;
        };

        TimeTextHost.BeginAnimation(WidthProperty, widthAnim);
        TimeText.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation
            {
                From = opacityFrom,
                To = opacityTo,
                Duration = duration,
                EasingFunction = ease,
                FillBehavior = FillBehavior.HoldEnd
            });
    }

    /// <summary>play+play: collapse app name into icon; reverse when leaving pair.</summary>
    private void AnimateSourceNameForPair(bool pair, bool animate)
    {
        if (_viewModel.IsVolumeEditorOpen && !pair)
        {
            // Volume editor owns the collapsed name while open.
            return;
        }

        if (pair == _pairNameCollapsed && animate)
        {
            return;
        }

        var duration = TimeSpan.FromMilliseconds(PlayPlayMorphMs);
        var ease = new CubicEase { EasingMode = pair ? EasingMode.EaseOut : EasingMode.EaseIn };
        AnimateSourceNameWidth(collapse: pair, animate, duration, ease);
        _pairNameCollapsed = pair;
    }

    private void AnimateSourceNameWidth(
        bool collapse,
        bool animate,
        Duration duration,
        IEasingFunction ease)
    {
        SourceText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var full = Math.Max(1, Math.Ceiling(SourceText.DesiredSize.Width + SourceText.Margin.Left));

        // Already collapsed (Width latched at 0): do not restart from=full — that is the jitter.
        var current = SourceNameHost.Width;
        if (collapse && !double.IsNaN(current) && current < 0.5 && SourceNameHost.ActualWidth < 0.5)
        {
            SourceNameHost.BeginAnimation(WidthProperty, null);
            SourceNameHost.Width = 0;
            return;
        }

        if (!collapse && double.IsNaN(current) && SourceNameHost.ActualWidth > 0.5)
        {
            return;
        }

        var from = SourceNameHost.ActualWidth > 0.5
            ? SourceNameHost.ActualWidth
            : (!double.IsNaN(current) && current >= 0 ? current : (collapse ? full : 0));
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
            FillBehavior = FillBehavior.HoldEnd
        };
        widthAnim.Completed += (_, _) =>
        {
            SourceNameHost.BeginAnimation(WidthProperty, null);
            SourceNameHost.Width = collapse ? 0 : double.NaN;
        };

        SourceNameHost.BeginAnimation(WidthProperty, widthAnim);
    }

    /// <summary>
    /// No source: morph into a tray-side pill icon, wipe right into it, then vanish.
    /// Source back: icon blooms and content expands left from the tray.
    /// </summary>
    private void AnimateWidgetVisibility(bool show)
    {
        if (_taskbarHost is null || show == _widgetShown)
        {
            return;
        }

        _widgetShown = show;
        _visibilityClock?.Cancel();
        PrepareDismissIcon(show);

        var from = _taskbarHost.RevealFraction;
        var to = show ? 1.0 : 0.0;
        var clock = new AnimationClockHolder();
        clock.ProgressChanged += t => ApplyWidgetReveal(from + (to - from) * t, showing: show);
        _visibilityClock = clock;
        clock.Start(
            0,
            1,
            TimeSpan.FromMilliseconds(WidgetRevealMs * Math.Max(0.4, Math.Abs(to - from))),
            new CubicEase { EasingMode = show ? EasingMode.EaseOut : EasingMode.EaseInOut },
            () =>
            {
                ApplyWidgetReveal(to, showing: show);
                if (!show)
                {
                    DismissIcon.Opacity = 0;
                    DismissIconArt.Source = null;
                }

                _visibilityClock = null;
            });
    }

    private void PrepareDismissIcon(bool showing)
    {
        // Prefer last artwork so the collapse feels like the track folds into itself.
        if (_viewModel.Artwork is not null)
        {
            DismissIconArt.Source = _viewModel.Artwork;
            DismissIconGlyph.Opacity = 0;
        }
        else
        {
            DismissIconArt.Source = null;
            DismissIconGlyph.Opacity = 1;
        }

        if (!showing && _taskbarHost is not null && _taskbarHost.RevealFraction > 0.95)
        {
            // Start of hide: content still full, icon waits to bloom.
            DismissIcon.Opacity = 0;
            DismissIconScale.ScaleX = 0.55;
            DismissIconScale.ScaleY = 0.55;
        }
    }

    /// <param name="p">Shell reveal 0..1 (band width toward tray).</param>
    /// <param name="showing">Direction — hide uses icon bloom then shrink-away.</param>
    private void ApplyWidgetReveal(double p, bool showing)
    {
        p = Math.Clamp(p, 0, 1);
        if (_taskbarHost is not null)
        {
            _taskbarHost.RevealFraction = p;
        }

        WidgetRoot.Opacity = 1;
        _widgetShift.X = (1 - p) * WidgetRevealShift;

        if (showing)
        {
            // Appear: icon first, then content fades in as the band opens.
            var icon = p < 0.55
                ? Math.Clamp(p / 0.35, 0, 1)
                : Math.Clamp(1.0 - (p - 0.55) / 0.35, 0, 1);
            var content = Math.Clamp((p - 0.25) / 0.55, 0, 1);
            DismissIcon.Opacity = icon;
            var scale = 0.55 + 0.45 * icon;
            DismissIconScale.ScaleX = scale;
            DismissIconScale.ScaleY = scale;
            WidgetContent.Opacity = content;
            return;
        }

        // Hide: content dissolves into the right-side pill, then pill shrinks with the wipe.
        var contentOut = Math.Clamp(p / 0.55, 0, 1);
        WidgetContent.Opacity = contentOut;

        double iconOpacity;
        double iconScale;
        if (p > 0.55)
        {
            iconOpacity = Math.Clamp((1.0 - p) / 0.35, 0, 1);
            iconScale = 0.55 + 0.45 * iconOpacity;
        }
        else if (p > 0.18)
        {
            iconOpacity = 1;
            iconScale = 1.0;
        }
        else
        {
            iconOpacity = Math.Clamp(p / 0.18, 0, 1);
            iconScale = 0.7 + 0.3 * iconOpacity;
        }

        DismissIcon.Opacity = iconOpacity;
        DismissIconScale.ScaleX = iconScale;
        DismissIconScale.ScaleY = iconScale;
    }

    /// <summary>
    /// Side chip takes over the primary slot: ghosts of the old frame cross over
    /// (old primary → left, old chip → right) while the new content slides in from the opposite side.
    /// </summary>
    private void OnPrimarySwapping(object? sender, PrimarySwapEventArgs e)
    {
        if (!_layoutReady || !_widgetShown || WidgetRoot.ActualWidth < 1)
        {
            return;
        }

        ResetSwapVisuals();

        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var artLeft = PrimaryArtHost.TranslatePoint(new Point(0, 0), SwapOverlay).X;
        var oldChipItem = _viewModel.SideSessions.FirstOrDefault(s => s.SessionKey == e.ToKey);
        var oldChip = oldChipItem is null
            ? null
            : ParkedList.ItemContainerGenerator.ContainerFromItem(oldChipItem) as FrameworkElement;
        var chipCenter = oldChip is not null && oldChip.ActualWidth > 1
            ? oldChip.TranslatePoint(new Point(oldChip.ActualWidth / 2, 0), SwapOverlay).X
            : artLeft - 60;
        var dx = Math.Max(40, artLeft + PrimaryArtHost.ActualWidth / 2 - chipCenter);

        var ghosts = new List<(UIElement Ghost, TranslateTransform Shift, double Direction)>();
        void AddGhost(FrameworkElement source, double direction)
        {
            var bitmap = Snapshot(source, pixelsPerDip);
            if (bitmap is null)
            {
                return;
            }

            var pos = source.TranslatePoint(new Point(0, 0), SwapOverlay);
            var shift = new TranslateTransform();
            var ghost = new Image
            {
                Source = bitmap,
                Width = source.ActualWidth,
                Height = source.ActualHeight,
                RenderTransform = shift
            };
            Canvas.SetLeft(ghost, pos.X);
            Canvas.SetTop(ghost, pos.Y);
            SwapOverlay.Children.Add(ghost);
            ghosts.Add((ghost, shift, direction));
        }

        AddGhost(PrimaryArtHost, -1);
        AddGhost(PrimaryMetaHost, -1);
        if (oldChip is not null)
        {
            AddGhost(oldChip, 1);
        }

        void ApplyFrame(double t)
        {
            var fadeOut = Math.Max(0, 1 - t * 1.6);
            foreach (var (ghost, shift, direction) in ghosts)
            {
                ghost.Opacity = fadeOut;
                shift.X = direction * dx * t;
            }

            var fadeIn = Math.Clamp((t - 0.1) * 1.4, 0, 1);
            _primaryArtShift.X = -dx * (1 - t);
            _primaryMetaShift.X = -dx * (1 - t);
            PrimaryArtHost.Opacity = fadeIn;
            PrimaryMetaHost.Opacity = fadeIn;

            // New chip (old primary) is generated after this event — pick it up once it exists.
            if (_swapChip is null)
            {
                var item = _viewModel.SideSessions.FirstOrDefault(s => s.SessionKey == e.FromKey);
                if (item is not null &&
                    ParkedList.ItemContainerGenerator.ContainerFromItem(item) is FrameworkElement container)
                {
                    _swapChip = container;
                    _swapChip.RenderTransform = new TranslateTransform();
                }
            }

            if (_swapChip?.RenderTransform is TranslateTransform chipShift)
            {
                chipShift.X = dx * (1 - t);
                _swapChip.Opacity = fadeIn;
            }
        }

        ApplyFrame(0);
        var clock = new AnimationClockHolder();
        clock.ProgressChanged += ApplyFrame;
        _swapClock = clock;
        clock.Start(
            0,
            1,
            TimeSpan.FromMilliseconds(PrimarySwapMs),
            new CubicEase { EasingMode = EasingMode.EaseOut },
            ResetSwapVisuals);
    }

    private void ResetSwapVisuals()
    {
        _swapClock?.Cancel();
        _swapClock = null;
        SwapOverlay.Children.Clear();
        _primaryArtShift.X = 0;
        _primaryMetaShift.X = 0;
        PrimaryArtHost.Opacity = 1;
        PrimaryMetaHost.Opacity = 1;
        if (_swapChip is not null)
        {
            _swapChip.RenderTransform = Transform.Identity;
            _swapChip.Opacity = 1;
            _swapChip = null;
        }
    }

    private static BitmapSource? Snapshot(FrameworkElement source, double pixelsPerDip)
    {
        var width = source.ActualWidth;
        var height = source.ActualHeight;
        if (width < 1 || height < 1)
        {
            return null;
        }

        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(width * pixelsPerDip),
            (int)Math.Ceiling(height * pixelsPerDip),
            96 * pixelsPerDip,
            96 * pixelsPerDip,
            PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var brush = new VisualBrush(source)
            {
                Stretch = Stretch.Fill,
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, width, height)
            };
            dc.DrawRectangle(brush, null, new Rect(0, 0, width, height));
        }

        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
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
        _appearanceIsLight = appearance.IsLight;
        _traySample = appearance.TraySample;
        _titleBrush.Color = appearance.Foreground;
        _mutedBrush.Color = appearance.MutedForeground;
        _progressTrackBrush.Color = appearance.ProgressTrack;
        _progressFillBrush.Color = appearance.ProgressFill;
        TitleMarquee.Foreground = _titleBrush;
        TitleMarquee.SetEdgeScrim(appearance.IsLight, appearance.TraySample);
        ApplyChipMarqueeScrims();
        ArtistText.Foreground = _mutedBrush;
        SourceText.Foreground = _mutedBrush;

        // Resource brushes get frozen once shared by templates — replace, never mutate.
        Resources["ProgressAccentBrush"] = new SolidColorBrush(appearance.ProgressFill);
        Resources["ProgressTrackBrush"] = new SolidColorBrush(appearance.ProgressTrack);

        ProgressBar.Background = _progressTrackBrush;
        ProgressBar.Foreground = _progressFillBrush;

        var iconBrush = new SolidColorBrush(appearance.Foreground);
        PreviousIcon.Foreground = iconBrush;
        NextIcon.Foreground = iconBrush;
    }

    private void OnContextMenuOpened(object sender, RoutedEventArgs e) =>
        AutostartMenuItem.IsChecked = StartupService.IsEnabled;

    private void OnAutostartClick(object sender, RoutedEventArgs e)
    {
        try
        {
            StartupService.SetEnabled(AutostartMenuItem.IsChecked);
        }
        catch
        {
        }

        AutostartMenuItem.IsChecked = StartupService.IsEnabled;
    }

    private void OnExitClick(object sender, RoutedEventArgs e) =>
        Application.Current.Shutdown();
}
