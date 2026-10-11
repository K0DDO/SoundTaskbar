using SoundTaskbar.Services;

namespace SoundTaskbar.ViewModels;

public enum PairKind
{
    None,
    /// <summary>
    /// Two playing, not twin-video: keep big primary + side chip with title/artist/duration.
    /// Used for audio+video and audio+audio — never the old dual-compact host.
    /// </summary>
    PrimaryWithExpandedSide,
    /// <summary>Two videos: twin full cards, left mirrored.</summary>
    TwinVideo
}

public readonly record struct PairPlan(
    PairKind Kind,
    MediaSessionSnapshot Primary,
    IReadOnlyList<MediaSessionSnapshot> SideSessions,
    IReadOnlyList<MediaSessionSnapshot> DualSessions,
    double WidgetWidth);

/// <summary>
/// Decides layout when 2+ SMTC sessions are playing at once.
/// </summary>
internal static class PairLayoutPlanner
{
    public static PairPlan Plan(
        IReadOnlyList<MediaSessionSnapshot> playing,
        string? stickyPrimaryKey,
        string? parkedSideKey = null)
    {
        if (playing.Count < 2)
        {
            return new PairPlan(
                PairKind.None,
                playing.Count > 0 ? playing[0] : new MediaSessionSnapshot(),
                Array.Empty<MediaSessionSnapshot>(),
                Array.Empty<MediaSessionSnapshot>(),
                WidgetWidth: 380);
        }

        var videos = playing.Where(s => s.IsVideo).ToList();

        // Twin video only when we confidently have 2+ videos and are not
        // upgrading a parked audio chip (pause+play → play+play).
        var upgradingParkedChip = !string.IsNullOrEmpty(parkedSideKey) &&
                                  playing.Any(s => s.SessionKey == parkedSideKey);

        if (videos.Count >= 2 && !upgradingParkedChip)
        {
            var right = PickSticky(videos, stickyPrimaryKey) ?? videos[0];
            var left = videos.FirstOrDefault(s => s.SessionKey != right.SessionKey) ?? videos[1];
            return new PairPlan(
                PairKind.TwinVideo,
                Primary: right,
                SideSessions: Array.Empty<MediaSessionSnapshot>(),
                DualSessions: new[] { left, right },
                WidgetWidth: 520);
        }

        // Default for any other pair (A/V, A/A, or uncertain): single primary + expanded side.
        // Prefer video as primary; otherwise sticky; otherwise first playing.
        var primary = videos.Count > 0
            ? (PickSticky(videos, stickyPrimaryKey) ?? videos[0])
            : (PickSticky(playing, stickyPrimaryKey) ?? playing[0]);

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

        return new PairPlan(
            PairKind.PrimaryWithExpandedSide,
            Primary: primary,
            SideSessions: new[] { side },
            DualSessions: Array.Empty<MediaSessionSnapshot>(),
            WidgetWidth: 520);
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
