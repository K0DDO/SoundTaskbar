using SoundTaskbar.Services;

namespace SoundTaskbar.ViewModels;

public enum PairKind
{
    None,
    /// <summary>
    /// Two playing sources on SingleHost: pause chip morphs into mirrored play + narrowed primary.
    /// </summary>
    PlayPlay
}

public readonly record struct PairPlan(
    PairKind Kind,
    MediaSessionSnapshot Primary,
    IReadOnlyList<MediaSessionSnapshot> SideSessions,
    double WidgetWidth);

/// <summary>
/// Decides layout when 2+ SMTC sessions are playing at once.
/// </summary>
internal static class PairLayoutPlanner
{
    public static PairPlan Plan(
        IReadOnlyList<MediaSessionSnapshot> playing,
        string? stickyPrimaryKey,
        string? parkedSideKey = null,
        bool forceStickyPrimary = false)
    {
        if (playing.Count < 2)
        {
            return new PairPlan(
                PairKind.None,
                playing.Count > 0 ? playing[0] : new MediaSessionSnapshot(),
                Array.Empty<MediaSessionSnapshot>(),
                WidgetWidth: MediaViewModel.SoloWidgetWidth);
        }

        var videos = playing.Where(s => s.IsVideo).ToList();

        // Divider swap: honor sticky even over video preference.
        MediaSessionSnapshot primary;
        var sticky = PickSticky(playing, stickyPrimaryKey);
        if (forceStickyPrimary && sticky is not null)
        {
            primary = sticky;
        }
        else
        {
            // Prefer video as primary; otherwise sticky; otherwise first playing.
            primary = videos.Count > 0
                ? (PickSticky(videos, stickyPrimaryKey) ?? videos[0])
                : (sticky ?? playing[0]);
        }

        // If we already had a parked chip, keep that session on the side (stable morph).
        MediaSessionSnapshot side;
        if (!string.IsNullOrEmpty(parkedSideKey))
        {
            side = playing.FirstOrDefault(s => s.SessionKey == parkedSideKey)
                   ?? playing.First(s => s.SessionKey != primary.SessionKey);
            if (side.SessionKey == primary.SessionKey)
            {
                primary = playing.First(s => s.SessionKey != side.SessionKey);
            }
        }
        else
        {
            side = playing.First(s => s.SessionKey != primary.SessionKey);
        }

        // Width is measured by MainWindow after chip morph — placeholder solo.
        return new PairPlan(
            PairKind.PlayPlay,
            Primary: primary,
            SideSessions: new[] { side },
            WidgetWidth: MediaViewModel.SoloWidgetWidth);
    }

    private static MediaSessionSnapshot? PickSticky(
        IReadOnlyList<MediaSessionSnapshot> candidates,
        string? stickyPrimaryKey)
    {
        if (string.IsNullOrEmpty(stickyPrimaryKey))
        {
            return null;
        }

        return candidates.FirstOrDefault(s => s.SessionKey == stickyPrimaryKey);
    }
}
