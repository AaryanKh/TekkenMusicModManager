using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Tmm.Core;
using Tmm.Core.Analysis;
using Tmm.Core.Catalog;
using Tmm.Core.Mods;
using Tmm.Core.Pak;
using Tmm.Core.Titles;
using Xunit;

namespace Tmm.Core.Tests;

/// <summary>
/// The jukebox titles container and the game-reading code under it: BLAKE3, the IoStore reader and
/// writer, the text table patcher, title fitting and the enable/disable lifecycle. Most tests build a
/// small synthetic text table and fake game container, so they need no game files. The ones at the end
/// read the real install when there is one (Kraken, the perfect hash, the live title table, Season 2
/// WEMs) and pass vacuously otherwise.
/// </summary>
public class JukeboxTitlesTests : IDisposable
{
    // Slot 110 in the sheet: "Heihachi (Arcade ver.) / TEKKEN TAG", 35 characters of room.
    private const int HeihachiSlot = 475760450;
    private const string HeihachiKey = "TEXT_000_UI_JUKEBOX_BGM_110";
    private const string HeihachiTitle = "Heihachi (Arcade ver.) / TEKKEN TAG";
    private const ulong PackageId = 0xee0c0d015cbc64d5;
    private const string PackagePath = "Polaris/Content/Localize/en/GTB_Jukebox.uasset";
    private const string GamePaks = @"C:\Program Files (x86)\Steam\steamapps\common\TEKKEN 8\Polaris\Content\Paks";

    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "tmm-titles-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    // ------------------------------------------------------------------ synthetic text table

    /// <summary>
    /// A package shaped like GTB_Jukebox: a stand-in header, the DataSize property, then a "gbtd" block
    /// of records ("text", 16, key size, value size, 8-byte hash, key, value; key and value null-terminated
    /// and padded to 4 bytes).
    /// </summary>
    private static byte[] FakeTable(params (string Key, string Value)[] entries)
    {
        static byte[] Padded(string s)
        {
            var b = Encoding.UTF8.GetBytes(s);
            var p = new byte[(b.Length + 1 + 3) & ~3];
            b.CopyTo(p, 0);
            return p;
        }
        using var block = new MemoryStream();
        var w = new BinaryWriter(block);
        w.Write("gbtd"u8.ToArray()); w.Write(0x40); w.Write(0x00010001); w.Write(1); w.Write(0); w.Write(entries.Length);
        w.Write(new byte[0x40 - 24]);
        foreach (var (key, value) in entries)
        {
            var k = Padded(key); var v = Padded(value);
            w.Write("text"u8.ToArray()); w.Write(16); w.Write(k.Length); w.Write(v.Length);
            w.Write(BinaryPrimitives.ReadUInt64LittleEndian(SHA1.HashData(Encoding.UTF8.GetBytes(key))));
            w.Write(k); w.Write(v);
        }
        w.Flush();
        var gbtd = block.ToArray();
        var package = new byte[0x40 + gbtd.Length];
        "FakeZenPackageHeader"u8.CopyTo(package);
        BinaryPrimitives.WriteInt32LittleEndian(package.AsSpan(0x40 - 8), gbtd.Length);   // DataSize
        gbtd.CopyTo(package, 0x40);
        return package;
    }

    private static byte[] StockTable(string heihachi = HeihachiTitle) => FakeTable(
        ("TEXT_000_UI_JUKEBOX_BGM_000", "Character Select (Arcade ver.) / TEKKEN"),
        (HeihachiKey, heihachi),
        ("TEXT_000_UI_JUKEBOX_BGM_111", "Unknown (Arcade ver.) / TEKKEN TAG"),
        ("TEXT_000_UI_OPTIONS_XESS_000", "Off"));

    // ------------------------------------------------------------------ BLAKE3

