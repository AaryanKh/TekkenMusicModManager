using System.Text.RegularExpressions;

namespace Tmm.Core.Pak;

/// <summary>Filesystem layout for a mod pak. The internal path must be exactly
/// Polaris/Content/WwiseAudio/Media/&lt;ID&gt;.wem or the override is silently ignored.</summary>
public static class PakLayout
{
    /// <summary>Returns the staging root &lt;scratch&gt;/&lt;mod_name&gt;_P/ ready to be packed.</summary>
    public static string Stage(string modName, IReadOnlyDictionary<int, string> wems, string scratch)
    {
        var root = Path.Combine(scratch, modName + Constants.PakSuffix);
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        var media = Path.Combine(root, Constants.WemMediaRelative);
        Directory.CreateDirectory(media);
        foreach (var (wid, src) in wems)
            File.Copy(src, Path.Combine(media, $"{wid}.wem"), overwrite: true);
        return root;
    }

    public static string PakFilename(string modName) => $"{modName}{Constants.PakSuffix}.pak";

    private static readonly Regex Unsafe = new(@"[^A-Za-z0-9_\-. ]+", RegexOptions.Compiled);

    /// <summary>A mod name the filesystem, UnrealPak and Unreal's mount logic all accept.</summary>
    public static string SanitizeModName(string name)
    {
        var s = Unsafe.Replace(name.Trim(), "").Replace(' ', '_');
        if (s.EndsWith(Constants.PakSuffix, StringComparison.OrdinalIgnoreCase)) s = s[..^Constants.PakSuffix.Length];
        return string.IsNullOrWhiteSpace(s) ? "MusicMod" : s;
    }
}
