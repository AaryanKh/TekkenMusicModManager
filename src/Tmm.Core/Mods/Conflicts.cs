using Tmm.Core.Pak;

namespace Tmm.Core.Mods;

/// <summary>Two enabled paks overriding the same WEM ID is undefined behaviour — Unreal picks by
/// mount order and the user sees the wrong song with no error. Catch it before install.</summary>
public sealed record Conflict(int WemId, string Ours, string Theirs, bool ThirdParty)
{
    public override string ToString() =>
        ThirdParty ? $"WEM {WemId}: already provided by third-party pak '{Theirs}'"
                   : $"WEM {WemId}: already provided by enabled mod '{Theirs}'";
}

public sealed record ThirdPartyPak(string Path, IReadOnlyList<int> WemIds);

public static class Conflicts
{
    /// <summary>Conflicts for enabling a pak that overrides <paramref name="wemIds"/>. Checks our own
    /// enabled mods and scans ~mods for paks we didn't build, so a downloaded megapack that already
    /// claims a slot is reported, not silently outranked.</summary>
    public static List<Conflict> Check(IReadOnlyList<int> wemIds, ModRegistry registry, string? modsDir,
                                       string ours, string? excludeMod = null)
    {
        var result = new List<Conflict>();
        var claimed = registry.ClaimedWemIds(excludeMod);
        var byId = registry.All().ToDictionary(m => m.ModId, m => m.Name);
        foreach (var id in wemIds)
            if (claimed.TryGetValue(id, out var mod))
                result.Add(new Conflict(id, ours, byId.TryGetValue(mod, out var n) ? n : mod, ThirdParty: false));

        foreach (var tp in ScanThirdParty(registry, modsDir))
            foreach (var id in wemIds)
                if (tp.WemIds.Contains(id))
                    result.Add(new Conflict(id, ours, Path.GetFileName(tp.Path), ThirdParty: true));
        return result;
    }

    /// <summary>Every pak in ~mods that we did not build, with the WEM IDs it overrides.</summary>
    public static List<ThirdPartyPak> ScanThirdParty(ModRegistry registry, string? modsDir)
    {
        var list = new List<ThirdPartyPak>();
        if (modsDir is null || !Directory.Exists(modsDir)) return list;
        var ours = new HashSet<string>(registry.All().Select(m => m.PakName), StringComparer.OrdinalIgnoreCase);
        foreach (var pak in Directory.EnumerateFiles(modsDir, "*.pak", SearchOption.AllDirectories))
        {
            if (ours.Contains(Path.GetFileName(pak))) continue;
            var ids = PakScan.FindWemIds(pak);
            if (ids.Count > 0) list.Add(new ThirdPartyPak(pak, ids));
        }
        return list;
    }
}
