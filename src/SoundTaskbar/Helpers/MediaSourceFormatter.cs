namespace SoundTaskbar.Helpers;

internal static class MediaSourceFormatter
{
    public static string Format(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            return string.Empty;
        }

        var id = appId.Trim();
        var lower = id.ToLowerInvariant();

        if (ContainsAny(lower, "spotify"))
        {
            return "Spotify";
        }

        if (ContainsAny(lower, "youtubemusic", "music.youtube"))
        {
            return "YouTube Music";
        }

        if (ContainsAny(lower, "youtube"))
        {
            return "YouTube";
        }

        if (ContainsAny(lower, "yandex", "yandexmusic", "яндекс"))
        {
            return "Яндекс Музыка";
        }

        if (ContainsAny(lower, "applemusic", "itunes", "mediaplayer"))
        {
            return "Apple Music";
        }

        if (ContainsAny(lower, "amazonmusic", "amazon.music"))
        {
            return "Amazon Music";
        }

        if (ContainsAny(lower, "deezer"))
        {
            return "Deezer";
        }

        if (ContainsAny(lower, "tidal"))
        {
            return "Tidal";
        }

        if (ContainsAny(lower, "soundcloud"))
        {
            return "SoundCloud";
        }

        if (ContainsAny(lower, "vkontakte", "vk_music", "vk.com", "vkclient"))
        {
            return "VK";
        }

        if (ContainsAny(lower, "netflix"))
        {
            return "Netflix";
        }

        if (ContainsAny(lower, "discord"))
        {
            return "Discord";
        }

        if (ContainsAny(lower, "telegram"))
        {
            return "Telegram";
        }

        if (ContainsAny(lower, "vlc"))
        {
            return "VLC";
        }

        if (ContainsAny(lower, "foobar"))
        {
            return "foobar2000";
        }

        if (ContainsAny(lower, "aimp"))
        {
            return "AIMP";
        }

        if (ContainsAny(lower, "wmplayer", "microsoft.zune", "zunevideo", "moviesandtv"))
        {
            return "Movies & TV";
        }

        if (ContainsAny(lower, "chrome"))
        {
            return "Chrome";
        }

        if (ContainsAny(lower, "msedge", "microsoftedge"))
        {
            return "Edge";
        }

        if (ContainsAny(lower, "firefox", "mozilla"))
        {
            return "Firefox";
        }

        if (ContainsAny(lower, "opera"))
        {
            return "Opera";
        }

        if (ContainsAny(lower, "brave"))
        {
            return "Brave";
        }

        return HumanizeRaw(id);
    }

    private static bool ContainsAny(string value, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (value.Contains(needle, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string HumanizeRaw(string appId)
    {
        var value = appId;
        var bang = value.LastIndexOf('!');
        if (bang >= 0 && bang < value.Length - 1)
        {
            value = value[(bang + 1)..];
        }

        var underscore = value.IndexOf('_');
        if (underscore > 0)
        {
            value = value[..underscore];
        }

        if (value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^4];
        }

        var dot = value.LastIndexOf('.');
        if (dot >= 0 && dot < value.Length - 1)
        {
            value = value[(dot + 1)..];
        }

        if (value.Length == 0)
        {
            return string.Empty;
        }

        return char.ToUpperInvariant(value[0]) + value[1..];
    }
}
