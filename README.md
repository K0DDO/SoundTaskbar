# SoundTaskbar

Медиа-контролы рядом с Windows-таскбаром: обложка, название, prev / play / next, длительность. Источники вроде Spotify, Яндекс Музыки, видео в браузере — через системный SMTC.

## Требования

- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

## Запуск

```powershell
dotnet run --project src/SoundTaskbar
```

Запусти музыку или видео, затем приложение — в правом нижнем углу появится компактная панель.

## Сейчас умеет

- Читать активную медиа-сессию (title, artist, artwork, status)
- Показывать прогресс и длительность
- Previous / play-pause / next

## Дальше

- Громкость конкретного приложения
- Более плотная интеграция с таскбаром

Живой контекст для разработки: `PROJECT_CONTEXT.md`.
