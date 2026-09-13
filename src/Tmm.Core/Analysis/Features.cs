namespace Tmm.Core.Analysis;

/// <summary>
/// Frame-level features used for seam matching: chroma + MFCC, plus RMS envelope — and the onset
/// envelopes the beat tracker needs, since they all come from one STFT pass.
/// </summary>
public sealed class Features
{
    public const int ChromaBins = 12;
    public const int MfccCoeffs = 13;

    public double HopSec { get; }
    public int Frames { get; }
    /// <summary>(T, 12) row-major, each row L2-normalized.</summary>
    public float[] Chroma { get; }
    /// <summary>(T, 13) row-major, z-scored per coefficient across the song.</summary>
    public float[] Mfcc { get; }
    /// <summary>(T,) RMS of the time-domain frame.</summary>
    public float[] Rms { get; }
    /// <summary>(T,) spectral-flux onset strength, full band.</summary>
    public float[] Onset { get; }
    /// <summary>(T,) spectral-flux onset strength below ~200 Hz. Downbeat cue.</summary>
    public float[] BassOnset { get; }

    public Features(double hopSec, int frames, float[] chroma, float[] mfcc, float[] rms, float[] onset, float[] bassOnset)
    {
        HopSec = hopSec; Frames = frames; Chroma = chroma; Mfcc = mfcc; Rms = rms; Onset = onset; BassOnset = bassOnset;
    }

    public int FrameAt(double t) => (int)Math.Round(t / HopSec);

    public ReadOnlySpan<float> ChromaRow(int t) => Chroma.AsSpan(t * ChromaBins, ChromaBins);
    public ReadOnlySpan<float> MfccRow(int t) => Mfcc.AsSpan(t * MfccCoeffs, MfccCoeffs);
}

public static class FeatureExtractor
{
    public const int NFft = 2048;
    private const int MelBands = 26;
    private const double BassHz = 200;

    public static Features Extract(float[] mono, int rate, double hopSec = 0.0232)
    {
        int hop = Math.Max(1, (int)Math.Round(hopSec * rate));
        double actualHop = (double)hop / rate;
        int n = mono.Length;
        int frames = Math.Max(1, (n + hop - 1) / hop);
        int bins = NFft / 2 + 1;

        var window = Fft.Hann(NFft);
        var re = new double[NFft]; var im = new double[NFft];
        var mag = new float[bins]; var prevLog = new float[bins]; var curLog = new float[bins];
        var frameBuf = new float[NFft];

        var chroma = new float[frames * Features.ChromaBins];
        var mfcc = new float[frames * Features.MfccCoeffs];
        var rms = new float[frames];
        var onset = new float[frames];
        var bass = new float[frames];

        var binToPc = BinToPitchClass(rate, bins);
        var mel = MelFilterbank(rate, bins);
        var melEnergy = new double[MelBands];
        var dct = DctMatrix(Features.MfccCoeffs, MelBands);
        int bassBinLimit = (int)(BassHz * NFft / rate);

        for (int t = 0; t < frames; t++)
        {
            int center = t * hop;
            int start = center - NFft / 2;
            double sq = 0;
            for (int i = 0; i < NFft; i++)
            {
                int idx = start + i;
                float v = idx >= 0 && idx < n ? mono[idx] : 0f;
                frameBuf[i] = v; sq += v * v;
            }
            rms[t] = (float)Math.Sqrt(sq / NFft);

            Fft.Magnitude(frameBuf, window, re, im, mag);

            // onset: positive spectral flux on log-magnitude
            double flux = 0, bflux = 0;
            for (int k = 0; k < bins; k++)
            {
                curLog[k] = (float)Math.Log(1.0 + mag[k]);
                double d = curLog[k] - prevLog[k];
                if (d > 0) { flux += d; if (k <= bassBinLimit) bflux += d; }
            }
            onset[t] = t == 0 ? 0f : (float)flux;
            bass[t] = t == 0 ? 0f : (float)bflux;
            (prevLog, curLog) = (curLog, prevLog);

            // chroma
            var crow = chroma.AsSpan(t * Features.ChromaBins, Features.ChromaBins);
            for (int k = 1; k < bins; k++)
                if (binToPc[k] >= 0) crow[binToPc[k]] += mag[k];
            double norm = 0;
            foreach (var c in crow) norm += c * c;
            norm = Math.Sqrt(norm);
            if (norm > 1e-9) for (int i = 0; i < crow.Length; i++) crow[i] = (float)(crow[i] / norm);

            // mfcc
            for (int b = 0; b < MelBands; b++)
            {
                double e = 0;
                var (lo, hi, w) = mel[b];
                for (int k = lo; k < hi; k++) e += w[k - lo] * mag[k] * mag[k];
                melEnergy[b] = Math.Log(e + 1e-10);
            }
            var mrow = mfcc.AsSpan(t * Features.MfccCoeffs, Features.MfccCoeffs);
            for (int c = 0; c < Features.MfccCoeffs; c++)
            {
                double s = 0;
                for (int b = 0; b < MelBands; b++) s += dct[c * MelBands + b] * melEnergy[b];
                mrow[c] = (float)s;
            }
        }

        ZScoreColumns(mfcc, frames, Features.MfccCoeffs);
        return new Features(actualHop, frames, chroma, mfcc, rms, onset, bass);
    }

