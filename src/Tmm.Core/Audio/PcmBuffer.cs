namespace Tmm.Core.Audio;

/// <summary>
/// Interleaved float32 PCM in [-1, 1]. The C# stand-in for the Python (frames, channels) ndarray.
/// Mutable on purpose: the render pipeline works on it in place.
/// </summary>
public sealed class PcmBuffer
{
    public float[] Data { get; }
    public int Channels { get; }
    public int Rate { get; }

    public int Frames => Data.Length / Channels;
    public double Seconds => (double)Frames / Rate;

    public PcmBuffer(float[] data, int channels, int rate)
    {
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        if (data.Length % channels != 0) throw new ArgumentException("data length is not a multiple of channels");
        Data = data; Channels = channels; Rate = rate;
    }

    public PcmBuffer(int frames, int channels, int rate) : this(new float[checked(frames * channels)], channels, rate) { }

    public float this[int frame, int channel]
    {
        get => Data[frame * Channels + channel];
        set => Data[frame * Channels + channel] = value;
    }

    /// <summary>Copy of frames [start, start + count).</summary>
    public PcmBuffer Slice(int start, int count)
    {
        if (start < 0 || count < 0 || start + count > Frames)
            throw new ArgumentOutOfRangeException(nameof(start), $"slice [{start}, {start + count}) outside 0..{Frames}");
        var d = new float[count * Channels];
        Array.Copy(Data, start * Channels, d, 0, d.Length);
        return new PcmBuffer(d, Channels, Rate);
    }

    /// <summary>Slice that clamps to the buffer instead of throwing (may return fewer frames).</summary>
    public PcmBuffer SliceClamped(int start, int count)
    {
        int s = Math.Clamp(start, 0, Frames);
        int e = Math.Clamp(start + count, 0, Frames);
        return Slice(s, e - s);
    }

    public PcmBuffer Clone() => new((float[])Data.Clone(), Channels, Rate);

    public static PcmBuffer Concat(params PcmBuffer[] parts)
    {
        if (parts.Length == 0) throw new ArgumentException("nothing to concat");
        var ch = parts[0].Channels; var rate = parts[0].Rate;
        long total = 0;
        foreach (var p in parts)
        {
            if (p.Channels != ch || p.Rate != rate) throw new ArgumentException("channel/rate mismatch in concat");
            total += p.Data.Length;
        }
        var d = new float[total];
        int pos = 0;
        foreach (var p in parts) { Array.Copy(p.Data, 0, d, pos, p.Data.Length); pos += p.Data.Length; }
        return new PcmBuffer(d, ch, rate);
    }

    public float[] ToMono()
    {
        int n = Frames; var m = new float[n];
        if (Channels == 1) { Array.Copy(Data, m, n); return m; }
        for (int i = 0; i < n; i++)
        {
            float s = 0;
            for (int c = 0; c < Channels; c++) s += Data[i * Channels + c];
            m[i] = s / Channels;
        }
        return m;
    }

    /// <summary>Round-and-clip to int16, interleaved. Same as Python verify.to_int16.</summary>
    public short[] ToInt16()
    {
        var o = new short[Data.Length];
        for (int i = 0; i < Data.Length; i++)
        {
            double v = Math.Round(Data[i] * 32767.0);
            if (v > 32767) v = 32767; else if (v < -32768) v = -32768;
            o[i] = (short)v;
        }
        return o;
    }

    public static PcmBuffer FromInt16(short[] interleaved, int channels, int rate)
    {
        var d = new float[interleaved.Length];
        for (int i = 0; i < d.Length; i++) d[i] = interleaved[i] / 32768f;
        return new PcmBuffer(d, channels, rate);
    }

    public void Scale(float gain)
    {
        for (int i = 0; i < Data.Length; i++) Data[i] *= gain;
    }

    public float PeakAbs()
    {
        float p = 0;
        foreach (var v in Data) { var a = Math.Abs(v); if (a > p) p = a; }
        return p;
    }

    public double PeakDbfs()
    {
        var p = PeakAbs();
        return p <= 0 ? double.NegativeInfinity : 20.0 * Math.Log10(p);
    }
}
