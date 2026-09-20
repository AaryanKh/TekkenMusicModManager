using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tmm.Core;
using Tmm.Core.Audio;
using Tmm.Core.Mods;
using Tmm.Core.Pak;
using Tmm.Core.Render;
using Tmm.Core.Wem;
using Xunit;

namespace Tmm.Core.Tests;

/// <summary>
/// The optional metadata file beside a pak: written on build, carried into ~mods on enable, removed on
/// disable, and read back so a pak the app does not know can be recognised and imported.
/// </summary>
public class ModMetadataTests : IDisposable
{
    private const int IntroId = 111, LoopId = 222;

    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "tmm-meta-" + Guid.NewGuid().ToString("N"));
    private readonly Settings _settings;
    private readonly ModRegistry _reg;

    public ModMetadataTests()
    {
        Directory.CreateDirectory(_tmp);
        var game = Path.Combine(_tmp, "game");
        Directory.CreateDirectory(Path.Combine(game, Constants.PaksRelative));
        _settings = new Settings { AppDir = Path.Combine(_tmp, "app"), GameRoot = game };
        _reg = new ModRegistry(_settings);
    }

    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    private string ModsDir => _settings.GameModsDir!;

    // ------------------------------------------------------------------ building a real mod

    private static Slot TestSlot() => new(
        new SlotIdentity(1, "Test Track / TEKKEN 7", "TEKKEN 7", IntroId, LoopId, 1, 2),
        new WemInfo(IntroId, 48_000, 48_000, 2, WemConstants.FormatWwiseVorbis, 0),
        new WemInfo(LoopId, 96_000, 48_000, 2, WemConstants.FormatWwiseVorbis, 0));

    private static PcmBuffer TestSong()
    {
        var pcm = new PcmBuffer(48_000 * 10, 2, 48_000);
        for (int i = 0; i < pcm.Frames; i++)
        {
            float v = 0.2f * MathF.Sin(2 * MathF.PI * 220f * i / 48_000f);
            pcm[i, 0] = v; pcm[i, 1] = v;
        }
        return pcm;
    }

    private static RenderPlan TestPlan() => new()
    {
        SlotKey = LoopId, LoopStartSec = 3.0, LoopBars = 1, Rho = 1.0,
        IntroStrategy = IntroStrategy.Real, CrossfadeMs = 20, SongFingerprint = "abc123",
    };

    /// <summary>Stands in for UnrealPak. The bytes name the WEMs the way a real pak index does, which is
    /// what <see cref="PakScan"/> looks for.</summary>
    private sealed class FakePacker : IPacker
    {
        public string Name => "fake";
        public string Pack(string stagedRoot, string outPak)
        {
            var media = Path.Combine(stagedRoot, Constants.WemMediaRelative);
            var ids = Directory.GetFiles(media, "*.wem").Select(Path.GetFileName);
            File.WriteAllText(outPak, "PAKHEADER " + string.Join(" ", ids.Select(f => $"{Constants.WemMediaPakPath}/{f}")));
            return outPak;
        }
        public IReadOnlyList<string> List(string pak) => Array.Empty<string>();
    }

    private ModManifest Build(string name, bool metadata)
    {
        _settings.WriteModMetadata = metadata;
        var builder = new ModBuilder(_reg, new ResampleStretcher(), new FakePacker());
        // A song in a folder named after a person, to prove the folder never reaches the file.
        var song = Path.Combine(_tmp, "Some Person", "My Song.flac");
        return builder.Build(name, song, TestSong(), TestSlot(), TestPlan());
    }

    // ------------------------------------------------------------------ writing

    [Fact]
    public void NothingExtraIsWrittenUnlessTheSettingIsOn()
    {
        Assert.False(new Settings().WriteModMetadata);   // off by default

        var off = Build("Plain", metadata: false);
        Assert.False(File.Exists(ModMetadata.PathFor(_reg.StorePak(off))));

        var on = Build("Described", metadata: true);
        Assert.True(File.Exists(ModMetadata.PathFor(_reg.StorePak(on))));
    }

    [Fact]
    public void TheFileNamesTheModAndHoldsNoFolders()
    {
        var m = Build("Described", metadata: true);
        var path = ModMetadata.PathFor(_reg.StorePak(m));
        var json = File.ReadAllText(path);

        Assert.EndsWith("Described_P.pak.tmm.json", path);
        var meta = ModMetadata.TryRead(path)!;
        Assert.Equal(m.ModId, meta.ModId);
        Assert.Equal("Described", meta.Name);
        Assert.Equal("Described_P.pak", meta.PakName);
        Assert.Equal(LoopId, meta.SlotKey);
        Assert.Equal("Test Track / TEKKEN 7", meta.SlotTitle);
        Assert.Equal(new[] { IntroId, LoopId }, meta.WemIds);
        Assert.Equal("My Song.flac", meta.SongFile);
        Assert.Equal("abc123", meta.SongFingerprint);

        Assert.DoesNotContain("Some Person", json);   // the song's folder is a person's account name
        Assert.DoesNotContain(_tmp, json);
        Assert.DoesNotContain("LoopStartSec", json, StringComparison.OrdinalIgnoreCase);   // and the plan stays out: "small"
    }

    // ------------------------------------------------------------------ enable / disable

    [Fact]
    public void EnableCarriesTheFileIntoModsAndDisableTakesItBack()
    {
        var m = Build("Described", metadata: true);
        var pak = Path.Combine(ModsDir, m.PakName);

        Installer.Enable(m, _reg);
        Assert.True(File.Exists(pak));
        Assert.True(File.Exists(ModMetadata.PathFor(pak)));
        Assert.Equal(File.ReadAllText(ModMetadata.PathFor(_reg.StorePak(m))), File.ReadAllText(ModMetadata.PathFor(pak)));

        Installer.Disable(m, _reg);
        Assert.False(File.Exists(pak));
        Assert.False(File.Exists(ModMetadata.PathFor(pak)));
        Assert.True(File.Exists(ModMetadata.PathFor(_reg.StorePak(m))));   // the store keeps its copy, so toggling is lossless

        Installer.Enable(m, _reg);
        Assert.True(File.Exists(ModMetadata.PathFor(pak)));
    }

    [Fact]
    public void EnablingAModWithoutMetadataClearsAStaleFileFromModsAndDeleteRemovesEverything()
    {
        var m = Build("Plain", metadata: false);
        Directory.CreateDirectory(ModsDir);
        var stale = ModMetadata.PathFor(Path.Combine(ModsDir, m.PakName));
        File.WriteAllText(stale, "{}");

        Installer.Enable(m, _reg);
        Assert.False(File.Exists(stale));
        Installer.Disable(m, _reg);          // both mods share the test slot, so only one may be enabled at a time

        var d = Build("Described", metadata: true);
        Installer.Enable(d, _reg);
        Installer.Delete(d, _reg);
        Assert.Empty(Directory.GetFiles(ModsDir, "Described*"));
        Assert.False(Directory.Exists(_reg.ModDir(d)));
    }

    // ------------------------------------------------------------------ reading is defensive

    private string WriteSidecar(Action<JsonObject>? tweak = null, string fileName = "Some_P.pak.tmm.json")
    {
        var node = new JsonObject
        {
            ["format"] = "tmm-mod", ["version"] = 1, ["modId"] = Guid.NewGuid().ToString(),
            ["name"] = "Some", ["pakName"] = "Some_P.pak", ["slotKey"] = LoopId, ["slotTitle"] = "Test Track",
            ["wemIds"] = new JsonArray(IntroId, LoopId), ["songFile"] = "a.flac", ["songFingerprint"] = "ff",
            ["created"] = "2026-09-19T19:54:13Z", ["appVersion"] = "0.1.0",
        };
        tweak?.Invoke(node);
        var path = Path.Combine(_tmp, fileName);
        File.WriteAllText(path, node.ToJsonString());
        return path;
    }

    [Fact]
    public void AWellFormedFileIsRead() => Assert.NotNull(ModMetadata.TryRead(WriteSidecar()));

    [Theory]
    [InlineData("wrong format")]
    [InlineData("version zero")]
    [InlineData("newer version")]
    [InlineData("mod id is a path")]        // becomes a folder name in the app store
    [InlineData("mod id is not a guid")]
    [InlineData("pak name has a folder")]   // is joined onto ~mods
    [InlineData("pak name climbs out")]
    [InlineData("pak name is not a pak")]
    [InlineData("no wem ids")]
    [InlineData("too many wem ids")]
    [InlineData("no name")]
    public void AFileThatFailsValidationIsIgnored(string defect)
    {
        var path = WriteSidecar(n =>
        {
            switch (defect)
            {
                case "wrong format": n["format"] = "something-else"; break;
                case "version zero": n["version"] = 0; break;
                case "newer version": n["version"] = ModMetadata.CurrentVersion + 1; break;
                case "mod id is a path": n["modId"] = @"..\..\..\Windows\evil"; break;
                case "mod id is not a guid": n["modId"] = "hello"; break;
                case "pak name has a folder": n["pakName"] = "sub/Some_P.pak"; break;
                case "pak name climbs out": n["pakName"] = @"..\Some_P.pak"; break;
                case "pak name is not a pak": n["pakName"] = "Some_P.exe"; break;
                case "no wem ids": n["wemIds"] = new JsonArray(); break;
                case "too many wem ids": n["wemIds"] = new JsonArray(Enumerable.Range(1, 40).Select(i => (JsonNode)i).ToArray()); break;
                case "no name": n["name"] = "  "; break;
            }
        });
        Assert.Null(ModMetadata.TryRead(path));
    }

    [Fact]
    public void GarbageEmptyAndOversizeFilesAreIgnoredNotFatal()
    {
        var garbage = Path.Combine(_tmp, "g.tmm.json"); File.WriteAllText(garbage, "not json {{{");
        var empty = Path.Combine(_tmp, "e.tmm.json"); File.WriteAllBytes(empty, Array.Empty<byte>());
        var big = Path.Combine(_tmp, "b.tmm.json"); File.WriteAllText(big, new string(' ', 200_000) + "{}");
        Assert.Null(ModMetadata.TryRead(garbage));
        Assert.Null(ModMetadata.TryRead(empty));
        Assert.Null(ModMetadata.TryRead(big));
        Assert.Null(ModMetadata.TryRead(Path.Combine(_tmp, "missing.tmm.json")));
    }

    [Fact]
    public void ASongFileWithAFolderInItIsReducedToTheName()
    {
        var meta = ModMetadata.TryRead(WriteSidecar(n => n["songFile"] = @"C:\Users\Someone\Music\a.flac"))!;
        Assert.Equal("a.flac", meta.SongFile);
    }

    // ------------------------------------------------------------------ recognising and importing

    /// <summary>A mod built with metadata and enabled, then the app's own record of it wiped: the state of
    /// a pak on a new machine or after clearing UserData.</summary>
    private ModManifest EnabledThenForgotten(string name = "Described")
    {
        var m = Build(name, metadata: true);
        Installer.Enable(m, _reg);
        Directory.Delete(_reg.ModDir(m), true);
        Assert.Empty(_reg.All());
        return m;
    }

    [Fact]
    public void APakTheAppAlreadyKnowsIsNotOfferedForImport()
    {
        var m = Build("Described", metadata: true);
        Installer.Enable(m, _reg);
        Assert.Empty(ModImport.Find(_reg, ModsDir));
    }

    [Fact]
    public void AForgottenPakIsRecognisedAndImportedIntoAWorkingMod()
    {
        var original = EnabledThenForgotten();
        var pak = Path.Combine(ModsDir, original.PakName);
        var pakBytes = File.ReadAllBytes(pak);

        var found = ModImport.Find(_reg, ModsDir);
        var r = Assert.Single(found);
        Assert.Equal("Described", r.Meta.Name);
        Assert.Equal("Test Track / TEKKEN 7", r.Meta.SlotTitle);

        // Before importing it is somebody else's pak as far as the third-party scan goes.
        Assert.Single(Conflicts.ScanThirdParty(_reg, ModsDir));

        var result = ModImport.Import(_reg, found);
        Assert.Equal(1, result.Imported);

        var m = Assert.Single(_reg.All());
        Assert.Equal(original.ModId, m.ModId);                 // identity survives the trip
        Assert.True(m.Imported);
        Assert.Equal("", m.SongPath);
        Assert.Equal(new[] { IntroId, LoopId }, m.WemIds);
        Assert.Equal(ModState.Enabled, _reg.StateOf(m));       // its pak is in ~mods, so it reads Enabled
        Assert.Equal(new[] { LoopId, IntroId }.OrderBy(i => i), _reg.ClaimedWemIds().Keys.OrderBy(i => i));   // and takes part in conflict detection
        Assert.Empty(Conflicts.ScanThirdParty(_reg, ModsDir)); // no longer somebody else's
        Assert.Equal(pakBytes, File.ReadAllBytes(pak));        // importing changed nothing in ~mods
        Assert.True(File.Exists(_reg.StorePak(m)));            // the pak was copied into the store
        Assert.True(File.Exists(ModMetadata.PathFor(_reg.StorePak(m))));
        Assert.Empty(ModImport.Find(_reg, ModsDir));           // and it is not offered again
    }

    [Fact]
    public void AnImportedModCanBeToggledAndDeletedButNotRebuilt()
    {
        var original = EnabledThenForgotten();
        ModImport.Import(_reg, ModImport.Find(_reg, ModsDir));
        var m = _reg.Get(original.ModId);
        var pak = Path.Combine(ModsDir, m.PakName);

        Installer.Disable(m, _reg);
        Assert.Equal(ModState.Disabled, _reg.StateOf(m));
        Assert.False(File.Exists(pak));
        Assert.False(File.Exists(ModMetadata.PathFor(pak)));

        Installer.Enable(m, _reg);                              // the store copy makes this possible
        Assert.Equal(ModState.Enabled, _reg.StateOf(m));
        Assert.True(File.Exists(ModMetadata.PathFor(pak)));     // and the file goes back beside it

        var builder = new ModBuilder(_reg, new ResampleStretcher(), new FakePacker());
        var ex = Assert.Throws<InstallException>(() => builder.Rebuild(m, TestSlot(), "ffmpeg-not-needed"));
        Assert.Contains("imported", ex.Message);

        Installer.Delete(m, _reg);
        Assert.Empty(_reg.All());
        Assert.Empty(Directory.GetFiles(ModsDir));
    }

    [Fact]
    public void ImportedModsStillGetEnableTimeProtections()
    {
        EnabledThenForgotten();
        ModImport.Import(_reg, ModImport.Find(_reg, ModsDir));

        // The name is now taken in the registry, so the naming check refuses it for a new mod.
        Assert.NotNull(_reg.FindNameConflict("Described"));
    }

    [Fact]
    public void ARecognisedPakIsSkippedWhenTheFileDoesNotMatchIt()
    {
        var m = EnabledThenForgotten();
        var pak = Path.Combine(ModsDir, m.PakName);

        // Someone swapped the pak's contents: it now overrides different WEMs than the file claims.
        File.WriteAllText(pak, $"PAKHEADER {Constants.WemMediaPakPath}/999.wem");
        Assert.Empty(ModImport.Find(_reg, ModsDir));

        // And a metadata file with no pak beside it is not a mod at all.
        File.Delete(pak);
        Assert.Empty(ModImport.Find(_reg, ModsDir));
    }

    [Fact]
    public void OnlyTheTopOfModsIsRecognised()
    {
        var m = EnabledThenForgotten();
        var sub = Path.Combine(ModsDir, "packs");
        Directory.CreateDirectory(sub);
        foreach (var f in Directory.GetFiles(ModsDir))
            File.Move(f, Path.Combine(sub, Path.GetFileName(f)));

        // In a subfolder its state could not be told from a disabled mod while the game still loaded it.
        Assert.Empty(ModImport.Find(_reg, ModsDir));
        Assert.Equal(2, Directory.GetFiles(sub).Length);
        _ = m;
    }

    [Fact]
    public void ASecondPakClaimingTheSameModOrNameIsNotImportedTwice()
    {
        var m = EnabledThenForgotten();
        // A copy under another name, with its own metadata file claiming the same mod id.
        var pak = Path.Combine(ModsDir, m.PakName);
        var dupPak = Path.Combine(ModsDir, "Copy_P.pak");
        File.Copy(pak, dupPak);
        var dupMeta = JsonNode.Parse(File.ReadAllText(ModMetadata.PathFor(pak)))!.AsObject();
        dupMeta["pakName"] = "Copy_P.pak";
        File.WriteAllText(ModMetadata.PathFor(dupPak), dupMeta.ToJsonString());

        var found = ModImport.Find(_reg, ModsDir);
        Assert.Single(found);                                    // one mod id, imported once
        ModImport.Import(_reg, found);
        Assert.Single(_reg.All());
    }
}
