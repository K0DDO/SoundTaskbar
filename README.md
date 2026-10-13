# SoundTaskbar

Медиа-контролы **в полосе Windows-таскбара** (слева от трея): обложка, название, prev / play / next, прогресс, громкость приложения. Источники — любой SMTC-плеер (Spotify, Яндекс Музыка, видео в браузере и т.д.).

## Требования

- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

## Запуск

```powershell
cd C:\Users\dima4ka\Documents\VScode\SoundTaskbar
dotnet run --project src/SoundTaskbar
```

Правый клик по виджету → автозапуск / перезапуск / выход.

Портативный exe:

```powershell
dotnet publish src/SoundTaskbar -p:PublishProfile=win-x64   # → dist\SoundTaskbar.exe
```

## Умеет

- SMTC: title, artist, artwork, play/pause, next/prev (или seek ±10s)
- Несколько источников: pause+play, play+play, до 5 side-чипов
- Per-app volume + VU
- Анимации смены трека / swap чипов / appear-dismiss

Живой контекст для разработки: [`PROJECT_CONTEXT.md`](PROJECT_CONTEXT.md).
