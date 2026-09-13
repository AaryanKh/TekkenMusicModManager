using Tmm.Core.Analysis;
using Tmm.Core.Audio;

namespace Tmm.Core.Render;

/// <summary>Seam handling and measurement for the loop half.</summary>
public static class Seams
{
    /// <summary>
    /// Equal-power crossfade so the wrap is continuous. Length is preserved. Skip (ms = 0) when the
    /// butt joint is already clean.
    ///
    /// The tail of the loop is faded out while the material that *naturally precedes the loop head*
    /// (<paramref name="preroll"/>, the last n frames before the loop start in the source) is faded
    /// in, so playback runs tail → (preroll blend) → head with no discontinuity. When no preroll is
    /// available (loop starts at 0) the head itself is blended into the tail, which softens the
    /// joint but does not make it continuous.
    /// </summary>
    public static PcmBuffer CrossfadeWrap(PcmBuffer loop, PcmBuffer? preroll, int rate, double ms)
    {
        int n = (int)(rate * ms / 1000);
        if (n <= 0 || loop.Frames < 2 * n) return loop;
        var src = preroll is not null && preroll.Frames >= n ? preroll.Slice(preroll.Frames - n, n) : loop.Slice(0, n);
        int tail = loop.Frames - n;
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / n;
            float gIn = (float)Math.Sin(t * Math.PI / 2), gOut = (float)Math.Cos(t * Math.PI / 2);
            for (int c = 0; c < loop.Channels; c++)
                loop[tail + i, c] = loop[tail + i, c] * gOut + src[i, c] * gIn;
        }
        return loop;
    }

    /// <summary>
    /// Discontinuity across the wrap: spectral + amplitude delta between the tail and head. Surfaced
    /// as a number in the preview; lower is better. 0 would be a perfectly continuous wrap.
    /// </summary>
    public static double SeamMetric(PcmBuffer loop, int rate)
    {
        int n = Math.Min(FeatureExtractor.NFft, loop.Frames / 2);
        if (n < 64) return 0;
        var mono = loop.ToMono();
        var head = mono.AsSpan(0, n);
        var tail = mono.AsSpan(mono.Length - n, n);

        // level delta (log RMS)
        double rh = Rms(head), rt = Rms(tail);
        double level = Math.Abs(Math.Log10(rh + 1e-6) - Math.Log10(rt + 1e-6));

        // spectral cosine distance
        int nfft = 1; while (nfft < n) nfft <<= 1;
        var w = Fft.Hann(n);
        var re = new double[nfft]; var im = new double[nfft];
        var mh = new float[nfft / 2 + 1]; var mt = new float[nfft / 2 + 1];
        Fft.Magnitude(head, PadWindow(w, nfft), re, im, mh);
        Fft.Magnitude(tail, PadWindow(w, nfft), re, im, mt);
        double dot = 0, nh = 0, nt = 0;
        for (int k = 0; k < mh.Length; k++) { dot += mh[k] * mt[k]; nh += mh[k] * mh[k]; nt += mt[k] * mt[k]; }
        double cosDist = nh > 0 && nt > 0 ? 1 - dot / Math.Sqrt(nh * nt) : 0;

        // sample-level jump at the wrap, relative to the local peak
        double jump = 0;
        for (int c = 0; c < loop.Channels; c++)
            jump = Math.Max(jump, Math.Abs(loop[0, c] - loop[loop.Frames - 1, c]));
        double peak = Math.Max(loop.PeakAbs(), 1e-6);

        return level + cosDist + jump / peak;
    }

    private static double Rms(ReadOnlySpan<float> x)
    {
        double s = 0; foreach (var v in x) s += v * v;
        return Math.Sqrt(s / Math.Max(1, x.Length));
    }

    private static double[] PadWindow(double[] w, int n)
    {
        if (w.Length == n) return w;
        var o = new double[n]; Array.Copy(w, o, w.Length); return o;
    }
}
