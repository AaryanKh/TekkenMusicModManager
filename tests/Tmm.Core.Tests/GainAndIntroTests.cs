using Tmm.Core;
using Tmm.Core.Audio;
using Tmm.Core.Render;
using Xunit;

namespace Tmm.Core.Tests;

/// <summary>Volume trim + limiter, the detached intro, and the assembled in-game track.</summary>
public class GainAndIntroTests
{
    private const int Rate = 48000;

    /// <summary>A steady tone at a known level, loud enough that a boost has to be limited.</summary>
    private static PcmBuffer Tone(double seconds, float amp = 0.5f, double hz = 220)
    {
        var pcm = new PcmBuffer((int)(seconds * Rate), 2, Rate);
        for (int i = 0; i < pcm.Frames; i++)
        {
            float v = (float)(amp * Math.Sin(2 * Math.PI * hz * i / Rate));
            pcm[i, 0] = v; pcm[i, 1] = v;
        }
        return pcm;
    }

    /// <summary>Loop-only slot, so the tests do not have to satisfy the intro rules unless they mean to.</summary>
    private static Slot LoopOnlySlot(double loopSec) =>
        new(new SlotIdentity(1, "Synthetic", "TEKKEN 8", null, 1, null, (int)loopSec),
            null, new WemInfo(1, (int)(loopSec * Rate), Rate, 2, 0xFFFF, 0));

    private static Slot SlotWithIntro(double loopSec, double introSec) =>
        new(new SlotIdentity(1, "Synthetic", "TEKKEN 8", 2, 1, (int)introSec, (int)loopSec),
            new WemInfo(2, (int)(introSec * Rate), Rate, 2, 0xFFFF, 0),
            new WemInfo(1, (int)(loopSec * Rate), Rate, 2, 0xFFFF, 0));

    // ------------------------------------------------------------------ limiter

    [Fact]
    public void LimiterKeepsThePeakUnderTheCeilingAndKeepsTheLength()
    {
        var pcm = Tone(2, amp: 0.95f);
        pcm.Scale(2.0f);                     // deliberately over full scale
        int before = pcm.Frames;
        double reduction = Limiter.Apply(pcm, Rate);
        Assert.Equal(before, pcm.Frames);    // the length gate depends on this
        Assert.True(reduction > 0, "a clipping signal should report gain reduction");
        Assert.True(pcm.PeakDbfs() <= -0.9, $"peak {pcm.PeakDbfs():0.00} dBFS is above the -1 dBFS ceiling");
    }

    [Fact]
    public void LimiterLeavesQuietAudioCompletelyAlone()
    {
        var pcm = Tone(1, amp: 0.2f);
        var before = (float[])pcm.Data.Clone();
        double reduction = Limiter.Apply(pcm, Rate);
        Assert.Equal(0, reduction);
        Assert.Equal(before, pcm.Data);
    }

    // ------------------------------------------------------------------ volume trim

    [Fact]
    public void PositiveGainRaisesLoudnessWithoutBreakingTheCeilingOrTheLength()
    {
        var pcm = Tone(6, amp: 0.25f);
        var slot = LoopOnlySlot(4);
        var plan = new RenderPlan { SlotKey = 1, LoopStartSec = 0, LoopBars = 1, Rho = 1.0 };

        var flat = RenderPipeline.RenderBuffers(pcm, slot, plan, new ResampleStretcher());
        plan.GainDb = 6;
        var loud = RenderPipeline.RenderBuffers(pcm, slot, plan, new ResampleStretcher());

        Assert.Equal(slot.LoopFrames, loud.Loop.Frames);
        Assert.True(loud.Lufs > flat.Lufs + 4, $"{loud.Lufs:0.0} LUFS should be well above {flat.Lufs:0.0}");
        Assert.True(loud.PeakDbfs <= -0.9, $"peak {loud.PeakDbfs:0.00} dBFS broke the ceiling");
        Assert.Equal(6, loud.GainDb);
    }

    [Fact]
    public void NegativeGainQuietensTheTrack()
    {
        var pcm = Tone(6, amp: 0.25f);
        var slot = LoopOnlySlot(4);
        var plan = new RenderPlan { SlotKey = 1, LoopStartSec = 0, LoopBars = 1, Rho = 1.0 };
        var flat = RenderPipeline.RenderBuffers(pcm, slot, plan, new ResampleStretcher());
        plan.GainDb = -6;
        var quiet = RenderPipeline.RenderBuffers(pcm, slot, plan, new ResampleStretcher());
        Assert.True(quiet.Lufs < flat.Lufs - 4, $"{quiet.Lufs:0.0} LUFS should be well below {flat.Lufs:0.0}");
    }

