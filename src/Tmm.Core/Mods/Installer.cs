using Tmm.Core.Titles;

namespace Tmm.Core.Mods;

/// <summary>Enable = copy pak from the app store into ~mods. Disable = remove it from ~mods.
/// The app store copy is never deleted by these operations, so toggling is lossless.
///
/// Each one finishes by bringing the shared jukebox titles container in line with the mods that are now
/// enabled, and returns what that did. A title problem never fails the operation itself.</summary>
public static class Installer
{
    public static TitleSyncResult Enable(ModManifest m, ModRegistry reg, bool force = false)
    {
        var modsDir = reg.Settings.GameModsDir
                      ?? throw new GameNotFoundException("Game folder is not set. Point Settings at ...\\steamapps\\common\\TEKKEN 8.");
        if (!reg.Settings.GameRootLooksValid())
            throw new GameNotFoundException($"'{reg.Settings.GameRoot}' does not contain {Constants.PaksRelative}.");
        var store = reg.StorePak(m);
        if (!File.Exists(store))
            throw new InstallException($"'{m.Name}' has no built pak in the app store; rebuild it first.");

        // Not skipped by force: that only overrides overlapping audio, whereas a shared file name means
        // this pak would replace the other mod's file outright.
        reg.EnsurePakNameUnique(m);

        if (!force)
        {
            var conflicts = Conflicts.Check(m.WemIds, reg, modsDir, m.Name, excludeMod: m.ModId);
            if (conflicts.Count > 0)
                throw new SlotConflictException(
                    $"'{m.Name}' overrides audio that another enabled pak already replaces:\n" + string.Join("\n", conflicts),
                    conflicts);
        }

        Directory.CreateDirectory(modsDir);   // "~mods", never "Mods" — Spike B Test 8
        var installed = Path.Combine(modsDir, m.PakName);
        FileOps.Copy(store, installed);

        // The metadata file, when this mod has one, goes in beside its pak. When it does not, clear any
        // earlier one so ~mods never holds a file describing a pak it no longer matches.
        var metaSrc = ModMetadata.PathFor(store);
        var metaDst = ModMetadata.PathFor(installed);
        if (File.Exists(metaSrc)) FileOps.Copy(metaSrc, metaDst);
        else FileOps.DeleteFile(metaDst);
        return JukeboxTitles.Sync(reg);
    }

    public static TitleSyncResult Disable(ModManifest m, ModRegistry reg)
    {
        var installed = reg.InstalledPak(m);
        if (installed is null) return JukeboxTitles.Sync(reg);
        FileOps.DeleteFile(installed);
        FileOps.DeleteFile(ModMetadata.PathFor(installed));   // it was copied in with the pak, so it leaves with it
        return JukeboxTitles.Sync(reg);
    }

    /// <summary>Disable, then remove from the app store. The only destructive operation.</summary>
    public static TitleSyncResult Delete(ModManifest m, ModRegistry reg)
    {
        var titles = Disable(m, reg);   // a disabled mod is already out of the titles
        var dir = reg.ModDir(m);
        FileOps.DeleteDirectory(dir);
        return titles;
    }
}
