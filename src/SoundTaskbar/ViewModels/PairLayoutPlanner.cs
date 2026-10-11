using SoundTaskbar.Services;

namespace SoundTaskbar.ViewModels;

public enum PairKind
{
    None,
    /// <summary>One audio + one video playing: big video + expanded audio chip.</summary>
    MixedAv,
    /// <summary>Two videos: twin full cards, left mirrored.</summary>
    TwinVideo,
    /// <summary>Two audio sources: compact dual cards.</summary>
    TwinAudio
}

public readonly record struct PairPlan(
    PairKind Kind,
    MediaSessionSnapshot Primary,
    IReadOnlyList<MediaSessionSnapshot> SideSessions,
    bool SideExpandedMeta,
    IReadOnlyList<MediaSessionSnapshot> DualSessions,
    bool DualTwinVideo,
    double WidgetWidth);

/// <summary>
/// Decides layout when 2+ SMTC sessions are playing at once.
/// </summary>
internal static class PairLayoutPlanner
{
    public static PairPlan Plan(
        IReadOnlyList<MediaSessionSnapshot> playing,
        string? stickyPrimaryKey)
    {
        if (playing.Count < 2)
        {
            return new PairPlan(
                PairKind.None,
                playing.Count > 0 ? playing[0] : new MediaSessionSnapshot(),
                Array.Empty<MediaSessionSnapshot>(),
                SideExpandedMeta: false,
                Array.Empty<MediaSessionSnapshot>(),
                DualTwinVideo: false,
                WidgetWidth: 380);
        }

        var videos = playing.Where(s => s.IsVideo).ToList();
        var audios = playing.Where(s => !s.IsVideo).ToList();

        if (videos.Count >= 1 && audios.Count >= 1)
        {
            var video = PickSticky(videos, stickyPrimaryKey) ?? videos[0];
            var audio = PickSticky(audios, stickyPrimaryKey) ?? audios[0];
            // Prefer video as the big primary even if sticky was audio.
            return new PairPlan(
                PairKind.MixedAv,
                Primary: video,
                SideSessions: new[] { audio },
                SideExpandedMeta: true,
                DualSessions: Array.Empty<MediaSessionSnapshot>(),
                DualTwinVideo: false,
                WidgetWidth: 460);
        }

        if (videos.Count >= 2)
        {
            var right = PickSticky(videos, stickyPrimaryKey) ?? videos[0];
            var left = videos.FirstOrDefault(s => s.SessionKey != right.SessionKey) ?? videos[1];
            return new PairPlan(
                PairKind.TwinVideo,
                Primary: right,
                SideSessions: Array.Empty<MediaSessionSnapshot>(),
                SideExpandedMeta: false,
                DualSessions: new[] { left, right },
                DualTwinVideo: true,
                WidgetWidth: 520);
        }

        // audio + audio (or unclassified pair treated as audio)
        var dual = playing.Take(2).ToList();
        var primary = PickSticky(dual, stickyPrimaryKey) ?? dual[0];
        return new PairPlan(
            PairKind.TwinAudio,
            Primary: primary,
            SideSessions: Array.Empty<MediaSessionSnapshot>(),
            SideExpandedMeta: false,
            DualSessions: dual,
            DualTwinVideo: false,
            WidgetWidth: 380);
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