    /// <summary>
    /// Distance between the frames just before <paramref name="endSec"/> and just after
    /// <paramref name="startSec"/> — i.e. what the ear hears when the loop wraps. Lower is better.
    /// Raw, not comparable across songs.
    /// </summary>
    public static double SeamDistance(Features f, double startSec, double endSec, int window = 8)
    {
        int s = Math.Clamp(f.FrameAt(startSec), 0, f.Frames - 1);
        int e = Math.Clamp(f.FrameAt(endSec), 1, f.Frames);
        int wa = Math.Max(1, Math.Min(window, e));           // frames [e-wa, e)
        int wb = Math.Max(1, Math.Min(window, f.Frames - s)); // frames [s, s+wb)

        Span<double> ca = stackalloc double[Features.ChromaBins];
        Span<double> cb = stackalloc double[Features.ChromaBins];
        Span<double> ma = stackalloc double[Features.MfccCoeffs];
        Span<double> mb = stackalloc double[Features.MfccCoeffs];
        double ra = 0, rb = 0;

        for (int t = e - wa; t < e; t++)
        {
            var c = f.ChromaRow(t); var m = f.MfccRow(t);
            for (int i = 0; i < ca.Length; i++) ca[i] += c[i];
            for (int i = 0; i < ma.Length; i++) ma[i] += m[i];
            ra += f.Rms[t];
        }
        for (int t = s; t < s + wb; t++)
        {
            var c = f.ChromaRow(t); var m = f.MfccRow(t);
            for (int i = 0; i < cb.Length; i++) cb[i] += c[i];
            for (int i = 0; i < mb.Length; i++) mb[i] += m[i];
            rb += f.Rms[t];
        }
        double dc = 0, dm = 0;
        for (int i = 0; i < ca.Length; i++) { double d = ca[i] / wa - cb[i] / wb; dc += d * d; }
        for (int i = 0; i < ma.Length; i++) { double d = ma[i] / wa - mb[i] / wb; dm += d * d; }
        ra /= wa; rb /= wb;
        double dr = Math.Abs(Math.Log(ra + 1e-6) - Math.Log(rb + 1e-6));

        // Chroma (harmony), timbre (z-scored MFCC per coefficient), level: equal-ish weight.
        return Math.Sqrt(dc) + Math.Sqrt(dm / Features.MfccCoeffs) + 0.5 * dr;
    }

    // ---------------------------------------------------------------- helpers

    private static int[] BinToPitchClass(int rate, int bins)
    {
        var map = new int[bins];
        for (int k = 0; k < bins; k++)
        {
            double hz = (double)k * rate / NFft;
            if (hz < 55 || hz > 5000) { map[k] = -1; continue; }
            double midi = 69 + 12 * Math.Log2(hz / 440.0);
            map[k] = ((int)Math.Round(midi) % 12 + 12) % 12;
        }
        return map;
    }

    private static double HzToMel(double hz) => 2595 * Math.Log10(1 + hz / 700);
    private static double MelToHz(double mel) => 700 * (Math.Pow(10, mel / 2595) - 1);

    private static (int lo, int hi, double[] w)[] MelFilterbank(int rate, int bins)
    {
        double maxMel = HzToMel(rate / 2.0);
        var edges = new double[MelBands + 2];
        for (int i = 0; i < edges.Length; i++) edges[i] = MelToHz(maxMel * i / (MelBands + 1));
        var binHz = (double)rate / NFft;
        var fb = new (int, int, double[])[MelBands];
        for (int b = 0; b < MelBands; b++)
        {
            double l = edges[b], c = edges[b + 1], r = edges[b + 2];
            int lo = (int)Math.Floor(l / binHz), hi = Math.Min(bins, (int)Math.Ceiling(r / binHz) + 1);
            var w = new double[Math.Max(0, hi - lo)];
            for (int k = lo; k < hi; k++)
            {
                double hz = k * binHz;
                double v = hz <= c ? (hz - l) / Math.Max(c - l, 1e-9) : (r - hz) / Math.Max(r - c, 1e-9);
                w[k - lo] = Math.Max(0, v);
            }
            fb[b] = (lo, hi, w);
        }
        return fb;
    }

    private static double[] DctMatrix(int coeffs, int bands)
    {
        var m = new double[coeffs * bands];
        for (int c = 0; c < coeffs; c++)
            for (int b = 0; b < bands; b++)
                m[c * bands + b] = Math.Cos(Math.PI * c * (b + 0.5) / bands) * Math.Sqrt(2.0 / bands);
        return m;
    }

    private static void ZScoreColumns(float[] data, int rows, int cols)
    {
        for (int c = 0; c < cols; c++)
        {
            double mean = 0; for (int r = 0; r < rows; r++) mean += data[r * cols + c]; mean /= rows;
            double var = 0; for (int r = 0; r < rows; r++) { double d = data[r * cols + c] - mean; var += d * d; }
            double sd = Math.Sqrt(var / Math.Max(1, rows - 1));
            if (sd < 1e-9) sd = 1;
            for (int r = 0; r < rows; r++) data[r * cols + c] = (float)((data[r * cols + c] - mean) / sd);
        }
    }
}
