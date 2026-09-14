using System.Text;
using Tmm.Core;
using Tmm.Core.Mods;
using Tmm.Core.Pak;
using Xunit;

namespace Tmm.Core.Tests;

/// <summary>
/// Renaming a pak by hand in ~mods used to leave the mod reading Disabled and the file looking like
/// somebody else's work. These pin the matching and the adoption that fixes it.
/// </summary>
public class ReconcileTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "tmm-rec-" + Guid.NewGuid().ToString("N"));
    private readonly Settings _settings;
    private readonly ModRegistry _reg;
    private readonly string _modsDir;

    public ReconcileTests()
    {
        Directory.CreateDirectory(_tmp);
        var game = Path.Combine(_tmp, "game");
        Directory.CreateDirectory(Path.Combine(game, Constants.PaksRelative));
        _settings = new Settings { AppDir = Path.Combine(_tmp, "app"), GameRoot = game };
        _reg = new ModRegistry(_settings);
        _modsDir = _settings.GameModsDir!;
        Directory.CreateDirectory(_modsDir);
    }

    public void Dispose() { try { FileOps.DeleteDirectory(_tmp); } catch { } }

    /// <summary>A pak body the scanner can read IDs out of, shaped like the real index: the directory
    /// appears once, each entry only by filename.</summary>
    private static byte[] PakBytes(params int[] wemIds)
    {
        var sb = new StringBuilder();
        sb.Append("../../../").Append(Constants.WemMediaPakPath).Append('\0');
        foreach (var id in wemIds) sb.Append('\0').Append(id).Append(".wem").Append('\0');
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private ModManifest NewMod(string name, params int[] wemIds)
    {
        var m = new ModManifest
        {
            Name = name, PakName = $"{name}_P.pak", SlotKey = wemIds[^1], SlotTitle = name,
            WemIds = wemIds.ToList(), SongPath = "x.mp3", Updated = DateTime.UtcNow.AddMinutes(-5),
        };
        Directory.CreateDirectory(_reg.ModDir(m));
        File.WriteAllBytes(_reg.StorePak(m), PakBytes(wemIds));
        _reg.Save(m);
        return m;
    }

    /// <summary>Enable it, then rename it in ~mods the way a user tidying their folder would.</summary>
    private string InstallAs(ModManifest m, string newName)
    {
        var dest = Path.Combine(_modsDir, newName);
        File.Copy(_reg.StorePak(m), dest, overwrite: true);
        return dest;
    }

    [Fact]
    public void ScannerFindsIdsWhenOnlyTheFilenameIsInThePak()
    {
        // The bug behind all of this: the index stores the folder once and filenames separately, so a
        // pattern anchored on the full path found nothing.
        var pak = Path.Combine(_tmp, "probe.pak");
        File.WriteAllBytes(pak, PakBytes(30861190, 456809320));
        var ids = PakScan.FindWemIds(pak);
        Assert.Equal(new[] { 30861190, 456809320 }, ids.OrderBy(i => i));
    }

    [Fact]
    public void ARenamedPakIsMatchedToItsMod()
    {
        var m = NewMod("SeymourBattle", 30861190, 456809320);
        InstallAs(m, "[T7]_FinalFantasyX_SeymourBattle_P.pak");

        Assert.Equal(ModState.Disabled, _reg.StateOf(m));   // the symptom

        var found = Reconcile.FindRenamed(_reg, _modsDir);
        var r = Assert.Single(found);
        Assert.Equal(m.ModId, r.Mod.ModId);
        Assert.Equal("SeymourBattle_P.pak", r.ExpectedName);
        Assert.Equal("[T7]_FinalFantasyX_SeymourBattle_P.pak", r.FoundName);
    }

    [Fact]
    public void AdoptingMakesTheModReadEnabledAndRenamesTheStoreCopy()
    {
        var m = NewMod("SeymourBattle", 30861190, 456809320);
        InstallAs(m, "[T7]_Seymour_P.pak");

        var log = Reconcile.Adopt(_reg, Reconcile.FindRenamed(_reg, _modsDir));
        Assert.Single(log);

        var reloaded = _reg.Get(m.ModId);
        Assert.Equal("[T7]_Seymour_P.pak", reloaded.PakName);
        Assert.True(File.Exists(_reg.StorePak(reloaded)), "the store copy should have been renamed to match");
        Assert.False(File.Exists(Path.Combine(_reg.ModDir(reloaded), "SeymourBattle_P.pak")));
        Assert.Equal(ModState.Enabled, _reg.StateOf(reloaded));
    }

    [Fact]
    public void AnAdoptedModNoLongerLooksThirdParty()
    {
        var m = NewMod("SeymourBattle", 30861190, 456809320);
        InstallAs(m, "[T7]_Seymour_P.pak");
        Assert.Single(Conflicts.ScanThirdParty(_reg, _modsDir));       // misread as someone else's

        Reconcile.Adopt(_reg, Reconcile.FindRenamed(_reg, _modsDir));
        Assert.Empty(Conflicts.ScanThirdParty(_reg, _modsDir));
    }

    [Fact]
    public void AModInstalledUnderItsOwnNameIsLeftAlone()
    {
        var m = NewMod("Plain", 111111, 222222);
        InstallAs(m, m.PakName);
        Assert.Empty(Reconcile.FindRenamed(_reg, _modsDir));
    }

    [Fact]
    public void SomebodyElsesPakIsNotClaimed()
    {
        NewMod("Mine", 111111, 222222);
        File.WriteAllBytes(Path.Combine(_modsDir, "SomeoneElse_P.pak"), PakBytes(999999, 888888));
        Assert.Empty(Reconcile.FindRenamed(_reg, _modsDir));
    }

    [Fact]
    public void TwoModsOnTheSameSlotAreSeparatedByContent()
    {
        // Same WEM IDs, so IDs alone cannot decide. The bytes can.
        var a = NewMod("VersionA", 111111, 222222);
        var b = NewMod("VersionB", 111111, 222222);
        File.WriteAllBytes(_reg.StorePak(b), PakBytes(111111, 222222).Concat(new byte[] { 7, 7, 7 }).ToArray());

        File.Copy(_reg.StorePak(b), Path.Combine(_modsDir, "[T8]_Renamed_P.pak"));

        var found = Reconcile.FindRenamed(_reg, _modsDir);
        var r = Assert.Single(found);
        Assert.Equal(b.ModId, r.Mod.ModId);
        Assert.Contains("identical", r.Evidence);
    }

    [Fact]
    public void AmbiguityIsLeftAloneRatherThanGuessed()
    {
        // Two indistinguishable mods and a renamed pak that matches both: adopting either could point
        // the wrong manifest at the file, so nothing is claimed.
        NewMod("VersionA", 111111, 222222);
        NewMod("VersionB", 111111, 222222);
        File.WriteAllBytes(Path.Combine(_modsDir, "[T8]_Renamed_P.pak"), PakBytes(333333, 444444));

        Assert.Empty(Reconcile.FindRenamed(_reg, _modsDir));
    }

    [Fact]
    public void APakWearingAnotherModsNameIsNotClaimed()
    {
        // Alpha's pak renamed to exactly Beta's expected filename. That name is already spoken for,
        // so nothing is adopted rather than two manifests being pointed at one file.
        var a = NewMod("Alpha", 111111, 222222);
        NewMod("Beta", 333333, 444444);
        InstallAs(a, "Beta_P.pak");

        Assert.Empty(Reconcile.FindRenamed(_reg, _modsDir));
        Assert.Equal("Alpha_P.pak", _reg.Get(a.ModId).PakName);
    }
}
