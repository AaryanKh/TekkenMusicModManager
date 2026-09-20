using Tmm.Core;
using Tmm.Core.Catalog;
using Tmm.Core.Mods;
using Tmm.Core.Wem;
using Xunit;

namespace Tmm.Core.Tests;

/// <summary>A third-party pak is matched to the track it replaces by the WEM ids inside it.</summary>
public class ReplacedTracksTests
{
    private static Slot Pair(int no, string title, int intro, int loop) => new(
        new SlotIdentity(no, title, "TEKKEN 7", intro, loop, 1, 2),
        new WemInfo(intro, 48_000, 48_000, 2, WemConstants.FormatWwiseVorbis, 0),
        new WemInfo(loop, 96_000, 48_000, 2, WemConstants.FormatWwiseVorbis, 0));

    private static Slot LoopOnly(int no, string title, int loop) => new(
        new SlotIdentity(no, title, "TEKKEN 7", null, loop, null, 2),
        null,
        new WemInfo(loop, 96_000, 48_000, 2, WemConstants.FormatWwiseVorbis, 0));

    private static readonly Slot[] Catalog =
    {
        Pair(1, "Alpha / TEKKEN 7", 10, 11),
        Pair(2, "Bravo / TEKKEN 7", 20, 21),
        LoopOnly(3, "Charlie / TEKKEN 7", 30),
        Pair(4, "Delta / TEKKEN 7", 40, 41),
        Pair(5, "Echo / TEKKEN 7", 50, 51),
    };

    [Fact]
    public void ABothWemsPakNamesItsTrack()
    {
        Assert.Equal("Replaces: Alpha / TEKKEN 7", ReplacedTracks.Describe(new[] { 10, 11 }, Catalog));
        Assert.Equal("Replaces: Alpha / TEKKEN 7", ReplacedTracks.Describe(new[] { 11, 10, 9999 }, Catalog));   // order and extras do not matter
    }

    [Fact]
    public void OneHalfOfATrackIsNotThatTrack()
    {
        Assert.Equal("", ReplacedTracks.Describe(new[] { 11 }, Catalog));     // loop only
        Assert.Equal("", ReplacedTracks.Describe(new[] { 10 }, Catalog));     // intro only
        Assert.Equal("", ReplacedTracks.Describe(new[] { 10, 21 }, Catalog)); // halves of two different tracks
    }

    [Fact]
    public void ALoopOnlySlotNeedsItsSingleWem()
        => Assert.Equal("Replaces: Charlie / TEKKEN 7", ReplacedTracks.Describe(new[] { 30 }, Catalog));

    [Fact]
    public void AnUnknownPakNamesNothing()
    {
        Assert.Equal("", ReplacedTracks.Describe(new[] { 1, 2 }, Catalog));
        Assert.Equal("", ReplacedTracks.Describe(Array.Empty<int>(), Catalog));
        Assert.Equal("", ReplacedTracks.Describe(new[] { 10, 11 }, Array.Empty<Slot>()));   // no catalog yet
    }

    [Fact]
    public void AMegapackListsEveryCompleteTrackInCatalogOrderAndSummarisesTheRest()
    {
        var all = new[] { 51, 50, 41, 40, 30, 21, 20, 11, 10 };
        var found = ReplacedTracks.Find(all, Catalog);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, found.Select(s => s.Identity.No));

        Assert.Equal("Replaces: Alpha / TEKKEN 7; Bravo / TEKKEN 7; Charlie / TEKKEN 7 and 2 more",
                     ReplacedTracks.Describe(all, Catalog));
        Assert.Equal("Replaces: Alpha / TEKKEN 7; Bravo / TEKKEN 7; Charlie / TEKKEN 7; Delta / TEKKEN 7; Echo / TEKKEN 7",
                     ReplacedTracks.Describe(all, Catalog, show: 5));
    }

    [Fact]
    public void WorksAgainstTheShippedCatalog()
    {
        // The pair the built mod on a real install overrides.
        var store = new CatalogStore(Path.Combine(Path.GetTempPath(), "tmm-none-" + Guid.NewGuid().ToString("N") + ".json"));
        Assert.True(store.IsBundled);
        Assert.Equal("Replaces: Angkor Wat, Cambodia (Arcade ver.) / TEKKEN",
                     ReplacedTracks.Describe(new[] { 942879699, 200163537 }, store.AllSlots()));

        // Every shipped slot is found by its own WEMs, and only by them.
        foreach (var slot in store.AllSlots())
            Assert.Contains(slot, ReplacedTracks.Find(slot.WemIds, store.AllSlots()));
    }
}
