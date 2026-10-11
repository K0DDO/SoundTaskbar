# SoundTaskbar

Медиа-контролы рядом с Windows-таскбаром: обложка, название, prev / play / next, длительность. Источники вроде Spotify, Яндекс Музыки, видео в браузере — через системный SMTC.

## Требования

- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

## Запуск

Из корня репозитория (`Documents\VScode\SoundTaskbar`):

```powershell
cd C:\Users\dima4ka\Documents\VScode\SoundTaskbar
dotnet run --project src/SoundTaskbar
```

Запусти музыку или видео, затем приложение — контролы появятся **в полосе таскбара** слева от системного трея.
Правый клик по виджету → Выход.

## Сейчас умеет

- Читать активную медиа-сессию (title, artist, artwork, status)
- Показывать прогресс и длительность
- Previous / play-pause / next
- Хостинг UI внутри таскбара (`Shell_TrayWnd`)

## Дальше

- Громкость конкретного приложения
- Точнее учитывать centered taskbar / виджеты Win11

Живой контекст для разработки: `PROJECT_CONTEXT.md`.
