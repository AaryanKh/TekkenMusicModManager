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
