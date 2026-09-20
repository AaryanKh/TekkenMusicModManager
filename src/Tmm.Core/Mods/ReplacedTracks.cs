namespace Tmm.Core.Mods;

/// <summary>
/// Names the jukebox track a pak replaces, by matching the WEM ids inside it against the catalog. A
/// pak that carries both WEMs of a slot (its intro and its loop) replaces exactly that track; a
/// loop-only slot has a single WEM, so that one id is enough.
///
/// A pak holding only one half of a slot is not reported: it replaces part of a track, and calling it
/// "the track" would be wrong. A megapack matches many slots, one per complete pair.
/// </summary>
public static class ReplacedTracks
{
    /// <summary>Catalog slots whose every WEM is among <paramref name="pakWemIds"/>, in catalog order.</summary>
    public static List<Slot> Find(IEnumerable<int> pakWemIds, IEnumerable<Slot> catalog)
    {
        var ids = pakWemIds as ISet<int> ?? new HashSet<int>(pakWemIds);
        return catalog.Where(s => s.WemIds.All(ids.Contains)).OrderBy(s => s.Identity.No).ToList();
    }

    /// <summary>"Replaces: A; B; C and 4 more", or an empty string when nothing in the catalog matches.</summary>
    public static string Describe(IEnumerable<int> pakWemIds, IEnumerable<Slot> catalog, int show = 3)
    {
        var slots = Find(pakWemIds, catalog);
        if (slots.Count == 0) return "";
        var shown = string.Join("; ", slots.Take(show).Select(s => s.Title));
        return slots.Count > show ? $"Replaces: {shown} and {slots.Count - show} more" : $"Replaces: {shown}";
    }
}
