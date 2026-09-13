using Tmm.Core.Audio;
using Tmm.Core.Wem;

namespace Tmm.Core.Render;

/// <summary>RenderPlan + decoded song + Slot -> two sample-exact int16 buffers, then WEMs.</summary>
public static class RenderPipeline
{
    /// <summary>Frames of slop we accept from a stretcher before calling it a bug.</summary>
    public const int StretchTolerance = 64;

    public sealed record Buffers(PcmBuffer Loop, PcmBuffer? Intro, double SeamMetric, double PeakDbfs, double Lufs, double Gain,
                                 double GainDb = 0, double LimiterReductionDb = 0);

    /// <summary>Render to in-memory buffers only (preview and the editor use this; no files touched).</summary>
    public static Buffers RenderBuffers(PcmBuffer pcm, Slot slot, RenderPlan plan, IStretcher stretcher)
    {
        if (!slot.Measured)
            throw new CatalogNotBuiltException($"'{slot.Title}' has provisional (spreadsheet) lengths. Build the catalog from your game's WEMs before rendering.");
        int rate = slot.Loop.SampleRate;
        if (pcm.Rate != rate)
            throw new RenderException($"song decoded at {pcm.Rate} Hz but the slot is {rate} Hz; decode at the slot's rate");
        if (plan.Rho <= 0) throw new RenderException("plan.Rho must be positive");

        int start = (int)Math.Round(plan.LoopStartSec * rate);
        int naturalFrames = (int)Math.Round(slot.LoopFrames / plan.Rho);   // frames to take before stretching
        if (start < 0 || start + naturalFrames > pcm.Frames)
            throw new RenderException($"loop [{plan.LoopStartSec:0.00}s + {naturalFrames / (double)rate:0.00}s] runs past the end of the song ({pcm.Seconds:0.00}s)");

        // 1. slice
        var loop = pcm.Slice(start, naturalFrames);
        // 2. stretch by rho; trim/pad by <= a few frames to exactly slot.loop_frames
        loop = stretcher.Stretch(loop, plan.Rho);
        loop = Verify.FitToFrames(loop, slot.LoopFrames, StretchTolerance, "loop");
        // 3. crossfade the wrap
        if (plan.CrossfadeMs > 0)
        {
            int n = (int)(rate * plan.CrossfadeMs / 1000);
            var preroll = start >= n ? pcm.Slice(start - n, n) : null;   // unstretched: ≤20 ms, drift ≤ 1.2 ms
            loop = Seams.CrossfadeWrap(loop, preroll, rate, plan.CrossfadeMs);
        }
        // 4. loudness
        double gain = 1.0;
        if (plan.TargetLufs is double target)
            gain = Loudness.Normalize(loop, rate, target);
        // 5. hard gate
        Verify.AssertExact(loop, slot.LoopFrames, "loop");

        // --- intro ---
        PcmBuffer? intro = null;
        if (slot.HasIntro)
        {
            var ip = plan.IntroStrategy == IntroStrategy.None ? IntroStrategy.Silence : plan.IntroStrategy;
            var p2 = plan.Clone(); p2.IntroStrategy = ip;
            intro = Intro.BuildIntro(pcm, rate, p2, slot.IntroFrames);
            if (intro is null) intro = new PcmBuffer(slot.IntroFrames, pcm.Channels, rate);
            if (gain != 1.0) intro.Scale((float)gain);        // same loudness gain as the loop
            Verify.AssertExact(intro, slot.IntroFrames, "intro");
        }

        // 6. manual level trim, then limiting. The same linear gain goes on both halves so the intro
        // does not step up or down as the game hands over to the loop; the limiter then runs on each
        // buffer independently because they are separate WEMs.
        double reduction = 0;
        if (Math.Abs(plan.GainDb) > 1e-9)
        {
            float user = (float)Math.Pow(10, plan.GainDb / 20);
            loop.Scale(user);
            intro?.Scale(user);
        }
        // The limiter runs unconditionally, not just when a trim was asked for. Without it anything
        // over full scale is hard-clipped by the int16 conversion further down, and loud masters do
        // land there on their own once resampling overshoot is added. It returns immediately and
        // leaves the samples untouched when nothing exceeds the ceiling.
        reduction = Limiter.Apply(loop, rate);
        if (intro is not null) reduction = Math.Max(reduction, Limiter.Apply(intro, rate));

        double seam = Seams.SeamMetric(loop, rate);
        double peak = Math.Max(loop.PeakDbfs(), intro?.PeakDbfs() ?? double.NegativeInfinity);
        double lufs = Loudness.MeasureLufs(loop, rate);
        return new Buffers(loop, intro, seam, peak, lufs, gain, plan.GainDb, reduction);
    }

    /// <summary>Full render: buffers -> WEM files in <paramref name="outDir"/>.</summary>
    public static RenderResult Render(PcmBuffer pcm, Slot slot, RenderPlan plan, string outDir, IStretcher stretcher)
    {
        var b = RenderBuffers(pcm, slot, plan, stretcher);
        int rate = slot.Loop.SampleRate;
        Directory.CreateDirectory(outDir);

        var loopPath = Path.Combine(outDir, $"{slot.Loop.WemId}.wem");
        WemWriter.WriteWem(loopPath, b.Loop.ToInt16(), b.Loop.Channels, rate, expectedFrames: slot.LoopFrames, loop: true);

        string? introPath = null;
        if (b.Intro is not null && slot.Intro is not null)
        {
            introPath = Path.Combine(outDir, $"{slot.Intro.WemId}.wem");
            WemWriter.WriteWem(introPath, b.Intro.ToInt16(), b.Intro.Channels, rate, expectedFrames: slot.IntroFrames, loop: false);
        }

        return new RenderResult(plan, introPath, loopPath, slot.IntroFrames, slot.LoopFrames, b.SeamMetric, b.PeakDbfs, b.Lufs,
                                b.GainDb, b.LimiterReductionDb);
    }
}
