using System.Collections.ObjectModel;
using Tmm.App.Mvvm;
using Tmm.App.Services;
using Tmm.Core;
using Tmm.Core.Analysis;
using Tmm.Core.Catalog;
using Tmm.Core.LoopFit;

namespace Tmm.App.ViewModels;

public sealed class SlotScoreRow
{
    public required int Rank { get; init; }
    public required Slot Slot { get; init; }
    public required SlotScore Score { get; init; }
    public required bool Claimed { get; init; }

    public int LoopId => Slot.Key;
    public string Title => Slot.Title;
    public string Game => Slot.Identity.Game;
    public double LoopSeconds => Slot.LoopSeconds;
    public string Length => Slot.HasIntro ? $"{Slot.IntroSeconds:0.0} + {Slot.LoopSeconds:0.0} s" : $"{Slot.LoopSeconds:0.0} s";
    public string IntroLabel => Slot.HasIntro ? "intro + loop" : "loop only";
    public double Headline => Score.Headline;
    public double LoopFit => Score.LoopFit;
    public double Seam => Score.SeamQuality;
    public double IntroFit => Score.IntroFit;
    public double Coverage => Score.Coverage;
    public int Bars => Score.Candidate.Bars;
    public double StretchPercent => Math.Abs(Score.Rho - 1) * 100;
    public string Explain => Score.Explain();
    public string ClaimedLabel => Claimed ? "in use" : "";
}

/// <summary>
/// All slots sorted by compatibility %, each row showing the four components. Never a bare number.
/// Filters: min %, source game, loop-length band, loop-only, hide slots claimed by enabled mods.
/// The stretch-cap slider re-ranks live (candidates are slot-independent, so this is a sorted
/// lookup, not a re-analysis). Selecting a row -&gt; EditorView.
/// </summary>
public sealed class RankingViewModel : ObservableObject
{
    public static readonly string[] LengthBands = { "Any length", "Under 30 s", "30–60 s", "60–120 s", "Over 120 s" };

    private readonly AppServices _app;
    private AnalyzedSong? _song;
    private IReadOnlyList<Slot> _slots = Array.Empty<Slot>();
    private List<SlotScore> _ranked = new();
    private Dictionary<int, string> _claimed = new();
    private bool _provisional;
    private double _stretchCapPercent;
    private double _minPercent = 0;
    private string _gameFilter = "All games";
    private string _lengthBand = LengthBands[0];
    private bool _loopOnly;
    private bool _hideClaimed = true;
    private string _search = "";
    private SlotScoreRow? _selected;
    private int _totalFits;

    public RankingViewModel(AppServices app)
    {
        _app = app;
        _stretchCapPercent = app.Settings.StretchCap * 100;
        ChooseCommand = new RelayCommand(Choose, () => Selected is not null);
        ResetFiltersCommand = new RelayCommand(ResetFilters);
        Games.Add("All games");
    }

    public event EventHandler<(AnalyzedSong song, Slot slot, SlotScore score)>? SlotChosen;

    public ObservableCollection<SlotScoreRow> Rows { get; } = new();
    public ObservableCollection<string> Games { get; } = new();
    public IReadOnlyList<string> Bands => LengthBands;
    public RelayCommand ChooseCommand { get; }
    public RelayCommand ResetFiltersCommand { get; }

    public AnalyzedSong? Song => _song;
    public string SongTitle => _song?.Song.Title ?? "";
    public bool Provisional => _provisional;
    public bool HasSong => _song is not null;
    public int TotalFits { get => _totalFits; private set => SetProperty(ref _totalFits, value); }
    public string ProvisionalNote => _provisional
        ? "Catalog not built yet — lengths come from the community sheet (whole seconds). Ranking is approximate and building is disabled until you run Settings → Build catalog."
        : "";

    public double StretchCapPercent
    {
        get => _stretchCapPercent;
        set { if (SetProperty(ref _stretchCapPercent, Math.Clamp(value, 0.5, 15))) ReRank(); }
    }
    public double MinPercent { get => _minPercent; set { if (SetProperty(ref _minPercent, value)) ApplyFilters(); } }
    public string GameFilter { get => _gameFilter; set { if (SetProperty(ref _gameFilter, value)) ApplyFilters(); } }
    public string LengthBand { get => _lengthBand; set { if (SetProperty(ref _lengthBand, value)) ApplyFilters(); } }
    public bool LoopOnly { get => _loopOnly; set { if (SetProperty(ref _loopOnly, value)) ApplyFilters(); } }
    public bool HideClaimed { get => _hideClaimed; set { if (SetProperty(ref _hideClaimed, value)) ApplyFilters(); } }
    public string Search { get => _search; set { if (SetProperty(ref _search, value ?? "")) ApplyFilters(); } }

