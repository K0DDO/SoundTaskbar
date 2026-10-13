# SoundTaskbar — project context

> **Для агента:** читай этот файл в начале каждой сессии и перед тем как контекст закончится.
> После значимых изменений обновляй разделы «Текущее состояние» и «Следующие шаги», затем коммить.

## Продукт

Windows-приложение: медиа-контролы **на полосе таскбара** (слева от трея) для музыки и видео.

Показывает: обложку, title / artist, prev · play/pause · next, прогресс + время, per-app volume (клик → слайдер, без master volume).

Источники: Spotify, Яндекс Музыка, браузерное видео и любой SMTC-плеер.

## Стек

| Слой | Технология |
|------|------------|
| UI | C# / WPF, `.NET 8`, TFM `net8.0-windows10.0.19041.0` |
| Медиа | `GlobalSystemMediaTransportControlsSessionManager` |
| Хост | full-taskbar child + `SetWindowRgn` (`TaskbarHostService`) |
| Громкость | NAudio Core Audio (`AppVolumeService`) |
| Решение | `SoundTaskbar.sln` → `src/SoundTaskbar` |

## Запуск

```powershell
cd C:\Users\dima4ka\Documents\VScode\SoundTaskbar
dotnet run --project src/SoundTaskbar
# портативный exe:
dotnet publish src/SoundTaskbar -p:PublishProfile=win-x64   # → dist\SoundTaskbar.exe
```

Правый клик → автозапуск / перезапуск / выход. Mutex одного экземпляра. Автозапуск пишет `HKCU\...\Run` только для published exe (не Debug/`dotnet run`).

## Архитектура (карта файлов)

```
App.xaml(.cs)              mutex, autostart, errors.log, restart
MainWindow.xaml(.cs)       весь UI-морф: chip/solo/pair, swap, track-skip, volume chrome
ViewModels/
  MediaViewModel.cs        SMTC refresh, primary timeline, demote, track-skip arm, volume
  SideSessionViewModel.cs  parked chip state + local timeline clock + volume
  PairLayoutPlanner.cs     sticky primary/side для ровно 2 playing
Services/
  MediaSessionService.cs   SMTC map, capture, transport, artwork decode
  AppVolumeService.cs      per-app volume/peak + IsAppRunning (кэш имён процессов)
  TaskbarHostService.cs    child на Shell_TrayWnd + region/reveal
  TaskbarAppearanceService.cs  tray sample → light/dark + accent
  StartupService.cs        HKCU Run
Controls/                  MarqueeText, PlayPauseIcon, TransportIcon
Helpers/                   AppIconLoader, MediaKindHelper, MediaSourceFormatter, TextMeasure, RelayCommand
Native/NativeMethods.cs    P/Invoke только для host + appearance
```

### Потоки данных

1. SMTC `Changed` / `TimelineChanged` → `MediaViewModel.RefreshAsync` (coalesce через `SemaphoreSlim`).
2. Full capture = props+art параллельно; light = playback/timeline.
3. `ApplyAll` на UI: filter parkable → playing set → solo / pause+play / play+play / empty.
4. Локальные часы: primary `_uiTicker` 50ms; side `TickTimelineClock`; volume `_volumeTicker` 33ms.
5. UI-морфы живут в `MainWindow` (не во ViewModel): parked reveal, play+play morph, divider cube-swap, track-skip.

## Целевое поведение UI

| Режим | Вид |
|-------|-----|
| Solo | ширина ~310, marquee, VU, volume open → dissolve имени/времени в прогресс |
| pause+play | chip(и) слева + divider + primary play; appear/dismiss 340ms |
| play+play | morph от pause chip; fixed meta `PlayPlayMetaWidth=96`; demote: 1s hold → wipe или chip shrink |
| 3+ | `MaxSideChips=5`; expanded play-чип последний; лишние — compact paused |
| Нет сессии | pill-иконка + wipe к трею (`RevealFraction`); grace пустых 900ms |
| Divider swap | vertical cube art/title/artist/icon; seek glyphs; slash-morph time; orange lerp |
| Audio next/prev | art cube-roll; title/artist slide; timeline slash-morph (как swap); video = seek-only |

## Важные ограничения Win11

- Маленький child `Shell_TrayWnd` прячется под XAML-слоем таскбара.
- Рабочий путь (FluentFlyout-style): child на **весь** таскбар + `SetWindowRgn` только на полосу виджета.
- Win+D / cloak hooks **откатывались** (ломали отрисовку) — не возвращать без отдельного плана.
- Style/exstyle и `SetWindowPos` писать только при реальном изменении.

## Текущее состояние (после аудита perf)

### Поведение — работает

- Cold start: `_layoutReady` после `SettleColdStartLayout` (2× Loaded).
- Solo / pause+play / play+play / 3+ chips / empty wipe — как в таблице выше.
- Primary volume в pause+play = как solo (без steal art/play/next). Compact chip volume: mute + slider + steal.
- Track-skip на primary и side (expanded/compact art).
- Empty SMTC grace 900ms + tick refresh. Только `IsParkable` + running app.
- Title edge-scrim из tray sample; resource brushes не мутировать.
- UI exceptions → `%LOCALAPPDATA%\SoundTaskbar\errors.log`.

### Perf (сделано в аудите)

| Было | Стало |
|------|--------|
| `IsAppRunning` → `Process.GetProcesses` на каждый вызов | кэш имён процессов ~500ms + кэш tokens |
| Side `RefreshVolume(forceRefresh:true)` каждые 33ms | общий 400ms session refresh с primary |
| Parked watchdog 100ms | 400ms |
| Appearance `GetPixel` каждые 300ms | 1500ms + reuse `UISettings` |
| Sequential SMTC media props | `Task.WhenAll` + `DecodePixelWidth=64` |
| `Geometry.Parse` на каждый transport redraw | frozen static geometries |
| Dead Win+D P/Invoke surface | удалён |
| Publish | `PublishReadyToRun=true` |

### Хрупкое — не трогать без регрессии

- Demote delay / commit / `HoldPlayPlayDuringDemoteDelay`
- Empty-session grace + tick kick
- Seek guard / no-rewind `SyncTimeline` (Spotify stale Position)
- Track-skip arm + empty-title wait + known-music override
- `RetargetSessionKey` + transport lock (PlayPause переживает swap)
- Force sticky keys для divider swap
- Playing-set: peak только из solo; pair = SMTC-only
- Host region / attach split / reveal wipe

## Правила коммитов / агента

1. Сообщение: lowercase, без точки, одно короткое предложение.
2. В начале сессии — этот файл; перед концом контекста — обновить «Текущее состояние» / «Следующие шаги» + коммит.
3. Не коммить секреты; не раздувать scope.
4. После логического куска — коммит; запускай приложение сам.
5. Не переписывать host/SMTC parkable/demote «для красоты».

## Следующие шаги

1. Diff-based SMTC `RebuildSessions` (сейчас clear-all + re-subscribe) — меньше churn при `SessionsChanged`.
2. Artwork content-hash cache, чтобы не перекодировать один и тот же thumbnail.
3. Проверить cube-swap грани (art/title/artist/time) в play+play.
4. Seek по клику на прогресс.
5. Win+D: безопасная защита без `WM_SHOWWINDOW` (прошлый откат — d7fc9cf).
6. README всё ещё частично устарел относительно volume/multi-chip — держать в синхе с этим файлом.
