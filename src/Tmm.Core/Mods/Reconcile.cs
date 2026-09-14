using System.Security.Cryptography;
using Tmm.Core.Pak;

namespace Tmm.Core.Mods;

/// <summary>A pak sitting in ~mods that belongs to one of our mods but no longer carries the filename
/// the manifest records.</summary>
public sealed record RenamedPak(ModManifest Mod, string ExpectedName, string FoundName, string FoundPath, string Evidence);

/// <summary>
/// Puts the app's records back in step with the filesystem after the user renames paks by hand.
///
/// Everything keys off <see cref="ModManifest.PakName"/>: the store copy, the installed copy, the
/// state badge, and whether a pak counts as third-party. Rename a pak in ~mods and the mod reads as
/// Disabled while the file itself shows up as somebody else's, which is misleading in both
/// directions. A pak can be identified by what it overrides, so the rename can simply be adopted.
/// </summary>
public static class Reconcile
{
    /// <summary>
    /// Paks in ~mods that are ours under a different name. A mod already installed under the name its
    /// manifest expects is left alone, and a pak is only claimed when exactly one mod can own it.
    /// </summary>
    public static List<RenamedPak> FindRenamed(ModRegistry reg, string? modsDir)
    {
        var found = new List<RenamedPak>();
        if (modsDir is null || !Directory.Exists(modsDir)) return found;

        var mods = reg.All();
        if (mods.Count == 0) return found;

        var expected = new HashSet<string>(mods.Select(m => m.PakName), StringComparer.OrdinalIgnoreCase);
        // Only mods that are not currently installed under their recorded name are up for adoption.
        var orphans = mods.Where(m => !reg.IsEnabled(m)).ToList();
        if (orphans.Count == 0) return found;

        var claimed = new HashSet<string>();   // one mod cannot adopt two files
        foreach (var pak in Directory.EnumerateFiles(modsDir, "*.pak", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(pak);
            if (expected.Contains(name)) continue;            // already accounted for

            var ids = PakScan.FindWemIds(pak);
            if (ids.Count == 0) continue;                     // not a jukebox audio pak at all

            var idSet = new HashSet<int>(ids);
            var candidates = orphans
                .Where(m => !claimed.Contains(m.ModId) && m.WemIds.Count > 0 && idSet.SetEquals(m.WemIds))
                .ToList();

            ModManifest? match = null;
            string evidence = "";
            if (candidates.Count == 1)
            {
                match = candidates[0];
                evidence = $"overrides the same WEM id(s): {string.Join(", ", match.WemIds)}";
            }
            else if (candidates.Count > 1)
            {
                // Two mods on the same slot look identical by ID, so fall back to the bytes.
                var identical = candidates.Where(m => SameBytes(pak, reg.StorePak(m))).ToList();
                if (identical.Count == 1)
                {
                    match = identical[0];
                    evidence = "byte-for-byte identical to the stored pak";
                }
            }

            if (match is null) continue;
            claimed.Add(match.ModId);
            found.Add(new RenamedPak(match, match.PakName, name, pak, evidence));
        }
        return found;
    }

    /// <summary>
    /// Point each manifest at the name the user chose, and rename the store copy to match so the two
    /// stay in step. Returns one line per change. A name already spoken for by another mod is skipped
    /// rather than allowed to collide.
    /// </summary>
    public static List<string> Adopt(ModRegistry reg, IReadOnlyList<RenamedPak> renamed)
    {
        var log = new List<string>();
        if (renamed.Count == 0) return log;

        var taken = new HashSet<string>(
            reg.All().Where(m => renamed.All(r => r.Mod.ModId != m.ModId)).Select(m => m.PakName),
            StringComparer.OrdinalIgnoreCase);

        foreach (var r in renamed)
        {
            if (!taken.Add(r.FoundName))
            {
                log.Add($"{r.Mod.Name}: skipped, '{r.FoundName}' is already another mod's pak name.");
                continue;
            }

            var m = r.Mod;
            var oldStore = reg.StorePak(m);
            m.PakName = r.FoundName;
            var newStore = reg.StorePak(m);

            if (!string.Equals(oldStore, newStore, StringComparison.OrdinalIgnoreCase) && File.Exists(oldStore))
            {
                FileOps.DeleteFile(newStore);           // a stale file under the new name would win otherwise
                FileOps.Move(oldStore, newStore);
            }
            // The display name follows the file, otherwise the dashboard keeps showing the old one.
            var stem = StemOf(r.FoundName);
            var wasCalled = m.Name;
            if (stem.Length > 0) m.Name = stem;

            reg.Save(m);
            log.Add($"{wasCalled}: now '{r.FoundName}' (was '{r.ExpectedName}') — {r.Evidence}.");
        }
        return log;
    }

    /// <summary>
    /// Bring each mod's display name into line with its pak filename, for mods whose pak was renamed
    /// rather than generated from the name. Without this the dashboard keeps showing the name the mod
    /// was built under while the file on disk says something else.
    ///
    /// A name that merely differs by sanitising ("My Song!" becoming My_Song_P.pak) is left alone,
    /// because that pak was still derived from the name and the punctuation is worth keeping.
    /// </summary>
    public static List<string> SyncNames(ModRegistry reg)
    {
        var log = new List<string>();
        foreach (var m in reg.All())
        {
            if (DerivedFromName(m)) continue;

            var stem = StemOf(m.PakName);
            if (stem.Length == 0 || stem == m.Name) continue;

            log.Add($"{m.Name} is now shown as {stem}.");
            m.Name = stem;
            reg.Save(m);
        }
        return log;
    }

    /// <summary>True when the pak filename is just the mod name run through the usual sanitising, so
    /// the two are already in step and the name should not be rewritten.</summary>
    private static bool DerivedFromName(ModManifest m) =>
        string.Equals(Pak.PakLayout.PakFilename(Pak.PakLayout.SanitizeModName(m.Name)), m.PakName,
                      StringComparison.OrdinalIgnoreCase);

    /// <summary>"[T7]_Seymour_P.pak" becomes "[T7]_Seymour".</summary>
    private static string StemOf(string pakName)
    {
        var stem = Path.GetFileNameWithoutExtension(pakName);
        return stem.EndsWith(Constants.PakSuffix, StringComparison.OrdinalIgnoreCase)
            ? stem[..^Constants.PakSuffix.Length]
            : stem;
    }

    private static bool SameBytes(string a, string b)
    {
        if (!File.Exists(a) || !File.Exists(b)) return false;
        if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
        using var sa = File.OpenRead(a);
        using var sb = File.OpenRead(b);
        return SHA256.HashData(sa).AsSpan().SequenceEqual(SHA256.HashData(sb));
    }
}
