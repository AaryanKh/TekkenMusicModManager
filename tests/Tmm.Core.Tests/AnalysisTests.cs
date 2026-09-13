using Tmm.Core;
using Tmm.Core.Analysis;
using Tmm.Core.Audio;
using Tmm.Core.LoopFit;
using Tmm.Core.Render;
using Xunit;

namespace Tmm.Core.Tests;

/// <summary>Baseline analyzer + render pipeline on synthetic audio (no ffmpeg needed).</summary>
public class AnalysisTests
{
    private const int Rate = 48000;

    /// <summary>Kicks on every beat, accented downbeat, a sustained tone; 40 s at the given BPM.</summary>
    private static PcmBuffer ClickTrack(double bpm, double seconds = 40)
    {
        int n = (int)(seconds * Rate);
        var pcm = new PcmBuffer(n, 2, Rate);
        double beat = 60 / bpm;
        for (int k = 0; k * beat < seconds; k++)
        {
            int s = (int)(k * beat * Rate); int len = Math.Min(n - s, (int)(0.1 * Rate));
            float amp = k % 4 == 0 ? 0.6f : 0.3f;   // peaks near -3 dBFS so a loudness boost has headroom to hit the ceiling
            for (int i = 0; i < len; i++)
            {
                double t = (double)i / Rate;
                float v = (float)(amp * Math.Sin(2 * Math.PI * (70 + 100 * Math.Exp(-t * 40)) * t) * Math.Exp(-t * 30));
                pcm[s + i, 0] += v; pcm[s + i, 1] += v;
            }
        }
        for (int i = 0; i < n; i++)
        {
            float tone = (float)(0.08 * Math.Sin(2 * Math.PI * 220 * i / Rate));
            pcm[i, 0] += tone; pcm[i, 1] += tone * 0.9f;
        }
        return pcm;
    }

    [Theory]
    [InlineData(100.0)]
    [InlineData(128.0)]
    [InlineData(150.0)]
    public void BeatTrackerRecoversTempo(double bpm)
    {
        var pcm = ClickTrack(bpm);
        var feats = FeatureExtractor.Extract(pcm.ToMono(), Rate);
        var grid = new AutocorrelationBeatTracker().Track(feats, pcm.Seconds);
        Assert.InRange(grid.Bpm, bpm * 0.995, bpm * 1.005);
        Assert.True(grid.Confidence > 0.5, $"confidence {grid.Confidence}");
        // downbeats land on the accented kicks (every 4 beats from t=0)
        double bar = 4 * 60 / bpm;
        foreach (var d in grid.DownbeatsSec.Take(5))
            Assert.InRange(d % bar, -0.03, 0.03 + (Math.Abs(d % bar - bar) < 0.03 ? bar : 0));
    }

    [Fact]
    public void RenderIsSampleExactAndCrossfadePreservesLength()
    {
        var pcm = ClickTrack(120, 30);
        var feats = FeatureExtractor.Extract(pcm.ToMono(), Rate);
        var grid = new AutocorrelationBeatTracker().Track(feats, pcm.Seconds);
        var song = new Song("synthetic", "synthetic", pcm.Seconds, Rate, 2, "abc");
        var analysis = new SongAnalysis(song, grid, Structure.Segment(feats, grid, pcm.Seconds), feats.HopSec);
        var cands = Candidates.Generate(analysis, feats);
        Assert.NotEmpty(cands);

        // A slot whose loop is 8 bars at 120 BPM (16 s) plus 1.3% and a 2 s intro.
        int loopFrames = (int)(16.0 * 1.013 * Rate), introFrames = 2 * Rate;
        var id = new SlotIdentity(1, "Synthetic", "TEKKEN 8", 2, 1, 2, 16);
        var slot = new Slot(id, new WemInfo(2, introFrames, Rate, 2, 0xFFFF, 0), new WemInfo(1, loopFrames, Rate, 2, 0xFFFF, 0));

        var score = Scoring.ScoreSlot(slot, cands, song.DurationSec, new Weights());
        Assert.NotNull(score);
        var plan = PlanFactory.FromScore(slot, score!, analysis, targetLufs: null);
        var raw = RenderPipeline.RenderBuffers(pcm, slot, plan, new ResampleStretcher());
        Assert.Equal(loopFrames, raw.Loop.Frames);
        Assert.NotNull(raw.Intro);
        Assert.Equal(introFrames, raw.Intro!.Frames);

        // Loudness matching is a peak-safe gain: it moves toward the target and never above the ceiling,
        // so a peaky click track lands somewhere between its own level and -16.
        plan.TargetLufs = -16;
        var norm = RenderPipeline.RenderBuffers(pcm, slot, plan, new ResampleStretcher());
        Assert.Equal(loopFrames, norm.Loop.Frames);
        Assert.True(norm.Lufs > raw.Lufs + 1, $"{norm.Lufs} should be clearly louder than {raw.Lufs}");
        Assert.True(norm.Lufs <= -15.5, $"{norm.Lufs} overshoots the target");
        Assert.True(norm.PeakDbfs <= -0.9, $"peak {norm.PeakDbfs} above the -1 dBFS ceiling");
    }

    [Fact]
    public void ProvisionalSlotsCannotBeRendered()
    {
        var pcm = ClickTrack(120, 20);
        var id = new SlotIdentity(1, "Sheet only", "TEKKEN 8", null, 1, null, 10);
        var slot = Slot.Provisional(id);
        var plan = new RenderPlan { SlotKey = 1, LoopStartSec = 0, LoopBars = 5, Rho = 1.0 };
        Assert.Throws<CatalogNotBuiltException>(() => RenderPipeline.RenderBuffers(pcm, slot, plan, new ResampleStretcher()));
    }

    [Fact]
    public void LoudnessMeterIsInTheRightBallpark()
    {
        // A full-scale-ish 1 kHz sine at -20 dBFS peak measures close to -20 LUFS (K-weighting adds ~0 dB at 1 kHz... within a few LU).
        var pcm = new PcmBuffer(5 * Rate, 2, Rate);
        for (int i = 0; i < pcm.Frames; i++) { float v = (float)(0.1 * Math.Sin(2 * Math.PI * 1000 * i / Rate)); pcm[i, 0] = v; pcm[i, 1] = v; }
        double lufs = Loudness.MeasureLufs(pcm, Rate);
        Assert.InRange(lufs, -24, -19);
    }
}
