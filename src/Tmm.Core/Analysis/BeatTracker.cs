namespace Tmm.Core.Analysis;

/// <summary>
/// Tempo, beat grid, downbeats — the baseline C# analyzer.
///
/// Autocorrelation of a spectral-flux onset envelope with a log-normal tempo prior (the same shape
/// librosa uses), parabolic peak refinement, then a fixed-period grid phase-locked to the onsets.
/// Downbeats: the beat phase (0..3) whose beats carry the most bass onset energy.
///
/// Limits, so nobody is surprised: constant tempo only (no drift tracking), 4/4 assumed, and the
/// downbeat cue is a heuristic. <see cref="BeatGrid.Confidence"/> below ~0.5 should route the user
/// to the manual loop editor rather than silently producing a bad grid. Rubato, live and free-tempo
/// material lands there. Swap in a better tracker behind <see cref="IBeatTracker"/> when it earns it.
/// </summary>
public interface IBeatTracker
{
    BeatGrid Track(Features f, double durationSec);
}

public sealed class AutocorrelationBeatTracker : IBeatTracker
{
    public double MinBpm { get; init; } = 60;
    public double MaxBpm { get; init; } = 200;
    public double PriorBpm { get; init; } = 120;
    public double PriorOctaves { get; init; } = 1.0;   // std-dev of the log2 prior
    public int BeatsPerBar { get; init; } = 4;

    public BeatGrid Track(Features f, double durationSec)
    {
        double hop = f.HopSec;
        int T = f.Frames;
        var o = Whiten(f.Onset, (int)Math.Round(0.5 / hop));
        var bo = Whiten(f.BassOnset, (int)Math.Round(0.5 / hop));

        int minLag = Math.Max(2, (int)Math.Floor(60.0 / MaxBpm / hop));
        int maxLag = Math.Min(T / 2, (int)Math.Ceiling(60.0 / MinBpm / hop));
        if (maxLag <= minLag + 2 || T < 4 * minLag)
            return Fallback(durationSec, 0.0);

        // normalized autocorrelation
        double ac0 = 0; for (int t = 0; t < T; t++) ac0 += o[t] * o[t];
        if (ac0 < 1e-12) return Fallback(durationSec, 0.0);
        var ac = new double[maxLag + 1];
        for (int lag = minLag; lag <= maxLag; lag++)
        {
            double s = 0;
            for (int t = 0; t + lag < T; t++) s += o[t] * o[t + lag];
            ac[lag] = s / ac0;
        }

        // prior-weighted peak pick
        int best = -1; double bestScore = double.NegativeInfinity;
        double sum = 0, cnt = 0;
        for (int lag = minLag; lag <= maxLag; lag++)
        {
            double bpm = 60.0 / (lag * hop);
            double z = Math.Log2(bpm / PriorBpm) / PriorOctaves;
            double prior = Math.Exp(-0.5 * z * z);
            double score = ac[lag] * prior;
            sum += ac[lag]; cnt++;
            bool isPeak = ac[lag] >= ac[Math.Max(minLag, lag - 1)] && ac[lag] >= ac[Math.Min(maxLag, lag + 1)];
            if (isPeak && score > bestScore) { bestScore = score; best = lag; }
        }
        if (best < 0) return Fallback(durationSec, 0.0);

        // parabolic refinement of the lag
        double lagF = best;
        if (best > minLag && best < maxLag)
        {
            double a = ac[best - 1], b = ac[best], c = ac[best + 1];
            double denom = a - 2 * b + c;
            if (Math.Abs(denom) > 1e-12) lagF = best + 0.5 * (a - c) / denom;
        }
        double periodSec = lagF * hop;
        double bpm0 = 60.0 / periodSec;
        double meanAc = cnt > 0 ? sum / cnt : 0;
        double confidence = Math.Clamp((ac[best] - meanAc) / Math.Max(1e-9, 1 - meanAc), 0, 1);

        // Joint (period, phase) refinement: the autocorrelation lag is quantized to the hop (~23 ms,
        // i.e. up to ±2% BPM at 120), which over a 16-bar loop is a 0.5 s drift. Sweep ±3% around the
        // estimate with a fine period step and pick the grid whose beats land on the most onset energy.
        int steps = 48, periodSteps = 121;
        double bestPhase = 0, bestPeriod = periodSec, bestE = double.NegativeInfinity;
        for (int ps = 0; ps < periodSteps; ps++)
        {
            double period = periodSec * (1 + 0.03 * (2.0 * ps / (periodSteps - 1) - 1));
            for (int i = 0; i < steps; i++)
            {
                double phi = period * i / steps;
                double e = 0; int cnt2 = 0;
                for (double t = phi; t < durationSec; t += period) { e += Sample(o, t / hop); cnt2++; }
                e /= Math.Max(1, cnt2);
                if (e > bestE) { bestE = e; bestPhase = phi; bestPeriod = period; }
            }
        }
        periodSec = bestPeriod;
        bpm0 = 60.0 / periodSec;

        var beats = new List<double>();
        for (double t = bestPhase; t < durationSec; t += periodSec) beats.Add(t);
        if (beats.Count == 0) return Fallback(durationSec, confidence);

        // downbeat phase: which of the 4 beat classes carries the most (bass-weighted) onset energy
        int bestK = 0; double bestD = double.NegativeInfinity;
        for (int k = 0; k < BeatsPerBar; k++)
        {
            double e = 0;
            for (int i = k; i < beats.Count; i += BeatsPerBar)
                e += Sample(o, beats[i] / hop) + 2.0 * Sample(bo, beats[i] / hop);
            if (e > bestD) { bestD = e; bestK = k; }
        }
        var downbeats = new List<double>();
        for (int i = bestK; i < beats.Count; i += BeatsPerBar) downbeats.Add(beats[i]);

        return new BeatGrid(bpm0, beats.ToArray(), downbeats.ToArray(), BeatsPerBar, confidence);
    }

    private static BeatGrid Fallback(double durationSec, double confidence)
    {
        // A 120 BPM grid from t=0 so downstream code always has *something*; confidence says it's fake.
        double period = 0.5;
        var beats = new List<double>(); for (double t = 0; t < durationSec; t += period) beats.Add(t);
        var down = new List<double>(); for (int i = 0; i < beats.Count; i += 4) down.Add(beats[i]);
        return new BeatGrid(120, beats.ToArray(), down.ToArray(), 4, confidence);
    }

    /// <summary>Subtract a moving average and half-wave rectify, then scale to unit max.</summary>
    private static double[] Whiten(float[] x, int halfWin)
    {
        int n = x.Length; var y = new double[n];
        halfWin = Math.Max(1, halfWin);
        var prefix = new double[n + 1];
        for (int i = 0; i < n; i++) prefix[i + 1] = prefix[i] + x[i];
        double max = 0;
        for (int i = 0; i < n; i++)
        {
            int a = Math.Max(0, i - halfWin), b = Math.Min(n, i + halfWin + 1);
            double mean = (prefix[b] - prefix[a]) / (b - a);
            y[i] = Math.Max(0, x[i] - mean);
            if (y[i] > max) max = y[i];
        }
        if (max > 0) for (int i = 0; i < n; i++) y[i] /= max;
        return y;
    }

    private static double Sample(double[] x, double idx)
    {
        if (idx < 0 || idx >= x.Length - 1) return idx >= 0 && idx < x.Length ? x[(int)idx] : 0;
        int i = (int)idx; double fr = idx - i;
        return x[i] * (1 - fr) + x[i + 1] * fr;
    }
}
