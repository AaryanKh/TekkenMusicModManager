using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tmm.Core.Catalog;

/// <summary>
/// Persistence for the measured catalog. Rebuilt from the user's install; versioned by game build so
/// a patch that moves IDs invalidates it cleanly.
///
/// Deviation from ARCHITECTURE.md: a JSON file instead of SQLite. 443 rows is ~100 KB, it is read
/// once at startup into memory, and dropping SQLite means the engine has zero native dependencies.
/// The schema (one row per slot keyed by loop_id, plus a meta map) is the same.
/// </summary>
public sealed class CatalogStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private sealed class Document
    {
        public Dictionary<string, string> Meta { get; set; } = new();
        public List<Slot> Slots { get; set; } = new();
    }

    /// <summary>Filename of the catalog shipped in <c>data\</c> next to the exe.</summary>
    public const string BundledFileName = "stock_catalog.json";

    private readonly string _path;
    private Document _doc;
    private bool _bundled;

    public CatalogStore(string path)
    {
        _path = path;
        if (File.Exists(path))
        {
            _doc = Read(path);
            if (_doc.Slots.Count > 0) return;
        }
        // Nothing measured locally: fall back to the catalog shipped with the app, so a fresh install
        // can rank and render without the user extracting anything. Rebuilding from their own install
        // writes to _path and takes over from then on.
        var bundled = BundledPath();
        _doc = bundled is null ? new Document() : Read(bundled);
        _bundled = _doc.Slots.Count > 0;
    }

    /// <summary>The shipped catalog, or null when the app was built without one.</summary>
    public static string? BundledPath()
    {
        foreach (var c in new[]
                 {
                     System.IO.Path.Combine(AppContext.BaseDirectory, "data", BundledFileName),
                     System.IO.Path.Combine("data", BundledFileName),
                 })
            if (File.Exists(c)) return c;
        return null;
    }

    private static Document Read(string path)
    {
        try { return JsonSerializer.Deserialize<Document>(File.ReadAllText(path), Options) ?? new Document(); }
        catch (JsonException) { return new Document(); }
    }

    public string Path => _path;
    public bool IsBuilt => _doc.Slots.Count > 0;
    /// <summary>True when the loaded rows came from the shipped catalog rather than from a build
    /// against this machine's game install. Still fully measured, so rendering is allowed.</summary>
    public bool IsBundled => _bundled;
    public int Count => _doc.Slots.Count;
    public string? GameBuild => _doc.Meta.TryGetValue("game_build", out var v) ? v : null;
    public DateTime? BuiltAt => _doc.Meta.TryGetValue("built_at", out var v) && DateTime.TryParse(v, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : null;

    public void Upsert(IEnumerable<Slot> slots)
    {
        var byKey = _doc.Slots.ToDictionary(s => s.Key);
        foreach (var s in slots) byKey[s.Key] = s;
        _doc.Slots = byKey.Values.OrderBy(s => s.Identity.No).ToList();
        _doc.Meta["built_at"] = DateTime.UtcNow.ToString("O");
        _doc.Meta.Remove("source");     // no longer the shipped rows
        _bundled = false;
        Save();
    }

    public IReadOnlyList<Slot> AllSlots()
    {
        if (!IsBuilt) throw new CatalogNotBuiltException("run catalog build first");
        return _doc.Slots;
    }

    public Slot? Get(int loopId) => _doc.Slots.FirstOrDefault(s => s.Key == loopId);

    public void SetGameBuild(string buildId) { _doc.Meta["game_build"] = buildId; Save(); }

    public void Clear() { _doc = new Document(); Save(); }

    private void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(_path))!);
        FileOps.WriteAllText(_path, JsonSerializer.Serialize(_doc, Options));
    }
}
