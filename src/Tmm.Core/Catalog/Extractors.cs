namespace Tmm.Core.Catalog;

/// <summary>
/// Pull stock WEMs out of the user's own game install. Never shipped with the app.
///
/// Only the header is needed for measurement (~4 KB), but the pak is Oodle-compressed in 64 KB
/// blocks, so the practical unit is "extract the file". ~886 files, done once.
/// </summary>
public interface IExtractor
{
    /// <summary>Return {wem_id: extracted_path}. Missing IDs throw <see cref="ExtractionException"/>.</summary>
    IReadOnlyDictionary<int, string> Extract(IEnumerable<int> wemIds, string dest, IProgress<(int done, int total, string what)>? progress = null);
}

/// <summary>For development and for users who already ran FModel by hand.</summary>
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

        // Index once: FModel exports may land in nested Media/ folders and may carry suffixes.
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

/// <summary>Drives FModel's CLI. Needs Mappings.usmap for the UI but not for raw .wem export.</summary>
public sealed class FModelCliExtractor : IExtractor
{
    public FModelCliExtractor(string fmodelExe, string gameRoot, string? usmap = null) { }
    public IReadOnlyDictionary<int, string> Extract(IEnumerable<int> wemIds, string dest, IProgress<(int, int, string)>? progress = null)
        => throw new NotImplementedException("FModel CLI extraction is not wired up yet; use a pre-extracted folder.");
}

/// <summary>
/// Preferred long-term: reference the CUE4Parse NuGet package and read pakchunk0 directly (Oodle
/// decompression needs oo2core on PATH). Removes the FModel install requirement. Kept as a contract
/// until the PreExtractedFolder path proves limiting.
/// </summary>
public sealed class Cue4ParseExtractor : IExtractor
{
    public Cue4ParseExtractor(string gameRoot) { }
    public IReadOnlyDictionary<int, string> Extract(IEnumerable<int> wemIds, string dest, IProgress<(int, int, string)>? progress = null)
        => throw new NotImplementedException("CUE4Parse extraction is not wired up yet; use a pre-extracted folder.");
}
