using System.Buffers.Binary;
using Tmm.Core;
using Tmm.Core.Wem;
using Xunit;

namespace Tmm.Core.Tests;

/// <summary>The writer/reader pair is the load-bearing piece; it gets real tests (ported from tests/test_wem_roundtrip.py).</summary>
public class WemRoundtripTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "tmm-tests-" + Guid.NewGuid().ToString("N"));
    public WemRoundtripTests() => Directory.CreateDirectory(_tmp);
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    private static short[] Pcm(int frames, int ch = 2)
    {
        var o = new short[frames * ch];
        for (int i = 0; i < frames; i++)
        {
            short v = (short)(Math.Sin(2 * Math.PI * 440 * i / 48000.0) * 12000);
            for (int c = 0; c < ch; c++) o[i * ch + c] = v;
        }
        return o;
    }

    [Fact]
    public void CorpusHeaderLayout()
    {
        var outPath = WemWriter.WriteWem(Path.Combine(_tmp, "x.wem"), Pcm(48000), 2, 48000);
        var blob = File.ReadAllBytes(outPath);
        var chunks = WemReader.ParseChunks(blob);
        Assert.Equal(new[] { "fmt ", "hash", "smpl", "data" }, chunks.Select(c => c.Id).ToArray());
        Assert.Equal(WemConstants.CorpusHeaderLen, chunks.First(c => c.Id == "data").PayloadOffset);
    }

    [Fact]
    public void ReaderSeesWhatWriterWrote()
    {
        var outPath = WemWriter.WriteWem(Path.Combine(_tmp, "123.wem"), Pcm(12345), 2, 48000);
        var info = WemReader.ReadHeader(outPath);
        Assert.Equal((123, 12345, 48000, 2), (info.WemId, info.Frames, info.SampleRate, info.Channels));
        Assert.Equal(WemConstants.FormatExtensible, info.FormatTag);
    }

    [Fact]
    public void LengthGateIsHard()
    {
        Assert.Throws<LengthMismatchException>(() =>
            WemWriter.WriteWem(Path.Combine(_tmp, "y.wem"), Pcm(1000), 2, 48000, expectedFrames: 1001));
    }

    [Fact]
    public void RiffSizeField()
    {
        var outPath = WemWriter.WriteWem(Path.Combine(_tmp, "z.wem"), Pcm(777), 2, 48000);
        var blob = File.ReadAllBytes(outPath);
        Assert.Equal((uint)(blob.Length - 8), BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(4)));
    }

    [Fact]
    public void VorbisFramesReadFromFmtExtension()
    {
        // Fabricate a stock-style Vorbis header: tag 0xFFFF, 66-byte fmt, frames at +24.
        var fmt = new byte[66];
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(0), 0xFFFF);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(2), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(4), 48000);
        BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(24), 353_920);
        var body = new List<byte>();
        body.AddRange("fmt "u8.ToArray()); body.AddRange(BitConverter.GetBytes(66u)); body.AddRange(fmt);
        body.AddRange("data"u8.ToArray()); body.AddRange(BitConverter.GetBytes(4u)); body.AddRange(new byte[4]);
        var p = Path.Combine(_tmp, "115061619.wem");
        var file = new List<byte>();
        file.AddRange("RIFF"u8.ToArray()); file.AddRange(BitConverter.GetBytes((uint)(4 + body.Count))); file.AddRange("WAVE"u8.ToArray()); file.AddRange(body);
        File.WriteAllBytes(p, file.ToArray());
        var info = WemReader.ReadHeader(p);
        Assert.Equal(353_920, info.Frames);
        Assert.Equal(115061619, info.WemId);
    }
}
