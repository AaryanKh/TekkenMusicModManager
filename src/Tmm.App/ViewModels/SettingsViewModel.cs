using System.Diagnostics;
using Tmm.App.Mvvm;
using Tmm.App.Services;
using Tmm.Core;
using Tmm.Core.Analysis;
using Tmm.Core.Catalog;
using Tmm.Core.Steam;

namespace Tmm.App.ViewModels;

/// <summary>Game root (auto-detect via Steam libraryfolders.vdf, else browse), packer choice and
/// path, ffmpeg path, stretch cap default, 'Build catalog' with progress.</summary>
public sealed class SettingsViewModel : ObservableObject
{
    public static readonly string[] Packers = { "unrealpak", "repak" };

    private readonly AppServices _app;
    private Settings _draft;
    private bool _isBuilding;
    private double _progress;
    private string _buildStatus = "";
    private string _catalogStatus = "";
    private string _ffmpegStatus = "";
    private string _gameStatus = "";
    private bool _dirty;
    private bool _isInstallingFfmpeg;

    public SettingsViewModel(AppServices app)
    {
        _app = app;
        _draft = app.Settings.Clone();
        AutoDetectCommand = new RelayCommand(AutoDetect);
        BrowseGameCommand = new RelayCommand(() => Pick(p => GameRoot = p, folder: true, "Select the TEKKEN 8 folder (…\\steamapps\\common\\TEKKEN 8)"));
        BrowseUnrealPakCommand = new RelayCommand(() => Pick(p => UnrealPakPath = p, false, "Locate UnrealPak.exe", "UnrealPak.exe|UnrealPak.exe|Executables|*.exe"));
        BrowseRepakCommand = new RelayCommand(() => Pick(p => RepakPath = p, false, "Locate repak.exe", "Executables|*.exe"));
        BrowseFfmpegCommand = new RelayCommand(() => Pick(p => FfmpegPath = p, false, "Locate ffmpeg.exe", "ffmpeg.exe|ffmpeg.exe|Executables|*.exe"));
        BrowseRubberBandCommand = new RelayCommand(() => Pick(p => RubberBandPath = p, false, "Locate rubberband.exe", "Executables|*.exe"));
        BrowseWemFolderCommand = new RelayCommand(() => Pick(p => WemSourceFolder = p, true, "Folder with the extracted stock .wem files"));
        SaveCommand = new RelayCommand(Save, () => Dirty);
        RevertCommand = new RelayCommand(Revert, () => Dirty);
        BuildCatalogCommand = new AsyncRelayCommand(BuildCatalogAsync, () => !IsBuilding && !string.IsNullOrWhiteSpace(WemSourceFolder));
        CheckFfmpegCommand = new RelayCommand(CheckFfmpeg);
        InstallFfmpegCommand = new AsyncRelayCommand(InstallFfmpegAsync, () => !IsInstallingFfmpeg && WingetAvailable);
        RefreshStatus();
    }

    public event EventHandler? Saved;

    public RelayCommand AutoDetectCommand { get; }
    public RelayCommand BrowseGameCommand { get; }
    public RelayCommand BrowseUnrealPakCommand { get; }
    public RelayCommand BrowseRepakCommand { get; }
    public RelayCommand BrowseFfmpegCommand { get; }
    public RelayCommand BrowseRubberBandCommand { get; }
    public RelayCommand BrowseWemFolderCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand RevertCommand { get; }
    public AsyncRelayCommand BuildCatalogCommand { get; }
    public RelayCommand CheckFfmpegCommand { get; }
    public AsyncRelayCommand InstallFfmpegCommand { get; }

    public IReadOnlyList<string> PackerOptions => Packers;

