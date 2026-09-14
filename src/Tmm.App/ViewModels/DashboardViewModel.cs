using System.Collections.ObjectModel;
using Tmm.App.Mvvm;
using Tmm.App.Services;
using Tmm.Core;
using Tmm.Core.Mods;

namespace Tmm.App.ViewModels;

public sealed class ModRow : ObservableObject
{
    private ModState _state;
    public required ModManifest Manifest { get; init; }
    public ModState State { get => _state; set { if (SetProperty(ref _state, value)) { OnPropertyChanged(nameof(StateLabel)); OnPropertyChanged(nameof(IsEnabled)); OnPropertyChanged(nameof(CanEnable)); } } }

    public string Name => Manifest.Name;
    public string SlotTitle => Manifest.SlotTitle;
    public string SongFile => Path.GetFileName(Manifest.SongPath);
    public string SongPath => Manifest.SongPath;
    public bool SongMissing => !File.Exists(Manifest.SongPath);
    public string LastBuilt => Manifest.Updated.ToLocalTime().ToString("g");
    public string WemIds => string.Join(", ", Manifest.WemIds);
    public string PlanSummary => $"start {Manifest.Plan.LoopStartSec:0.00} s · {Manifest.Plan.LoopBars} bars · stretch {Math.Abs(Manifest.Plan.Rho - 1) * 100:0.0}% · intro {Manifest.Plan.IntroStrategy}" +
                                 (Manifest.Plan.ManualOverrides.Count > 0 ? " · edited by hand" : "");

    /// <summary>Measured loudness of the last render plus any manual trim, so two mods can be
    /// compared side by side when one sounds quieter in game than the other.</summary>
    public string LoudnessSummary
    {
        get
        {
            var parts = new List<string>();
            if (Manifest.Lufs is double l && !double.IsNegativeInfinity(l)) parts.Add($"{l:0.0} LUFS");
            if (Math.Abs(Manifest.Plan.GainDb) > 1e-9) parts.Add($"trim {Manifest.Plan.GainDb:+0.0;-0.0} dB");
            return parts.Count == 0 ? "" : string.Join(" · ", parts);
        }
    }
    public string StateLabel => State switch
    {
        ModState.Enabled => "Enabled",
        ModState.Disabled => "Disabled",
        ModState.Stale => "Needs rebuild",
        ModState.Broken => "Broken",
        _ => State.ToString(),
    };
    public bool IsEnabled => State == ModState.Enabled;
    public bool CanEnable => State is ModState.Disabled or ModState.Stale;
}

public sealed class ThirdPartyRow
{
    public required string File { get; init; }
    public required int Count { get; init; }
    public required string Ids { get; init; }
}

/// <summary>
/// Mod manager. Table of ModManifest rows: name, slot, state badge, song file, last built.
/// Per-row: Enable / Disable (with conflict check), Rebuild, Open editor, Delete.
/// Header: 'Rebuild all stale', 'Scan ~mods for third-party paks', game path status.
/// State is derived from the filesystem on every refresh, never cached.
/// </summary>
public sealed class DashboardViewModel : ObservableObject
{
    private readonly AppServices _app;
    private bool _isBusy;
    private string _busyText = "";
    private string _gameStatus = "";
    private bool _gameOk;
    private string _thirdPartyNote = "";
    private string _setupStatus = "";
    private bool _setupOk;

    public DashboardViewModel(AppServices app)
    {
        _app = app;
        RefreshCommand = new RelayCommand(Refresh);
        EnableCommand = new AsyncRelayCommand(p => EnableAsync(p as ModRow), p => p is ModRow r && r.CanEnable && !IsBusy);
        DisableCommand = new AsyncRelayCommand(p => DisableAsync(p as ModRow), p => p is ModRow r && r.IsEnabled && !IsBusy);
        RebuildCommand = new AsyncRelayCommand(p => RebuildAsync(p as ModRow), p => p is ModRow && !IsBusy);
        EditCommand = new RelayCommand(p => { if (p is ModRow r) EditRequested?.Invoke(this, r.Manifest); }, p => p is ModRow && !IsBusy);
        DeleteCommand = new AsyncRelayCommand(p => DeleteAsync(p as ModRow), p => p is ModRow && !IsBusy);
        RebuildStaleCommand = new AsyncRelayCommand(RebuildStaleAsync, () => !IsBusy && Rows.Any(r => r.State == ModState.Stale));
        ScanCommand = new RelayCommand(ScanThirdParty, () => !IsBusy);
        OpenModsFolderCommand = new RelayCommand(() => OpenFolderRequested?.Invoke(this, _app.Settings.GameModsDir ?? ""), () => _app.Settings.GameModsDir is not null);
        GoToSettingsCommand = new RelayCommand(() => SettingsRequested?.Invoke(this, EventArgs.Empty));
        NewModCommand = new RelayCommand(() => NewModRequested?.Invoke(this, EventArgs.Empty));
    }

