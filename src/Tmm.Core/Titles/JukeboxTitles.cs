using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tmm.Core.Analysis;
using Tmm.Core.Mods;
using Tmm.Core.Pak;

namespace Tmm.Core.Titles;

/// <summary>
/// The jukebox title table as the installed game has it, read straight from the game's IoStore
/// containers (decompressing Oodle Kraken with <see cref="Kraken"/>), plus the map from slots to
/// entries that ships in <c>data\jukebox_text\jukebox_text.json</c>. Nothing of the game's is shipped:
/// the table is read from the user's own install every time, so a game update that changes it is
/// simply picked up.
/// </summary>
public sealed class JukeboxTextBaseline
{
    public const string FolderName = "jukebox_text";
    public const string MetaFileName = "jukebox_text.json";

    public string Culture { get; private init; } = "en";
    /// <summary>"Polaris/Content/Localize/en/GTB_Jukebox.uasset".</summary>
    public string PackagePath { get; private init; } = "";
    public ulong PackageId { get; private init; }
    /// <summary>Store-entry facts the game registers the package with, copied into our container.</summary>
    public int ExportCount { get; private init; } = 1;
    public int ExportBundleCount { get; private init; } = 1;
    /// <summary>The cooked package bytes, unpatched, exactly as the game loads them.</summary>
    public byte[] Package { get; private init; } = Array.Empty<byte>();
    /// <summary>Slot key (loop WEM id) to text key, for every slot whose title lives in this table.</summary>
    public IReadOnlyDictionary<int, string> SlotKeys { get; private init; } = new Dictionary<int, string>();
    /// <summary>The game container the table was read from, e.g. "pakchunk404-Windows_0_P.utoc".</summary>
    public string Source { get; private init; } = "";

    private sealed class MetaDoc
    {
        public string Culture { get; set; } = "en";
        public string PackagePath { get; set; } = "";
        public string PackageId { get; set; } = "";
        public Dictionary<string, string> Slots { get; set; } = new();
    }