    [Fact]
    public void TrimAppliesEquallyToIntroAndLoopSoTheHandoverDoesNotStep()
    {
        var pcm = Tone(20, amp: 0.2f);
        var slot = SlotWithIntro(loopSec: 4, introSec: 2);
        var plan = new RenderPlan
        {
            SlotKey = 1, LoopStartSec = 8, LoopBars = 1, Rho = 1.0,
            IntroStrategy = IntroStrategy.Real, GainDb = 6,
            // No wrap crossfade: on a pure tone it sums in phase and lifts the loop's peak by ~3 dB,
            // which would mask the thing under test.
            CrossfadeMs = 0,
        };
        var b = RenderPipeline.RenderBuffers(pcm, slot, plan, new ResampleStretcher());
        Assert.NotNull(b.Intro);
        // Same source level on both sides, so after the same gain their peaks should match closely.
        Assert.True(Math.Abs(b.Loop.PeakAbs() - b.Intro!.PeakAbs()) < 0.01,
            $"loop peak {b.Loop.PeakAbs():0.0000} vs intro peak {b.Intro.PeakAbs():0.0000}");
    }

    // ------------------------------------------------------------------ detached intro

    [Fact]
    public void DetachedIntroComesFromItsOwnPositionNotFromBeforeTheLoop()
    {
        // Two halves at different levels: the intro should carry the level of where it was cut from.
        var pcm = new PcmBuffer(20 * Rate, 2, Rate);
        for (int i = 0; i < pcm.Frames; i++)
        {
            float amp = i < 5 * Rate ? 0.5f : 0.1f;     // loud opening, quiet remainder
            float v = (float)(amp * Math.Sin(2 * Math.PI * 220 * i / Rate));
            pcm[i, 0] = v; pcm[i, 1] = v;
        }
        var slot = SlotWithIntro(loopSec: 4, introSec: 2);
        var plan = new RenderPlan
        {
            SlotKey = 1, LoopStartSec = 12, LoopBars = 1, Rho = 1.0,
            IntroStrategy = IntroStrategy.Detached, IntroStartSec = 0,
        };
        var b = RenderPipeline.RenderBuffers(pcm, slot, plan, new ResampleStretcher());

        Assert.NotNull(b.Intro);
        Assert.Equal(slot.IntroFrames, b.Intro!.Frames);
        // Cut from the loud opening, not from the quiet material just before the loop start.
        Assert.True(b.Intro.PeakAbs() > 0.4f, $"intro peak {b.Intro.PeakAbs():0.000} is not the opening's level");
        Assert.True(b.Loop.PeakAbs() < 0.2f, $"loop peak {b.Loop.PeakAbs():0.000} is not the later material");
    }

    [Fact]
    public void DraggingADetachedIntroChangesOnlyTheIntro()
    {
        // What the waveform does when the user grabs the amber band: move IntroStartSec and nothing
        // else. The loop must come back byte-identical, or dragging the intro would silently
        // re-render the loop too.
        var pcm = new PcmBuffer(20 * Rate, 2, Rate);
        for (int i = 0; i < pcm.Frames; i++)
        {
            float v = (float)(0.3 * Math.Sin(2 * Math.PI * (200 + i / (double)Rate * 20) * i / Rate));
            pcm[i, 0] = v; pcm[i, 1] = v;
        }
        var slot = SlotWithIntro(loopSec: 4, introSec: 2);
        var plan = new RenderPlan
        {
            SlotKey = 1, LoopStartSec = 12, LoopBars = 1, Rho = 1.0,
            IntroStrategy = IntroStrategy.Detached, IntroStartSec = 0,
        };
        var before = RenderPipeline.RenderBuffers(pcm, slot, plan, new ResampleStretcher());

        plan.IntroStartSec = 6;                       // the drag
        var after = RenderPipeline.RenderBuffers(pcm, slot, plan, new ResampleStretcher());

        Assert.Equal(before.Loop.Data, after.Loop.Data);
        Assert.NotEqual(before.Intro!.Data, after.Intro!.Data);
        Assert.Equal(slot.IntroFrames, after.Intro.Frames);
        // And it really is the material at 6 s.
        Assert.Equal(pcm.Slice(6 * Rate, slot.IntroFrames).Data, after.Intro.Data);
    }

