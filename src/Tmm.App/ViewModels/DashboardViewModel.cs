using System.Collections.ObjectModel;
using Tmm.App.Mvvm;
using Tmm.App.Services;
using Tmm.Core;
using Tmm.Core.Analysis;
using Tmm.Core.Mods;

namespace Tmm.App.ViewModels;

public sealed class ModRow : ObservableObject
{
    private ModState _state;
    public required ModManifest Manifest { get; init; }
    public ModState State { get => _state; set { if (SetProperty(ref _state, value)) { OnPropertyChanged(nameof(StateLabel)); OnPropertyChanged(nameof(IsEnabled)); OnPropertyChanged(nameof(CanEnable)); OnPropertyChanged(nameof(CoverForState)); } } }

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

    // ------------------------------------------------------------------ tile view

    private System.Windows.Media.ImageSource? _cover;
    private System.Windows.Media.ImageSource? _coverGray;

    /// <summary>The song's own album art, or the question-mark placeholder. Set after extraction.</summary>
    public System.Windows.Media.ImageSource? Cover
    {
        get => _cover;
        set { if (SetProperty(ref _cover, value)) { _coverGray = null; OnPropertyChanged(nameof(CoverForState)); OnPropertyChanged(nameof(HasOwnArt)); } }
    }

    /// <summary>Desaturated copy shown while the mod is not enabled.</summary>
    public System.Windows.Media.ImageSource? CoverGray { get => _coverGray; set => _coverGray = value; }

    /// <summary>Colour when enabled, grey otherwise, so the dot and the art tell the same story.</summary>
    public System.Windows.Media.ImageSource? CoverForState => IsEnabled ? _cover : (_coverGray ?? _cover);

    /// <summary>The Tekken game this slot belongs to, drawn behind the tile when it has focus.</summary>
    private System.Windows.Media.ImageSource? _gameCover;
    public System.Windows.Media.ImageSource? GameCover { get => _gameCover; set => SetProperty(ref _gameCover, value); }

