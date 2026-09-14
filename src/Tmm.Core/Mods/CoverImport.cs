using System.Text.RegularExpressions;

namespace Tmm.Core.Mods;

/// <summary>
/// Maps a picture's filename to the Tekken tag it is for, so a folder of exported jukebox covers can
/// be dropped in without renaming each one. Nothing is shipped: the pictures come from the user's own
/// game files (FModel exports them as PNG) or anywhere else they choose.
/// </summary>
public static class CoverImport
{
    private static readonly (Regex pattern, string tag)[] Rules =
    {
        // Order matters: "tag 2" must win over "tag", and the numbered games over bare "tekken".
        (new Regex(@"(?:tekken)?\s*tag\s*(?:tournament)?\s*2|ttt2|tt2", RegexOptions.IgnoreCase | RegexOptions.Compiled), "TTT2"),
        (new Regex(@"(?:tekken)?\s*tag(?:\s*tournament)?|ttt(?!2)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "TTT"),
        (new Regex(@"revolution|trev|rev(?![a-z])", RegexOptions.IgnoreCase | RegexOptions.Compiled), "TREV"),
        (new Regex(@"(?:tekken|tk|t)\s*[_\-]?\s*([1-8])(?![0-9])", RegexOptions.IgnoreCase | RegexOptions.Compiled), "T{0}"),
        (new Regex(@"\btekken\b|\btk\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "T1"),
    };

    /// <summary>The tag a filename refers to, or null when it does not look like any Tekken game.
    /// "T_UI_Jukebox_Tekken7.png", "tk7.png" and "TEKKEN TAG 2.png" all resolve.</summary>
    public static string? TagFor(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName ?? "");
        if (stem.Length == 0) return null;
        var text = Regex.Replace(stem, @"[_\-\.]+", " ");
        foreach (var (pattern, tag) in Rules)
        {
            var m = pattern.Match(text);
            if (!m.Success) continue;
            return tag.Contains("{0}") ? string.Format(tag, m.Groups[1].Value) : tag;
        }
        return null;
    }

    public static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg" };

    /// <summary>Every picture in <paramref name="folder"/> with the tag it maps to. Files that map to
    /// nothing are reported with a null tag so the user can see what was skipped.</summary>
    public static List<(string path, string? tag)> Plan(string folder)
    {
        var list = new List<(string, string?)>();
        if (!Directory.Exists(folder)) return list;
        foreach (var f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            if (!ImageExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)) continue;
            list.Add((f, TagFor(Path.GetFileName(f))));
        }
        return list;
    }
}
