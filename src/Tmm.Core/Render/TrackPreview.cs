using Tmm.Core.Audio;

namespace Tmm.Core.Render;

/// <summary>One boundary in an assembled preview: where a section starts and what it is.</summary>
public sealed record TrackSection(string Label, double StartSec, double EndSec)
{
    public double Seconds => EndSec - StartSec;
}

/// <summary>
/// Assemble the two rendered WEMs the way the game plays them: the intro once, then the loop over
/// and over. The seam that matters in practice is the loop's own wrap point, which only shows up
/// from the second repeat onward, and the intro-to-loop handover, which the loop-only preview never
/// exercises at all.
/// </summary>
public static class TrackPreview
{
    /// <summary>How much of the track to assemble by default. Long enough to hear the handover and
    /// two wraps, short enough to render in well under a second.</summary>
    public const double DefaultSeconds = 90.0;
    public const double FadeOutSeconds = 2.0;

    /// <summary>Loop repeats needed to fill <paramref name="seconds"/> after the intro, at least 2 so
    /// the wrap point is always audible.</summary>
    public static int RepeatsFor(double introSec, double loopSec, double seconds = DefaultSeconds)
    {
        if (loopSec <= 0) return 1;
        int n = (int)Math.Ceiling(Math.Max(0, seconds - introSec) / loopSec);
        return Math.Clamp(n, 2, 64);
    }

    /// <summary>
    /// intro + loop x N, with a short fade so the preview ends instead of being cut off mid-bar.
    /// The buffers are not modified; the result is a fresh buffer.
    /// </summary>
    public static (PcmBuffer audio, IReadOnlyList<TrackSection> sections) Assemble(
        PcmBuffer? intro, PcmBuffer loop, int rate, int repeats)
    {
        if (repeats < 1) throw new ArgumentOutOfRangeException(nameof(repeats));
        var parts = new List<PcmBuffer>(repeats + 1);
        var sections = new List<TrackSection>(repeats + 1);
        double t = 0;

        if (intro is not null && intro.Frames > 0)
        {
            parts.Add(intro);
            sections.Add(new TrackSection("Intro", t, t + intro.Seconds));
            t += intro.Seconds;
        }
        for (int i = 0; i < repeats; i++)
        {
            parts.Add(loop);
            sections.Add(new TrackSection($"Loop {i + 1}", t, t + loop.Seconds));
            t += loop.Seconds;
        }

        var audio = PcmBuffer.Concat(parts.ToArray()).Clone();   // Concat copies, Clone keeps callers safe
        FadeOut(audio, rate, FadeOutSeconds);
        return (audio, sections);
    }

    private static void FadeOut(PcmBuffer pcm, int rate, double seconds)
    {
        int n = Math.Min(pcm.Frames, (int)(rate * seconds));
        if (n <= 1) return;
        int start = pcm.Frames - n;
        for (int i = 0; i < n; i++)
        {
            float g = 1f - (float)i / (n - 1);
            for (int c = 0; c < pcm.Channels; c++) pcm.Data[(start + i) * pcm.Channels + c] *= g;
        }
    }
}
