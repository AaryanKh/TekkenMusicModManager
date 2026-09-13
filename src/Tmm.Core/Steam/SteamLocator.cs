using System.Text.RegularExpressions;

namespace Tmm.Core.Steam;

/// <summary>Find the Tekken 8 install: Steam registry key -> libraryfolders.vdf -> steamapps/common/TEKKEN 8.</summary>
public static class SteamLocator
{
    public const string GameFolderName = "TEKKEN 8";
    private static readonly Regex PathLine = new("\"path\"\\s+\"([^\"]+)\"", RegexOptions.Compiled);

    public static string? FindGameRoot()
    {
        foreach (var lib in LibraryFolders())
        {
            var candidate = Path.Combine(lib, "steamapps", "common", GameFolderName);
            if (Directory.Exists(Path.Combine(candidate, Constants.PaksRelative))) return candidate;
        }
        return null;
    }

    public static IEnumerable<string> LibraryFolders()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var steam in SteamRoots())
        {
            if (seen.Add(steam)) yield return steam;
            var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            string text;
            try { text = File.ReadAllText(vdf); } catch { continue; }
            foreach (Match m in PathLine.Matches(text))
            {
                var p = m.Groups[1].Value.Replace("\\\\", "\\");
                if (seen.Add(p)) yield return p;
            }
        }
    }

    private static IEnumerable<string> SteamRoots()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var key in new[] { @"HKEY_CURRENT_USER\Software\Valve\Steam", @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam" })
            {
                string? v = null;
                try { v = Microsoft.Win32.Registry.GetValue(key, key.StartsWith("HKEY_CURRENT") ? "SteamPath" : "InstallPath", null) as string; } catch { }
                if (!string.IsNullOrEmpty(v) && Directory.Exists(v)) yield return Path.GetFullPath(v);
            }
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrEmpty(pf86)) yield return Path.Combine(pf86, "Steam");
            yield return @"C:\Program Files (x86)\Steam";
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            yield return Path.Combine(home, ".steam", "steam");
            yield return Path.Combine(home, ".local", "share", "Steam");
        }
    }
}
