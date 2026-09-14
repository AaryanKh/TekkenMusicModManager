using System.Text;
using System.Text.RegularExpressions;

namespace Tmm.Core.Mods;

/// <summary>
/// Suggests a mod name in the shape <c>[T7]_FinalFantasyX_SeymourBattle</c>: the Tekken game the slot
/// belongs to, optionally where the music came from, then the track.
///
/// Sorting a ~mods folder by name is the only grouping the game gives you, so leading with the Tekken
/// title keeps a slot's mods together. Only two of the three parts can be derived — the tag comes from
/// the slot and the track from the filename — because which game the music came from is knowledge the
/// app does not have. That part is filled in only when the song's folder plausibly says so, and left
/// out rather than guessed at otherwise.
/// </summary>
public static class NameSuggester
{
    /// <summary>Sheet game label to the short tag used in the name.</summary>
    private static readonly Dictionary<string, string> Tags = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TEKKEN"] = "T1",
        ["TEKKEN 2"] = "T2",
        ["TEKKEN 3"] = "T3",
        ["TEKKEN 4"] = "T4",
        ["TEKKEN 5"] = "T5",
        ["TEKKEN 6"] = "T6",
        ["TEKKEN 7"] = "T7",
        ["TEKKEN 8"] = "T8",
        ["TEKKEN TAG"] = "TTT",
        ["TEKKEN TAG 2"] = "TTT2",
        ["TEKKEN REVOLUTION"] = "TREV",
    };

    /// <summary>Folders that say nothing about where the music came from.</summary>
    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        "desktop", "downloads", "download", "documents", "music", "musik", "videos", "video",
        "onedrive", "users", "user", "public", "temp", "tmp", "new folder", "audio", "sound",
        "sounds", "songs", "tracks", "media", "mp3", "mp3s", "flac", "wav", "ogg", "m4a",
        "my music", "itunes", "spotify", "soulseek", "complete", "shared", "misc", "stuff",
        "untitled", "export", "exports", "output", "rips", "rip", "ost", "osts", "soundtrack",
        "soundtracks", "bgm", "game music",
    };

    /// <summary>A leading track number: "4-15 - ", "19. ", "03) ", "07 ".</summary>
    private static readonly Regex TrackNumber = new(@"^\s*\d{1,3}([\-.]\d{1,3})?\s*[\-.)\]:]*\s+", RegexOptions.Compiled);

    /// <summary>The Tekken tag for a slot, e.g. "T7". Falls back to a compacted form of the label.</summary>
    public static string TagFor(string gameLabel)
    {
        var g = (gameLabel ?? "").Trim();
        if (g.Length == 0) return "TEK";
        if (Tags.TryGetValue(g, out var tag)) return tag;
        // Unknown label (a sheet revision, say): keep something readable rather than nothing.
        var compact = new string(g.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return compact.Length == 0 ? "TEK" : compact[..Math.Min(6, compact.Length)];
    }

    /// <summary>
    /// A name for this song in this slot. <paramref name="taken"/> is the names already in use, so the
    /// suggestion does not collide with a mod that already exists.
    /// </summary>
    public static string Suggest(Slot slot, string? songPath, IEnumerable<string>? taken = null)
    {
        var parts = new List<string> { $"[{TagFor(slot.Identity.Game)}]" };

        var source = SourceFrom(songPath);
        if (source.Length > 0) parts.Add(source);

        var track = TrackFrom(songPath);
        parts.Add(track.Length > 0 ? track : PascalCase(slot.Title));

        var name = string.Join('_', parts.Where(p => p.Length > 0));
        if (name.Length == 0) name = "MusicMod";
        return MakeUnique(name, taken);
    }

    /// <summary>The song's folder, when it looks like it names a game rather than a download bin.</summary>
    private static string SourceFrom(string? songPath)
    {
        if (string.IsNullOrWhiteSpace(songPath)) return "";
        string dir;
        try { dir = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(songPath)) ?? "") ?? ""; }
        catch (ArgumentException) { return ""; }
        catch (PathTooLongException) { return ""; }

        if (dir.Length < 3 || Generic.Contains(dir.Trim())) return "";
        if (dir.All(c => !char.IsLetter(c))) return "";           // "2019", "01"
        var pascal = PascalCase(dir);
        return pascal.Length < 3 ? "" : pascal;
    }

    /// <summary>The filename, with any leading track number dropped, in PascalCase.</summary>
    private static string TrackFrom(string? songPath)
    {
        if (string.IsNullOrWhiteSpace(songPath)) return "";
        string stem;
        try { stem = Path.GetFileNameWithoutExtension(songPath) ?? ""; }
        catch (ArgumentException) { return ""; }
        return PascalCase(TrackNumber.Replace(stem, ""));
    }

    /// <summary>
    /// "Seymour Battle" becomes "SeymourBattle". Non-ASCII is dropped because the packer strips it
    /// anyway, and a word that is already shouting (AJURIKA) keeps its case.
    /// </summary>
    public static string PascalCase(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var words = Regex.Split(text, @"[^A-Za-z0-9]+").Where(w => w.Length > 0);
        var sb = new StringBuilder();
        foreach (var w in words)
        {
            if (w.All(c => !char.IsLower(c))) sb.Append(w);                       // AJURIKA, XIII, 15
            else sb.Append(char.ToUpperInvariant(w[0])).Append(w.AsSpan(1));
            if (sb.Length > 60) break;
        }
        var s = sb.ToString();
        return s.Length > 60 ? s[..60] : s;
    }

    private static string MakeUnique(string name, IEnumerable<string>? taken)
    {
        if (taken is null) return name;
        var used = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        if (!used.Contains(name)) return name;
        for (int n = 2; n < 1000; n++)
        {
            var candidate = $"{name}_{n}";
            if (!used.Contains(candidate)) return candidate;
        }
        return name;
    }
}
