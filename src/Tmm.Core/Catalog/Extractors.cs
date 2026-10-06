namespace Tmm.Core.Catalog;

/// <summary>
/// Stock WEMs as files on disk. The app reads the game's paks itself (<see cref="GameInstallWemSource"/>);
/// this remains for a folder someone already exported, which is also how tests feed the builder.
/// </summary>
public interface IExtractor
{
    /// <summary>Return {wem_id: extracted_path}. Missing IDs throw <see cref="ExtractionException"/>.</summary>
    IReadOnlyDictionary<int, string> Extract(IEnumerable<int> wemIds, string dest, IProgress<(int done, int total, string what)>? progress = null);
}

/// <summary>A folder of WEMs exported by hand. Not needed any more for a normal rebuild.</summary>
public sealed class PreExtractedFolderExtractor : IExtractor
{
    public string Folder { get; }

    /// <summary>When true, IDs with no matching .wem are left out of the returned map instead of
    /// throwing. Stock WEMs live in several pakchunks (Season 2 and collab tracks are not in
    /// pakchunk0), so an export that covers only some of them should still yield a usable catalog.</summary>
    public bool AllowMissing { get; }

    public PreExtractedFolderExtractor(string folder, bool allowMissing = false)
    {
        Folder = folder;
        AllowMissing = allowMissing;
    }

    public IReadOnlyDictionary<int, string> Extract(IEnumerable<int> wemIds, string dest, IProgress<(int, int, string)>? progress = null)
    {
        if (!Directory.Exists(Folder))
            throw new ExtractionException($"WEM source folder not found: {Folder}");

        // Index once: exports may land in nested Media/ folders and may carry suffixes.
        var index = new Dictionary<int, string>();
        foreach (var f in Directory.EnumerateFiles(Folder, "*.wem", SearchOption.AllDirectories))
        {
            var stem = Path.GetFileNameWithoutExtension(f).Split('_')[0];
            if (int.TryParse(stem, out var id) && !index.ContainsKey(id)) index[id] = f;
        }

        var ids = wemIds.ToList();
        var missing = ids.Where(i => !index.ContainsKey(i)).ToList();
        if (missing.Count > 0 && !AllowMissing)
            throw new ExtractionException($"{missing.Count} WEM(s) not found in {Folder} (first: {missing[0]}.wem)");

        var outMap = new Dictionary<int, string>();
        for (int n = 0; n < ids.Count; n++)
        {
            if (index.TryGetValue(ids[n], out var path)) outMap[ids[n]] = path;
            progress?.Report((n + 1, ids.Count, $"{ids[n]}.wem"));
        }
        return outMap;
    }
}

/// <summary>The first bytes of a stock WEM and its full size: all that measuring a slot needs.</summary>
public sealed record WemHead(byte[] Head, long Size, string Source);

/// <summary>Supplies WEM headers without writing whole files anywhere.</summary>
public interface IWemHeadSource
{
    /// <summary>{wem_id: header} for every id it can find; ids it cannot find are left out.</summary>
    IReadOnlyDictionary<int, WemHead> ReadHeads(IEnumerable<int> wemIds, IProgress<(int done, int total, string what)>? progress = null);
}

/// <summary>
/// Reads stock WEMs straight from the game's own .pak files: no FModel, no export folder. Every pak under
/// Paks (not ~mods) that lists WwiseAudio/Media/&lt;id&gt;.wem is indexed; when two hold the same id, the
/// one the engine would load wins (see <see cref="Pak.PakPriority"/>). That covers pakchunk0 for the base
/// game and pakchunk0_0_P / pakchunk500(_0_P) for updates, Season 2 and collaborations. Only the first
/// compressed block of each file is decompressed, which holds the header.
/// </summary>
public sealed class GameInstallWemSource : IWemHeadSource
{
    private const int HeadBytes = 4096;
    private readonly string _paksDir;

    public GameInstallWemSource(string gamePaksDir) => _paksDir = gamePaksDir;

    public IReadOnlyDictionary<int, WemHead> ReadHeads(IEnumerable<int> wemIds, IProgress<(int done, int total, string what)>? progress = null)
    {
        if (!Directory.Exists(_paksDir))
            throw new ExtractionException($"game paks folder not found: {_paksDir}");

        // Index every audio file in every real pak (the 339-byte stubs beside IoStore containers hold none).
        var best = new Dictionary<int, (Pak.PakReader Pak, Pak.PakEntry Entry, int Rank)>();
        var paks = Directory.EnumerateFiles(_paksDir, "*.pak", SearchOption.TopDirectoryOnly)
                            .Where(f => new FileInfo(f).Length > 4096).ToList();
        for (int i = 0; i < paks.Count; i++)
        {
            progress?.Report((i, paks.Count, $"Reading {Path.GetFileName(paks[i])}"));
            Pak.PakReader pak;
            try { pak = Pak.PakReader.Open(paks[i]); }
            catch (PackException) { continue; }   // not a pak this reader handles; it holds no audio we use
            int rank = Pak.PakPriority.Rank(paks[i]);
            foreach (var (path, entry) in pak.Entries)
            {
                if (!path.StartsWith(Constants.WemMediaPakPath + "/", StringComparison.OrdinalIgnoreCase) ||
                    !path.EndsWith(".wem", StringComparison.OrdinalIgnoreCase)) continue;
                if (!int.TryParse(Path.GetFileNameWithoutExtension(path), out var id)) continue;
                if (!best.TryGetValue(id, out var have) || rank > have.Rank) best[id] = (pak, entry, rank);
            }
        }

        var ids = wemIds.ToList();
        var result = new Dictionary<int, WemHead>();
        for (int n = 0; n < ids.Count; n++)
        {
            if (!best.TryGetValue(ids[n], out var hit)) continue;
            result[ids[n]] = new WemHead(hit.Pak.Read(hit.Entry, HeadBytes), hit.Entry.UncompressedSize, Path.GetFileName(hit.Pak.FilePath));
            progress?.Report((n + 1, ids.Count, $"{ids[n]}.wem"));
        }
        return result;
    }
}
