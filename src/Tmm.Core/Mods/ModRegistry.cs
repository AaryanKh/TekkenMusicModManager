using Tmm.Core.Pak;

namespace Tmm.Core.Mods;

/// <summary>
/// The dashboard's source of truth: every mod we've built, its state, and what it overrides.
///
/// State is derived, not stored: ENABLED iff the pak is in ~mods, DISABLED iff it's only in the app
/// store, STALE iff manifest.updated &gt; pak mtime, BROKEN iff neither exists. Nothing caches this,
/// so the dashboard can't lie after the user moves files by hand.
/// </summary>
public sealed class ModRegistry
{
    public Settings Settings { get; }
    public ModRegistry(Settings settings) => Settings = settings;

    public string ModDir(ModManifest m) => Path.Combine(Settings.ModsStore, m.ModId);
    public string ModDir(string modId) => Path.Combine(Settings.ModsStore, modId);
    public string WemDir(ModManifest m) => Path.Combine(ModDir(m), "wems");
    public string StorePak(ModManifest m) => Path.Combine(ModDir(m), m.PakName);
    public string? InstalledPak(ModManifest m)
    {
        var d = Settings.GameModsDir;
        return d is null ? null : Path.Combine(d, m.PakName);
    }

    public List<ModManifest> All()
    {
        var list = new List<ModManifest>();
        if (!Directory.Exists(Settings.ModsStore)) return list;
        foreach (var dir in Directory.EnumerateDirectories(Settings.ModsStore))
        {
            if (!File.Exists(Path.Combine(dir, ManifestIo.FileName))) continue;
            try { list.Add(ManifestIo.Load(dir)); }
            catch (InstallException) { /* skip corrupt entries; the dashboard shows what it can */ }
        }
        return list.OrderByDescending(m => m.Updated).ToList();
    }

    public ModManifest Get(string modId)
    {
        var dir = ModDir(modId);
        if (!File.Exists(Path.Combine(dir, ManifestIo.FileName)))
            throw new InstallException($"no such mod: {modId}");
        return ManifestIo.Load(dir);
    }

    public void Save(ModManifest m) => ManifestIo.Save(m, ModDir(m));

    // ------------------------------------------------------------------ pak name uniqueness

    // Everything downstream keys on the pak file name: ~mods holds one file per name, and ModState
    // reads "Enabled" whenever a file of that name exists. Two mods sharing a name would therefore
    // both read Enabled while the second one silently replaced the first's audio. So a name is
    // refused when a new mod is created, and a shared name is refused again when a mod is enabled.

    private static bool SamePak(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Why <paramref name="modName"/> cannot be used for a new mod, or null when it is free. The name is
    /// compared the way it will be written to disk, after sanitising, so "My Song!" and "My Song" are the
    /// same pak. Taken means one of your mods already owns the file name, or a pak by that name is already
    /// in ~mods (somebody else's, since none of yours has it yet).
    /// </summary>
    public string? FindNameConflict(string modName)
    {
        var pakName = PakLayout.PakFilename(PakLayout.SanitizeModName(modName));
        if (Titles.JukeboxTitles.IsTitlesFile(pakName))
            return $"'{pakName}' is reserved for the jukebox titles this app writes. Choose a different name.";

        var owner = All().FirstOrDefault(m => SamePak(m.PakName, pakName));
        if (owner is not null)
            return $"'{owner.PakName}' is already used by your mod '{owner.Name}'. Choose a different name.";

        var modsDir = Settings.GameModsDir;
        if (modsDir is not null && Directory.Exists(modsDir))
        {
            try
            {
                // Same recursive scope as the third-party scan, so a pak in a subfolder counts too.
                var existing = Directory.EnumerateFiles(modsDir, "*.pak", SearchOption.AllDirectories)
                                        .FirstOrDefault(f => SamePak(Path.GetFileName(f), pakName));
                if (existing is not null)
                    return $"A pak named '{pakName}' is already in ~mods and was not built by this app. " +
                           "Enabling this mod would overwrite it. Choose a different name.";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // An unreadable folder must not block building; ModBuilder and Enable still guard.
            }
        }
        return null;
    }

    /// <summary><paramref name="name"/> if it is free, otherwise the first "name_2", "name_3", … that is.</summary>
    public string MakeUniqueName(string name)
    {
        if (FindNameConflict(name) is null) return name;
        for (int n = 2; n < 1000; n++)
        {
            var candidate = $"{name}_{n}";
            if (FindNameConflict(candidate) is null) return candidate;
        }
        return name;
    }

    /// <summary>Throws when another mod of ours has the same pak file name as <paramref name="m"/>.
    /// Mods built before names were checked can already be in this state.</summary>
    public void EnsurePakNameUnique(ModManifest m)
    {
        var twin = All().FirstOrDefault(o => o.ModId != m.ModId && SamePak(o.PakName, m.PakName));
        if (twin is null) return;
        var who = string.Equals(m.Name, twin.Name, StringComparison.OrdinalIgnoreCase)
            ? $"Two of your mods are both called '{m.Name}'"
            : $"'{m.Name}' and '{twin.Name}' use the same pak file";
        throw new InstallException(
            $"{who} ('{m.PakName}'), so installing one would overwrite the other in ~mods. " +
            "Delete one of them and build it again under a different name.");
    }

    /// <summary>Store an edited plan without rebuilding. The mod reads as STALE until Rebuild replays it.</summary>
    public void UpdatePlan(ModManifest m, RenderPlan plan)
    {
        m.Plan = plan.Clone();
        m.Updated = DateTime.UtcNow.AddSeconds(3);   // clear the mtime tolerance so Stale is unambiguous
        Save(m);
    }

    public ModState StateOf(ModManifest m)
    {
        var store = StorePak(m);
        var installed = InstalledPak(m);
        bool inStore = File.Exists(store);
        bool inGame = installed is not null && File.Exists(installed);
        if (!inStore && !inGame) return ModState.Broken;

        // Stale = the manifest (plan) was edited after the pak was last built.
        var pakTime = inStore ? File.GetLastWriteTimeUtc(store) : File.GetLastWriteTimeUtc(installed!);
        if (m.Updated.ToUniversalTime() > pakTime.AddSeconds(2)) return ModState.Stale;

        return inGame ? ModState.Enabled : ModState.Disabled;
    }

    public bool IsEnabled(ModManifest m)
    {
        var installed = InstalledPak(m);
        return installed is not null && File.Exists(installed);
    }

    /// <summary>{wem_id: mod_id} for every ENABLED mod. Conflict detection reads this.</summary>
    public Dictionary<int, string> ClaimedWemIds(string? exclude = null)
    {
        var claimed = new Dictionary<int, string>();
        foreach (var m in All())
        {
            if (m.ModId == exclude || !IsEnabled(m)) continue;
            foreach (var id in m.WemIds) claimed.TryAdd(id, m.ModId);
        }
        return claimed;
    }

    /// <summary>{slot_key: mod_id} for every ENABLED mod. The ranking view hides these by default.</summary>
    public Dictionary<int, string> ClaimedSlots()
    {
        var claimed = new Dictionary<int, string>();
        foreach (var m in All())
            if (IsEnabled(m)) claimed.TryAdd(m.SlotKey, m.ModId);
        return claimed;
    }
}
