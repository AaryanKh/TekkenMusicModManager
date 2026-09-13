using Tmm.Core.Audio;

namespace Tmm.Core.Render;

/// <summary>
/// Match integrated LUFS to the game's own tracks. T7 modders repeatedly shipped music that was too
/// quiet; this is the fix. Integrated loudness per ITU-R BS.1770-4 (K-weighting, 400 ms blocks,
/// 75% overlap, absolute -70 LUFS and relative -10 LU gates).
///
/// Normalization is a peak-safe gain, not a true-peak limiter: if hitting the target would push
/// the sample peak above <c>ceilingDbfs</c> the gain is reduced so it does not. The result then
/// lands below the target and the render result reports the LUFS actually achieved.
/// </summary>
public static class Loudness
{
    public static double MeasureLufs(PcmBuffer pcm, int rate)
    {
        int ch = pcm.Channels;
        int block = (int)(0.4 * rate), hop = block / 4;
        if (pcm.Frames < block) return double.NegativeInfinity;

        // K-weighting: pre-filter (high shelf) + RLB high-pass, coefficients for the sample rate.
        var (b1, a1) = HighShelf(rate);
        var (b2, a2) = HighPass(rate);

        var filtered = new double[ch][];
        for (int c = 0; c < ch; c++)
        {
            var x = new double[pcm.Frames];
            for (int i = 0; i < pcm.Frames; i++) x[i] = pcm[i, c];
            Biquad(x, b1, a1); Biquad(x, b2, a2);
            filtered[c] = x;
        }

        var blocks = new List<double>();
        for (int start = 0; start + block <= pcm.Frames; start += hop)
        {
            double sum = 0;
            for (int c = 0; c < ch; c++)
            {
                double s = 0; var x = filtered[c];
                for (int i = start; i < start + block; i++) s += x[i] * x[i];
                sum += s / block;   // channel weights are 1.0 for L/R
            }
            blocks.Add(-0.691 + 10 * Math.Log10(sum + 1e-20));
        }

        var gated = blocks.Where(l => l > -70).ToList();
        if (gated.Count == 0) return double.NegativeInfinity;
        double rel = MeanPower(gated) - 10;
        var gated2 = gated.Where(l => l > rel).ToList();
        if (gated2.Count == 0) return double.NegativeInfinity;
        return MeanPower(gated2);
    }

    private static double MeanPower(List<double> lufs)
    {
        double s = 0; foreach (var l in lufs) s += Math.Pow(10, (l + 0.691) / 10);
        return -0.691 + 10 * Math.Log10(s / lufs.Count);
    }

    /// <summary>Returns the gain (linear) applied. Modifies <paramref name="pcm"/> in place.</summary>
    public static double Normalize(PcmBuffer pcm, int rate, double targetLufs, double ceilingDbfs = -1.0)
    {
        double measured = MeasureLufs(pcm, rate);
        if (double.IsNegativeInfinity(measured)) return 1.0;
        double gainDb = targetLufs - measured;
        double gain = Math.Pow(10, gainDb / 20);
        double peak = pcm.PeakAbs();
        double ceiling = Math.Pow(10, ceilingDbfs / 20);
        // Boosting: never push the sample peak over the ceiling, and never *attenuate* a track that
        // merely started out peaky — that would move loudness away from the target. Attenuating
        // already heads in the safe direction, so it is applied as-is.
        if (gain > 1 && peak * gain > ceiling) gain = Math.Max(1.0, ceiling / peak);
        if (Math.Abs(gain - 1) < 1e-9) return 1.0;
        pcm.Scale((float)gain);
        return gain;
    }

    // BS.1770 filter stages, redesigned for the given sample rate (the spec's coefficients are for
    // 48 kHz; these formulas reproduce them at 48k and generalize).
    private static (double[] b, double[] a) HighShelf(int rate)
    {
        double f0 = 1681.974450955533, G = 3.999843853973347, Q = 0.7071752369554196;
        double K = Math.Tan(Math.PI * f0 / rate);
        double Vh = Math.Pow(10, G / 20), Vb = Math.Pow(Vh, 0.4996667741545416);
        double a0 = 1 + K / Q + K * K;
        double[] b = { (Vh + Vb * K / Q + K * K) / a0, 2 * (K * K - Vh) / a0, (Vh - Vb * K / Q + K * K) / a0 };
        double[] a = { 1, 2 * (K * K - 1) / a0, (1 - K / Q + K * K) / a0 };
        return (b, a);
    }

    private static (double[] b, double[] a) HighPass(int rate)
    {
        double f0 = 38.13547087602444, Q = 0.5003270373238773;
        double K = Math.Tan(Math.PI * f0 / rate);
        double a0 = 1 + K / Q + K * K;
        // The spec's RLB stage is published with b = {1, -2, 1} (unit high-frequency gain); the
        // denominator below reproduces its a1/a2 at 48 kHz to 1e-12.
        double[] b = { 1, -2, 1 };
        double[] a = { 1, 2 * (K * K - 1) / a0, (1 - K / Q + K * K) / a0 };
        return (b, a);
    }

    private static void Biquad(double[] x, double[] b, double[] a)
    {
        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double x0 = x[i];
            double y0 = b[0] * x0 + b[1] * x1 + b[2] * x2 - a[1] * y1 - a[2] * y2;
            x2 = x1; x1 = x0; y2 = y1; y1 = y0;
            x[i] = y0;
        }
    }
}
