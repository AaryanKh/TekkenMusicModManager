using Tmm.Core;
using Tmm.Core.Catalog;
using Xunit;

namespace Tmm.Core.Tests;

/// <summary>
/// Guards the catalog shipped in <c>data\stock_catalog.json</c>. It is the reason a normal install
/// does not need FModel, so a corrupt or truncated file has to fail here rather than in someone's
/// first render.
/// </summary>
public class ShippedCatalogTests
{
    private static string Bundled() =>
        CatalogStore.BundledPath() ?? throw new InvalidOperationException("data/stock_catalog.json was not copied to the test output");

    [Fact]
    public void ShippedCatalogIsPresentAndBigEnoughToBeUseful()
    {
        var store = new CatalogStore(Path.Combine(Path.GetTempPath(), $"tmm-none-{Guid.NewGuid():N}", "catalog.json"));
        Assert.True(store.IsBuilt, "a fresh install must not be catalog-less");
        Assert.True(store.IsBundled, "with no user catalog the shipped one should load");
        Assert.True(store.Count >= 400, $"only {store.Count} slots shipped");
    }

    [Fact]
    public void EveryShippedSlotIsMeasuredAndRenderable()
    {
        var store = new CatalogStore(Path.Combine(Path.GetTempPath(), $"tmm-none-{Guid.NewGuid():N}", "catalog.json"));
        foreach (var slot in store.AllSlots())
        {
            // Provisional rows would be refused by the renderer, which would defeat the point.
            Assert.True(slot.Measured, $"slot {slot.Key} ({slot.Title}) is provisional");
            Assert.True(slot.LoopFrames > 0, $"slot {slot.Key} has no loop frames");
            Assert.True(slot.Loop.SampleRate > 0, $"slot {slot.Key} has no sample rate");
            Assert.True(slot.Loop.Channels is 1 or 2, $"slot {slot.Key} has {slot.Loop.Channels} channels");
            if (slot.HasIntro) Assert.True(slot.IntroFrames > 0, $"slot {slot.Key} has an intro WEM with no frames");
        }
    }

    [Fact]
    public void ShippedLengthsAgreeWithTheSheetToWithinRounding()
    {
        // The sheet carries whole seconds. A measured length that disagrees by more than a second
        // means the rows were built against the wrong game version or the wrong IDs.
        var store = new CatalogStore(Path.Combine(Path.GetTempPath(), $"tmm-none-{Guid.NewGuid():N}", "catalog.json"));
        foreach (var slot in store.AllSlots())
        {
            double sheet = slot.Identity.LoopSecSheet;
            Assert.True(Math.Abs(slot.LoopSeconds - sheet) < 1.0,
                $"slot {slot.Key} ({slot.Title}): measured {slot.LoopSeconds:0.00}s vs sheet {sheet}s");
        }
    }

    [Fact]
    public void SlotKeysAreUnique()
    {
        var store = new CatalogStore(Path.Combine(Path.GetTempPath(), $"tmm-none-{Guid.NewGuid():N}", "catalog.json"));
        var keys = store.AllSlots().Select(s => s.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public void AUserBuiltCatalogWinsOverTheShippedOne()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tmm-cat-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "catalog.json");
            var id = new SlotIdentity(1, "Local", "TEKKEN 8", null, 4242, null, 10);
            var slot = new Slot(id, null, new WemInfo(4242, 480000, 48000, 2, 0xFFFF, 0));
            new CatalogStore(path).Upsert(new[] { slot });

            var reopened = new CatalogStore(path);
            Assert.False(reopened.IsBundled, "a catalog written locally must not still report as shipped");
            Assert.NotNull(reopened.Get(4242));
            Assert.Equal(480000, reopened.Get(4242)!.LoopFrames);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void RebuildingStartsFromTheShippedRowsRatherThanWipingThem()
    {
        // Upsert merges, and the in-memory rows start as the shipped ones, so a partial rebuild
        // updates what it measured and leaves every other slot at its shipped value instead of
        // dropping it. Worth pinning down: it is the difference between a partial re-measure and
        // losing 400 slots.
        var dir = Path.Combine(Path.GetTempPath(), $"tmm-cat-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "catalog.json");
            int shipped = new CatalogStore(path).Count;

            var id = new SlotIdentity(1, "Local", "TEKKEN 8", null, 4242, null, 10);
            new CatalogStore(path).Upsert(new[] { new Slot(id, null, new WemInfo(4242, 480000, 48000, 2, 0xFFFF, 0)) });

            var reopened = new CatalogStore(path);
            Assert.Equal(shipped + 1, reopened.Count);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
