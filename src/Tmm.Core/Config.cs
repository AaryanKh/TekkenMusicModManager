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
    /// <summary>Where the jukebox titles container goes: plain Paks\mods, not ~mods. Confirmed in game:
    /// the titles only show from here. The audio paks stay in ~mods (see above).</summary>
    public const string TitlesDirName = "mods";
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
    /// <summary>Folder of stock WEMs exported by hand. The catalog build only uses it when the game folder is not set.</summary>
    public string? WemSourceFolder { get; set; }
    /// <summary>Loudness target for rendered loops. Null = leave the song's level alone.</summary>
    public double? TargetLufs { get; set; } = null;
    /// <summary>Optional, off by default: LLM-phrased explanations. Not on the critical path.</summary>
    public bool LlmExplanations { get; set; } = false;
    /// <summary>Dashboard shows album-art tiles instead of the table. Purely a view preference.</summary>
    public bool DashboardTiles { get; set; } = false;
    /// <summary>Also write a small <c>&lt;pak&gt;.tmm.json</c> beside each pak that is built. It travels with
    /// the pak into ~mods, so the manager can recognise the pak later (see <c>ModMetadata</c>). Off by
    /// default: nothing extra is written unless asked for.</summary>
    public bool WriteModMetadata { get; set; } = false;
    /// <summary>Show each enabled mod's song in the jukebox instead of the stock track name, through one
    /// extra container in ~mods (see <c>Titles.JukeboxTitles</c>). English game text only.</summary>
    public bool RenameJukeboxTitles { get; set; } = true;

    [JsonIgnore] public string CatalogPath => Path.Combine(AppDir, "catalog.json");
    /// <summary>Where we keep manifests + rendered WEMs + paks, independent of the game dir.</summary>
    [JsonIgnore] public string ModsStore => Path.Combine(AppDir, "mods");
    [JsonIgnore] public string CacheDir => Path.Combine(AppDir, "cache");
    [JsonIgnore] public string ScratchDir => Path.Combine(AppDir, "scratch");
    [JsonIgnore] public string SettingsPath => Path.Combine(AppDir, "settings.json");
    [JsonIgnore] public string? GameModsDir =>
        GameRoot is null ? null : Path.Combine(GameRoot, Constants.PaksRelative, Constants.ModsDirName);
    /// <summary>Paks\mods, where the jukebox titles container is written.</summary>
    [JsonIgnore] public string? GameTitlesDir =>
        GameRoot is null ? null : Path.Combine(GameRoot, Constants.PaksRelative, Constants.TitlesDirName);
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
        FileOps.WriteAllText(s.SettingsPath, JsonSerializer.Serialize(s, Options));
    }
}
