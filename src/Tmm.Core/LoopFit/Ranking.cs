namespace Tmm.Core.LoopFit;

/// <summary>Score every slot and return them sorted. Filters live in the UI, not here.</summary>
public static class Ranking
{
    public static List<SlotScore> Rank(IReadOnlyList<Slot> slots, IReadOnlyList<LoopCandidate> candidates,
                                       double songDurationSec, Weights? w = null)
    {
        w ??= new Weights();
        var scored = new List<SlotScore>(slots.Count);
        foreach (var s in slots)
        {
            var sc = Scoring.ScoreSlot(s, candidates, songDurationSec, w);
            if (sc is not null) scored.Add(sc);
        }
        scored.Sort((a, b) => b.Headline.CompareTo(a.Headline));
        return scored;
    }
}
