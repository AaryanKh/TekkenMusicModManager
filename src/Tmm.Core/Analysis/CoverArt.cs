using System.ComponentModel;
using System.Diagnostics;

namespace Tmm.Core.Analysis;

/// <summary>
/// Pulls the embedded album art out of a song file with ffmpeg. MP3 (ID3 APIC), FLAC (picture
/// block) and M4A all carry it as a still "video" stream, so one command covers every format the
/// decoder already accepts and no extra library is needed.
/// </summary>
public static class CoverArt
{
    /// <summary>Cached next to the manifest once extracted.</summary>
    public const string FileName = "cover.jpg";
    /// <summary>Written when a song was checked and has no art, so ffmpeg is not run again on every refresh.</summary>
    public const string NoneMarker = "cover.none";

    /// <summary>
    /// Write the song's embedded picture to <paramref name="outPath"/> as JPEG. Returns false when the
    /// file has no art (or ffmpeg is unavailable); the caller decides whether to remember that.
    /// </summary>
    public static bool Extract(string songPath, string ffmpeg, string outPath)
    {
        if (!File.Exists(songPath)) return false;
        FileOps.PrepareWrite(outPath);
        FileOps.DeleteFile(outPath);

        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // "0:v?" — the trailing ? makes the map optional, so a song with no picture exits 0 with
        // nothing written rather than failing the command.
        foreach (var a in new[] { "-v", "error", "-y", "-i", songPath, "-an", "-map", "0:v?", "-c:v", "mjpeg", "-frames:v", "1", outPath })
            psi.ArgumentList.Add(a);

        try
        {
            using var p = Process.Start(psi);
            if (p is null) return false;
            var errTask = p.StandardError.ReadToEndAsync();
            p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            errTask.GetAwaiter().GetResult();
            if (p.ExitCode != 0) { FileOps.DeleteFile(outPath); return false; }
        }
        catch (Win32Exception) { return false; }   // ffmpeg missing: no art, not an error worth a dialog

        if (!File.Exists(outPath)) return false;
        if (new FileInfo(outPath).Length == 0) { FileOps.DeleteFile(outPath); return false; }
        return true;
    }
}
