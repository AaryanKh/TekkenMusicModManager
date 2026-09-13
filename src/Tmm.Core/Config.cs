using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tmm.Core;

/// <summary>
/// Application-wide constants. Every value that came from Spike B is annotated with the test that
/// established it. Do not change those without re-running the corresponding test in-game.
/// </summary>
public static class Constants
{
    // --- Audio format the game expects (Spike B, corpus survey: 44/44 files) ---------------
    public const int TargetSampleRate = 48_000;
    public const int TargetChannels = 2;
    public const int TargetBits = 16;

    // --- Fit engine defaults --------------------------------------------------------------
    /// <summary>|rho - 1| beyond this is rejected (user-adjustable).</summary>
    public const double DefaultStretchCap = 0.06;
    /// <summary>Applied only when the butt joint is audible.</summary>
    public const double DefaultSeamCrossfadeMs = 20;
    /// <summary>Shortest loop candidate we will propose.</summary>
    public const int MinBars = 4;
    /// <summary>&lt;= 0.5% stretch scores 100 on loop_fit.</summary>
    public const double MaxStretchFor100 = 0.005;

    // --- Game install layout (Spike B Test 8) -----------------------------------------------
    // Loose paks must go in ~mods. The tilde sorts last and Unreal mounts later-sorting paks at
    // higher priority; paks in plain Mods/ are shadowed by the game's own patch containers for
    // some slots and silently fall back to stock. Three rounds of file-format work were wasted
    // on this before it was found. Do not "tidy" this path.
    public static readonly string PaksRelative = Path.Combine("Polaris", "Content", "Paks");
    public const string ModsDirName = "~mods";
    public static readonly string WemMediaRelative = Path.Combine("Polaris", "Content", "WwiseAudio", "Media");
    /// <summary>Path *inside* the pak, forward slashes, as UnrealPak wants it.</summary>
    public const string WemMediaPakPath = "Polaris/Content/WwiseAudio/Media";
    public const string StockPakName = "pakchunk0-Windows.pak";

    // --- Mod packaging conventions (Spike B corpus survey) ----------------------------------
    public const string PakSuffix = "_P";           // <ModName>_P.pak
    public const int WemsPerMod = 2;                // intro + loop, one pak per track
    public const bool PakCompression = false;       // UnrealPak without compression; matches tutorial + corpus

    /// <summary>Marker file next to the executable. When present, settings, catalog, mods and scratch
    /// live in &lt;exe folder&gt;\UserData instead of %LOCALAPPDATA% — the portable build ships it.</summary>
    public const string PortableMarker = "portable.txt";
    public const string PortableDataDirName = "UserData";

    public static bool IsPortable => File.Exists(Path.Combine(AppContext.BaseDirectory, PortableMarker));

    public static string DefaultAppDir()
    {
        if (IsPortable)
            return Path.Combine(AppContext.BaseDirectory, PortableDataDirName);
        var baseDir = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (string.IsNullOrEmpty(baseDir))
            baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(baseDir, "TekkenMusicModManager");
    }
}

/// <summary>User-editable settings, persisted as JSON under the app dir.</summary>
public sealed class Settings
{
    /// <summary>...\steamapps\common\TEKKEN 8</summary>
    public string? GameRoot { get; set; }
    public string AppDir { get; set; } = Constants.DefaultAppDir();
    public double StretchCap { get; set; } = Constants.DefaultStretchCap;
    public bool IncludeCoverageInScore { get; set; } = false;
    public double CoverageWeight { get; set; } = 0.0;
    /// <summary>"unrealpak" or "repak".</summary>
    public string Packer { get; set; } = "unrealpak";
    public string? UnrealPakPath { get; set; }
    public string? RepakPath { get; set; }
    public string? FfmpegPath { get; set; }
    /// <summary>Optional rubberband CLI. When absent the resampling fallback stretcher is used.</summary>
    public string? RubberBandPath { get; set; }
    /// <summary>Folder of stock WEMs the user already extracted (FModel). Used by the catalog build.</summary>
    public string? WemSourceFolder { get; set; }
    /// <summary>Loudness target for rendered loops. Null = leave the song's level alone.</summary>
    public double? TargetLufs { get; set; } = null;
    /// <summary>Optional, off by default: LLM-phrased explanations. Not on the critical path.</summary>
    public bool LlmExplanations { get; set; } = false;

    [JsonIgnore] public string CatalogPath => Path.Combine(AppDir, "catalog.json");
    /// <summary>Where we keep manifests + rendered WEMs + paks, independent of the game dir.</summary>
    [JsonIgnore] public string ModsStore => Path.Combine(AppDir, "mods");
    [JsonIgnore] public string CacheDir => Path.Combine(AppDir, "cache");
    [JsonIgnore] public string ScratchDir => Path.Combine(AppDir, "scratch");
    [JsonIgnore] public string SettingsPath => Path.Combine(AppDir, "settings.json");
    [JsonIgnore] public string? GameModsDir =>
        GameRoot is null ? null : Path.Combine(GameRoot, Constants.PaksRelative, Constants.ModsDirName);
    [JsonIgnore] public string? GamePaksDir =>
        GameRoot is null ? null : Path.Combine(GameRoot, Constants.PaksRelative);

    [JsonIgnore] public string FfmpegExe => string.IsNullOrWhiteSpace(FfmpegPath) ? "ffmpeg" : FfmpegPath!;

    public bool GameRootLooksValid() =>
        GameRoot is not null && Directory.Exists(Path.Combine(GameRoot, Constants.PaksRelative));

    public Settings Clone() => (Settings)MemberwiseClone();
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static Settings Load(string? appDir = null)
    {
        var dir = appDir ?? Constants.DefaultAppDir();
        var path = Path.Combine(dir, "settings.json");
        if (!File.Exists(path))
            return new Settings { AppDir = dir };
        try
        {
            var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Options) ?? new Settings();
            s.AppDir = dir;   // the file lives in the app dir by definition; never trust a stale value
            return s;
        }
        catch (JsonException)
        {
            return new Settings { AppDir = dir };
        }
    }

    public static void Save(Settings s)
    {
        Directory.CreateDirectory(s.AppDir);
        File.WriteAllText(s.SettingsPath, JsonSerializer.Serialize(s, Options));
    }
}