    public SlotScoreRow? Selected
    {
        get => _selected;
        set { if (SetProperty(ref _selected, value)) ChooseCommand.RaiseCanExecuteChanged(); }
    }

    public void Load(AnalyzedSong song)
    {
        _song = song;
        (_slots, _provisional) = CatalogBuilder.SlotsForRanking(_app.Catalog, _app.SheetPath);
        _claimed = _app.Registry.ClaimedSlots();
        var games = _slots.Select(s => s.Identity.Game).Distinct().OrderBy(g => g, StringComparer.OrdinalIgnoreCase).ToList();
        Games.Clear(); Games.Add("All games"); foreach (var g in games) Games.Add(g);
        if (!Games.Contains(GameFilter)) _gameFilter = "All games";
        OnPropertyChanged(nameof(GameFilter));
        OnPropertyChanged(nameof(SongTitle)); OnPropertyChanged(nameof(Provisional)); OnPropertyChanged(nameof(ProvisionalNote)); OnPropertyChanged(nameof(HasSong));
        ReRank();
    }

    /// <summary>Called when mods are enabled/disabled so the "in use" column stays truthful.</summary>
    public void RefreshClaims()
    {
        _claimed = _app.Registry.ClaimedSlots();
        ApplyFilters();
    }

    private void ReRank()
    {
        if (_song is null) { Rows.Clear(); TotalFits = 0; return; }
        var w = new Weights(
            Coverage: _app.Settings.IncludeCoverageInScore ? _app.Settings.CoverageWeight : 0.0,
            StretchCap: _stretchCapPercent / 100.0);
        _ranked = Ranking.Rank(_slots, _song.Candidates, _song.Song.DurationSec, w);
        TotalFits = _ranked.Count;
        ApplyFilters();
    }

    private void ApplyFilters()
    {
        var byKey = _slots.ToDictionary(s => s.Key);
        var prevKey = Selected?.LoopId;
        Rows.Clear();
        int rank = 0;
        foreach (var sc in _ranked)
        {
            rank++;
            var slot = byKey[sc.SlotKey];
            if (sc.Headline < _minPercent) continue;
            if (_gameFilter != "All games" && slot.Identity.Game != _gameFilter) continue;
            if (_loopOnly && slot.HasIntro) continue;
            bool claimed = _claimed.ContainsKey(slot.Key);
            if (_hideClaimed && claimed) continue;
            if (!InBand(slot.LoopSeconds)) continue;
            if (_search.Length > 0 && !slot.Title.Contains(_search, StringComparison.OrdinalIgnoreCase)) continue;
            Rows.Add(new SlotScoreRow { Rank = rank, Slot = slot, Score = sc, Claimed = claimed });
        }
        Selected = prevKey is int k ? Rows.FirstOrDefault(r => r.LoopId == k) : null;
    }

    private bool InBand(double sec) => _lengthBand switch
    {
        "Under 30 s" => sec < 30,
        "30–60 s" => sec >= 30 && sec < 60,
        "60–120 s" => sec >= 60 && sec < 120,
        "Over 120 s" => sec >= 120,
        _ => true,
    };

    private void ResetFilters()
    {
        _minPercent = 0; _gameFilter = "All games"; _lengthBand = LengthBands[0]; _loopOnly = false; _hideClaimed = true; _search = "";
        OnPropertyChanged(nameof(MinPercent)); OnPropertyChanged(nameof(GameFilter)); OnPropertyChanged(nameof(LengthBand));
        OnPropertyChanged(nameof(LoopOnly)); OnPropertyChanged(nameof(HideClaimed)); OnPropertyChanged(nameof(Search));
        ApplyFilters();
    }

    private void Choose()
    {
        if (_song is null || Selected is null) return;
        SlotChosen?.Invoke(this, (_song, Selected.Slot, Selected.Score));
    }

    /// <summary>Double-click / Enter on a row.</summary>
    public void ChooseRow(SlotScoreRow row) { Selected = row; Choose(); }
}
