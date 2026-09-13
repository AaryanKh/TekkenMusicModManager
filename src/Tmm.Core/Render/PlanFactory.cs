namespace Tmm.Core.Render;

/// <summary>Build a RenderPlan from a chosen SlotScore. The plan is the only input the renderer takes,
/// so the manual editor edits a plan and 'rebuild' replays one.</summary>
public static class PlanFactory
{
    public static RenderPlan FromScore(Slot slot, SlotScore score, SongAnalysis analysis, double? targetLufs)
    {
        var c = score.Candidate;
        var (strategy, trim) = ChooseIntro(slot, c.StartSec);
        return new RenderPlan
        {
            SlotKey = slot.Key,
            SongFingerprint = analysis.Song.Fingerprint,
            LoopStartSec = c.StartSec,
            LoopBars = c.Bars,
            Rho = score.Rho,
            IntroStrategy = strategy,
            IntroTrimSec = trim,
            CrossfadeMs = Constants.DefaultSeamCrossfadeMs,
            TargetLufs = targetLufs,
        };
    }

    /// <summary>Same rule the factory uses, exposed so the editor can re-derive the intro strategy
    /// after the user drags the loop start.</summary>
    public static (IntroStrategy strategy, double? trim) ChooseIntro(Slot slot, double loopStartSec)
    {
        if (!slot.HasIntro) return (IntroStrategy.None, null);
        if (loopStartSec >= slot.IntroSeconds) return (IntroStrategy.Real, loopStartSec - slot.IntroSeconds);
        return (IntroStrategy.FadeIn, 0.0);
    }

    /// <summary>Rho for a plan whose bar count / start changed in the editor: L / (bars * bar_sec).</summary>
    public static double RhoFor(Slot slot, int bars, double barSec) => slot.LoopSeconds / (bars * barSec);
}