    /// <summary>The shipped data folder, or null when the app was built without it.</summary>
    public static string? ShippedDir()
    {
        foreach (var c in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, "data", FolderName),
                     Path.Combine("data", FolderName),
                 })
            if (File.Exists(Path.Combine(c, MetaFileName))) return c;
        return null;
    }

    private static (string Key, JukeboxTextBaseline Value)? _cache;

    /// <summary>
    /// Read the table from the game install named in <paramref name="s"/>. Throws <see cref="TmmException"/>
    /// saying why when it cannot (no game folder, no shipped map, the table missing or unreadable).
    /// Cached until the container it came from changes on disk.
    /// </summary>
    public static JukeboxTextBaseline Load(Settings s)
    {
        if (!s.GameRootLooksValid()) throw new GameNotFoundException("the game folder is not set");
        var dir = ShippedDir() ?? throw new TmmException("this copy of the app has no title data (data\\jukebox_text is missing)");
        var meta = JsonSerializer.Deserialize<MetaDoc>(File.ReadAllText(Path.Combine(dir, MetaFileName)))
                   ?? throw new TmmException($"{MetaFileName} is empty");
        var packageId = ulong.Parse(meta.PackageId, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        var utoc = FindContainer(s.GamePaksDir!, meta.PackagePath)
                   ?? throw new TmmException($"no game container lists {meta.PackagePath}");
        var info = new FileInfo(utoc);
        var ucas = new FileInfo(Path.ChangeExtension(utoc, ".ucas"));
        var key = $"{utoc}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{(ucas.Exists ? ucas.LastWriteTimeUtc.Ticks : 0)}";
        if (_cache is { } c && c.Key == key) return c.Value;

        var toc = IoStoreToc.Read(utoc);
        int index = toc.IndexOfFile(meta.PackagePath);
        if (index < 0 || toc.ChunkIds[index] != IoChunkId.Package(packageId))
            throw new TmmException($"{info.Name} stores {meta.PackagePath} under a different package id");
        var package = toc.ReadChunk(index);   // checked against the hash the .utoc records

        int headerIndex = toc.Resolve(IoChunkId.Header(toc.ContainerId));
        if (headerIndex < 0) throw new TmmException($"{info.Name} has no container header");
        var entry = IoContainerHeader.FindStoreEntry(toc.ReadChunk(headerIndex), packageId)
                    ?? throw new TmmException($"{info.Name} does not register {meta.PackagePath}");
        if (entry.ImportCount != 0 || entry.ShaderMapCount != 0)
            throw new TmmException($"{meta.PackagePath} has imports or shader maps, which the titles container cannot carry");

        var baseline = new JukeboxTextBaseline
        {
            Culture = meta.Culture,
            PackagePath = meta.PackagePath,
            PackageId = packageId,
            ExportCount = entry.ExportCount,
            ExportBundleCount = entry.ExportBundleCount,
            Package = package,
            SlotKeys = meta.Slots.ToDictionary(kv => int.Parse(kv.Key, CultureInfo.InvariantCulture), kv => kv.Value),
            Source = info.Name,
        };
        _cache = (key, baseline);
        return baseline;
    }

    /// <summary>
    /// The container under Paks (not ~mods) the engine would load <paramref name="packagePath"/> from: a
    /// patch ("_P") beats the base, and a higher patch number beats a lower one. Only each container's
    /// directory index is read to find it.
    /// </summary>
    public static string? FindContainer(string gamePaksDir, string packagePath)
    {
        if (!Directory.Exists(gamePaksDir)) return null;
        var found = new List<string>();
        foreach (var utoc in Directory.EnumerateFiles(gamePaksDir, "*.utoc", SearchOption.TopDirectoryOnly))
        {
            try { if (IoStoreToc.FindFile(utoc, packagePath) is not null) found.Add(utoc); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or EndOfStreamException) { }
        }
        return found.OrderBy(PakPriority.Rank).ThenBy(f => f, StringComparer.OrdinalIgnoreCase).LastOrDefault();
    }
}

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum TitleSyncOutcome
{
    /// <summary>The titles pak was written (or rewritten) in Paks\mods.</summary>
    Written,
    /// <summary>The titles pak in Paks\mods already said exactly this.</summary>
    Unchanged,
    /// <summary>No enabled mod needs a title, so any titles pak was removed.</summary>
    Removed,
    /// <summary>Renaming is switched off in Settings; any titles pak was removed.</summary>
    Off,
    /// <summary>Titles could not be applied (no game folder, no data, game patched). Audio is unaffected.</summary>
    Skipped,
}

public sealed record AppliedTitle(string ModName, int SlotKey, string TextKey, string Original, string Title);

public sealed record TitleSyncResult(TitleSyncOutcome Outcome, string Message, IReadOnlyList<AppliedTitle> Applied, IReadOnlyList<string> Warnings)
{
    public static TitleSyncResult Of(TitleSyncOutcome o, string message, IReadOnlyList<string>? warnings = null) =>
        new(o, message, Array.Empty<AppliedTitle>(), warnings ?? Array.Empty<string>());

    /// <summary>True when something went wrong that the user should hear about.</summary>
    public bool NeedsAttention => Outcome == TitleSyncOutcome.Skipped || Warnings.Count > 0;
}

/// <summary>
/// Shows each enabled mod's song in the jukebox instead of the stock track name.
///
/// Every title sits in one game file (GTB_Jukebox), so this cannot be one pak per mod: two mods each
/// shipping their own copy would overwrite each other's titles. Instead there is a single
/// <see cref="PakStem"/> container in Paks\mods (not ~mods, unlike the audio paks: the titles only
/// show from there) holding the stock table with every enabled mod's slot
/// renamed, rewritten whenever a mod is enabled, disabled, deleted or rebuilt, and removed when no
/// enabled mod needs it. Its contents are a pure function of which mods are enabled.
///
/// A title must fit in the space the stock title occupies (see <see cref="GryphonTextEntry.MaxBytes"/>).
/// Longer ones are shortened with an ellipsis rather than moving bytes in the cooked package.
/// </summary>
public static class JukeboxTitles
{
    /// <summary>Name of the titles container in Paks\mods (.pak + .utoc + .ucas). "_P" for patch priority.</summary>
    public const string PakStem = "TMM_JukeboxTitles_P";
    public const string Ellipsis = "…";

