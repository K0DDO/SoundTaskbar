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

- **Solo**: 310 / marquee / icon / VU — layout заморожен; volume open → имя + `TimeText` dissolve в прогресс (ширина+opacity, 260ms)
- **pause+play заморожен** — chip + divider + play; ширина `(310 - 28) + parked + 16.5`; appear/dismiss (`SealParkedReveal`, 340ms) — не трогать
- **play+play**: morph от pause chip; fixed left meta `PlayPlayMetaWidth=96`; name→icon latched; DualHost удалён. Morph **последовательный** (520ms). Volume слева как solo: `ParkedList` Left-align (транспорт на месте), chip растёт, artist→icon + time→progress dissolve. Context menu: автозапуск / перезапуск / выход
- **3+ источника**: `MaxSideChips=3`; лишние — маленькие paused-чипы слева, разделители между чипами (`DividerVisibility` в шаблоне); expanded play-чип всегда последний (у основного). `MorphSideChips` — прогресс на каждый чип, один clock, коалесинг через `ScheduleSideMorph` (ждёт конец ParkedReveal)
- **Нет источника** → `HasSession=false` → morph в pill-иконку справа (обложка/♪) + wipe к трею (`RevealFraction`). Grace пустых сессий 900ms + tick refresh (баг: раньше grace истекал без refresh). Только `IsParkable` + running app; Stopped/Closed/мёртвый процесс → collapse. `_staleVisuals` держит последний трек на время анимации
- **Смена доминирующего** (чип стал основным): `PrimarySwapping` → снапшоты старого кадра в `SwapOverlay` разъезжаются, новый контент въезжает навстречу (420ms)
- Хост: таймер 800ms делает attach (стили пишутся только при изменении); анимации ширины/reveal идут через лёгкий `UpdateRegion` (только геометрия + регион). Win+D-защиты сейчас нет (откат в d7fc9cf)
- pair/empty SMTC grace 2–2.5s; клик play/pause из виджета grace пропускает (1.5s)
- Title fade: edge-scrim from tray sample; ресурсные кисти не мутировать (frozen) — только заменять
- Необработанные UI-исключения → `%LOCALAPPDATA%\SoundTaskbar\errors.log`, приложение не падает

## Следующие шаги

1. Проверить вживую: 3 источника, swap pause+play ↔ play+pause, collapse при закрытии всех плееров
2. Win+D: безопасная защита без перехвата WM_SHOWWINDOW (прошлая попытка ломала отрисовку)
3. Seek по клику на прогресс
