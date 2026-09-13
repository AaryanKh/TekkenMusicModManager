namespace Tmm.Core.Mods;

/// <summary>Enable = copy pak from the app store into ~mods. Disable = remove it from ~mods.
/// The app store copy is never deleted by these operations, so toggling is lossless.</summary>
public static class Installer
{
    public static void Enable(ModManifest m, ModRegistry reg, bool force = false)
    {
        var modsDir = reg.Settings.GameModsDir
                      ?? throw new GameNotFoundException("Game folder is not set. Point Settings at ...\\steamapps\\common\\TEKKEN 8.");
        if (!reg.Settings.GameRootLooksValid())
            throw new GameNotFoundException($"'{reg.Settings.GameRoot}' does not contain {Constants.PaksRelative}.");
        var store = reg.StorePak(m);
        if (!File.Exists(store))
            throw new InstallException($"'{m.Name}' has no built pak in the app store; rebuild it first.");

        if (!force)
        {
            var conflicts = Conflicts.Check(m.WemIds, reg, modsDir, m.Name, excludeMod: m.ModId);
            if (conflicts.Count > 0)
                throw new SlotConflictException(
                    $"'{m.Name}' overrides audio that another enabled pak already replaces:\n" + string.Join("\n", conflicts),
                    conflicts);
        }

        Directory.CreateDirectory(modsDir);   // "~mods", never "Mods" — Spike B Test 8
        File.Copy(store, Path.Combine(modsDir, m.PakName), overwrite: true);
    }

    public static void Disable(ModManifest m, ModRegistry reg)
    {
        var installed = reg.InstalledPak(m);
        if (installed is not null && File.Exists(installed))
            File.Delete(installed);
    }

    /// <summary>Disable, then remove from the app store. The only destructive operation.</summary>
    public static void Delete(ModManifest m, ModRegistry reg)
    {
        Disable(m, reg);
        var dir = reg.ModDir(m);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}
