using Tmm.Core.Analysis;
using Tmm.Core.Audio;
using Xunit;

namespace Tmm.Core.Tests;

/// <summary>
/// Album-art extraction for the tile view. The extractor shells out to ffmpeg, so the cases that
/// need it are skipped rather than failed on a machine without it; the rest of the suite stays
/// ffmpeg-free.
/// </summary>
public class CoverArtTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "tmm-cover-" + Guid.NewGuid().ToString("N"));
    private static readonly bool Ffmpeg = Decoder.FfmpegAvailable("ffmpeg");

    public CoverArtTests() => Directory.CreateDirectory(_tmp);
    public void Dispose() { try { FileOps.DeleteDirectory(_tmp); } catch { } }

    [Fact]
    public void AMissingSongYieldsNoArtAndNoFile()
    {
        var outPath = Path.Combine(_tmp, "cover.jpg");
        Assert.False(CoverArt.Extract(Path.Combine(_tmp, "nope.mp3"), "ffmpeg", outPath));
        Assert.False(File.Exists(outPath));
    }

    [Fact]
    public void AnUnavailableFfmpegIsNotAnError()
    {
        // No art is the honest answer, and the dashboard must not throw a dialog per tile over it.
        var song = Path.Combine(_tmp, "tone.wav");
        WavIo.WritePcm16(song, Tone());
        var outPath = Path.Combine(_tmp, "cover.jpg");
        Assert.False(CoverArt.Extract(song, Path.Combine(_tmp, "definitely-not-ffmpeg.exe"), outPath));
        Assert.False(File.Exists(outPath));
    }

    [Fact]
    public void ASongWithNoPictureYieldsNoArtAndLeavesNoEmptyFile()
    {
        if (!Ffmpeg) return;   // needs ffmpeg; the behaviour is covered on machines that have it
        var song = Path.Combine(_tmp, "tone.wav");
        WavIo.WritePcm16(song, Tone());
        var outPath = Path.Combine(_tmp, "cover.jpg");

        Assert.False(CoverArt.Extract(song, "ffmpeg", outPath));
        // ffmpeg exits 0 here with nothing to write; an empty leftover would read as a broken picture.
        Assert.False(File.Exists(outPath));
    }

    [Fact]
    public void EmbeddedArtComesOutAsAReadableJpeg()
    {
        if (!Ffmpeg) return;
        // Build a FLAC with a picture attached, the way a tagged album rip carries one.
        var art = Path.Combine(_tmp, "art.png");
        File.WriteAllBytes(art, TinyPng());
        var wav = Path.Combine(_tmp, "tone.wav");
        WavIo.WritePcm16(wav, Tone());
        var flac = Path.Combine(_tmp, "tagged.flac");
        var mux = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ffmpeg")
        {
            ArgumentList = { "-v", "error", "-y", "-i", wav, "-i", art, "-map", "0:a", "-map", "1", "-c:a", "flac", "-c:v", "png", "-disposition:v", "attached_pic", flac },
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
        })!;
        mux.WaitForExit();
        if (mux.ExitCode != 0 || !File.Exists(flac)) return;   // this ffmpeg build cannot attach pictures; nothing to test

        var outPath = Path.Combine(_tmp, "cover.jpg");
        Assert.True(CoverArt.Extract(flac, "ffmpeg", outPath));
        var bytes = File.ReadAllBytes(outPath);
        Assert.True(bytes.Length > 100, "jpeg is suspiciously small");
        Assert.Equal(0xFF, bytes[0]); Assert.Equal(0xD8, bytes[1]);   // JPEG SOI marker
    }

    [Fact]
    public void ExtractingOverAnExistingFileReplacesIt()
    {
        if (!Ffmpeg) return;
        var song = Path.Combine(_tmp, "tone.wav");
        WavIo.WritePcm16(song, Tone());
        var outPath = Path.Combine(_tmp, "cover.jpg");
        File.WriteAllText(outPath, "stale");
        new FileInfo(outPath).Attributes |= FileAttributes.ReadOnly;   // as a sync client would leave it

        Assert.False(CoverArt.Extract(song, "ffmpeg", outPath));
        Assert.False(File.Exists(outPath), "a stale cover must not survive a re-check that found none");
    }

    private static PcmBuffer Tone()
    {
        var pcm = new PcmBuffer(48000, 2, 48000);
        for (int i = 0; i < pcm.Frames; i++) { float v = (float)(0.2 * Math.Sin(2 * Math.PI * 440 * i / 48000.0)); pcm[i, 0] = v; pcm[i, 1] = v; }
        return pcm;
    }

    /// <summary>A valid 2x2 red PNG, the smallest picture ffmpeg will happily attach.</summary>
    private static byte[] TinyPng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAAEklEQVR4nGP4z8DwHwyBFAMDAC5nBP9gnr3nAAAAAElFTkSuQmCC");
}
