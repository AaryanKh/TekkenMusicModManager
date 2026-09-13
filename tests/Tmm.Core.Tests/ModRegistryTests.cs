using Tmm.Core;
using Tmm.Core.Catalog;
using Tmm.Core.Mods;
using Xunit;

namespace Tmm.Core.Tests;

/// <summary>ModState is derived from the filesystem every time; these pin that contract.</summary>
public class ModRegistryTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "tmm-reg-" + Guid.NewGuid().ToString("N"));
    private readonly Settings _settings;
    private readonly ModRegistry _reg;

    public ModRegistryTests()
    {
        Directory.CreateDirectory(_tmp);
        var game = Path.Combine(_tmp, "game");
        Directory.CreateDirectory(Path.Combine(game, Constants.PaksRelative));
        _settings = new Settings { AppDir = Path.Combine(_tmp, "app"), GameRoot = game };
        _reg = new ModRegistry(_settings);
    }

    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    private ModManifest NewMod(string name, params int[] wemIds)
    {
        var m = new ModManifest { Name = name, PakName = $"{name}_P.pak", SlotKey = wemIds[^1], SlotTitle = name, WemIds = wemIds.ToList(), SongPath = "x.mp3" };
        Directory.CreateDirectory(_reg.ModDir(m));
        File.WriteAllBytes(_reg.StorePak(m), new byte[] { 1, 2, 3 });
        m.Updated = DateTime.UtcNow.AddMinutes(-1);
        _reg.Save(m);
        return m;
    }

    [Fact]
    public void StateFollowsTheFilesystem()
    {
        var m = NewMod("A", 10, 11);
        Assert.Equal(ModState.Disabled, _reg.StateOf(m));

        Installer.Enable(m, _reg);
        Assert.Equal(ModState.Enabled, _reg.StateOf(m));
        Assert.True(File.Exists(Path.Combine(_settings.GameModsDir!, "A_P.pak")));
        Assert.EndsWith(Constants.ModsDirName, _settings.GameModsDir!);   // "~mods", never "Mods"

        File.Delete(Path.Combine(_settings.GameModsDir!, "A_P.pak"));    // user removed it by hand
        Assert.Equal(ModState.Disabled, _reg.StateOf(m));

        _reg.UpdatePlan(m, m.Plan);
        Assert.Equal(ModState.Stale, _reg.StateOf(m));

        File.Delete(_reg.StorePak(m));
        Assert.Equal(ModState.Broken, _reg.StateOf(m));
    }

    [Fact]
    public void EnableRefusesOverlappingWemIds()
    {
        var a = NewMod("A", 10, 11);
        var b = NewMod("B", 11, 12);
        Installer.Enable(a, _reg);
        var ex = Assert.Throws<SlotConflictException>(() => Installer.Enable(b, _reg));
        Assert.Single(ex.Conflicts);
        Assert.Equal(11, ex.Conflicts[0].WemId);
        Installer.Enable(b, _reg, force: true);
        Assert.Equal(ModState.Enabled, _reg.StateOf(b));
    }

    [Fact]
    public void ThirdPartyPaksAreScannedForWemIds()
    {
        var a = NewMod("A", 10, 11);
        var mods = _settings.GameModsDir!;
        Directory.CreateDirectory(mods);
        File.WriteAllText(Path.Combine(mods, "MegaPack_P.pak"), "junk Polaris/Content/WwiseAudio/Media/11.wem junk ..\\..\\..\\Polaris\\Content\\WwiseAudio\\Media\\999.wem");
        var tp = Conflicts.ScanThirdParty(_reg, mods);
        Assert.Single(tp);
        Assert.Equal(new[] { 11, 999 }, tp[0].WemIds);
        var ex = Assert.Throws<SlotConflictException>(() => Installer.Enable(a, _reg));
        Assert.True(ex.Conflicts[0].ThirdParty);
    }

    [Fact]
    public void DeleteRemovesEverything()
    {
        var a = NewMod("A", 10, 11);
        Installer.Enable(a, _reg);
        Installer.Delete(a, _reg);
        Assert.False(Directory.Exists(_reg.ModDir(a)));
        Assert.False(File.Exists(Path.Combine(_settings.GameModsDir!, "A_P.pak")));
        Assert.Empty(_reg.All());
    }

    [Fact]
    public void SheetLoadsAllRowsIncludingUnnumberedOnes()
    {
        var rows = SheetLoader.Load(Path.Combine(AppContext.BaseDirectory, "data", "jukebox_slots.csv"));
        Assert.Equal(443, rows.Count);
        Assert.Equal(26, rows.Count(r => r.IntroId is null));
        Assert.Contains(rows, r => r.Title.StartsWith("Ancient Powers (Normal)"));
        Assert.Equal(rows.Count, rows.Select(r => r.LoopId).Distinct().Count());
    }
}
