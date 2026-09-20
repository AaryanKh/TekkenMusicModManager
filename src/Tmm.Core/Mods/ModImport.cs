using Tmm.Core.Pak;

namespace Tmm.Core.Mods;

/// <summary>A pak in ~mods that carries a metadata file but is not in this app's mod list.</summary>
public sealed record RecognisedPak(ModMetadata Meta, string PakPath, string SidecarPath);

/// <summary>How many mods were imported, and one line per pak saying what happened to it.</summary>
public sealed record ImportResult(int Imported, IReadOnlyList<string> Notes);

/// <summary>
/// Adopts paks that carry a <see cref="ModMetadata"/> file. This is what makes such a pak "seen" by
/// the manager: after a move to a new machine, a wiped app folder, or a pak sent by a friend, the
/// dashboard's Scan lists it by name, slot and song rather than as an anonymous third-party file.
///
/// An imported mod has no source song and no render plan (neither is in the metadata), so it can be
/// enabled, disabled and deleted like any other, but not edited or rebuilt. See
/// <see cref="ModManifest.Imported"/>.
/// </summary>
public static class ModImport
{
    /// <summary>
    /// Paks in ~mods that a metadata file describes and that this app does not already have.
    ///
    /// Only the top level of ~mods is looked at, because a mod's state is "a file of this name is in
    /// ~mods". A pak in a subfolder would import as Disabled while the game kept loading it.
    /// Skipped, on purpose: anything already in the mod list (by id or by pak name), a metadata file
    /// with no pak beside it, and a pak whose WEM ids differ from what the file claims, which means
    /// the pak was replaced or edited after the file was written.
    /// </summary>
    public static List<RecognisedPak> Find(ModRegistry reg, string? modsDir)
    {
        var found = new List<RecognisedPak>();
        if (modsDir is null || !Directory.Exists(modsDir)) return found;

        var mods = reg.All();
        var knownIds = new HashSet<string>(mods.Select(m => m.ModId), StringComparer.OrdinalIgnoreCase);
        var knownPaks = new HashSet<string>(mods.Select(m => m.PakName), StringComparer.OrdinalIgnoreCase);

        string[] sidecars;
        try { sidecars = Directory.GetFiles(modsDir, "*" + ModMetadata.Suffix, SearchOption.TopDirectoryOnly); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return found; }
        Array.Sort(sidecars, StringComparer.OrdinalIgnoreCase);

        foreach (var sidecar in sidecars)
        {
            var meta = ModMetadata.TryRead(sidecar);
            if (meta is null) continue;
            if (knownIds.Contains(meta.ModId) || knownPaks.Contains(meta.PakName)) continue;

            var pak = Path.Combine(modsDir, meta.PakName);
            if (!File.Exists(pak)) continue;
            if (!new HashSet<int>(PakScan.FindWemIds(pak)).SetEquals(meta.WemIds)) continue;

            // Two metadata files claiming one mod or one pak: take the first, so the copies cannot collide.
            if (!knownIds.Add(meta.ModId) || !knownPaks.Add(meta.PakName)) continue;
            found.Add(new RecognisedPak(meta, pak, sidecar));
        }
        return found;
    }

    /// <summary>
    /// Bring each recognised pak into the app's store and list it. The pak and its metadata are copied,
    /// never moved, so nothing in ~mods changes: the mod reads as Enabled because its pak is there.
    /// Returns one line per mod.
    /// </summary>
    public static ImportResult Import(ModRegistry reg, IReadOnlyList<RecognisedPak> found)
    {
        var log = new List<string>();
        int imported = 0;
        foreach (var r in found)
        {
            var meta = r.Meta;
            var m = new ModManifest
            {
                ModId = meta.ModId,
                Name = PakLayout.SanitizeModName(meta.Name),
                SlotKey = meta.SlotKey,
                SlotTitle = meta.SlotTitle,
                SongPath = "",
                SongFingerprint = meta.SongFingerprint,
                Plan = new RenderPlan { SlotKey = meta.SlotKey, SongFingerprint = meta.SongFingerprint },
                PakName = meta.PakName,
                WemIds = meta.WemIds.ToList(),
                Imported = true,
            };

            var dir = reg.ModDir(m);
            if (Directory.Exists(dir))
            {
                log.Add($"{m.Name}: skipped, the app store already has a folder for this mod.");
                continue;
            }

            try
            {
                Directory.CreateDirectory(dir);
                FileOps.Copy(r.PakPath, reg.StorePak(m));
                FileOps.Copy(r.SidecarPath, ModMetadata.PathFor(reg.StorePak(m)));

                var pakTime = File.GetLastWriteTimeUtc(reg.StorePak(m));
                var created = meta.Created == default ? pakTime : meta.Created.ToUniversalTime();
                m.Created = created;
                m.Updated = created < pakTime ? created : pakTime;   // never older than its pak, so it cannot read as Stale
                reg.Save(m);                                         // last: nothing is listed until the copies are in place
            }
            catch
            {
                FileOps.DeleteDirectory(dir);                        // no half-imported mod left behind
                throw;
            }
            imported++;
            log.Add($"{m.Name}: imported ({(string.IsNullOrEmpty(meta.SlotTitle) ? "slot " + meta.SlotKey : meta.SlotTitle)}).");
        }
        return new ImportResult(imported, log);
    }
}
