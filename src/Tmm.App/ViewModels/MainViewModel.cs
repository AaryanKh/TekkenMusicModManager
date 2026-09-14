using Tmm.App.Mvvm;
using Tmm.App.Services;
using Tmm.Core;
using Tmm.Core.Analysis;

namespace Tmm.App.ViewModels;

public enum Section { Create, Dashboard, Settings }
public enum CreateStep { Import, Ranking, Editor }

/// <summary>
/// Shell: a sidebar with the top-level modes and a content area.
///   Create      import -&gt; ranking -&gt; editor/preview -&gt; build   (a wizard, left to right)
///   Dashboard   installed mods, enable/disable, rebuild, conflicts
///   Settings    game path, packer, stretch cap, optional catalog rebuild
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly AppServices _app;
    private Section _section = Section.Dashboard;
    private CreateStep _step = CreateStep.Import;
    private string _status = "";
    private bool _isBusy;

    public MainViewModel(AppServices app)
    {
        _app = app;
        Import = new ImportViewModel(app);
        Ranking = new RankingViewModel(app);
        Editor = new EditorViewModel(app);
        Dashboard = new DashboardViewModel(app);
        Settings = new SettingsViewModel(app);

        GoCreateCommand = new RelayCommand(() => Section = Section.Create);
        GoDashboardCommand = new RelayCommand(() => { Section = Section.Dashboard; Dashboard.Refresh(); });
        GoSettingsCommand = new RelayCommand(() => Section = Section.Settings);
        StepImportCommand = new RelayCommand(() => Step = CreateStep.Import);
        StepRankingCommand = new RelayCommand(() => Step = CreateStep.Ranking, () => Ranking.HasSong);
        StepEditorCommand = new RelayCommand(() => Step = CreateStep.Editor, () => Editor.HasSong);

        Import.Analyzed += (_, song) => { Ranking.Load(song); Step = CreateStep.Ranking; Status = $"Analyzed {song.Song.Title}: {Ranking.TotalFits} slots fit inside the stretch cap."; };
        Ranking.SlotChosen += (_, e) => { Editor.Load(e.song, e.slot, e.score); Step = CreateStep.Editor; Status = $"Editing {e.slot.Title}"; };
        Editor.BackRequested += (_, _) => Step = Ranking.HasSong ? CreateStep.Ranking : CreateStep.Import;
        Editor.Built += (_, m) =>
        {
            Status = $"Built '{m.Name}' for {m.SlotTitle}. Enable it from the dashboard.";
            Ranking.RefreshClaims();
            Section = Section.Dashboard; Dashboard.Refresh();
        };
        Editor.PlanSaved += (_, m) => { Status = $"Saved plan for '{m.Name}' — it now needs a rebuild."; Section = Section.Dashboard; Dashboard.Refresh(); };
        Dashboard.EditRequested += async (_, m) => await OpenExistingAsync(m);
        Dashboard.SettingsRequested += (_, _) => Section = Section.Settings;
        Dashboard.NewModRequested += (_, _) => { Section = Section.Create; Step = CreateStep.Import; };
        Dashboard.ModsChanged += (_, _) => Ranking.RefreshClaims();
        Dashboard.OpenFolderRequested += (_, path) => OpenFolderRequested?.Invoke(this, path);
        Settings.GameCoversChanged += (_, _) => Dashboard.ReloadGameCovers();
        Settings.Saved += (_, _) => { Status = "Settings saved. " + _app.ReadinessSummary(); Dashboard.Refresh(); if (Ranking.Song is not null) Ranking.Load(Ranking.Song); };

        Dashboard.Refresh();
        Status = _app.ReadinessSummary();
    }

    public event EventHandler<string>? OpenFolderRequested;

    public ImportViewModel Import { get; }
    public RankingViewModel Ranking { get; }
    public EditorViewModel Editor { get; }
    public DashboardViewModel Dashboard { get; }
    public SettingsViewModel Settings { get; }

    public RelayCommand GoCreateCommand { get; }
    public RelayCommand GoDashboardCommand { get; }
    public RelayCommand GoSettingsCommand { get; }
    public RelayCommand StepImportCommand { get; }
    public RelayCommand StepRankingCommand { get; }
    public RelayCommand StepEditorCommand { get; }

    public Section Section
    {
        get => _section;
        set
        {
            if (SetProperty(ref _section, value))
            {
                OnPropertyChanged(nameof(IsCreate)); OnPropertyChanged(nameof(IsDashboard)); OnPropertyChanged(nameof(IsSettings));
                OnPropertyChanged(nameof(CurrentPage));
            }
        }
    }

    public CreateStep Step
    {
        get => _step;
        set
        {
            if (SetProperty(ref _step, value))
            {
                OnPropertyChanged(nameof(IsImportStep)); OnPropertyChanged(nameof(IsRankingStep)); OnPropertyChanged(nameof(IsEditorStep));
                OnPropertyChanged(nameof(CurrentStepPage));
                StepRankingCommand.RaiseCanExecuteChanged(); StepEditorCommand.RaiseCanExecuteChanged();
            }
        }
    }

    // Radio buttons bind these two-way: a click pushes `true`; `false` (the group unchecking the
    // others) is ignored. Two-way is deliberate — a one-way binding would be discarded by the
    // control's own local value on the first click.
    public bool IsCreate { get => _section == Section.Create; set { if (value) Section = Section.Create; } }
    public bool IsDashboard { get => _section == Section.Dashboard; set { if (value) { Section = Section.Dashboard; Dashboard.Refresh(); } } }
    public bool IsSettings { get => _section == Section.Settings; set { if (value) Section = Section.Settings; } }
    public bool IsImportStep { get => _step == CreateStep.Import; set { if (value) Step = CreateStep.Import; } }
    public bool IsRankingStep { get => _step == CreateStep.Ranking; set { if (value && Ranking.HasSong) Step = CreateStep.Ranking; } }
    public bool IsEditorStep { get => _step == CreateStep.Editor; set { if (value && Editor.HasSong) Step = CreateStep.Editor; } }

    /// <summary>The section page; the Create page hosts <see cref="CurrentStepPage"/>.</summary>
    public object CurrentPage => _section switch
    {
        Section.Create => this,
        Section.Dashboard => Dashboard,
        _ => Settings,
    };

    public object CurrentStepPage => _step switch
    {
        CreateStep.Import => Import,
        CreateStep.Ranking => Ranking,
        _ => Editor,
    };

    public string Status { get => _status; set => SetProperty(ref _status, value); }
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public string Title => "Tekken Music Mod Manager";

    /// <summary>Dashboard → Edit: decode + analyze the mod's song again, then open the editor on its plan.</summary>
    private async Task OpenExistingAsync(ModManifest m)
    {
        var slot = _app.Catalog.Get(m.SlotKey);
        if (slot is null) { _app.Dialogs.ShowError("Catalog", $"Slot {m.SlotKey} ({m.SlotTitle}) is not in the catalog. Season 2 and collaboration tracks are not in the shipped catalog because they live outside pakchunk0 — export those paks and rebuild the catalog in Settings to add them."); return; }
        if (!File.Exists(m.SongPath)) { _app.Dialogs.ShowError("Song missing", $"The source song is no longer at:\n{m.SongPath}"); return; }
        IsBusy = true; Status = $"Re-analyzing {Path.GetFileName(m.SongPath)}…";
        try
        {
            var analyzer = _app.Analyzer();
            var progress = new Progress<(int done, int total, string what)>(p => Status = p.what);
            var song = await Task.Run(() => analyzer.Analyze(m.SongPath, progress));
            if (song.Song.Fingerprint != m.SongFingerprint)
                _app.Dialogs.ShowInfo("Song changed", "The song file's contents differ from when this mod was built. The plan still applies, but check the loop points.");
            Ranking.Load(song);
            Editor.LoadExisting(song, slot, m);
            Section = Section.Create; Step = CreateStep.Editor;
            Status = $"Editing '{m.Name}'";
        }
        catch (TmmException e) { _app.Dialogs.ShowError("Could not open mod", e.Message); Status = ""; }
        finally { IsBusy = false; }
    }

    /// <summary>File dropped anywhere on the window.</summary>
    public void DropFile(string path)
    {
        Section = Section.Create; Step = CreateStep.Import;
        Import.DropFile(path);
    }
}
