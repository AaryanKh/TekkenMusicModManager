namespace Tmm.Core.Catalog;

/// <summary>Outcome of a catalog build. <paramref name="Skipped"/> is non-empty only when the
/// extractor could not supply every WEM the sheet names — those slots are absent from the catalog
/// and cannot be modded until the missing WEMs are exported and the build is re-run.</summary>
public sealed record CatalogBuildResult(int Measured, IReadOnlyList<SlotIdentity> Skipped)
{
    public int Total => Measured + Skipped.Count;
}

/// <summary>Orchestrates a catalog build: sheet -> extract -> measure -> store. Optional for users,
/// since a measured catalog ships with the app; this is for re-measuring after a game patch and for
/// adding slots whose WEMs are outside pakchunk0.</summary>
public static class CatalogBuilder
{
    public static CatalogBuildResult Build(string sheetCsv, IExtractor extractor, string scratch, CatalogStore store,
                                           IProgress<(int done, int total, string what)>? progress = null,
                                           CancellationToken ct = default)
    {
        var identities = SheetLoader.Load(sheetCsv);
        Directory.CreateDirectory(scratch);
        var paths = extractor.Extract(WemIdsOf(identities), scratch, progress);
        return MeasureAll(identities, id => paths.TryGetValue(id, out var p) ? Wem.WemReader.ReadHeader(p, id) : null,
                          store, progress, ct, "WEM not exported", "in the export folder");
    }

    /// <summary>Build from the game install itself: <see cref="GameInstallWemSource"/> reads the headers out
    /// of the game's paks, so nothing has to be exported first.</summary>
    public static CatalogBuildResult Build(string sheetCsv, IWemHeadSource source, CatalogStore store,
                                           IProgress<(int done, int total, string what)>? progress = null,
                                           CancellationToken ct = default)
    {
        var identities = SheetLoader.Load(sheetCsv);
        var heads = source.ReadHeads(WemIdsOf(identities), progress);
        return MeasureAll(identities, id => heads.TryGetValue(id, out var h) ? Wem.WemReader.ReadHeader(h.Head, h.Size, id, $"{id}.wem") : null,
                          store, progress, ct, "WEM not in the game files", "in the game's paks");
    }

    private static SortedSet<int> WemIdsOf(IEnumerable<SlotIdentity> identities)
    {
        var ids = new SortedSet<int>();
        foreach (var i in identities)
        {
            ids.Add(i.LoopId);
            if (i.IntroId is int iid) ids.Add(iid);
        }
        return ids;
    }

    private static CatalogBuildResult MeasureAll(List<SlotIdentity> identities, Func<int, WemInfo?> header, CatalogStore store,
                                                 IProgress<(int done, int total, string what)>? progress, CancellationToken ct,
                                                 string skippedNote, string where)
    {
        var slots = new List<Slot>(identities.Count);
        var skipped = new List<SlotIdentity>();
        for (int n = 0; n < identities.Count; n++)
        {
            ct.ThrowIfCancellationRequested();
            var ident = identities[n];
            // A slot is measurable only if every WEM it names was found. Half a slot is useless:
            // the renderer needs both frame counts to hit the exact-length gate.
            var loop = header(ident.LoopId);
            WemInfo? intro = ident.IntroId is int iid ? header(iid) : null;
            if (loop is null || (ident.IntroId is not null && intro is null))
            {
                skipped.Add(ident);
                progress?.Report((n + 1, identities.Count, $"{ident.Title} (skipped, {skippedNote})"));
                continue;
            }
            slots.Add(new Slot(ident, intro, loop, null, Measured: true));
            progress?.Report((n + 1, identities.Count, ident.Title));
        }
        if (slots.Count == 0)
            throw new ExtractionException($"no slots could be measured — none of the sheet's WEMs were found {where}.");
        store.Upsert(slots);
        return new CatalogBuildResult(slots.Count, skipped);
    }

    /// <summary>The slots the ranking engine should use right now: measured when the catalog is built,
    /// otherwise provisional rows from the sheet so the UI is not empty on first run.</summary>
    public static (IReadOnlyList<Slot> slots, bool provisional) SlotsForRanking(CatalogStore store, string? sheetCsv)
    {
        if (store.IsBuilt) return (store.AllSlots(), false);
        if (sheetCsv is null || !File.Exists(sheetCsv)) return (Array.Empty<Slot>(), true);
        return (SheetLoader.Load(sheetCsv).Select(i => Slot.Provisional(i)).ToList(), true);
    }
}
