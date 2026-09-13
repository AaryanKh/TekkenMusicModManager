using Tmm.Core.Audio;

namespace Tmm.Core.Render;

/// <summary>Construct the intro half. Real material where it exists; fade-in from silence otherwise.</summary>
public static class Intro
{
    public static PcmBuffer? BuildIntro(PcmBuffer pcm, int rate, RenderPlan plan, int frames)
    {
        if (plan.IntroStrategy == IntroStrategy.None || frames <= 0) return null;
        int loopStart = (int)Math.Round(plan.LoopStartSec * rate);

        switch (plan.IntroStrategy)
        {
            case IntroStrategy.Real:
                if (loopStart < frames)
                    throw new RenderException($"intro strategy 'Real' needs {frames / (double)rate:0.00}s before the loop start; only {loopStart / (double)rate:0.00}s available. Use FadeIn or move the loop start.");
                return pcm.Slice(loopStart - frames, frames);

            case IntroStrategy.Detached:
            {
                // The intro is cut from wherever the user pointed, with no relationship to the loop
                // start. That is the whole point: keep the song's own opening, loop the chorus.
                if (plan.IntroStartSec is not double startSec)
                    throw new RenderException("intro strategy 'Detached' needs IntroStartSec on the plan.");
                int st = (int)Math.Round(startSec * rate);
                if (st < 0)
                    throw new RenderException($"detached intro starts at {startSec:0.00}s, before the song does.");
                if (st + frames > pcm.Frames)
                    throw new RenderException($"detached intro [{startSec:0.00}s + {frames / (double)rate:0.00}s] runs past the end of the song ({pcm.Seconds:0.00}s). Move it earlier.");
                return pcm.Slice(st, frames);
            }

            case IntroStrategy.Silence:
                return new PcmBuffer(frames, pcm.Channels, rate);

            case IntroStrategy.FadeIn:
            default:
            {
                // whatever lead-in exists, left-padded with silence, faded
                var have = pcm.Slice(0, Math.Min(loopStart, pcm.Frames));
                PcmBuffer seg;
                if (have.Frames >= frames) seg = have.Slice(have.Frames - frames, frames);
                else
                {
                    var pad = new PcmBuffer(frames - have.Frames, pcm.Channels, rate);
                    seg = PcmBuffer.Concat(pad, have);
                }
                int ramp = Math.Min(seg.Frames, rate / 2);
                for (int i = 0; i < ramp; i++)
                {
                    float g = ramp <= 1 ? 1f : (float)i / (ramp - 1);
                    for (int c = 0; c < seg.Channels; c++) seg[i, c] *= g;
                }
                if (seg.Frames != frames) throw new LengthMismatchException($"intro: {seg.Frames} frames, need {frames}");
                return seg;
            }
        }
    }
}
