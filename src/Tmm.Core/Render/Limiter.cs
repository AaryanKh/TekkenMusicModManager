using Tmm.Core.Audio;

namespace Tmm.Core.Render;

/// <summary>
/// Look-ahead peak limiter. This is what makes <see cref="RenderPlan.GainDb"/> useful: most masters
/// already peak near 0 dBFS, so a plain gain would just clip. The limiter pulls the level down only
/// around the peaks that would overshoot, so the quiet-mod-next-to-a-loud-one case can be fixed by
/// asking for more gain rather than by re-mastering the source.
///
/// Gain reduction is computed ahead of each peak (sliding minimum over the look-ahead window) and
/// smoothed, so the level is already down when the transient arrives. The audio is never delayed,
/// so the sample-exact length gate is unaffected.
/// </summary>
public static class Limiter
{
    public const double DefaultCeilingDbfs = -1.0;
    public const double DefaultLookaheadMs = 5.0;
    public const double DefaultReleaseMs = 80.0;

    /// <summary>
    /// Limit <paramref name="pcm"/> in place. Returns the worst gain reduction applied, in dB as a
    /// positive number (0 means nothing exceeded the ceiling and the buffer was not touched).
    /// </summary>
    public static double Apply(PcmBuffer pcm, int rate, double ceilingDbfs = DefaultCeilingDbfs,
                               double lookaheadMs = DefaultLookaheadMs, double releaseMs = DefaultReleaseMs)
    {
        if (pcm.Frames == 0) return 0;
        double ceiling = Math.Pow(10, ceilingDbfs / 20);
        if (pcm.PeakAbs() <= ceiling) return 0;   // nothing to do; leave the samples bit-identical

        int n = pcm.Frames, ch = pcm.Channels;
        int look = Math.Max(1, (int)Math.Round(rate * lookaheadMs / 1000));

        // Per-frame required gain: 1 where we are under the ceiling.
        var need = new double[n];
        for (int i = 0; i < n; i++)
        {
            float p = 0;
            for (int c = 0; c < ch; c++) { float a = Math.Abs(pcm.Data[i * ch + c]); if (a > p) p = a; }
            need[i] = p > ceiling ? ceiling / p : 1.0;
        }

        // Sliding minimum over [i, i + look] via a monotonic deque, so the gain is already down
        // before a peak arrives rather than reacting to it after the fact.
        var target = new double[n];
        var dq = new int[n];
        int head = 0, tail = 0;   // [head, tail)
        for (int i = n - 1; i >= 0; i--)
        {
            while (tail > head && need[dq[tail - 1]] >= need[i]) tail--;
            dq[tail++] = i;
            int limit = i + look;
            while (dq[head] > limit) head++;
            target[i] = need[dq[head]];
        }

        // Smooth the envelope. Attack is fast (it only has to cover the look-ahead window); release
        // is slow enough that the gain does not pump between adjacent transients.
        double atkCoef = Math.Exp(-1.0 / Math.Max(1, look));
        double relCoef = Math.Exp(-1.0 / Math.Max(1, rate * releaseMs / 1000));
        double env = 1.0, worst = 1.0;
        for (int i = 0; i < n; i++)
        {
            double t = target[i];
            double coef = t < env ? atkCoef : relCoef;
            env = t + (env - t) * coef;
            if (env > 1) env = 1;
            if (env < worst) worst = env;
            for (int c = 0; c < ch; c++) pcm.Data[i * ch + c] *= (float)env;
        }

        // The smoothing can leave a sample a hair over the ceiling. Clamp those few rather than
        // shipping an overshoot; this is a handful of samples, not audible shaping.
        float ceilF = (float)ceiling;
        for (int i = 0; i < pcm.Data.Length; i++)
        {
            if (pcm.Data[i] > ceilF) pcm.Data[i] = ceilF;
            else if (pcm.Data[i] < -ceilF) pcm.Data[i] = -ceilF;
        }

        return worst >= 1.0 ? 0 : -20 * Math.Log10(worst);
    }
}
