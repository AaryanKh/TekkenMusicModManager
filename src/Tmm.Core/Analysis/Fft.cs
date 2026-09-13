namespace Tmm.Core.Analysis;

/// <summary>Iterative radix-2 complex FFT. Sizes must be powers of two.</summary>
public static class Fft
{
    public static void Forward(double[] re, double[] im)
    {
        int n = re.Length;
        if (n != im.Length || (n & (n - 1)) != 0) throw new ArgumentException("FFT size must be a power of two");

        // bit-reversal permutation
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            double wRe = Math.Cos(ang), wIm = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cRe = 1, cIm = 0;
                int half = len >> 1;
                for (int j = 0; j < half; j++)
                {
                    int a = i + j, b = i + j + half;
                    double tRe = re[b] * cRe - im[b] * cIm;
                    double tIm = re[b] * cIm + im[b] * cRe;
                    re[b] = re[a] - tRe; im[b] = im[a] - tIm;
                    re[a] += tRe; im[a] += tIm;
                    double nRe = cRe * wRe - cIm * wIm;
                    cIm = cRe * wIm + cIm * wRe;
                    cRe = nRe;
                }
            }
        }
    }

    /// <summary>Magnitude spectrum (n/2 + 1 bins) of a windowed real frame.</summary>
    public static void Magnitude(ReadOnlySpan<float> frame, double[] window, double[] re, double[] im, float[] magOut)
    {
        int n = re.Length;
        for (int i = 0; i < n; i++)
        {
            re[i] = i < frame.Length ? frame[i] * window[i] : 0.0;
            im[i] = 0.0;
        }
        Forward(re, im);
        for (int k = 0; k <= n / 2; k++)
            magOut[k] = (float)Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
    }

    public static double[] Hann(int n)
    {
        var w = new double[n];
        for (int i = 0; i < n; i++) w[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n);
        return w;
    }
}
