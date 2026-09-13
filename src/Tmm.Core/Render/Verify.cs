using Tmm.Core.Audio;

namespace Tmm.Core.Render;

/// <summary>Hard gates before anything is written. LengthMismatch here is a bug, not a warning.</summary>
public static class Verify
{
    public static void AssertExact(PcmBuffer pcm, int frames, string what)
    {
        if (pcm.Frames != frames)
            throw new LengthMismatchException($"{what}: {pcm.Frames} frames, need {frames} (delta {pcm.Frames - frames:+#;-#;0})");
    }

    /// <summary>3x concatenated loop for the preview player so seams are audible.</summary>
    public static PcmBuffer PreviewTriple(PcmBuffer loop) => PcmBuffer.Concat(loop, loop, loop);

    /// <summary>Trim or zero-pad to exactly <paramref name="frames"/>. Only for the few-frame slop a
    /// time-stretcher leaves; anything larger is a planning error and throws.</summary>
    public static PcmBuffer FitToFrames(PcmBuffer pcm, int frames, int tolerance, string what)
    {
        int delta = pcm.Frames - frames;
        if (Math.Abs(delta) > tolerance)
            throw new RenderException($"{what}: stretcher returned {pcm.Frames} frames for a {frames}-frame target (delta {delta}); beyond the {tolerance}-frame trim tolerance");
        if (delta == 0) return pcm;
        if (delta > 0) return pcm.Slice(0, frames);
        var o = new PcmBuffer(frames, pcm.Channels, pcm.Rate);
        Array.Copy(pcm.Data, o.Data, pcm.Data.Length);
        return o;
    }
}
