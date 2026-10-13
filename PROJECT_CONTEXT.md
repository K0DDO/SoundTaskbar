# SoundTaskbar — project context

> **Для агента:** читай этот файл в начале каждой сессии и перед тем как контекст закончится.
> После значимых изменений обновляй разделы «Текущее состояние» и «Следующие шаги».

## Продукт

Windows-приложение: медиа-контролы **на полосе таскбара** для музыки и видео.

Когда играет источник вроде Spotify, Яндекс Музыки, YouTube или другого плеера, на таскбаре видно:

- обложку / превью
- название трека или видео (и при наличии — исполнитель / канал)
- кнопки назад, пауза/плей, вперёд
- прогресс / длительность
- громкость именно этого источника: клик открывает слайдер, можно сделать тише или громче без трогания системной громкости

Та же схема для видео, не только для песен.

## Целевое поведение

1. Подхватывать активный медиа-источник системы (SMTC / session API).
2. Показывать UI на полосе таскбара слева от трея.
3. Play / pause / next / previous через системные команды медиа-сессии.
4. Per-app volume: менять громкость конкретного audio session, не master volume.
5. Несколько SMTC-источников: пауза оставляет чип с ▶ на обложке; два playing — упрощённый dual-вид.

## Важные ограничения Win11 25H2

- Маленький child `Shell_TrayWnd` оказывается под XAML-слоем таскбара → не видно.
- Top-level overlay видно, но Win+D / «показать рабочий стол» его прячет (WorkerW/cloak).
- Рабочий путь (FluentFlyout-style): child на **весь** таскбар + `SetWindowRgn` только на полосу виджета + прозрачный фон.

## Стек

- Платформа: Windows 10/11
- UI: C# / WPF (`.NET 8`, TFM `net8.0-windows10.0.19041.0`)
- Медиа: `GlobalSystemMediaTransportControlsSessionManager`
- Хостинг: full-taskbar child + `SetWindowRgn` (`TaskbarHostService`)
- Громкость источника: NAudio Core Audio (`AppVolumeService`) + `VolumeFlyout`
- Решение: `SoundTaskbar.sln` → `src/SoundTaskbar`

## Запуск

```powershell
cd C:\Users\dima4ka\Documents\VScode\SoundTaskbar
dotnet run --project src/SoundTaskbar
```

Правый клик по виджету → «Запускать с Windows» / Выход.

Портативный exe (self-contained, single-file, .NET внутри):

```powershell
dotnet publish src/SoundTaskbar -p:PublishProfile=win-x64   # → dist\SoundTaskbar.exe
```

Автозапуск: `HKCU\...\Run\SoundTaskbar`, включается при первом запуске exe (не из bin\Debug), путь обновляется если exe перенесли. Один экземпляр (mutex).

## Правила коммитов

- Сообщение с маленькой буквы
- Без точки в конце
- Одно короткое предложение

## Правила для агента

1. В начале работы перечитай этот файл.
2. Перед исчерпанием контекста обнови «Текущее состояние» и «Следующие шаги», затем закоммить.
3. Не коммить секреты.
4. Не раздувай scope.
5. После логического куска — коммит (договорённость сессии).
6. Запускай приложение сам после изменений.

## Текущее состояние

- **Cold start**: `_layoutReady` после `SettleColdStartLayout` (2× Loaded) — иначе при уже играющих источниках chip/meta меряются без контейнеров и кривят layout
- **Solo**: 310 / marquee / icon / VU — layout заморожен; volume open → имя + `TimeText` dissolve в прогресс (ширина+opacity, 260ms)
- **pause+play** — chip + divider + play; ширина `(310 - 28) + parked + 16.5`; appear/dismiss (`SealParkedReveal`, 340ms). Compact chip volume: mute + slider + steal art/next. Primary volume: **как solo** — mute in place, VolumeHost 16→74, без steal art/play/next
- **play+play**: morph от pause chip; fixed left meta `PlayPlayMetaWidth=96`. Demote: пауза → ждём 1s (chrome play+play), unpause отменяет; затем тот же primary → shrink expanded, primary сменился → hide/settle/show. Peak только из solo; pair = только SMTC. Divider swap **только play↔play / pause↔pause**. Context menu: автозапуск / перезапуск / выход
- **3+ источника**: `MaxSideChips=5`; лишние — маленькие paused-чипы слева, разделители между чипами; expanded play-чип всегда последний. Те же demote-правила, что для двух. `MorphSideChips` — прогресс на каждый чип
- **Нет источника** → `HasSession=false` → morph в pill-иконку справа (обложка/♪) + wipe к трею (`RevealFraction`). Grace пустых сессий 900ms + tick refresh (баг: раньше grace истекал без refresh). Только `IsParkable` + running app; Stopped/Closed/мёртвый процесс → collapse. `_staleVisuals` держит последний трек на время анимации
- **Смена доминирующего** / divider swap: cube (art/title/artist/app-icon); progress Value lerp на обеих сторонах (freeze binding); duration выходит из бара (shrink) или после роста fill (expand); prev/next = seek; pause/volume/VU не трогаем. Retarget unlocks chip transport. На swap `ParkedHost.ClipToBounds=false`
- **Audio next/prev**: art — горизонтальный cube-roll; title/artist — горизонтальный slide (съезд); primary и side-чип (expanded / compact art); video/seek-only без этого
- Хост: таймер 800ms делает attach (стили пишутся только при изменении); анимации ширины/reveal идут через лёгкий `UpdateRegion` (только геометрия + регион). Win+D-защиты сейчас нет (откат в d7fc9cf)
- empty SMTC grace 900ms (Win+D blip); pair-exit grace убран — demote play+play→pause+play сразу
- Title fade: edge-scrim from tray sample; ресурсные кисти не мутировать (frozen) — только заменять
- Необработанные UI-исключения → `%LOCALAPPDATA%\SoundTaskbar\errors.log`, приложение не падает

## Следующие шаги

1. Проверить cube-swap по divider в play+play (грани art/title/artist/time)
2. Win+D: безопасная защита без перехвата WM_SHOWWINDOW (прошлая попытка ломала отрисовку)
3. Seek по клику на прогресс