    [Fact]
    public void DetachedIntroRunningPastTheEndIsRejected()
    {
        var pcm = Tone(10);
        var slot = SlotWithIntro(loopSec: 2, introSec: 2);
        var plan = new RenderPlan
        {
            SlotKey = 1, LoopStartSec = 0, LoopBars = 1, Rho = 1.0,
            IntroStrategy = IntroStrategy.Detached, IntroStartSec = 9.5,
        };
        Assert.Throws<RenderException>(() => RenderPipeline.RenderBuffers(pcm, slot, plan, new ResampleStretcher()));
    }

    [Fact]
    public void DetachedIntroWithoutAPositionIsRejected()
    {
        var pcm = Tone(10);
        var slot = SlotWithIntro(loopSec: 2, introSec: 2);
        var plan = new RenderPlan { SlotKey = 1, LoopStartSec = 0, LoopBars = 1, Rho = 1.0, IntroStrategy = IntroStrategy.Detached };
        Assert.Throws<RenderException>(() => RenderPipeline.RenderBuffers(pcm, slot, plan, new ResampleStretcher()));
    }

    [Fact]
    public void DetachedIntroSurvivesAPlanRoundTrip()
    {
        var plan = new RenderPlan { IntroStrategy = IntroStrategy.Detached, IntroStartSec = 3.5, GainDb = -2.5 };
        var copy = plan.Clone();
        Assert.Equal(IntroStrategy.Detached, copy.IntroStrategy);
        Assert.Equal(3.5, copy.IntroStartSec);
        Assert.Equal(-2.5, copy.GainDb);
    }

    // ------------------------------------------------------------------ full-track preview

    [Fact]
    public void AssembledTrackIsIntroThenTheLoopRepeated()
    {
        var intro = Tone(2, amp: 0.3f);
        var loop = Tone(4, amp: 0.3f);
        var (audio, sections) = TrackPreview.Assemble(intro, loop, Rate, repeats: 3);

        Assert.Equal(intro.Frames + 3 * loop.Frames, audio.Frames);
        Assert.Equal(4, sections.Count);
        Assert.Equal("Intro", sections[0].Label);
        Assert.Equal(0, sections[0].StartSec);
        Assert.Equal(intro.Seconds, sections[1].StartSec, 6);
        Assert.Equal(intro.Seconds + loop.Seconds, sections[2].StartSec, 6);
    }

    [Fact]
    public void AssembledTrackWorksWithoutAnIntroSlot()
    {
        var loop = Tone(3, amp: 0.3f);
        var (audio, sections) = TrackPreview.Assemble(null, loop, Rate, repeats: 2);
        Assert.Equal(2 * loop.Frames, audio.Frames);
        Assert.All(sections, s => Assert.StartsWith("Loop", s.Label));
    }

    [Fact]
    public void AssemblyNeverMutatesTheRenderedBuffers()
    {
        var loop = Tone(3, amp: 0.3f);
        var before = (float[])loop.Data.Clone();
        TrackPreview.Assemble(null, loop, Rate, repeats: 2);   // fades the copy, not the source
        Assert.Equal(before, loop.Data);
    }

    [Fact]
    public void TheFirstLoopWrapIsTheThirdSectionWithAnIntroAndTheSecondWithout()
    {
        // The editor's "Skip to the seam" picks a section by index, so the shape of this list is load
        // bearing: with an intro the first boundary is the intro handover, and the wrap is the next
        // one along. Off by one here would drop the user on the wrong join.
        var intro = Tone(2, amp: 0.3f);
        var loop = Tone(4, amp: 0.3f);

        var (_, withIntro) = TrackPreview.Assemble(intro, loop, Rate, repeats: 3);
        Assert.Equal("Intro", withIntro[0].Label);
        Assert.Equal("Loop 1", withIntro[1].Label);
        Assert.Equal("Loop 2", withIntro[2].Label);
        Assert.Equal(intro.Seconds + loop.Seconds, withIntro[2].StartSec, 6);

        var (_, noIntro) = TrackPreview.Assemble(null, loop, Rate, repeats: 3);
        Assert.Equal("Loop 1", noIntro[0].Label);
        Assert.Equal("Loop 2", noIntro[1].Label);
        Assert.Equal(loop.Seconds, noIntro[1].StartSec, 6);
    }

    [Fact]
    public void RepeatCountAlwaysExercisesTheWrapPoint()
    {
        // Even a loop longer than the preview window repeats twice, otherwise the seam never plays.
        Assert.Equal(2, TrackPreview.RepeatsFor(introSec: 0, loopSec: 300));
        Assert.True(TrackPreview.RepeatsFor(introSec: 0, loopSec: 10) >= 9);
    }
}