    public static readonly string[] Extensions = { ".pak", ".utoc", ".ucas" };

    /// <summary>Fixed, so a rewrite replaces the container rather than adding a second one.</summary>
    public static readonly ulong ContainerId = Fnv1a64("TMM_JukeboxTitles");

    /// <summary>True for any of the titles container's three files.</summary>
    public static bool IsTitlesFile(string fileName) =>
        Extensions.Any(ext => string.Equals(fileName, PakStem + ext, StringComparison.OrdinalIgnoreCase));

    public static IEnumerable<string> FilesIn(string modsDir) => Extensions.Select(ext => Path.Combine(modsDir, PakStem + ext));

    // ------------------------------------------------------------------ titles

    /// <summary>
    /// The title for a song, before fitting: "Title / Artist" like the game's own "Track / TEKKEN 7" when
    /// it fits in <paramref name="maxBytes"/>, otherwise the title alone. The title falls back to the
    /// filename when the file has no tags.
    /// </summary>
    public static string Suggest(SongTags tags, string songPath, int maxBytes)
    {
        var title = Clean(tags.Title);
        if (title.Length == 0) title = FromFileName(songPath);
        var artist = Clean(tags.Artist);
        if (artist.Length > 0)
        {
            var both = $"{title} / {artist}";
            if (Encoding.UTF8.GetByteCount(both) <= maxBytes) return both;
        }
        return title;
    }

    /// <summary>
    /// <paramref name="desired"/> cleaned up and cut to at most <paramref name="maxBytes"/> UTF-8 bytes,
    /// on a character boundary, ending in "…" when it had to be shortened.
    /// </summary>
    public static string Fit(string desired, int maxBytes)
    {
        var s = Clean(desired);
        if (maxBytes <= 0) return "";
        if (Encoding.UTF8.GetByteCount(s) <= maxBytes) return s;
        int ellipsis = Encoding.UTF8.GetByteCount(Ellipsis);
        bool withEllipsis = maxBytes > ellipsis;
        int budget = withEllipsis ? maxBytes - ellipsis : maxBytes;
        var sb = new StringBuilder();
        int used = 0;
        bool midWord = false;
        foreach (var rune in s.EnumerateRunes())
        {
            if (used + rune.Utf8SequenceLength > budget) { midWord = rune.Value != ' ' && sb.Length > 0 && sb[^1] != ' '; break; }
            sb.Append(rune.ToString());
            used += rune.Utf8SequenceLength;
        }
        var cut = sb.ToString();
        // Back off to the last word break rather than ending on half a word, unless that throws away
        // more than half of what fits (one long word, or text without spaces).
        int space = cut.LastIndexOf(' ');
        if (midWord && space >= cut.Length / 2) cut = cut[..space];
        cut = cut.TrimEnd(' ', '/', '-', ',', '(', '[', ':');
        return withEllipsis ? cut + Ellipsis : cut;
    }

