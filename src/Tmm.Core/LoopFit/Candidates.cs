using Tmm.Core.Analysis;

namespace Tmm.Core.LoopFit;

/// <summary>
/// Slot-independent loop candidates: every (start downbeat, bar count) the song supports.
///
/// Computed once per song. Scoring 443 slots against them is a sorted lookup, which is why moving
/// the stretch slider re-ranks instantly without re-analysis.
/// </summary>
public static class Candidates
{
    public static List<LoopCandidate> Generate(SongAnalysis analysis, Features feats, int? maxBars = null,
                                               int nBaseline = 500, int seed = 0)
    {
        var grid = analysis.Grid;
        double bar = grid.BarSec;
        double dur = analysis.Song.DurationSec;
        var downbeats = grid.DownbeatsSec;
        int maxB = maxBars ?? (int)Math.Floor(dur / bar);
        var outList = new List<LoopCandidate>();
        if (downbeats.Length < 2) return outList;

        // Baseline: random same-song cuts. Song-relative on purpose: a raw distance means nothing
        // across genres; "tighter than 94% of arbitrary cuts in your track" means the same thing for
        // a ballad and a metal mix.
        var rng = new Random(seed);
        var baseline = new List<double>(nBaseline);
        for (int i = 0; i < nBaseline; i++)
        {
            int a = rng.Next(downbeats.Length), b = rng.Next(downbeats.Length);
            if (a == b) continue;
            baseline.Add(FeatureExtractor.SeamDistance(feats, downbeats[a], downbeats[b]));
        }
        baseline.Sort();

        foreach (var s in downbeats)
        {
            for (int k = Constants.MinBars; k <= maxB; k++)
            {
                double e = s + k * bar;
                if (e > dur) break;
                double raw = FeatureExtractor.SeamDistance(feats, s, e);
                outList.Add(new LoopCandidate(
                    StartSec: s, Bars: k, NaturalSec: k * bar, SeamRaw: raw,
                    SeamPct: Percentile(baseline, raw),
                    CrossesSegment: Crosses(s, e, analysis)));
            }
        }
        outList.Sort((x, y) => x.NaturalSec.CompareTo(y.NaturalSec));
        return outList;
    }

    /// <summary>Percent of random same-song cuts that are worse (larger distance) than this seam.</summary>
    private static double Percentile(List<double> sortedBaseline, double raw)
    {
        if (sortedBaseline.Count == 0) return 50.0;
        int idx = sortedBaseline.BinarySearch(raw);
        if (idx < 0) idx = ~idx;
        else { while (idx < sortedBaseline.Count && sortedBaseline[idx] <= raw) idx++; }
        return 100.0 * (sortedBaseline.Count - idx) / sortedBaseline.Count;
    }

    private static bool Crosses(double start, double end, SongAnalysis a)
    {
        foreach (var s in a.Segments)
            if ((start < s.StartSec && s.StartSec < end) || (start < s.EndSec && s.EndSec < end)) return true;
        return false;
    }
}
