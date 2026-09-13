namespace Tmm.Core.LoopFit;

/// <summary>
/// Compatibility %: four sub-scores and a geometric-mean headline.
///
/// Geometric, not arithmetic, so a 20% seam cannot hide behind two 95s. One bad component should
/// sink the slot because it will sink the result. Weights are a choice, not a measurement — the UI
/// shows all four components for exactly that reason, and Phase 2's exit criterion is that the
/// headline correlates with blind listener ratings.
/// </summary>
public sealed record Weights(
    double LoopFit = 1.0,
    double Seam = 1.0,
    double Intro = 0.6,
    double Coverage = 0.0,                       // off by default; taste, not defect
    double StretchCap = Constants.DefaultStretchCap,
    double SegmentPenalty = 0.7)                 // multiplier on seam when the loop straddles a boundary
{
    public static Weights FromSettings(Settings s) => new(
        Coverage: s.IncludeCoverageInScore ? s.CoverageWeight : 0.0,
        StretchCap: s.StretchCap);
}

public static class Scoring
{
    public static double LoopFitScore(double rho, double cap)
    {
        double d = Math.Abs(rho - 1.0);
        if (d <= Constants.MaxStretchFor100) return 100.0;
        if (d >= cap) return 0.0;
        return 100.0 * (1.0 - (d - Constants.MaxStretchFor100) / (cap - Constants.MaxStretchFor100));
    }

    /// <summary>100 if the song has intro_sec of real material before the loop start landing on a bar
    /// boundary; degrades toward 0 as the renderer must fabricate (fade-in / silence).</summary>
    public static double IntroFitScore(Slot slot, LoopCandidate cand)
    {
        if (!slot.HasIntro) return 100.0;
        double need = slot.IntroSeconds;
        double have = cand.StartSec;
        if (need <= 0 || have >= need) return 100.0;
        return 100.0 * have / need;
    }

    public static double CoverageScore(Slot slot, LoopCandidate cand, double songDurationSec)
    {
        double used = (slot.HasIntro ? slot.IntroSeconds : 0.0) + cand.NaturalSec;
        return Math.Min(100.0, 100.0 * used / songDurationSec);
    }

    /// <summary>pairs of (score 0..100, weight). Any zero score with positive weight -> 0.</summary>
    public static double GeoMean(IReadOnlyList<(double score, double weight)> pairs)
    {
        double wsum = 0, acc = 0;
        foreach (var (s, w) in pairs)
        {
            if (w <= 0) continue;
            if (s <= 0) return 0.0;
            wsum += w; acc += w * Math.Log(s);
        }
        return wsum <= 0 ? 0.0 : Math.Exp(acc / wsum);
    }

    /// <summary>Best candidate for this slot, or null if nothing is inside the stretch cap.
    /// <paramref name="candidates"/> must be sorted by NaturalSec (Candidates.Generate guarantees this).</summary>
    public static SlotScore? ScoreSlot(Slot slot, IReadOnlyList<LoopCandidate> candidates, double songDurationSec, Weights w)
    {
        double L = slot.LoopSeconds;
        if (L <= 0 || candidates.Count == 0) return null;
        double lo = L / (1 + w.StretchCap), hi = L / (1 - w.StretchCap);
        int i0 = LowerBound(candidates, lo), i1 = UpperBound(candidates, hi);

        SlotScore? best = null;
        for (int i = i0; i < i1; i++)
        {
            var c = candidates[i];
            double rho = L / c.NaturalSec;
            double lf = LoopFitScore(rho, w.StretchCap);
            double seam = c.SeamPct * (c.CrossesSegment ? w.SegmentPenalty : 1.0);
            double intro = IntroFitScore(slot, c);
            double cov = CoverageScore(slot, c, songDurationSec);
            double head = GeoMean(new[] { (lf, w.LoopFit), (seam, w.Seam), (intro, w.Intro), (cov, w.Coverage) });
            if (best is null || head > best.Headline)
                best = new SlotScore(slot.Key, c, rho, lf, seam, intro, cov, head);
        }
        return best;
    }

    /// <summary>Score one specific candidate against a slot (the editor uses this after manual edits).</summary>
    public static SlotScore ScoreCandidate(Slot slot, LoopCandidate c, double songDurationSec, Weights w)
    {
        double rho = slot.LoopSeconds / c.NaturalSec;
        double lf = LoopFitScore(rho, w.StretchCap);
        double seam = c.SeamPct * (c.CrossesSegment ? w.SegmentPenalty : 1.0);
        double intro = IntroFitScore(slot, c);
        double cov = CoverageScore(slot, c, songDurationSec);
        double head = GeoMean(new[] { (lf, w.LoopFit), (seam, w.Seam), (intro, w.Intro), (cov, w.Coverage) });
        return new SlotScore(slot.Key, c, rho, lf, seam, intro, cov, head);
    }

    // first index with NaturalSec >= x
    private static int LowerBound(IReadOnlyList<LoopCandidate> c, double x)
    {
        int lo = 0, hi = c.Count;
        while (lo < hi) { int mid = (lo + hi) >> 1; if (c[mid].NaturalSec < x) lo = mid + 1; else hi = mid; }
        return lo;
    }

    // first index with NaturalSec > x
    private static int UpperBound(IReadOnlyList<LoopCandidate> c, double x)
    {
        int lo = 0, hi = c.Count;
        while (lo < hi) { int mid = (lo + hi) >> 1; if (c[mid].NaturalSec <= x) lo = mid + 1; else hi = mid; }
        return lo;
    }
}