    public event EventHandler<ModManifest>? EditRequested;
    public event EventHandler<string>? OpenFolderRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? NewModRequested;
    public event EventHandler? ModsChanged;

    public ObservableCollection<ModRow> Rows { get; } = new();
    public ObservableCollection<ThirdPartyRow> ThirdParty { get; } = new();

    public RelayCommand RefreshCommand { get; }
    public AsyncRelayCommand EnableCommand { get; }
    public AsyncRelayCommand DisableCommand { get; }
    public AsyncRelayCommand RebuildCommand { get; }
    public RelayCommand EditCommand { get; }
    public AsyncRelayCommand DeleteCommand { get; }
    public AsyncRelayCommand RebuildStaleCommand { get; }
    public RelayCommand ScanCommand { get; }
    public RelayCommand OpenModsFolderCommand { get; }
    public RelayCommand GoToSettingsCommand { get; }
    public RelayCommand NewModCommand { get; }

    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) RaiseCommands(); } }
    public string BusyText { get => _busyText; private set => SetProperty(ref _busyText, value); }
    public string GameStatus { get => _gameStatus; private set => SetProperty(ref _gameStatus, value); }
    public bool GameOk { get => _gameOk; private set => SetProperty(ref _gameOk, value); }
    public string ThirdPartyNote { get => _thirdPartyNote; private set => SetProperty(ref _thirdPartyNote, value); }
    /// <summary>Game folder + ffmpeg + packer + catalog, in one line. Drives the "Settings" nudge.</summary>
    public string SetupStatus { get => _setupStatus; private set => SetProperty(ref _setupStatus, value); }
    public bool SetupOk { get => _setupOk; private set => SetProperty(ref _setupOk, value); }
    public bool HasMods => Rows.Count > 0;
    public string CountLine => Rows.Count == 0 ? "No mods yet." :
        $"{Rows.Count} mod(s): {Rows.Count(r => r.State == ModState.Enabled)} enabled, {Rows.Count(r => r.State == ModState.Disabled)} disabled, {Rows.Count(r => r.State == ModState.Stale)} need rebuild, {Rows.Count(r => r.State == ModState.Broken)} broken";

    private void RaiseCommands()
    {
        EnableCommand.RaiseCanExecuteChanged(); DisableCommand.RaiseCanExecuteChanged(); RebuildCommand.RaiseCanExecuteChanged();
        EditCommand.RaiseCanExecuteChanged(); DeleteCommand.RaiseCanExecuteChanged(); RebuildStaleCommand.RaiseCanExecuteChanged();
        ScanCommand.RaiseCanExecuteChanged(); OpenModsFolderCommand.RaiseCanExecuteChanged();
    }

    public void Refresh()
    {
        var s = _app.Settings;
        GameOk = s.GameRootLooksValid();
        GameStatus = GameOk ? $"Game: {s.GameRoot}  →  installs to {Constants.PaksRelative}\\{Constants.ModsDirName}"
                            : "Game folder not set or invalid — set it in Settings before enabling mods.";
        SetupStatus = _app.ReadinessSummary();
        SetupOk = SetupStatus == "Ready.";
        Rows.Clear();
        foreach (var m in _app.Registry.All())
            Rows.Add(new ModRow { Manifest = m, State = _app.Registry.StateOf(m) });
        OnPropertyChanged(nameof(HasMods)); OnPropertyChanged(nameof(CountLine));
        RaiseCommands();
    }

    private async Task EnableAsync(ModRow? row)
    {
        if (row is null) return;
        await RunAsync($"Enabling {row.Name}…", () =>
        {
            try { Installer.Enable(row.Manifest, _app.Registry); }
            catch (SlotConflictException e)
            {
                var msg = e.Message + "\n\nEnable anyway? Unreal will pick one of them by mount order and the other silently loses.";
                if (ConfirmOnUi("Slot conflict", msg))
                    Installer.Enable(row.Manifest, _app.Registry, force: true);
            }
        });
    }

    // Dialogs must run on the UI thread; RunAsync executes work on a background thread, so this
    // hop is done through a captured SynchronizationContext.
    private bool ConfirmOnUi(string title, string message)
    {
        var ctx = _uiContext;
        if (ctx is null) return _app.Dialogs.Confirm(title, message);
        bool result = false;
        ctx.Send(_ => result = _app.Dialogs.Confirm(title, message), null);
        return result;
    }

    private SynchronizationContext? _uiContext;
    // Created on the UI thread (constructor) so reports marshal back to it from Task.Run work.
    private Progress<(int done, int total, string what)>? _progress;

    private Task DisableAsync(ModRow? row) => row is null ? Task.CompletedTask
        : RunAsync($"Disabling {row.Name}…", () => Installer.Disable(row.Manifest, _app.Registry));

    private Task RebuildAsync(ModRow? row) => row is null ? Task.CompletedTask
        : RunAsync($"Rebuilding {row.Name}…", () => RebuildOne(row.Manifest));

    private void RebuildOne(ModManifest m)
    {
        var slot = _app.Catalog.Get(m.SlotKey)
                   ?? throw new CatalogNotBuiltException($"slot {m.SlotKey} ({m.SlotTitle}) is not in the catalog. Season 2 and collaboration tracks are not in the shipped catalog because they live outside pakchunk0 — export those paks and rebuild the catalog in Settings to add them.");
        _app.Builder().Rebuild(m, slot, _app.Settings.FfmpegExe, _progress);
    }

    private Task RebuildStaleAsync() => RunAsync("Rebuilding stale mods…", () =>
    {
        foreach (var r in Rows.Where(r => r.State == ModState.Stale).ToList()) RebuildOne(r.Manifest);
    });

    private async Task DeleteAsync(ModRow? row)
    {
        if (row is null) return;
        if (!_app.Dialogs.Confirm("Delete mod", $"Delete '{row.Name}'? This removes it from ~mods and from the app store. The original song file is not touched."))
            return;
        await RunAsync($"Deleting {row.Name}…", () => Installer.Delete(row.Manifest, _app.Registry));
    }

    private void ScanThirdParty()
    {
        // Renaming a pak by hand leaves the mod reading as Disabled and the file looking third-party.
        // Reclaim those first, so the third-party list below is genuinely other people's work.
        string adopted = AdoptRenamed();

        ThirdParty.Clear();
        var list = Conflicts.ScanThirdParty(_app.Registry, _app.Settings.GameModsDir);
        foreach (var t in list)
            ThirdParty.Add(new ThirdPartyRow { File = Path.GetFileName(t.Path), Count = t.WemIds.Count, Ids = string.Join(", ", t.WemIds.Take(6)) + (t.WemIds.Count > 6 ? "…" : "") });
        ThirdPartyNote = !_app.Settings.GameRootLooksValid() ? "Game folder not set."
                       : list.Count == 0 ? "No third-party audio paks found in ~mods." + adopted
                       : $"{list.Count} third-party pak(s) in ~mods override jukebox audio. Their slots are flagged when you enable a mod." + adopted;
    }

    /// <summary>Offer to re-point manifests at paks the user renamed on disk. Returns a note for the
    /// scan line, empty when there was nothing to do.</summary>
    private string AdoptRenamed()
    {
        var renamed = Reconcile.FindRenamed(_app.Registry, _app.Settings.GameModsDir);
        if (renamed.Count == 0)
        {
            // Nothing new to claim, but a mod adopted earlier may still be displaying the name it was
            // built under rather than the one on disk.
            var synced = Reconcile.SyncNames(_app.Registry);
            if (synced.Count == 0) return "";
            Refresh();
            ModsChanged?.Invoke(this, EventArgs.Empty);
            return $"  Renamed {synced.Count} mod(s) to match their pak.";
        }

        var nl = Environment.NewLine;
        var preview = string.Join(nl, renamed.Select(r =>
            $"  {r.ExpectedName}{nl}      becomes {r.FoundName}{nl}      ({r.Evidence})"));
        bool ok = _app.Dialogs.Confirm(
            renamed.Count == 1 ? "A renamed pak was found" : $"{renamed.Count} renamed paks were found",
            "These paks in ~mods belong to mods you built, under names you changed:" + nl + nl +
            preview + nl + nl +
            "Update the mods to use the new names? The copies kept by the app are renamed to match, so " +
            "rebuilding and enabling keep working. Nothing in the game folder is touched.");
        if (!ok) return $"  {renamed.Count} renamed pak(s) were left alone.";

        var log = Reconcile.Adopt(_app.Registry, renamed);
        Refresh();
        ModsChanged?.Invoke(this, EventArgs.Empty);
        return $"  Adopted {log.Count} renamed pak(s).";
    }

    private async Task RunAsync(string what, Action work)
    {
        _uiContext = SynchronizationContext.Current;
        _progress ??= new Progress<(int done, int total, string what)>(p => BusyText = p.what);
        IsBusy = true; BusyText = what;
        try { await Task.Run(work); }
        catch (TmmException e) { _app.Dialogs.ShowError("Operation failed", e.Message); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _app.Dialogs.ShowError("Operation failed", FileOps.Explain(e)); }
        finally
        {
            IsBusy = false; BusyText = "";
            Refresh();
            ModsChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
