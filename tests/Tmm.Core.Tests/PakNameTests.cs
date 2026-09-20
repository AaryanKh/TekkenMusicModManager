using Tmm.Core;
using Tmm.Core.Audio;
using Tmm.Core.Mods;
using Tmm.Core.Pak;
using Tmm.Core.Render;
using Xunit;

namespace Tmm.Core.Tests;

/// <summary>
/// Two mods with the same pak file name used to overwrite each other in ~mods while both read
/// "Enabled": state is derived from whether a file of that name exists. These pin the guards.
/// </summary>
public class PakNameTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "tmm-name-" + Guid.NewGuid().ToString("N"));
    private readonly Settings _settings;
    private readonly ModRegistry _reg;

    public PakNameTests()
    {
        Directory.CreateDirectory(_tmp);
        var game = Path.Combine(_tmp, "game");
        Directory.CreateDirectory(Path.Combine(game, Constants.PaksRelative));
        _settings = new Settings { AppDir = Path.Combine(_tmp, "app"), GameRoot = game };
        _reg = new ModRegistry(_settings);
    }

    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    /// <summary>Same as the registry tests' helper. Called twice with one name it yields two mods that
    /// share a pak name, which is how mods built before the check exist on disk.</summary>
    private ModManifest NewMod(string name, string payload, params int[] wemIds)
    {
        var m = new ModManifest { Name = name, PakName = PakLayout.PakFilename(name), SlotKey = wemIds[^1], SlotTitle = name, WemIds = wemIds.ToList(), SongPath = "x.mp3" };
        Directory.CreateDirectory(_reg.ModDir(m));
        File.WriteAllText(_reg.StorePak(m), payload);
        m.Updated = DateTime.UtcNow.AddMinutes(-1);
        _reg.Save(m);
        return m;
    }

    private string ModsDir => _settings.GameModsDir!;

    // ------------------------------------------------------------------ naming

    [Fact]
    public void AFreeNameHasNoConflict()
    {
        NewMod("Other", "x", 1, 2);
        Assert.Null(_reg.FindNameConflict("My Song"));
    }

    [Theory]
    [InlineData("My_Song")]      // exact
    [InlineData("my_song")]      // case-insensitive, like the filesystem
    [InlineData("My Song")]      // sanitised: spaces become underscores
    [InlineData("My Song!")]     // sanitised: punctuation is dropped
    [InlineData("My_Song_P")]    // sanitised: a trailing _P is stripped, then re-added
    public void ANameThatSanitisesToAnExistingPakIsRefused(string typed)
    {
        NewMod("My_Song", "x", 1, 2);
        var why = _reg.FindNameConflict(typed);
        Assert.NotNull(why);
        Assert.Contains("My_Song_P.pak", why);
        Assert.Contains("already used by your mod", why);
    }

    [Fact]
    public void APakAlreadyInModsBlocksTheNameEvenInASubfolder()
    {
        Directory.CreateDirectory(Path.Combine(ModsDir, "packs"));
        File.WriteAllText(Path.Combine(ModsDir, "packs", "Foreign_P.pak"), "someone else's");

        var why = _reg.FindNameConflict("Foreign");
        Assert.NotNull(why);
        Assert.Contains("already in ~mods", why);
        Assert.Contains("overwrite", why);
    }

    [Fact]
    public void MakeUniqueNameWalksPastEveryTakenSuffix()
    {
        Assert.Equal("Song", _reg.MakeUniqueName("Song"));
        NewMod("Song", "x", 1, 2);
        Assert.Equal("Song_2", _reg.MakeUniqueName("Song"));
        NewMod("Song_2", "x", 3, 4);
        Assert.Equal("Song_3", _reg.MakeUniqueName("Song"));
    }

    [Fact]
    public void BuildRefusesATakenNameBeforeTouchingAnything()
    {
        var first = NewMod("Taken", "original", 1, 2);
        var builder = new ModBuilder(_reg, new ResampleStretcher(), new NoPacker());
        var slot = Slot.Provisional(new SlotIdentity(1, "Slot", "TEKKEN 7", 100, 200, 5, 60));
        var pcm = new PcmBuffer(48_000, 2, 48_000);

        var ex = Assert.Throws<InstallException>(() =>
            builder.Build("Taken", Path.Combine(_tmp, "song.wav"), pcm, slot, new RenderPlan()));

        Assert.Contains("Taken_P.pak", ex.Message);
        Assert.Single(_reg.All());                                   // no second manifest was written
        Assert.Equal("original", File.ReadAllText(_reg.StorePak(first)));   // and the first mod's pak is intact
    }

    // ------------------------------------------------------------------ enabling

    [Fact]
    public void EnableRefusesAModWhosePakNameAnotherModOwns()
    {
        var a = NewMod("Same", "PAK-A", 10, 11);
        var b = NewMod("Same", "PAK-B", 20, 21);   // different slot, so the WEM-id check would not object
        Assert.NotEqual(a.ModId, b.ModId);

        var ex = Assert.ThrowsAny<InstallException>(() => Installer.Enable(a, _reg));
        Assert.IsNotType<SlotConflictException>(ex);                 // not the "enable anyway?" prompt
        Assert.Contains("Same_P.pak", ex.Message);
        Assert.False(File.Exists(Path.Combine(ModsDir, "Same_P.pak")));   // nothing was installed
        Assert.ThrowsAny<InstallException>(() => Installer.Enable(b, _reg));
    }

    [Fact]
    public void ForceDoesNotBypassASharedPakName()
    {
        var a = NewMod("Same", "PAK-A", 10, 11);
        NewMod("Same", "PAK-B", 20, 21);
        var ex = Assert.ThrowsAny<InstallException>(() => Installer.Enable(a, _reg, force: true));
        Assert.IsNotType<SlotConflictException>(ex);
        Assert.False(File.Exists(Path.Combine(ModsDir, "Same_P.pak")));
    }

    [Fact]
    public void UniquelyNamedModsStillEnableAndReEnable()
    {
        var a = NewMod("A", "PAK-A", 10, 11);
        var b = NewMod("B", "PAK-B", 20, 21);
        Installer.Enable(a, _reg);
        Installer.Enable(b, _reg);
        Assert.Equal("PAK-A", File.ReadAllText(Path.Combine(ModsDir, "A_P.pak")));
        Assert.Equal("PAK-B", File.ReadAllText(Path.Combine(ModsDir, "B_P.pak")));

        _reg.UpdatePlan(a, a.Plan);                                  // stale but installed: re-enabling is fine
        Installer.Enable(a, _reg);
        Assert.Equal(ModState.Enabled, _reg.StateOf(b));
    }

    [Fact]
    public void RebuildOfAnEnabledModRefusesASharedNameBeforeRendering()
    {
        var song = Path.Combine(_tmp, "song.mp3");
        File.WriteAllBytes(song, new byte[] { 0 });
        var a = NewMod("Same", "PAK-A", 10, 11);
        a.SongPath = song; _reg.Save(a);
        NewMod("Same", "PAK-B", 20, 21);
        Directory.CreateDirectory(ModsDir);
        File.WriteAllText(Path.Combine(ModsDir, "Same_P.pak"), "PAK-A");   // currently installed

        var builder = new ModBuilder(_reg, new ResampleStretcher(), new NoPacker());
        var slot = Slot.Provisional(new SlotIdentity(1, "Slot", "TEKKEN 7", 100, 200, 5, 60));

        // "ffmpeg" is never reached: the check fires first, so no decode or render happens.
        var ex = Assert.Throws<InstallException>(() => builder.Rebuild(a, slot, "ffmpeg-not-needed"));
        Assert.Contains("Same_P.pak", ex.Message);
        Assert.Equal("PAK-A", File.ReadAllText(_reg.StorePak(a)));   // the store copy was not replaced
    }

    private sealed class NoPacker : IPacker
    {
        public string Name => "none";
        public string Pack(string stagedRoot, string outPak) => throw new InvalidOperationException("must not pack");
        public IReadOnlyList<string> List(string pak) => Array.Empty<string>();
    }
}
