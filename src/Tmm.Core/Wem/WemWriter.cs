using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Tmm.Core.Wem;

/// <summary>
/// Write a game-ready PCM WEM without Wwise.
///
/// Layout is the corpus recipe (fmt + hash + smpl + data, 144-byte header). Tests 3, 4c and 7
/// proved hash and smpl are inert and the runtime accepts a 52-byte fmt+data file, but 44/44
/// known-good community files use this exact shape, so we do too.
/// </summary>
public static class WemWriter
{
    private static byte[] FmtPayload(int rate, int channels, int bits = 16)
    {
        int block = channels * bits / 8;
        uint mask = channels == 2 ? WemConstants.ChannelMaskStereo : WemConstants.ChannelMaskMono;
        var b = new byte[WemConstants.FmtPayloadLen];
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(0), WemConstants.FormatExtensible);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)rate);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), (uint)(rate * block));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(12), (ushort)block);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(14), (ushort)bits);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(16), 6);              // cbSize
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(18), (ushort)bits);   // validBits
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(20), mask);           // channelMask
        return b;
    }

    private static byte[] SmplPayload(int rate, int frames)
    {
        uint period = WemConstants.SmplSamplePeriodIsRate ? (uint)rate : (uint)(1e9 / rate);
        var b = new byte[WemConstants.SmplPayloadLen];
        uint[] header = { 0, 0, period, 60, 0, 0, 0, 1, 0 };
        uint[] loop = { 0, 0, 0, (uint)(frames - 1), 0, 0 };   // forward, whole file, infinite
        for (int i = 0; i < 9; i++) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i * 4), header[i]);
        for (int i = 0; i < 6; i++) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(36 + i * 4), loop[i]);
        return b;
    }

    private static void WriteChunk(Stream s, string id, ReadOnlySpan<byte> payload)
    {
        s.Write(Encoding.ASCII.GetBytes(id));
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(len, (uint)payload.Length);
        s.Write(len);
        s.Write(payload);
        if ((payload.Length & 1) == 1) s.WriteByte(0);
    }

    /// <summary>
    /// pcm: int16 interleaved, <paramref name="channels"/> channels. <paramref name="expectedFrames"/>
    /// is a hard gate — a mismatch throws <see cref="LengthMismatchException"/>.
    /// </summary>
    public static string WriteWem(string outPath, short[] pcm, int channels, int rate,
                                  int? expectedFrames = null, bool loop = true)
    {
        if (channels <= 0 || pcm.Length % channels != 0)
            throw new ArgumentException("pcm length must be a multiple of channels");
        int frames = pcm.Length / channels;
        if (expectedFrames is int want && frames != want)
            throw new LengthMismatchException($"{Path.GetFileName(outPath)}: {frames} frames rendered, slot needs {want}");

        var data = new byte[pcm.Length * 2];
        for (int i = 0; i < pcm.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * 2), pcm[i]);

        var body = new MemoryStream();
        WriteChunk(body, "fmt ", FmtPayload(rate, channels));
        WriteChunk(body, "hash", RandomNumberGenerator.GetBytes(WemConstants.HashPayloadLen));
        if (loop) WriteChunk(body, "smpl", SmplPayload(rate, frames));
        WriteChunk(body, "data", data);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        using var fs = FileOps.Create(outPath);
        fs.Write("RIFF"u8);
        Span<byte> riffLen = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(riffLen, (uint)(4 + body.Length));
        fs.Write(riffLen);
        fs.Write("WAVE"u8);
        body.Position = 0;
        body.CopyTo(fs);
        return outPath;
    }
}