    public string? GameRoot { get => _draft.GameRoot; set { _draft.GameRoot = Blank(value); Touch(); RefreshStatus(); } }
    public string Packer { get => _draft.Packer; set { _draft.Packer = value ?? "unrealpak"; Touch(); OnPropertyChanged(nameof(IsUnrealPak)); OnPropertyChanged(nameof(IsRepak)); } }
    public bool IsUnrealPak => _draft.Packer != "repak";
    public bool IsRepak => _draft.Packer == "repak";
    public string? UnrealPakPath { get => _draft.UnrealPakPath; set { _draft.UnrealPakPath = Blank(value); Touch(); } }
    public string? RepakPath { get => _draft.RepakPath; set { _draft.RepakPath = Blank(value); Touch(); } }
    public string? FfmpegPath { get => _draft.FfmpegPath; set { _draft.FfmpegPath = Blank(value); Touch(); } }
    public string? RubberBandPath { get => _draft.RubberBandPath; set { _draft.RubberBandPath = Blank(value); Touch(); } }
    public string? WemSourceFolder { get => _draft.WemSourceFolder; set { _draft.WemSourceFolder = Blank(value); Touch(); BuildCatalogCommand.RaiseCanExecuteChanged(); } }
    public double StretchCapPercent { get => _draft.StretchCap * 100; set { _draft.StretchCap = Math.Clamp(value, 0.5, 15) / 100; Touch(); } }
    public bool UseLoudnessTarget { get => _draft.TargetLufs is not null; set { _draft.TargetLufs = value ? (_draft.TargetLufs ?? -16.0) : null; Touch(); OnPropertyChanged(nameof(TargetLufs)); } }
    public double TargetLufs { get => _draft.TargetLufs ?? -16.0; set { if (_draft.TargetLufs is not null) { _draft.TargetLufs = Math.Clamp(value, -30, -6); Touch(); } } }
    public bool IncludeCoverage { get => _draft.IncludeCoverageInScore; set { _draft.IncludeCoverageInScore = value; _draft.CoverageWeight = value ? Math.Max(0.3, _draft.CoverageWeight) : 0; Touch(); } }
    public string AppDir => _draft.AppDir;

    public bool Dirty { get => _dirty; private set { if (SetProperty(ref _dirty, value)) { SaveCommand.RaiseCanExecuteChanged(); RevertCommand.RaiseCanExecuteChanged(); } } }
    public bool IsBuilding { get => _isBuilding; private set { if (SetProperty(ref _isBuilding, value)) BuildCatalogCommand.RaiseCanExecuteChanged(); } }
    public double Progress { get => _progress; private set => SetProperty(ref _progress, value); }
    public string BuildStatus { get => _buildStatus; private set => SetProperty(ref _buildStatus, value); }
    public string CatalogStatus { get => _catalogStatus; private set => SetProperty(ref _catalogStatus, value); }
    public string FfmpegStatus { get => _ffmpegStatus; private set => SetProperty(ref _ffmpegStatus, value); }
    public string GameStatus { get => _gameStatus; private set => SetProperty(ref _gameStatus, value); }
    public bool IsInstallingFfmpeg { get => _isInstallingFfmpeg; private set { if (SetProperty(ref _isInstallingFfmpeg, value)) InstallFfmpegCommand.RaiseCanExecuteChanged(); } }
    public bool IsPortable => Constants.IsPortable;
    public string StorageNote => Constants.IsPortable
        ? "Portable mode: settings, catalog and mods live in the UserData folder next to the program."
        : "Stored at " + _draft.AppDir;