    [Theory]
    [InlineData(0, "af1349b9f5f9a1a6a0404dea36dcc9499bcb25c9adc112b7cc9a93cae41f3262")]
    [InlineData(1025, "d00278ae47eb27b34faecf67b4fe263f82d5412916c1ffd97c8cb7fb814b8444")]
    [InlineData(2048, "e776b6028c7cd22a4d0ba182a8bf62205d2ef576467e838ed6f2529b85fba24a")]
    [InlineData(3073, "7124b49501012f81cc7f11ca069ec9226cecb8a2c850cfe644e327d22d3e1cd3")]
    public void Blake3MatchesTheOfficialVectors(int length, string expected)
    {
        // The official vectors hash bytes 0, 1, ..., 250, 0, 1, ... of the given length.
        var input = Enumerable.Range(0, length).Select(i => (byte)(i % 251)).ToArray();
        Assert.Equal(expected, Convert.ToHexString(Blake3.Hash(input)).ToLowerInvariant());
    }

    // ------------------------------------------------------------------ text table

    [Fact]
    public void SetRewritesOneValueInPlaceAndNothingElse()
    {
        var original = StockTable();
        var table = GryphonText.Parse(original);
        Assert.Equal(4, table.Entries.Count);
        var entry = table.Find(HeihachiKey)!;
        Assert.Equal(HeihachiTitle, entry.Value);
        Assert.Equal(35, entry.MaxBytes);   // 35 characters + null = 36, already a multiple of 4

        table.Set(HeihachiKey, "Bad Apple / Alstroemeria");
        var patched = table.ToArray();
        Assert.Equal(original.Length, patched.Length);
        Assert.Equal("Bad Apple / Alstroemeria", GryphonText.Parse(patched).Find(HeihachiKey)!.Value);
        for (int i = 0; i < original.Length; i++)
            if (i < entry.ValueOffset || i >= entry.ValueOffset + entry.ValueCapacity)
                Assert.True(original[i] == patched[i], $"byte 0x{i:X} changed");
    }

    [Fact]
    public void SetRefusesATitleThatDoesNotFit()
    {
        var table = GryphonText.Parse(StockTable());
        Assert.Throws<TmmException>(() => table.Set(HeihachiKey, new string('x', 36)));
        table.Set(HeihachiKey, new string('x', 35));   // exactly the room there is
    }

    [Fact]
    public void ParseRejectsSomethingThatIsNotATextTable()
    {
        Assert.Throws<TmmException>(() => GryphonText.Parse(Encoding.ASCII.GetBytes("definitely not a package")));
    }

    // ------------------------------------------------------------------ titles

    [Theory]
    [InlineData("Short", 35, "Short")]
    [InlineData("  Spaced   out\ttitle  ", 35, "Spaced out title")]
    [InlineData("A title that is much too long for the slot", 20, "A title that is…")]
    [InlineData("Ends in a slash / Artist name here", 20, "Ends in a slash…")]
    public void FitShortensOnlyWhenItHasTo(string desired, int max, string expected)
    {
        var fitted = JukeboxTitles.Fit(desired, max);
        Assert.Equal(expected, fitted);
        Assert.True(Encoding.UTF8.GetByteCount(fitted) <= max);
    }

    [Fact]
    public void FitNeverSplitsACharacter()
    {
        // Each of these is 3 bytes in UTF-8; 10 bytes of room leaves 7 for text after the 3-byte ellipsis.
        var fitted = JukeboxTitles.Fit("鉄拳鉄拳鉄拳鉄拳", 10);
        Assert.Equal("鉄拳…", fitted);
        Assert.True(Encoding.UTF8.GetByteCount(fitted) <= 10);
    }

    [Fact]
    public void SuggestUsesTitleAndArtistWhenTheyFit()
    {
        var tags = new SongTags("Bad Apple", "Alstroemeria", "");
        Assert.Equal("Bad Apple / Alstroemeria", JukeboxTitles.Suggest(tags, "x.mp3", 35));
        Assert.Equal("Bad Apple", JukeboxTitles.Suggest(tags, "x.mp3", 15));   // drop the artist before cutting the title
    }

