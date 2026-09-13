using System.Text.Json;
using Tmm.Core;
using Tmm.Core.Analysis;
using Tmm.Core.Catalog;
using Tmm.Core.Mods;
using Tmm.Core.Pak;
using Tmm.Core.Render;
using Tmm.Core.Steam;

namespace Tmm.App.Services;

/// <summary>Composition root for the engine. One instance per app; rebuilt pieces pick up new settings.</summary>
public sealed class AppServices
{
    /// <summary>Written next to the exe by the installer (installer/TekkenMusicModManager.iss).
    /// Any field it carries fills the matching setting when that setting is empty or stale.</summary>
    public const string InstallHintsFile = "install-hints.json";

    public Settings Settings { get; private set; }
    public CatalogStore Catalog { get; private set; }
    public ModRegistry Registry { get; private set; }
    public IDialogService Dialogs { get; }
    public IAudioPreview Preview { get; }

    public event EventHandler? SettingsChanged;

    public AppServices(IDialogService dialogs, IAudioPreview preview, string? appDir = null)
    {
        Dialogs = dialogs; Preview = preview;
        Settings = SettingsStore.Load(appDir);
        if (ApplyInstallHints(Settings) | ApplyGameAutoDetect(Settings))
            SettingsStore.Save(Settings);
        Catalog = new CatalogStore(Settings.CatalogPath);
        Registry = new ModRegistry(Settings);
    }

    public string? SheetPath
    {
        get
        {
            foreach (var c in new[] { Path.Combine(AppContext.BaseDirectory, "data", "jukebox_slots.csv"), Path.Combine("data", "jukebox_slots.csv") })
                if (File.Exists(c)) return c;
            return null;
        }
    }

    public ISongAnalyzer Analyzer() => new SongAnalyzer(Settings.FfmpegExe);
    public IStretcher Stretcher() => StretcherFactory.FromSettings(Settings);
    public IPacker Packer() => PackerFactory.FromSettings(Settings);
    public ModBuilder Builder() => new(Registry, Stretcher(), Packer());

    public void SaveSettings(Settings updated)
    {
        SettingsStore.Save(updated);
        Settings = updated;
        Catalog = new CatalogStore(Settings.CatalogPath);
        Registry = new ModRegistry(Settings);
        _ffmpegOk = null;
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ReloadCatalog() => Catalog = new CatalogStore(Settings.CatalogPath);

    // ------------------------------------------------------------------ readiness

    private bool? _ffmpegOk;

    /// <summary>Spawns `ffmpeg -version` once per settings change; cached afterwards.</summary>
    public bool FfmpegOk => _ffmpegOk ??= Decoder.FfmpegAvailable(Settings.FfmpegExe);

    public bool PackerOk => Settings.Packer.Equals("repak", StringComparison.OrdinalIgnoreCase)
        ? !string.IsNullOrWhiteSpace(Settings.RepakPath) && File.Exists(Settings.RepakPath)
        : !string.IsNullOrWhiteSpace(Settings.UnrealPakPath) && File.Exists(Settings.UnrealPakPath);

    public bool GameOk => Settings.GameRootLooksValid();

    /// <summary>One line for the status bar / dashboard: what is still missing before a mod can ship.</summary>
    public string ReadinessSummary()
    {
        var missing = new List<string>();
        if (!GameOk) missing.Add("TEKKEN 8 folder");
        if (!FfmpegOk) missing.Add("ffmpeg");
        if (!PackerOk) missing.Add(Settings.Packer.Equals("repak", StringComparison.OrdinalIgnoreCase) ? "repak.exe" : "UnrealPak.exe");
        if (!Catalog.IsBuilt) missing.Add("slot catalog");
        return missing.Count == 0 ? "Ready." : "Not set up yet: " + string.Join(", ", missing) + " — see Settings.";
    }

    // ------------------------------------------------------------------ first-run helpers

    private sealed class InstallHints
    {
        public string? FfmpegPath { get; set; }
        public string? UnrealPakPath { get; set; }
        public string? RepakPath { get; set; }
        public string? RubberBandPath { get; set; }
        public string? GameRoot { get; set; }
    }

    /// <summary>Returns true when a setting was changed.</summary>
    private static bool ApplyInstallHints(Settings s)
    {
        var path = Path.Combine(AppContext.BaseDirectory, InstallHintsFile);
        if (!File.Exists(path)) return false;
        InstallHints? h;
        try { h = JsonSerializer.Deserialize<InstallHints>(File.ReadAllText(path)); }
        catch (JsonException) { return false; }
        if (h is null) return false;

        bool changed = false;
        changed |= Fill(h.FfmpegPath, () => s.FfmpegPath, v => s.FfmpegPath = v);
        changed |= Fill(h.UnrealPakPath, () => s.UnrealPakPath, v => s.UnrealPakPath = v);
        changed |= Fill(h.RepakPath, () => s.RepakPath, v => s.RepakPath = v);
        changed |= Fill(h.RubberBandPath, () => s.RubberBandPath, v => s.RubberBandPath = v);
        if (!string.IsNullOrWhiteSpace(h.GameRoot) && Directory.Exists(h.GameRoot) && !s.GameRootLooksValid())
        {
            s.GameRoot = h.GameRoot; changed = true;
        }
        return changed;

        // A hint wins only when the current value is empty or points at a file that no longer exists.
        static bool Fill(string? hint, Func<string?> get, Action<string> set)
        {
            if (string.IsNullOrWhiteSpace(hint) || !File.Exists(hint)) return false;
            var cur = get();
            if (!string.IsNullOrWhiteSpace(cur) && File.Exists(cur)) return false;
            set(hint);
            return true;
        }
    }

    private static bool ApplyGameAutoDetect(Settings s)
    {
        if (s.GameRootLooksValid()) return false;
        var root = SteamLocator.FindGameRoot();
        if (root is null) return false;
        s.GameRoot = root;
        return true;
    }
}
