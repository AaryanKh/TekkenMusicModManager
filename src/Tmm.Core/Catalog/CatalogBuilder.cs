namespace Tmm.Core.Catalog;

/// <summary>Outcome of a catalog build. <paramref name="Skipped"/> is non-empty only when the
/// extractor could not supply every WEM the sheet names — those slots are absent from the catalog
/// and cannot be modded until the missing WEMs are exported and the build is re-run.</summary>
public sealed record CatalogBuildResult(int Measured, IReadOnlyList<SlotIdentity> Skipped)
{
    public int Total => Measured + Skipped.Count;
}

/// <summary>Orchestrates first-run catalog build: sheet -> extract -> measure -> store.</summary>
public static class CatalogBuilder
{
    public static CatalogBuildResult Build(string sheetCsv, IExtractor extractor, string scratch, CatalogStore store,
                                           IProgress<(int done, int total, string what)>? progress = null,
                                           CancellationToken ct = default)
    {
        var identities = SheetLoader.Load(sheetCsv);
        var ids = new SortedSet<int>(identities.Select(i => i.LoopId));
        foreach (var i in identities) if (i.IntroId is int iid) ids.Add(iid);

        Directory.CreateDirectory(scratch);
        var paths = extractor.Extract(ids, scratch, progress);

        var slots = new List<Slot>(identities.Count);
        var skipped = new List<SlotIdentity>();
        for (int n = 0; n < identities.Count; n++)
        {
            ct.ThrowIfCancellationRequested();
            var ident = identities[n];
            // A slot is measurable only if every WEM it names was extracted. Half a slot is useless:
            // the renderer needs both frame counts to hit the exact-length gate.
            if (!paths.ContainsKey(ident.LoopId) || (ident.IntroId is int iid && !paths.ContainsKey(iid)))
            {
                skipped.Add(ident);
                progress?.Report((n + 1, identities.Count, $"{ident.Title} (skipped, WEM not exported)"));
                continue;
            }
            slots.Add(Measure.MeasureSlot(ident, paths));
            progress?.Report((n + 1, identities.Count, ident.Title));
        }
        if (slots.Count == 0)
            throw new ExtractionException("no slots could be measured — none of the sheet's WEMs were found in the export folder.");
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
