using Tmm.Core;
using Tmm.Core.LoopFit;
using Xunit;

namespace Tmm.Core.Tests;

/// <summary>Ported from tests/test_scoring.py plus the bisect window.</summary>
public class ScoringTests
{
    private const double Cap = Constants.DefaultStretchCap;

    [Fact] public void LoopFitIs100InsideTolerance() => Assert.Equal(100.0, Scoring.LoopFitScore(1.004, Cap));
    [Fact] public void LoopFitIs0AtCap() => Assert.Equal(0.0, Scoring.LoopFitScore(1 + Cap, Cap));

    [Fact]
    public void LoopFitIsMonotone()
    {
        double a = Scoring.LoopFitScore(1.01, Cap), b = Scoring.LoopFitScore(1.02, Cap), c = Scoring.LoopFitScore(1.04, Cap);
        Assert.True(a > b && b > c);
    }

    [Fact]
    public void GeoMeanPunishesOneBadComponent()
    {
        double good = Scoring.GeoMean(new[] { (95.0, 1.0), (95.0, 1.0), (95.0, 1.0) });
        double oneBad = Scoring.GeoMean(new[] { (95.0, 1.0), (95.0, 1.0), (20.0, 1.0) });
        double arith = (95 + 95 + 20) / 3.0;
        Assert.True(oneBad < arith && arith < good);
    }

    [Fact] public void ZeroComponentSinksSlot() => Assert.Equal(0.0, Scoring.GeoMean(new[] { (100.0, 1.0), (0.0, 1.0) }));

    [Fact] public void ZeroWeightIgnoresComponent() => Assert.Equal(100.0, Scoring.GeoMean(new[] { (100.0, 1.0), (0.0, 0.0) }), 9);

    private static Slot SlotOf(double loopSec, double? introSec = null)
    {
        var id = new SlotIdentity(1, "t", "TEKKEN 8", introSec is null ? null : 2, 1, (int?)introSec, (int)loopSec);
        WemInfo? intro = introSec is double s ? new WemInfo(2, (int)(s * 48000), 48000, 2, 0xFFFF, 0) : null;
        return new Slot(id, intro, new WemInfo(1, (int)(loopSec * 48000), 48000, 2, 0xFFFF, 0));
    }

    private static LoopCandidate Cand(double start, int bars, double barSec, double seamPct = 90, bool crosses = false)
        => new(start, bars, bars * barSec, 1.0, seamPct, crosses);

    [Fact]
    public void ScoreSlotPicksInsideTheStretchWindowOnly()
    {
        double bar = 2.0;
        var cands = Enumerable.Range(4, 40).Select(b => Cand(0, b, bar)).ToList();   // 8 s .. 86 s
        var slot = SlotOf(30.5);
        var best = Scoring.ScoreSlot(slot, cands, 120, new Weights());
        Assert.NotNull(best);
        Assert.Equal(15, best!.Candidate.Bars);                  // 30 s natural, 1.7% stretch
        Assert.InRange(Math.Abs(best.Rho - 1), 0, Cap);
        Assert.Null(Scoring.ScoreSlot(SlotOf(4.0), cands, 120, new Weights()));   // nothing shorter than 8 s
    }

    [Fact]
    public void IntroFitDegradesWhenSongLacksLeadIn()
    {
        var slot = SlotOf(30, introSec: 4);
        Assert.Equal(100.0, Scoring.IntroFitScore(slot, Cand(4.0, 8, 2)));
        Assert.Equal(50.0, Scoring.IntroFitScore(slot, Cand(2.0, 8, 2)));
        Assert.Equal(100.0, Scoring.IntroFitScore(SlotOf(30), Cand(0, 8, 2)));
    }

    [Fact]
    public void RankingIsSortedDescending()
    {
        double bar = 2.0;
        var cands = Enumerable.Range(4, 60).Select(b => Cand(0, b, bar, seamPct: 50 + b % 7 * 5)).ToList();
        var slots = new[] { SlotOf(20.1), SlotOf(40.3), SlotOf(60.0), SlotOf(500) };
        var ranked = Ranking.Rank(slots, cands, 200, new Weights());
        Assert.Equal(3, ranked.Count);
        for (int i = 1; i < ranked.Count; i++) Assert.True(ranked[i - 1].Headline >= ranked[i].Headline);
    }
}