    /// <summary>Collapse whitespace, drop control characters (the table cannot hold a null).</summary>
    public static string Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) sb.Append(char.IsControl(c) || char.IsWhiteSpace(c) ? ' ' : c);
        return Regex.Replace(sb.ToString(), " {2,}", " ").Trim();
    }

    /// <summary>"03 - My_Song" becomes "My Song".</summary>
    public static string FromFileName(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path).Replace('_', ' ');
        stem = Regex.Replace(stem, @"^\s*\d{1,3}\s*[-.)]\s*", "");
        var cleaned = Clean(stem);
        return cleaned.Length > 0 ? cleaned : Clean(Path.GetFileNameWithoutExtension(path));
    }

    /// <summary>Room for a title in <paramref name="slotKey"/>'s entry, or null when the slot's title is not
    /// in the shipped table (Season 2 tracks keep theirs elsewhere).</summary>
    public static int? MaxBytesFor(JukeboxTextBaseline baseline, int slotKey)
    {
        if (!baseline.SlotKeys.TryGetValue(slotKey, out var key)) return null;
        return Table(baseline).Find(key)?.MaxBytes;
    }

    private static GryphonText? _table;
    private static byte[]? _tableSource;

    /// <summary>The parsed, unpatched table. Cached; patching works on its own copy.</summary>
    public static GryphonText Table(JukeboxTextBaseline baseline)
    {
        if (!ReferenceEquals(_tableSource, baseline.Package))
        {
            _table = GryphonText.Parse(baseline.Package);
            _tableSource = baseline.Package;
        }
        return _table!;
    }

    /// <summary>
    /// The title a mod asks for: its own <see cref="ModManifest.JukeboxTitle"/>, else one suggested from
    /// the song's tags (or the mod name, for a mod imported without its song). A suggestion is saved to
    /// the manifest so it is worked out once, not on every sync.
    /// </summary>
    public static string DesiredTitle(ModManifest m, ModRegistry reg, int maxBytes)
    {
        if (!string.IsNullOrWhiteSpace(m.JukeboxTitle)) return m.JukeboxTitle!;
        string title;
        if (!m.Imported && File.Exists(m.SongPath))
            title = Suggest(SongTagReader.Read(m.SongPath, reg.Settings.FfmpegExe), m.SongPath, maxBytes);
        else
            title = Clean(m.Name.Replace('_', ' '));
        if (title.Length == 0) return "";
        m.JukeboxTitle = title;
        try { reg.Save(m); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return title;
    }

    // ------------------------------------------------------------------ sync

    /// <summary>
    /// Make the titles container in ~mods match the enabled mods. Never throws for title problems: the
    /// result says what happened, so audio installs are never blocked by a title. File errors (the game
    /// holding the pak open, say) are reported the same way.
    /// </summary>
    public static TitleSyncResult Sync(ModRegistry reg)
    {
        var modsDir = reg.Settings.GameTitlesDir;
        if (modsDir is null || !reg.Settings.GameRootLooksValid())
            return TitleSyncResult.Of(TitleSyncOutcome.Skipped, "Jukebox titles: the game folder is not set.");
        try
        {
            if (!reg.Settings.RenameJukeboxTitles)
                return Remove(modsDir, TitleSyncOutcome.Off, "Jukebox titles are switched off in Settings.");

            var enabled = reg.All().Where(reg.IsEnabled).OrderBy(m => m.Created).ToList();
            if (enabled.Count == 0)
                return Remove(modsDir, TitleSyncOutcome.Removed, "No mods are enabled, so the jukebox shows its own titles.");

            // Read fresh from the install, so a game update that changed the table is followed. When it
            // cannot be read, any titles pak already installed goes too: it would carry an older table.
            JukeboxTextBaseline baseline;
            try { baseline = JukeboxTextBaseline.Load(reg.Settings); }
            catch (Exception e) when (e is TmmException or IOException or UnauthorizedAccessException or JsonException or EndOfStreamException)
            {
                return Remove(modsDir, TitleSyncOutcome.Skipped,
                    $"Jukebox titles were not changed: the game's title table could not be read ({(e is TmmException ? e.Message : FileOps.Explain(e))}). " +
                    "The mods still play; the jukebox shows the stock names.");
            }

            var table = GryphonText.Parse(baseline.Package);
            var applied = new List<AppliedTitle>();
            var warnings = new List<string>();
            foreach (var m in enabled)
            {
                if (!baseline.SlotKeys.TryGetValue(m.SlotKey, out var key))
                {
                    warnings.Add($"{m.Name}: '{m.SlotTitle}' keeps its name; its title is not in the table this app can change.");
                    continue;
                }
                var entry = table.Find(key);
                if (entry is null)
                {
                    warnings.Add($"{m.Name}: the title table has no entry {key}.");
                    continue;
                }
                var title = Fit(DesiredTitle(m, reg, entry.MaxBytes), entry.MaxBytes);
                if (title.Length == 0) continue;
                table.Set(key, title);
                applied.Add(new AppliedTitle(m.Name, m.SlotKey, key, entry.Value, title));
            }
            if (applied.Count == 0)
                return Remove(modsDir, TitleSyncOutcome.Removed, "None of the enabled mods has a title to show.", warnings);

            warnings.AddRange(OtherTitlePaks(new[] { modsDir, reg.Settings.GameModsDir! }, baseline).Select(p =>
                $"'{p}' also replaces the jukebox titles. Only one of the two can win; remove one if the names look wrong."));

            var package = new IoStorePackage(baseline.PackageId, baseline.PackagePath, table.ToArray(),
                                             baseline.ExportCount, baseline.ExportBundleCount);
            var chunks = new List<IoStoreChunk>
            {
                new(IoChunkId.Package(package.PackageId), package.Data, package.Path),
                new(IoChunkId.Header(ContainerId), IoStoreWriter.ContainerHeader(ContainerId, new[] { package })),
            };
            var (utoc, ucas) = IoStoreWriter.Build(ContainerId, chunks);
            var basePath = Path.Combine(modsDir, PakStem);
            if (SameFile(basePath + ".utoc", utoc) && SameFile(basePath + ".ucas", ucas) && File.Exists(basePath + ".pak"))
            {
                RemoveFromOldLocation(reg.Settings);
                return new TitleSyncResult(TitleSyncOutcome.Unchanged, Summary(applied), applied, warnings);
            }

            Directory.CreateDirectory(modsDir);
            RemoveFromOldLocation(reg.Settings);
            Write(basePath + ".ucas", ucas);
            Write(basePath + ".utoc", utoc);
            Write(basePath + ".pak", IoStoreWriter.EmptyPak());
            return new TitleSyncResult(TitleSyncOutcome.Written, Summary(applied), applied, warnings);
        }
        catch (Exception e) when (e is TmmException or IOException or UnauthorizedAccessException or JsonException)
        {
            return TitleSyncResult.Of(TitleSyncOutcome.Skipped, $"Jukebox titles were not updated: {(e is TmmException ? e.Message : FileOps.Explain(e))}");
        }
    }

    private static string Summary(IReadOnlyList<AppliedTitle> applied) =>
        applied.Count == 1 ? $"Jukebox shows \"{applied[0].Title}\" for {applied[0].ModName}."
                           : $"Jukebox titles set for {applied.Count} mods.";

    private static TitleSyncResult Remove(string modsDir, TitleSyncOutcome outcome, string message, IReadOnlyList<string>? warnings = null)
    {
        foreach (var f in FilesIn(modsDir)) FileOps.DeleteFile(f);
        var oldDir = Path.Combine(Path.GetDirectoryName(modsDir)!, Constants.ModsDirName);
        foreach (var f in FilesIn(oldDir)) FileOps.DeleteFile(f);
        return TitleSyncResult.Of(outcome, message, warnings);
    }

    private static void Write(string path, byte[] data)
    {
        FileOps.PrepareWrite(path);
        File.WriteAllBytes(path, data);
    }

    private static bool SameFile(string path, byte[] data)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != data.Length) return false;
        return File.ReadAllBytes(path).AsSpan().SequenceEqual(data);
    }

    /// <summary>Earlier builds wrote the titles container to ~mods. Clear any copy left there, so the
    /// game does not load two of them.</summary>
    private static void RemoveFromOldLocation(Settings s)
    {
        if (s.GameModsDir is null) return;
        foreach (var f in FilesIn(s.GameModsDir)) FileOps.DeleteFile(f);
    }

    /// <summary>Containers in the given mod folders, other than ours, that also carry the title table.</summary>
    public static List<string> OtherTitlePaks(IEnumerable<string> modDirs, JukeboxTextBaseline baseline)
    {
        var list = new List<string>();
        foreach (var modsDir in modDirs.Where(Directory.Exists))
        foreach (var utoc in Directory.EnumerateFiles(modsDir, "*.utoc", SearchOption.AllDirectories))
        {
            if (IsTitlesFile(Path.GetFileName(utoc))) continue;
            try
            {
                if (IoStoreToc.FindFile(utoc, baseline.PackagePath) is not null)
                    list.Add(Path.GetFileNameWithoutExtension(utoc) + ".pak");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or EndOfStreamException) { }
        }
        return list;
    }

    private static ulong Fnv1a64(string s)
    {
        ulong h = 0xcbf29ce484222325;
        foreach (var b in Encoding.UTF8.GetBytes(s)) { h ^= b; h *= 0x00000100000001B3; }
        return h;
    }
}