    /// <summary>winget ships with Windows 10 1709+/11 as the "App Installer"; absent on some LTSC/enterprise images.</summary>
    public static bool WingetAvailable
    {
        get
        {
            var local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (!string.IsNullOrEmpty(local) && File.Exists(Path.Combine(local, "Microsoft", "WindowsApps", "winget.exe"))) return true;
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                if (File.Exists(Path.Combine(dir.Trim(), "winget.exe"))) return true;
            return false;
        }
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private void Touch([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        Dirty = true;
        OnPropertyChanged(name);
    }

    private void Pick(Action<string> set, bool folder, string title, string filter = "All files|*.*")
    {
        var p = folder ? _app.Dialogs.PickFolder(title) : _app.Dialogs.PickFile(title, filter);
        if (p is not null) set(p);
    }

    private void AutoDetect()
    {
        var root = SteamLocator.FindGameRoot();
        if (root is null) { _app.Dialogs.ShowInfo("Not found", "TEKKEN 8 was not found in any Steam library. Browse to it manually."); return; }
        GameRoot = root;
    }

    public void RefreshStatus()
    {
        var c = _app.Catalog;
        CatalogStatus = c switch
        {
            { IsBundled: true } => $"Using the catalog shipped with the app: {c.Count} slots, measured from stock WEM headers. " +
                                   "Nothing to extract — you can build mods right now. Rebuild below only if a game patch changes a track's length.",
            { IsBuilt: true } => $"Catalog built from your own install: {c.Count} slots measured ({c.BuiltAt?.ToLocalTime():g}).",
            _ => "No catalog. Ranking uses the sheet's whole-second lengths until you build one; building mods is disabled.",
        };
        GameStatus = _draft.GameRoot is null ? "Not set." : Directory.Exists(Path.Combine(_draft.GameRoot, Constants.PaksRelative)) ? "OK — Paks folder found." : "Folder exists but Polaris\\Content\\Paks is missing — is this the TEKKEN 8 root?";
        OnPropertyChanged(nameof(GameStatus));
    }

    /// <summary>Runs `winget install Gyan.FFmpeg` in a visible console (so its prompts/progress are seen),
    /// then points the ffmpeg setting at winget's link so it works without a re-login.</summary>
    private async Task InstallFfmpegAsync()
    {
        IsInstallingFfmpeg = true;
        FfmpegStatus = "Installing ffmpeg with winget… (a console window shows the progress)";
        try
        {
            int code = await Task.Run(() =>
            {
                var psi = new ProcessStartInfo("cmd.exe", "/C winget install --id Gyan.FFmpeg -e --accept-source-agreements --accept-package-agreements")
                { UseShellExecute = true };
                using var p = Process.Start(psi);
                if (p is null) return -1;
                p.WaitForExit();
                return p.ExitCode;
            });
            var local = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "";
            var link = Path.Combine(local, "Microsoft", "WinGet", "Links", "ffmpeg.exe");
            if (File.Exists(link))
            {
                FfmpegPath = link;
                FfmpegStatus = $"ffmpeg installed: {link}. Click Save.";
            }
            else if (Decoder.FfmpegAvailable("ffmpeg"))
                FfmpegStatus = "ffmpeg is available on PATH.";
            else
                FfmpegStatus = code == 0
                    ? "winget finished but ffmpeg.exe was not found where winget normally links it. Restart the app, or browse to it."
                    : $"winget exited with code {code}. Install ffmpeg manually (https://www.gyan.dev/ffmpeg/builds/) and browse to ffmpeg.exe.";
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            FfmpegStatus = "Could not start winget: " + e.Message;
        }
        finally { IsInstallingFfmpeg = false; }
    }

    private void CheckFfmpeg()
    {
        var exe = string.IsNullOrWhiteSpace(_draft.FfmpegPath) ? "ffmpeg" : _draft.FfmpegPath!;
        FfmpegStatus = Decoder.FfmpegAvailable(exe) ? $"ffmpeg OK ({exe})" : $"ffmpeg not runnable at '{exe}'. Install with: winget install Gyan.FFmpeg";
    }

    private void Save()
    {
        _app.SaveSettings(_draft.Clone());
        Dirty = false;
        RefreshStatus();
        Saved?.Invoke(this, EventArgs.Empty);
    }

    private void Revert()
    {
        _draft = _app.Settings.Clone();
        Dirty = false;
        OnPropertyChanged(null);
        RefreshStatus();
    }

    private async Task BuildCatalogAsync()
    {
        var sheet = _app.SheetPath;
        if (sheet is null) { _app.Dialogs.ShowError("Sheet missing", "data/jukebox_slots.csv was not found next to the app."); return; }
        var wems = WemSourceFolder;
        if (wems is null) return;
        if (Dirty) Save();
        IsBuilding = true; Progress = 0; BuildStatus = "Reading stock WEM headers…";
        var progress = new Progress<(int done, int total, string what)>(p => { Progress = 100.0 * p.done / Math.Max(1, p.total); BuildStatus = $"{p.done}/{p.total}  {p.what}"; });
        try
        {
            var store = _app.Catalog; var scratch = _app.Settings.ScratchDir;
            var r = await Task.Run(() => CatalogBuilder.Build(sheet, new PreExtractedFolderExtractor(wems, allowMissing: true), scratch, store, progress));
            _app.ReloadCatalog();
            BuildStatus = r.Skipped.Count == 0
                ? $"Done: {r.Measured} slots measured."
                : $"Done: {r.Measured} of {r.Total} slots measured, {r.Skipped.Count} skipped.";
            if (r.Skipped.Count > 0)
            {
                var names = string.Join("\n", r.Skipped.Take(15).Select(s => $"  #{s.No}  {s.Title}"));
                if (r.Skipped.Count > 15) names += $"\n  …and {r.Skipped.Count - 15} more";
                _app.Dialogs.ShowError("Some slots were skipped",
                    $"{r.Measured} of {r.Total} slots were measured. These {r.Skipped.Count} had no stock WEM in the export folder, " +
                    "so they cannot be used as mod targets:\n\n" + names +
                    "\n\nSeason 2 and collab tracks are not in pakchunk0. Export the other pakchunks with FModel and build again to add them.");
            }
        }
        catch (TmmException e) { BuildStatus = "Failed."; _app.Dialogs.ShowError("Catalog build failed", e.Message); }
        finally { IsBuilding = false; RefreshStatus(); }
    }
}
