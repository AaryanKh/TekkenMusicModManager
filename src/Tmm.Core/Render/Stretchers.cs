using System.ComponentModel;
using System.Diagnostics;
using Tmm.Core.Audio;

namespace Tmm.Core.Render;

/// <summary>Time-stretch by rho. rho &gt; 1 lengthens. Output length is approximate; the caller trims
/// to exact frames. Every external tool sits behind an interface — this is one of them.</summary>
public interface IStretcher
{
    string Name { get; }
    PcmBuffer Stretch(PcmBuffer pcm, double rho);
}

/// <summary>
/// Wraps the Rubber Band command-line tool (phase-locked, formant preservation off — music). Best
/// quality; needs <c>rubberband.exe</c> (R3 engine when available). Runs one process per call.
/// </summary>
public sealed class RubberBandCliStretcher : IStretcher
{
    private readonly string _exe;
    private readonly string _scratch;
    public string Name => "rubberband";

    public RubberBandCliStretcher(string exe, string scratch) { _exe = exe; _scratch = scratch; }

    public PcmBuffer Stretch(PcmBuffer pcm, double rho)
    {
        if (Math.Abs(rho - 1.0) < 1e-6) return pcm;
        Directory.CreateDirectory(_scratch);
        var id = Guid.NewGuid().ToString("N")[..8];
        var inPath = Path.Combine(_scratch, $"rb_{id}_in.wav");
        var outPath = Path.Combine(_scratch, $"rb_{id}_out.wav");
        try
        {
            WavIo.WriteFloat32(inPath, pcm);
            var psi = new ProcessStartInfo(_exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "--time", rho.ToString("R", System.Globalization.CultureInfo.InvariantCulture), "-q", inPath, outPath })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi) ?? throw new RenderException("could not start rubberband");
            var err = p.StandardError.ReadToEndAsync();
            p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0 || !File.Exists(outPath))
                throw new RenderException($"rubberband failed: {err.Result.Trim()}");
            return WavIo.Read(outPath);
        }
        catch (Win32Exception e)
        {
            throw new RenderException($"rubberband not found at '{_exe}': {e.Message}");
        }
        finally
        {
            try { File.Delete(inPath); File.Delete(outPath); } catch { /* scratch */ }
        }
    }
}

/// <summary>
/// Fallback with no external dependency: 4-point Hermite resampling. Changes pitch by the same
/// ratio as the tempo (up to ±6% ≈ one semitone at the cap), which is audible on melodic material.
/// Good enough to get a mod in-game; install rubberband for a pitch-preserving stretch.
/// </summary>
public sealed class ResampleStretcher : IStretcher
{
    public string Name => "resample (pitch shifts)";

    public PcmBuffer Stretch(PcmBuffer pcm, double rho)
    {
        if (Math.Abs(rho - 1.0) < 1e-6) return pcm;
        int inFrames = pcm.Frames, ch = pcm.Channels;
        int outFrames = (int)Math.Round(inFrames * rho);
        var o = new PcmBuffer(outFrames, ch, pcm.Rate);
        double step = (double)inFrames / outFrames;
        for (int i = 0; i < outFrames; i++)
        {
            double pos = i * step;
            int i1 = (int)pos; double t = pos - i1;
            int i0 = Math.Max(0, i1 - 1), i2 = Math.Min(inFrames - 1, i1 + 1), i3 = Math.Min(inFrames - 1, i1 + 2);
            i1 = Math.Min(inFrames - 1, i1);
            for (int c = 0; c < ch; c++)
            {
                double y0 = pcm[i0, c], y1 = pcm[i1, c], y2 = pcm[i2, c], y3 = pcm[i3, c];
                double c0 = y1, c1 = 0.5 * (y2 - y0), c2 = y0 - 2.5 * y1 + 2 * y2 - 0.5 * y3, c3 = 0.5 * (y3 - y0) + 1.5 * (y1 - y2);
                o[i, c] = (float)(((c3 * t + c2) * t + c1) * t + c0);
            }
        }
        return o;
    }
}

public static class StretcherFactory
{
    public static IStretcher FromSettings(Settings s)
    {
        if (!string.IsNullOrWhiteSpace(s.RubberBandPath) && File.Exists(s.RubberBandPath))
            return new RubberBandCliStretcher(s.RubberBandPath!, s.ScratchDir);
        return new ResampleStretcher();
    }
}
