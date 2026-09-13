using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using Tmm.Core.Audio;

namespace Tmm.Core.Analysis;

/// <summary>Decode any input (mp3/flac/wav/m4a/ogg) to float32 at TargetSampleRate via ffmpeg.</summary>
public static class Decoder
{
    public static string Fingerprint(string path)
    {
        using var fs = File.OpenRead(path);
        var hash = SHA256.HashData(fs);
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();   // 16 hex chars, like the Python
    }

    /// <summary>Returns (Song, interleaved float32 buffer in [-1, 1]).</summary>
    public static (Song song, PcmBuffer pcm) Decode(string path, string ffmpeg = "ffmpeg",
                                                    int rate = Constants.TargetSampleRate,
                                                    int channels = Constants.TargetChannels,
                                                    CancellationToken ct = default)
    {
        if (!File.Exists(path)) throw new DecodeException($"file not found: {path}");

        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "-v", "error", "-i", path, "-f", "f32le", "-acodec", "pcm_f32le", "-ac", channels.ToString(), "-ar", rate.ToString(), "-" })
            psi.ArgumentList.Add(a);

        byte[] raw;
        string err;
        try
        {
            using var p = Process.Start(psi) ?? throw new DecodeException("could not start ffmpeg");
            var errTask = p.StandardError.ReadToEndAsync();
            using var ms = new MemoryStream();
            using (var reg = ct.Register(() => { try { p.Kill(); } catch { /* already gone */ } }))
                p.StandardOutput.BaseStream.CopyTo(ms);
            p.WaitForExit();
            err = errTask.GetAwaiter().GetResult();
            ct.ThrowIfCancellationRequested();
            if (p.ExitCode != 0) throw new DecodeException($"ffmpeg failed on {Path.GetFileName(path)}: {err.Trim()}");
            raw = ms.ToArray();
        }
        catch (Win32Exception e)
        {
            throw new DecodeException($"ffmpeg not found ('{ffmpeg}'). Install ffmpeg or set its path in Settings.", e);
        }

        int samples = raw.Length / 4 - (raw.Length / 4) % channels;
        if (samples == 0) throw new DecodeException($"ffmpeg produced no audio for {Path.GetFileName(path)}: {err.Trim()}");
        var data = new float[samples];
        Buffer.BlockCopy(raw, 0, data, 0, samples * 4);
        var pcm = new PcmBuffer(data, channels, rate);
        var song = new Song(path, Path.GetFileNameWithoutExtension(path), pcm.Seconds, rate, channels, Fingerprint(path));
        return (song, pcm);
    }

    public static bool FfmpegAvailable(string ffmpeg = "ffmpeg")
    {
        try
        {
            var psi = new ProcessStartInfo(ffmpeg, "-version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }
}
