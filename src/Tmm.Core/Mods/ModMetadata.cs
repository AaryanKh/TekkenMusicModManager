using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tmm.Core.Pak;

namespace Tmm.Core.Mods;

/// <summary>
/// The small file written beside a pak as <c>&lt;pak name&gt;.tmm.json</c> when
/// <see cref="Settings.WriteModMetadata"/> is on. It says what the pak is: which mod, which jukebox
/// slot, which WEMs. A pak on its own says none of that, so without this the manager can only show a
/// pak in ~mods as somebody else's file that overrides some numbers.
///
/// It is copied into ~mods next to the pak when the mod is enabled and removed when it is disabled.
/// <see cref="ModImport"/> reads it back, so a pak carrying one can be recognised and adopted on a new
/// machine, after the app's data was wiped, or when it was sent to someone else.
///
/// Deliberately small, and deliberately without the render plan and without the song's folder: the
/// file may be shared, and a path names the person's account. Unreal only mounts <c>*.pak</c>, so the
/// extra file is ignored by the game.
/// </summary>
public sealed class ModMetadata
{
    public const string FormatId = "tmm-mod";
    public const int CurrentVersion = 1;
    public const string Suffix = ".tmm.json";

    /// <summary>A sidecar is tiny; anything bigger is not one, and is not read.</summary>
    private const long MaxBytes = 64 * 1024;
    private const int MaxWemIds = 16;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Format { get; set; } = FormatId;
    public int Version { get; set; } = CurrentVersion;
    public string ModId { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>The pak this describes, a bare file name found in the same folder as the metadata file.</summary>
    public string PakName { get; set; } = "";
    public int SlotKey { get; set; }
    public string SlotTitle { get; set; } = "";
    /// <summary>What the pak overrides. Conflict detection keys on this.</summary>
    public List<int> WemIds { get; set; } = new();
    /// <summary>File name only, never the folder.</summary>
    public string SongFile { get; set; } = "";
    public string SongFingerprint { get; set; } = "";
    public DateTime Created { get; set; }
    public string AppVersion { get; set; } = "";

    /// <summary>Where the metadata for <paramref name="pakPath"/> lives: the same path plus <see cref="Suffix"/>.</summary>
    public static string PathFor(string pakPath) => pakPath + Suffix;

    public static ModMetadata From(ModManifest m) => new()
    {
        ModId = m.ModId,
        Name = m.Name,
        PakName = m.PakName,
        SlotKey = m.SlotKey,
        SlotTitle = m.SlotTitle,
        WemIds = m.WemIds.ToList(),
        SongFile = Path.GetFileName(m.SongPath),
        SongFingerprint = m.SongFingerprint,
        Created = m.Created,
        AppVersion = CurrentAppVersion(),
    };

    /// <summary>Write the metadata for <paramref name="m"/> beside <paramref name="pakPath"/>.</summary>
    public static string Write(ModManifest m, string pakPath)
    {
        var path = PathFor(pakPath);
        FileOps.WriteAllText(path, JsonSerializer.Serialize(From(m), Options));
        return path;
    }

    /// <summary>
    /// The metadata in <paramref name="path"/>, or null when it is unreadable, not ours, from a newer
    /// format than this build understands, or fails validation. Never throws for a bad file: the caller
    /// treats null as "not a recognisable pak".
    ///
    /// The values are used to build paths inside the app's store, so they are checked rather than
    /// trusted: the mod id must be a GUID (it becomes a folder name) and the pak name must be a bare
    /// <c>*.pak</c> file name (it is joined onto the mods folder).
    /// </summary>
    public static ModMetadata? TryRead(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length == 0 || fi.Length > MaxBytes) return null;
            var meta = JsonSerializer.Deserialize<ModMetadata>(File.ReadAllText(path), Options);
            if (meta is null) return null;

            if (!string.Equals(meta.Format, FormatId, StringComparison.Ordinal)) return null;
            if (meta.Version < 1 || meta.Version > CurrentVersion) return null;

            if (!Guid.TryParse(meta.ModId, out var id)) return null;
            meta.ModId = id.ToString();

            if (!IsBarePakName(meta.PakName)) return null;
            if (string.IsNullOrWhiteSpace(meta.Name)) return null;
            if (meta.WemIds is null || meta.WemIds.Count == 0 || meta.WemIds.Count > MaxWemIds) return null;
            meta.SlotTitle ??= "";
            meta.SongFile = Path.GetFileName(meta.SongFile ?? "");   // even a hand-edited file cannot smuggle a folder in
            meta.SongFingerprint ??= "";
            return meta;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool IsBarePakName(string? name)
        => !string.IsNullOrWhiteSpace(name)
           && name.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)
           && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
           && Path.GetFileName(name) == name
           && name != ".pak";

    private static string CurrentAppVersion()
    {
        var v = typeof(ModMetadata).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        // "0.1.0+9e04f9f…": the build hash after the plus is noise in a file that gets shared.
        return string.IsNullOrEmpty(v) ? "" : v.Split('+')[0];
    }
}
