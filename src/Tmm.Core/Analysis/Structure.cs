namespace Tmm.Core.Analysis;

/// <summary>
/// Structural segmentation (verse/chorus/...) so loop candidates can be penalized for straddling a
/// section boundary. Baseline: per-bar timbre+harmony vectors, a checkerboard novelty over ±4 bars,
/// peaks above mean + 1 sd become boundaries (at least 8 bars apart). Labels are assigned by nearest
/// previously-seen segment centroid, so repeated sections tend to share a letter.
/// </summary>
public static class Structure
{
    public static List<Segment> Segment(Features f, BeatGrid grid, double durationSec)
    {
        var bars = grid.DownbeatsSec;
        int nb = bars.Length;
        if (nb < 8)
            return new List<Segment> { new(0, durationSec, "A") };

        int dim = Features.ChromaBins + Features.MfccCoeffs;
        var vec = new double[nb][];
        for (int b = 0; b < nb; b++)
        {
            double s = bars[b], e = b + 1 < nb ? bars[b + 1] : durationSec;
            int fs = Math.Clamp(f.FrameAt(s), 0, f.Frames - 1);
            int fe = Math.Clamp(f.FrameAt(e), fs + 1, f.Frames);
            var v = new double[dim];
            for (int t = fs; t < fe; t++)
            {
                var c = f.ChromaRow(t); var m = f.MfccRow(t);
                for (int i = 0; i < Features.ChromaBins; i++) v[i] += c[i];
                for (int i = 0; i < Features.MfccCoeffs; i++) v[Features.ChromaBins + i] += m[i] * 0.3;
            }
            for (int i = 0; i < dim; i++) v[i] /= (fe - fs);
            vec[b] = v;
        }

        const int span = 4;
        var novelty = new double[nb];
        for (int b = span; b + span <= nb; b++)
        {
            var before = new double[dim]; var after = new double[dim];
            for (int k = 1; k <= span; k++)
                for (int i = 0; i < dim; i++) { before[i] += vec[b - k][i]; after[i] += vec[b + k - 1][i]; }
            double d = 0;
            for (int i = 0; i < dim; i++) { double x = (before[i] - after[i]) / span; d += x * x; }
            novelty[b] = Math.Sqrt(d);
        }
        double mean = novelty.Average();
        double sd = Math.Sqrt(novelty.Select(x => (x - mean) * (x - mean)).Average());
        double thr = mean + sd;

        var boundaries = new List<int> { 0 };
        for (int b = span; b + span <= nb; b++)
        {
            bool peak = novelty[b] > thr && novelty[b] >= novelty[b - 1] && novelty[b] >= novelty[Math.Min(nb - 1, b + 1)];
            if (peak && b - boundaries[^1] >= 8) boundaries.Add(b);
        }

        var segments = new List<Segment>();
        var centroids = new List<(string label, double[] c)>();
        for (int i = 0; i < boundaries.Count; i++)
        {
            int b0 = boundaries[i], b1 = i + 1 < boundaries.Count ? boundaries[i + 1] : nb;
            var c = new double[dim];
            for (int b = b0; b < b1; b++) for (int k = 0; k < dim; k++) c[k] += vec[b][k];
            for (int k = 0; k < dim; k++) c[k] /= Math.Max(1, b1 - b0);

            string label = ((char)('A' + centroids.Count)).ToString();
            double bestD = double.PositiveInfinity;
            foreach (var (l, cc) in centroids)
            {
                double d = 0; for (int k = 0; k < dim; k++) { double x = c[k] - cc[k]; d += x * x; }
                d = Math.Sqrt(d);
                if (d < bestD) { bestD = d; if (d < 0.6 * thr) label = l; }
            }
            if (!centroids.Any(x => x.label == label)) centroids.Add((label, c));

            double start = bars[b0], end = b1 < nb ? bars[b1] : durationSec;
            segments.Add(new Segment(start, end, label));
        }
        return segments;
    }
}
