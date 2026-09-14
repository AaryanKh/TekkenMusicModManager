using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Tmm.Core.Analysis;

/// <summary>Title / artist / album as written in the file's tags, or empty when absent.</summary>
public sealed record SongTags(string Title, string Artist, string Album)
{
    public static readonly SongTags Empty = new("", "", "");
    public bool IsEmpty => Title.Length == 0 && Artist.Length == 0 && Album.Length == 0;
}

/// <summary>
/// Reads tags with <c>ffmpeg -f ffmetadata</c>. ffprobe would be the obvious tool, but ffmpeg alone is
/// the one dependency the app asks users for, and ffmpeg can dump the same metadata itself.
/// </summary>
public static class SongTagReader
{
    public static SongTags Read(string songPath, string ffmpeg)
    {
        if (!File.Exists(songPath)) return SongTags.Empty;
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "-v", "error", "-i", songPath, "-f", "ffmetadata", "-" })
            psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return SongTags.Empty;
            var errTask = p.StandardError.ReadToEndAsync();
            var text = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            errTask.GetAwaiter().GetResult();
            return p.ExitCode == 0 ? Parse(text) : SongTags.Empty;
        }
        catch (Win32Exception) { return SongTags.Empty; }
    }

    /// <summary>
    /// The ffmetadata format: a ";FFMETADATA1" header, then KEY=VALUE lines. Keys arrive in whatever
    /// case the container used (FLAC shouts, MP3 does not), and a handful of characters are escaped
    /// with a backslash. Pure, so it can be tested without ffmpeg.
    /// </summary>
    public static SongTags Parse(string ffmetadata)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in ffmetadata.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line[0] == ';' || line[0] == '#' || line[0] == '[') continue;
            int eq = IndexOfUnescaped(line, '=');
            if (eq <= 0) continue;
            var key = Unescape(line[..eq]).Trim();
            var val = Unescape(line[(eq + 1)..]).Trim();
            if (key.Length > 0 && !map.ContainsKey(key)) map[key] = val;
        }
        string Get(params string[] keys)
        {
            foreach (var k in keys) if (map.TryGetValue(k, out var v) && v.Length > 0) return v;
            return "";
        }
        return new SongTags(Get("title"), Get("artist", "album_artist", "albumartist", "performer"), Get("album"));
    }

    private static int IndexOfUnescaped(string s, char c)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\') { i++; continue; }
            if (s[i] == c) return i;
        }
        return -1;
    }

    private static string Unescape(string s)
    {
        if (s.IndexOf('\\') < 0) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length) { sb.Append(s[++i]); continue; }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }
}