    public bool HasOwnArt { get; set; }
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
    private bool _showTiles;
    private ModRow? _selectedRow;
    private int _refreshGeneration;

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
        ToggleViewCommand = new RelayCommand(() => ShowTiles = !ShowTiles);
        PlayPauseCommand = new RelayCommand(OnPlayPause, () => CanPlay);
        _app.Preview.PositionChanged += (_, _) => OnPlayerTick();
        _app.Preview.PlaybackEnded += (_, _) => { if (IsOurs) { _playingModId = null; _fromTicker = true; PlayerPositionSec = 0; _fromTicker = false; } RaisePlayerState(); };
        ClearSelectionCommand = new RelayCommand(() => SelectedRow = null, () => HasSelection);
        FindArtCommand = new AsyncRelayCommand(p => FindArtAsync(p as ModRow), p => p is ModRow r && !IsBusy && !r.SongMissing);
        RemoveArtCommand = new RelayCommand(p => RemoveArt(p as ModRow), p => p is ModRow r && r.HasOwnArt && !IsBusy);
        _showTiles = _app.Settings.DashboardTiles;
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
    public RelayCommand ToggleViewCommand { get; }
    public RelayCommand PlayPauseCommand { get; }
    public RelayCommand ClearSelectionCommand { get; }
    public AsyncRelayCommand FindArtCommand { get; }
    public RelayCommand RemoveArtCommand { get; }

    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) RaiseCommands(); } }
    public string BusyText { get => _busyText; private set => SetProperty(ref _busyText, value); }
    public string GameStatus { get => _gameStatus; private set => SetProperty(ref _gameStatus, value); }
    public bool GameOk { get => _gameOk; private set => SetProperty(ref _gameOk, value); }
    public string ThirdPartyNote { get => _thirdPartyNote; private set => SetProperty(ref _thirdPartyNote, value); }
    /// <summary>Game folder + ffmpeg + packer + catalog, in one line. Drives the "Settings" nudge.</summary>
    public string SetupStatus { get => _setupStatus; private set => SetProperty(ref _setupStatus, value); }
    public bool SetupOk { get => _setupOk; private set => SetProperty(ref _setupOk, value); }
    public bool HasMods => Rows.Count > 0;

    /// <summary>Tiles with album art, or the table. Remembered across launches; the app dir is not
    /// rebuilt for a view preference, so the setting is saved directly.</summary>
    public bool ShowTiles
    {
        get => _showTiles;
        set
        {
            if (!SetProperty(ref _showTiles, value)) return;
            OnPropertyChanged(nameof(ShowTable)); OnPropertyChanged(nameof(ViewToggleLabel));
            _app.Settings.DashboardTiles = value;
            try { SettingsStore.Save(_app.Settings); } catch (TmmException) { /* a view preference is not worth a dialog */ }
            if (value) LoadCoversAsync();
        }
    }
    public bool ShowTable => !_showTiles;
    public string ViewToggleLabel => _showTiles ? "☰  Table view" : "▦  Tile view";

    /// <summary>The tile with focus. Its details and actions appear beneath the grid.</summary>
    public ModRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (!SetProperty(ref _selectedRow, value)) return;
            StopPlayer();                       // a different mod means a different song
            OnPropertyChanged(nameof(HasSelection));
            RaiseCommands();
        }
    }
    public bool HasSelection => _selectedRow is not null;
    public string CountLine => Rows.Count == 0 ? "No mods yet." :
        $"{Rows.Count} mod(s): {Rows.Count(r => r.State == ModState.Enabled)} enabled, {Rows.Count(r => r.State == ModState.Disabled)} disabled, {Rows.Count(r => r.State == ModState.Stale)} need rebuild, {Rows.Count(r => r.State == ModState.Broken)} broken";

    private void RaiseCommands()
    {
        EnableCommand.RaiseCanExecuteChanged(); DisableCommand.RaiseCanExecuteChanged(); RebuildCommand.RaiseCanExecuteChanged();
        EditCommand.RaiseCanExecuteChanged(); DeleteCommand.RaiseCanExecuteChanged(); RebuildStaleCommand.RaiseCanExecuteChanged();
        ScanCommand.RaiseCanExecuteChanged(); OpenModsFolderCommand.RaiseCanExecuteChanged();
        ClearSelectionCommand.RaiseCanExecuteChanged(); FindArtCommand.RaiseCanExecuteChanged();
        RemoveArtCommand.RaiseCanExecuteChanged();
    }

    public void Refresh()
    {
        var s = _app.Settings;
        GameOk = s.GameRootLooksValid();
        GameStatus = GameOk ? $"Game: {s.GameRoot}  →  installs to {Constants.PaksRelative}\\{Constants.ModsDirName}"
                            : "Game folder not set or invalid — set it in Settings before enabling mods.";
        SetupStatus = _app.ReadinessSummary();
        SetupOk = SetupStatus == "Ready.";
        var keepId = _selectedRow?.Manifest.ModId;
        Rows.Clear();
        foreach (var m in _app.Registry.All())
            Rows.Add(new ModRow { Manifest = m, State = _app.Registry.StateOf(m) });
        OnPropertyChanged(nameof(HasMods)); OnPropertyChanged(nameof(CountLine));
        SelectedRow = keepId is null ? null : Rows.FirstOrDefault(r => r.Manifest.ModId == keepId);
        RaiseCommands();
        if (_showTiles) LoadCoversAsync();
    }

    /// <summary>Drop the cached Tekken cards and re-attach them, after they were changed in Settings.</summary>
    public void ReloadGameCovers()
    {
        _app.Covers.ForgetGameCovers();
        foreach (var r in Rows) r.GameCover = null;
        if (_showTiles) LoadCoversAsync();
    }

    /// <summary>
    /// Fill in every tile's pictures. Anything already cached is attached immediately; songs not yet
    /// checked go through ffmpeg on a worker and land as they finish, so the grid appears at once and
    /// art fades in behind it. A newer refresh cancels the results of an older one.
    /// </summary>
    private void LoadCoversAsync()
    {
        var covers = _app.Covers;
        var placeholder = covers.Placeholder;
        int generation = ++_refreshGeneration;
        var rows = Rows.ToList();

        foreach (var row in rows)
        {
            row.GameCover ??= covers.GameCover(_app.Catalog.Get(row.Manifest.SlotKey)?.Identity.Game ?? "");
            if (row.Cover is not null) continue;
            if (covers.IsCached(row.Manifest)) Attach(row, covers.LoadSongCover(row.Manifest), placeholder);
        }

        var pending = rows.Where(r => r.Cover is null).ToList();
        if (pending.Count == 0) return;
        var ui = SynchronizationContext.Current;
        Task.Run(() =>
        {
            foreach (var row in pending)
            {
                if (generation != _refreshGeneration) return;
                covers.EnsureExtracted(row.Manifest);
                var art = covers.LoadSongCover(row.Manifest);     // frozen: safe to hand across threads
                if (ui is null) Attach(row, art, placeholder);
                else ui.Post(_ => { if (generation == _refreshGeneration) Attach(row, art, placeholder); }, null);
            }
        });
    }

    private void Attach(ModRow row, System.Windows.Media.ImageSource? art, System.Windows.Media.ImageSource placeholder)
    {
        row.HasOwnArt = art is not null;
        var shown = art ?? placeholder;
        row.CoverGray = _app.Covers.Gray(shown);
        row.Cover = shown;
        RemoveArtCommand.RaiseCanExecuteChanged();
    }

    // ------------------------------------------------------------------ track player

    private string? _playingModId;
    private double _playerPosition;
    private bool _fromTicker;

    /// <summary>Only a selected mod with its song still on disk can be played.</summary>
    public bool CanPlay => _selectedRow is { SongMissing: false };

    /// <summary>True while this dashboard's player owns what the audio service is playing. The editor
    /// shares that service, so the button must not claim a preview it did not start.</summary>
    private bool IsOurs => _playingModId is not null && _playingModId == _selectedRow?.Manifest.ModId;

    public bool IsPlayerActive => IsOurs && _app.Preview.IsPlaying;
    public bool IsPlayerPaused => IsPlayerActive && _app.Preview.IsPaused;
    public string PlayPauseGlyph => IsPlayerActive && !IsPlayerPaused ? "❚❚" : "▶";

    public double PlayerDurationSec => IsOurs ? Math.Max(0, _app.Preview.DurationSec) : 0;

    /// <summary>Bound two-way to the scrub bar. Writes from the ticker must not be read back as a
    /// seek, or playback would fight the user's drag.</summary>
    public double PlayerPositionSec
    {
        get => _playerPosition;
        set
        {
            if (!SetProperty(ref _playerPosition, value)) return;
            if (!_fromTicker && IsOurs) _app.Preview.Seek(value);
        }
    }

    public string PlayerTimeText => $"{Clock(PlayerPositionSec)} / {Clock(PlayerDurationSec)}";

    private static string Clock(double s)
    {
        if (double.IsNaN(s) || double.IsInfinity(s) || s < 0) s = 0;
        var ts = TimeSpan.FromSeconds(s);
        return $"{(int)ts.TotalMinutes}:{ts.Seconds:00}";
    }

    /// <summary>What the player is playing: the song the mod was built from.</summary>
    public string PlayerTitle => _selectedRow is null ? ""
        : _selectedRow.SongMissing ? "Source song is missing" : _selectedRow.SongFile;

    private void OnPlayPause()
    {
        var row = _selectedRow;
        if (row is null || row.SongMissing) return;

        if (IsOurs && _app.Preview.IsPlaying)
        {
            if (_app.Preview.IsPaused) _app.Preview.Resume(); else _app.Preview.Pause();
            RaisePlayerState();
            return;
        }

        try
        {
            _app.Preview.Play(row.Manifest.SongPath);
            _playingModId = row.Manifest.ModId;
            // Start where the mod's loop does, so pressing play lands on the part that was used
            // rather than an intro the mod may not even include.
            _startAtSec = row.Manifest.Plan.LoopStartSec;
            RaisePlayerState();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _app.Dialogs.ShowError("Could not play this song", e.Message);
        }
    }

    private double _startAtSec;

    /// <summary>Stop whatever this dashboard started. Leaves an editor preview alone.</summary>
    private void StopPlayer()
    {
        if (IsOurs && _app.Preview.IsPlaying) _app.Preview.Stop();
        _playingModId = null;
        _startAtSec = 0;
        _fromTicker = true; PlayerPositionSec = 0; _fromTicker = false;
        RaisePlayerState();
    }

    private void OnPlayerTick()
    {
        if (!IsOurs) return;
        // The seek has to wait until the file is open and its length is known.
        if (_startAtSec > 0 && _app.Preview.DurationSec > 0)
        {
            var target = Math.Min(_startAtSec, Math.Max(0, _app.Preview.DurationSec - 1));
            _startAtSec = 0;
            _app.Preview.Seek(target);
        }
        _fromTicker = true;
        PlayerPositionSec = _app.Preview.PositionSec;
        _fromTicker = false;
        OnPropertyChanged(nameof(PlayerTimeText));
        OnPropertyChanged(nameof(PlayerDurationSec));
    }

    private void RaisePlayerState()
    {
        OnPropertyChanged(nameof(IsPlayerActive)); OnPropertyChanged(nameof(IsPlayerPaused));
        OnPropertyChanged(nameof(PlayPauseGlyph)); OnPropertyChanged(nameof(PlayerDurationSec));
        OnPropertyChanged(nameof(PlayerTimeText)); OnPropertyChanged(nameof(CanPlay));
        OnPropertyChanged(nameof(PlayerTitle));
        PlayPauseCommand.RaiseCanExecuteChanged();
    }

    // ------------------------------------------------------------------ album art actions (tile view)

    /// <summary>
    /// Offer pictures for this mod: the one inside the song file if it has one, then whatever the
    /// song's tags turn up online. The user picks; nothing is applied without a choice.
    /// </summary>
    private async Task FindArtAsync(ModRow? row)
    {
        if (row is null) return;
        var m = row.Manifest;
        IsBusy = true; BusyText = "Looking for album art…";
        try
        {
            var ffmpeg = _app.Settings.FfmpegExe;
            var (candidates, query, failure) = await Task.Run(async () =>
            {
                var list = new List<ArtCandidate>();
                var embedded = _app.Covers.EmbeddedCandidate(m);
                if (embedded is not null) list.Add(embedded);
                var tags = SongTagReader.Read(m.SongPath, ffmpeg);
                // Tags first, then the mod's own name and looser pieces of it, until something lands.
                var ladder = AlbumArtSearch.QueryLadder(tags, m.SongPath, m.Name);
                var q = ladder.Count > 0 ? ladder[0] : "";
                string? fail = null;
                try
                {
                    var (found, used) = await AlbumArtSearch.SearchLadderAsync(ladder);
                    list.AddRange(found);
                    if (found.Count > 0) q = used;
                }
                catch (Exception e) when (e is System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
                {
                    fail = e is TaskCanceledException ? "The lookup timed out." : e.Message;
                }
                return (list, q, fail);
            });

            if (candidates.Count == 0)
            {
                _app.Dialogs.ShowInfo("No art found",
                    (failure is null ? "" : "Online lookup failed: " + failure + Environment.NewLine + Environment.NewLine) +
                    "Nothing was found for “" + query + "” and the song file has no picture inside it." + Environment.NewLine +
                    "Tagging the file with its album name usually fixes this.");
                return;
            }

            var heading = "Album art for " + m.Name
                + (query.Length > 0 ? " — searched for “" + query + "”" : "")
                + (failure is null ? "" : " (online lookup failed: " + failure + ")");
            var chosen = _app.Dialogs.PickArt(heading, candidates);
            if (chosen is null) return;

            BusyText = "Fetching picture…";
            var bytes = await AlbumArtSearch.DownloadAsync(chosen);
            _app.Covers.SetCover(m, bytes);
            Attach(row, _app.Covers.LoadSongCover(m), _app.Covers.Placeholder);
        }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            _app.Dialogs.ShowError("Could not fetch the picture", e.Message);
        }
        catch (TmmException e) { _app.Dialogs.ShowError("Could not save the picture", e.Message); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _app.Dialogs.ShowError("Could not save the picture", FileOps.Explain(e)); }
        finally { IsBusy = false; BusyText = ""; }
    }

    private void RemoveArt(ModRow? row)
    {
        if (row is null) return;
        try
        {
            _app.Covers.RemoveCover(row.Manifest);
            Attach(row, null, _app.Covers.Placeholder);
        }
        catch (TmmException e) { _app.Dialogs.ShowError("Could not remove the picture", e.Message); }
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
