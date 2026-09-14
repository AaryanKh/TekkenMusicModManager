using System.Buffers.Binary;
using Tmm.Core.Wem;

namespace Tmm.Core.Audio;

/// <summary>Minimal plain-WAV reader/writer for previews and external-tool hand-offs (rubberband).
/// Not for game files — those go through <see cref="WemWriter"/>.</summary>
public static class WavIo
{
    public static void WritePcm16(string path, PcmBuffer pcm)
    {
        var s16 = pcm.ToInt16();
        var data = new byte[s16.Length * 2];
        for (int i = 0; i < s16.Length; i++) BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * 2), s16[i]);
        Write(path, data, pcm.Channels, pcm.Rate, bits: 16, formatTag: 1);
    }

    public static void WriteFloat32(string path, PcmBuffer pcm)
    {
        var data = new byte[pcm.Data.Length * 4];
        for (int i = 0; i < pcm.Data.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(i * 4), pcm.Data[i]);
        Write(path, data, pcm.Channels, pcm.Rate, bits: 32, formatTag: 3);
    }

    private static void Write(string path, byte[] data, int channels, int rate, int bits, int formatTag)
    {
        int block = channels * bits / 8;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var fs = FileOps.Create(path);
        Span<byte> h = stackalloc byte[44];
        "RIFF"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], (uint)(36 + data.Length + (data.Length & 1)));
        "WAVE"u8.CopyTo(h[8..]);
        "fmt "u8.CopyTo(h[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(h[20..], (ushort)formatTag);
        BinaryPrimitives.WriteUInt16LittleEndian(h[22..], (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(h[24..], (uint)rate);
        BinaryPrimitives.WriteUInt32LittleEndian(h[28..], (uint)(rate * block));
        BinaryPrimitives.WriteUInt16LittleEndian(h[32..], (ushort)block);
        BinaryPrimitives.WriteUInt16LittleEndian(h[34..], (ushort)bits);
        "data"u8.CopyTo(h[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[40..], (uint)data.Length);
        fs.Write(h);
        fs.Write(data);
        if ((data.Length & 1) == 1) fs.WriteByte(0);
    }

    /// <summary>Reads PCM16, PCM24, PCM32 and float32 WAVs (plain or WAVE_FORMAT_EXTENSIBLE).</summary>
    public static PcmBuffer Read(string path)
    {
        var blob = File.ReadAllBytes(path);
        var chunks = WemReader.ParseChunks(blob);
        var fmt = WemReader.Find(chunks, "fmt ") ?? throw new WemFormatException($"{path}: no fmt chunk");
        var data = WemReader.Find(chunks, "data") ?? throw new WemFormatException($"{path}: no data chunk");
        var f = blob.AsSpan(fmt.PayloadOffset);
        int tag = BinaryPrimitives.ReadUInt16LittleEndian(f);
        int ch = BinaryPrimitives.ReadUInt16LittleEndian(f[2..]);
        int rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(f[4..]);
        int bits = BinaryPrimitives.ReadUInt16LittleEndian(f[14..]);
        if (tag == WemConstants.FormatExtensible && fmt.Size >= 26)
            tag = BinaryPrimitives.ReadUInt16LittleEndian(f[24..]);   // SubFormat GUID's first two bytes

        int bytesPer = bits / 8;
        int count = Math.Min(data.Size, blob.Length - data.PayloadOffset) / bytesPer;
        var samples = new float[count - count % ch];
        var d = blob.AsSpan(data.PayloadOffset);
        for (int i = 0; i < samples.Length; i++)
        {
            var s = d.Slice(i * bytesPer, bytesPer);
            samples[i] = (tag, bits) switch
            {
                (3, 32) => BinaryPrimitives.ReadSingleLittleEndian(s),
                (1, 16) => BinaryPrimitives.ReadInt16LittleEndian(s) / 32768f,
                (1, 24) => ((s[0] | (s[1] << 8) | (s[2] << 16)) << 8 >> 8) / 8388608f,
                (1, 32) => BinaryPrimitives.ReadInt32LittleEndian(s) / 2147483648f,
                _ => throw new WemFormatException($"{path}: unsupported WAV format tag {tag} / {bits} bits"),
            };
        }
        return new PcmBuffer(samples, ch, rate);
    }
}
