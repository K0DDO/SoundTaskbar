using Windows.Media;

namespace SoundTaskbar.Helpers;

internal static class MediaKindHelper
{
    public static bool IsVideo(
        MediaPlaybackType? playbackType,
        string? appId,
        bool canSkipPrevious,
        bool canSkipNext,
        bool canSeek)
    {
        if (playbackType == MediaPlaybackType.Video)
        {
            return true;
        }

        if (playbackType == MediaPlaybackType.Music)
        {
            return false;
        }

        var id = (appId ?? string.Empty).ToLowerInvariant();

        if (IsKnownMusicApp(id))
        {
            return false;
        }

        if (IsKnownVideoApp(id))
        {
            return true;
        }

        // Browsers almost always host video when SMTC is active.
        if (IsBrowser(id))
        {
            return true;
        }

        // Seek-only controls are typical for video players.
        if (canSeek && !canSkipPrevious && !canSkipNext)
        {
            return true;
        }

        return false;
    }

    private static bool IsKnownMusicApp(string id) =>
        ContainsAny(
            id,
            "spotify",
            "youtubemusic",
            "music.youtube",
            "yandexmusic",
            "yandex.music",
            "applemusic",
            "itunes",
            "amazonmusic",
            "deezer",
            "tidal",
            "soundcloud",
            "foobar",
            "aimp",
            "vk_music");

    private static bool IsKnownVideoApp(string id) =>
        ContainsAny(
            id,
            "netflix",
            "moviesandtv",
            "zunevideo",
            "vlc",
            "plex",
            "mpv",
            "potplayer",
            "wmplayer");

    private static bool IsBrowser(string id) =>
        ContainsAny(
            id,
            "chrome",
            "msedge",
            "microsoftedge",
            "firefox",
            "mozilla",
            "opera",
            "brave",
            "vivaldi");

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
}