    [Fact]
    public void SuggestFallsBackToACleanedFileName()
    {
        Assert.Equal("My Great Song", JukeboxTitles.Suggest(SongTags.Empty, @"C:\music\03 - My_Great_Song.flac", 35));
    }

    // ------------------------------------------------------------------ IoStore

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(64)]
    public void PerfectHashResolvesEveryChunkToItself(int count)
    {
        var rng = new Random(count);
        var chunks = Enumerable.Range(0, count)
            .Select(i => new IoStoreChunk(new IoChunkId((ulong)rng.NextInt64(), 0, IoChunkId.ExportBundleData), new byte[] { (byte)i }))
            .ToList();
        var (utoc, _) = IoStoreWriter.Build(42, chunks);
        var toc = IoStoreToc.Parse(utoc);
        foreach (var c in chunks)
        {
            int slot = toc.Resolve(c.Id);
            Assert.True(slot >= 0, $"chunk {c.Id} not found");
            Assert.Equal(c.Id, toc.ChunkIds[slot]);
        }
        Assert.Equal(-1, toc.Resolve(new IoChunkId(12345, 0, IoChunkId.ExportBundleData)));
    }

    [Fact]
    public void ChunksNoSeedCanSplitGoToOverflowLikeTheGamesOwn()
    {
        // The two chunks of the game's pakchunk202optional-Windows_0_P: one seed bucket, and FNV keeps their
        // parity equal for every seed, so the game lists both as overflow with seed -(n + 1). Ours must too.
        var ids = new[]
        {
            IoChunkId.FromBytes(Convert.FromHexString("a7885fdb2f9e9dd200000003")),
            IoChunkId.FromBytes(Convert.FromHexString("f9778db3d396ddcc00000006")),
        };
        var (seeds, overflow, _) = IoStoreWriter.PerfectHash(ids);
        Assert.Equal(new[] { -3 }, seeds);
        Assert.Equal(2, overflow.Length);

        var (utoc, _) = IoStoreWriter.Build(1, ids.Select(i => new IoStoreChunk(i, new byte[] { 1 })).ToList());
        var toc = IoStoreToc.Parse(utoc);
        foreach (var id in ids) Assert.Equal(id, toc.ChunkIds[toc.Resolve(id)]);
    }

    /// <summary>Write a container of the given packages, with its header, as &lt;dir&gt;/&lt;name&gt;.utoc/.ucas/.pak.</summary>
    private static string WriteContainer(string dir, string name, ulong containerId, params IoStorePackage[] packages)
    {
        Directory.CreateDirectory(dir);
        var chunks = packages.Select(p => new IoStoreChunk(IoChunkId.Package(p.PackageId), p.Data, p.Path)).ToList();
        chunks.Add(new IoStoreChunk(IoChunkId.Header(containerId), IoStoreWriter.ContainerHeader(containerId, packages)));
        var (utoc, ucas) = IoStoreWriter.Build(containerId, chunks);
        var basePath = Path.Combine(dir, name);
        File.WriteAllBytes(basePath + ".utoc", utoc);
        File.WriteAllBytes(basePath + ".ucas", ucas);
        File.WriteAllBytes(basePath + ".pak", IoStoreWriter.EmptyPak());
        return basePath + ".utoc";
    }

    [Fact]
    public void WrittenContainerReadsBackLikeTheGamesOwn()
    {
        var table = StockTable();
        var utocPath = WriteContainer(_tmp, "roundtrip", 77, new IoStorePackage(PackageId, PackagePath, table, 1, 1));
        var toc = IoStoreToc.Read(utocPath);

        Assert.Equal(5, toc.Version);
        Assert.Equal(77UL, toc.ContainerId);
        Assert.Equal(new[] { "None" }, toc.CompressionMethods);
        int idx = toc.IndexOfFile(PackagePath);
        Assert.True(idx >= 0);
        Assert.Equal(IoChunkId.Package(PackageId), toc.ChunkIds[idx]);
        Assert.Equal(Blake3.Hash(table, 20), toc.ChunkHashes[idx]);
        Assert.Equal(table, toc.ReadChunk(idx));   // through the block table, hash-checked, as the engine reads it

        // The container header registers the one package with the store entry the game uses.
        var header = toc.ReadChunk(toc.Resolve(IoChunkId.Header(77)));
        Assert.Equal(76, header.Length);
        Assert.Equal(new IoStoreEntry(1, 1, 0, 0), IoContainerHeader.FindStoreEntry(header, PackageId));
        Assert.Null(IoContainerHeader.FindStoreEntry(header, 12345));
        foreach (var b in toc.Blocks) Assert.Equal(0, b.Offset % 16);
    }

    [Fact]
    public void ReadChunkRefusesDataThatDoesNotMatchItsHash()
    {
        var utocPath = WriteContainer(_tmp, "tampered", 78, new IoStorePackage(PackageId, PackagePath, StockTable(), 1, 1));
        var ucas = Path.ChangeExtension(utocPath, ".ucas");
        var bytes = File.ReadAllBytes(ucas);
        bytes[100] ^= 0xFF;
        File.WriteAllBytes(ucas, bytes);
        var toc = IoStoreToc.Read(utocPath);
        Assert.Throws<PackException>(() => toc.ReadChunk(toc.IndexOfFile(PackagePath)));
    }

    [Fact]
    public void EmptyPakHasTheGamesLayoutAndValidHashes()
    {
        var pak = IoStoreWriter.EmptyPak();
        Assert.Equal(339, pak.Length);   // same size as the stubs beside the game's own containers
        int footer = pak.Length - 221;
        Assert.Equal(0x5A6F12E1u, BinaryPrimitives.ReadUInt32LittleEndian(pak.AsSpan(footer + 17)));
        Assert.Equal(11, BinaryPrimitives.ReadInt32LittleEndian(pak.AsSpan(footer + 21)));
        long indexSize = BinaryPrimitives.ReadInt64LittleEndian(pak.AsSpan(footer + 33));
        Assert.Equal(SHA1.HashData(pak.AsSpan(0, (int)indexSize)), pak.AsSpan(footer + 41, 20).ToArray());

        // And our own pak reader accepts it as an empty pak.
        var path = Path.Combine(_tmp, "empty.pak");
        Directory.CreateDirectory(_tmp);
        File.WriteAllBytes(path, pak);
        Assert.Empty(PakReader.Open(path).Entries);
    }

    [Theory]
    [InlineData("pakchunk404-Windows.utoc", 0)]
    [InlineData("pakchunk404-Windows_0_P.utoc", 1)]
    [InlineData("pakchunk404-Windows_1_P.utoc", 2)]
    [InlineData("TMM_JukeboxTitles_P.pak", 1)]
    public void PatchContainersOutrankTheBase(string name, int rank) => Assert.Equal(rank, PakPriority.Rank(name));

    // ------------------------------------------------------------------ lifecycle

    /// <summary>A fake game with the title table in "pakchunk404-Windows_0_P", like the real one.</summary>
    private (Settings, ModRegistry) FakeInstall(byte[]? table = null)
    {
        var game = Path.Combine(_tmp, "game");
        var paks = Path.Combine(game, Constants.PaksRelative);
        Directory.CreateDirectory(paks);
        if (table is not null)
            WriteContainer(paks, "pakchunk404-Windows_0_P", 99, new IoStorePackage(PackageId, PackagePath, table, 1, 1));
        var settings = new Settings { AppDir = Path.Combine(_tmp, "app"), GameRoot = game, FfmpegPath = "no-ffmpeg-here" };
        return (settings, new ModRegistry(settings));
    }

    private static ModManifest NewMod(ModRegistry reg, string name, int slot, string? title)
    {
        var m = new ModManifest { Name = name, PakName = $"{name}_P.pak", SlotKey = slot, SlotTitle = "stock", WemIds = new() { slot }, SongPath = "missing.mp3", JukeboxTitle = title };
        Directory.CreateDirectory(reg.ModDir(m));
        File.WriteAllBytes(reg.StorePak(m), new byte[] { 1, 2, 3 });
        m.Updated = DateTime.UtcNow.AddMinutes(-1);
        reg.Save(m);
        return m;
    }

    private static GryphonText TableInTitlesPak(string modsDir)
    {
        var toc = IoStoreToc.Read(Path.Combine(modsDir, JukeboxTitles.PakStem + ".utoc"));   // modsDir: Paks\mods
        return GryphonText.Parse(toc.ReadChunk(toc.IndexOfFile(PackagePath)));
    }

    [Fact]
    public void EnablingWritesTheTitleAndDisablingRemovesIt()
    {
        var (s, reg) = FakeInstall(StockTable());
        var m = NewMod(reg, "Apple", HeihachiSlot, "Bad Apple / Alstroemeria");

        var r = Installer.Enable(m, reg);
        Assert.Equal(TitleSyncOutcome.Written, r.Outcome);
        foreach (var f in JukeboxTitles.FilesIn(s.GameTitlesDir!)) Assert.True(File.Exists(f), f);
        var table = TableInTitlesPak(s.GameTitlesDir!);
        Assert.Equal("Bad Apple / Alstroemeria", table.Find(HeihachiKey)!.Value);
        Assert.Equal("Off", table.Find("TEXT_000_UI_OPTIONS_XESS_000")!.Value);   // the rest of the table rides along untouched

        Assert.Equal(TitleSyncOutcome.Unchanged, JukeboxTitles.Sync(reg).Outcome);   // idempotent

        var off = Installer.Disable(m, reg);
        Assert.Equal(TitleSyncOutcome.Removed, off.Outcome);
        foreach (var f in JukeboxTitles.FilesIn(s.GameTitlesDir!)) Assert.False(File.Exists(f), f);
    }

    [Fact]
    public void TheTitlesFollowTheNewestGameTable()
    {
        // A game update ships the table again in a later patch container with other text changed. The
        // titles pak must be built from that one, or it would put the old text back.
        var (s, reg) = FakeInstall(StockTable());
        WriteContainer(s.GamePaksDir!, "pakchunk404-Windows_1_P", 100,
            new IoStorePackage(PackageId, PackagePath, FakeTable(
                ("TEXT_000_UI_JUKEBOX_BGM_000", "Character Select (Arcade ver.) / TEKKEN"),
                (HeihachiKey, HeihachiTitle),
                ("TEXT_000_UI_JUKEBOX_BGM_111", "Unknown (Arcade ver.) / TEKKEN TAG"),
                ("TEXT_000_UI_OPTIONS_XESS_000", "Disabled")), 1, 1));
        var m = NewMod(reg, "Apple", HeihachiSlot, "Bad Apple");
        Installer.Enable(m, reg);
        var table = TableInTitlesPak(s.GameTitlesDir!);
        Assert.Equal("Bad Apple", table.Find(HeihachiKey)!.Value);
        Assert.Equal("Disabled", table.Find("TEXT_000_UI_OPTIONS_XESS_000")!.Value);
    }

    [Fact]
    public void LongTitlesAreShortenedToTheSlot()
    {
        var (s, reg) = FakeInstall(StockTable());
        var m = NewMod(reg, "Long", HeihachiSlot, "An extremely long song title that will never fit / Some Artist");
        var r = Installer.Enable(m, reg);
        var title = TableInTitlesPak(s.GameTitlesDir!).Find(HeihachiKey)!.Value;
        Assert.EndsWith(JukeboxTitles.Ellipsis, title);
        Assert.True(Encoding.UTF8.GetByteCount(title) <= 35);
        Assert.Equal(title, r.Applied.Single().Title);
    }

    [Fact]
    public void ImportedModsFallBackToTheirName()
    {
        var (s, reg) = FakeInstall(StockTable());
        var m = NewMod(reg, "My_Imported_Song", HeihachiSlot, null);
        m.Imported = true; reg.Save(m);
        Installer.Enable(m, reg);
        Assert.Equal("My Imported Song", TableInTitlesPak(s.GameTitlesDir!).Find(HeihachiKey)!.Value);
        Assert.Equal("My Imported Song", reg.Get(m.ModId).JukeboxTitle);   // worked out once, then kept
    }

    [Fact]
    public void AnUnreadableGameTableMeansNoTitlesButTheModStillInstalls()
    {
        var (s, reg) = FakeInstall(table: null);   // the game has no title table where it should be
        var m = NewMod(reg, "Apple", HeihachiSlot, "Bad Apple");
        // A titles pak left over from before must go: it would carry an older table.
        Directory.CreateDirectory(s.GameTitlesDir!);
        File.WriteAllBytes(Path.Combine(s.GameTitlesDir!, JukeboxTitles.PakStem + ".utoc"), new byte[] { 1 });

        var r = Installer.Enable(m, reg);
        Assert.Equal(TitleSyncOutcome.Skipped, r.Outcome);
        Assert.True(r.NeedsAttention);
        Assert.True(File.Exists(reg.InstalledPak(m)));
        foreach (var f in JukeboxTitles.FilesIn(s.GameTitlesDir!)) Assert.False(File.Exists(f), f);
    }

    [Fact]
    public void TheTitlesGoInPaksModsAndAnOldCopyInTildeModsIsCleared()
    {
        var (s, reg) = FakeInstall(StockTable());
        Assert.EndsWith(Path.Combine("Paks", "mods"), s.GameTitlesDir!);
        // What an earlier build left in ~mods.
        Directory.CreateDirectory(s.GameModsDir!);
        foreach (var f in JukeboxTitles.FilesIn(s.GameModsDir!)) File.WriteAllBytes(f, new byte[] { 1 });

        var m = NewMod(reg, "Apple", HeihachiSlot, "Bad Apple");
        Assert.Equal(TitleSyncOutcome.Written, Installer.Enable(m, reg).Outcome);
        foreach (var f in JukeboxTitles.FilesIn(s.GameTitlesDir!)) Assert.True(File.Exists(f), f);
        foreach (var f in JukeboxTitles.FilesIn(s.GameModsDir!)) Assert.False(File.Exists(f), f);
        Assert.True(File.Exists(reg.InstalledPak(m)));   // the audio pak itself stays in ~mods
        Assert.Equal(s.GameModsDir, Path.GetDirectoryName(reg.InstalledPak(m)));
    }

    [Fact]
    public void TurningTheSettingOffRemovesTheTitles()
    {
        var (s, reg) = FakeInstall(StockTable());
        var m = NewMod(reg, "Apple", HeihachiSlot, "Bad Apple");
        Installer.Enable(m, reg);
        s.RenameJukeboxTitles = false;
        Assert.Equal(TitleSyncOutcome.Off, JukeboxTitles.Sync(reg).Outcome);
        foreach (var f in JukeboxTitles.FilesIn(s.GameTitlesDir!)) Assert.False(File.Exists(f), f);
    }

    [Fact]
    public void ASlotOutsideTheTableIsReportedNotFatal()
    {
        var (s, reg) = FakeInstall(StockTable());
        var inTable = NewMod(reg, "Apple", HeihachiSlot, "Bad Apple");
        var season2 = NewMod(reg, "Ancient", 145043262, "Something");   // Ancient Powers: title lives elsewhere
        Installer.Enable(inTable, reg);
        var r = Installer.Enable(season2, reg);
        Assert.Equal(TitleSyncOutcome.Unchanged, r.Outcome);
        Assert.Contains(r.Warnings, w => w.Contains("Ancient"));
        Assert.Equal("Bad Apple", TableInTitlesPak(s.GameTitlesDir!).Find(HeihachiKey)!.Value);
    }

    [Fact]
    public void TheTitlesNameIsReserved()
    {
        var (_, reg) = FakeInstall(StockTable());
        Assert.NotNull(reg.FindNameConflict("TMM_JukeboxTitles"));
    }

    // ------------------------------------------------------------------ the real game, when present
    // These read the installed game (default Steam library) and pass vacuously on a machine without it.
    // When one fails on a machine with the game, a game update has changed something they pin down.

    [Fact]
    public void KrakenDecompressesEveryChunkOfTheTextContainer()
    {
        var utoc = Path.Combine(GamePaks, "pakchunk404-Windows_0_P.utoc");
        if (!File.Exists(utoc)) return;
        var toc = IoStoreToc.Read(utoc);
        Assert.Contains(toc.Blocks, b => b.Method != 0);
        for (int i = 0; i < toc.ChunkIds.Count; i++) toc.ReadChunk(i);   // throws unless each matches its BLAKE3
    }

    [Fact]
    public void TheReaderResolvesEveryChunkInTheGamesContainers()
    {
        // The engine's own perfect hashes: if the hash function or the seed rules were wrong, some chunk
        // in these 50-odd containers would fail to resolve to its own slot.
        if (!Directory.Exists(GamePaks)) return;
        foreach (var utoc in Directory.EnumerateFiles(GamePaks, "*.utoc"))
        {
            var toc = IoStoreToc.Read(utoc);
            for (int i = 0; i < toc.ChunkIds.Count; i++)
                Assert.True(toc.Resolve(toc.ChunkIds[i]) == i, $"{Path.GetFileName(utoc)}: chunk {i} did not resolve to itself");
        }
    }

    [Fact]
    public void TheLiveTitleTableHasEveryMappedSlot()
    {
        if (!Directory.Exists(GamePaks)) return;
        var settings = new Settings { GameRoot = Path.GetFullPath(Path.Combine(GamePaks, "..", "..", "..")) };
        var b = JukeboxTextBaseline.Load(settings);
        var table = GryphonText.Parse(b.Package);
        Assert.Equal(HeihachiTitle, table.Find(HeihachiKey)!.Value);
        Assert.True(b.SlotKeys.Count >= 430, $"only {b.SlotKeys.Count} slots mapped");
        foreach (var (slot, key) in b.SlotKeys)
            Assert.True(table.Find(key) is not null, $"slot {slot} maps to {key}, which the game's table no longer has");
        Assert.Equal(b.SlotKeys.Count, b.SlotKeys.Values.Distinct().Count());
        Assert.Equal((1, 1), (b.ExportCount, b.ExportBundleCount));
    }

    [Fact]
    public void SeasonTwoWemsAreReadFromTheGamesPaks()
    {
        // Ancient Powers (Normal): intro and loop live in pakchunk500-Windows_0_P.pak, outside pakchunk0.
        if (!Directory.Exists(GamePaks)) return;
        var heads = new GameInstallWemSource(GamePaks).ReadHeads(new[] { 966353609, 145043262 });
        var intro = Wem.WemReader.ReadHeader(heads[966353609].Head, heads[966353609].Size, 966353609);
        var loop = Wem.WemReader.ReadHeader(heads[145043262].Head, heads[145043262].Size, 145043262);
        Assert.InRange(intro.Seconds, 15.5, 16.5);   // the sheet says 0:16
        Assert.InRange(loop.Seconds, 98.5, 99.5);    // and 1:39
    }
}
