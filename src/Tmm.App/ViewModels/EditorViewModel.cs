using Tmm.App.Mvvm;
using Tmm.App.Services;
using Tmm.Core;
using Tmm.Core.Analysis;
using Tmm.Core.Audio;
using Tmm.Core.LoopFit;
using Tmm.Core.Render;

namespace Tmm.App.ViewModels;

/// <summary>
/// Waveform with the proposed intro/loop regions overlaid. The user can drag the loop start, change
/// the bar count, pick a different intro strategy, toggle crossfade. Every change edits the
/// RenderPlan; Preview renders it in a worker and plays the loop 3x so seams are audible, with the
/// seam metric shown as a number. Build -&gt; ModBuilder. There is no second code path: manual edits
/// produce a plan with ManualOverrides set and go through the same pipeline.
/// </summary>
public sealed class EditorViewModel : ObservableObject
{
    public const int WaveformColumns = 1600;

    private readonly AppServices _app;
    private AnalyzedSong? _song;
    private Slot? _slot;
    private SlotScore? _recommended;
    private ModManifest? _existing;
    private RenderPlan _plan = new();
    private bool _userPickedIntro;
    private string _modName = "";
    private bool _isBusy;
    private string _busyText = "";
    private string _previewInfo = "";
    private string _previewAdvice = "";
    private string? _previewPath;
    private float[] _peaks = Array.Empty<float>();
    private float[] _trackPeaks = Array.Empty<float>();
    private IReadOnlyList<TrackSection> _trackSections = Array.Empty<TrackSection>();
    private double _trackDurationSec;

    public EditorViewModel(AppServices app)
    {
        _app = app;
        BarsUpCommand = new RelayCommand(() => LoopBars += 1, () => HasSong);
        BarsDownCommand = new RelayCommand(() => LoopBars -= 1, () => HasSong && LoopBars > 1);
        SnapCommand = new RelayCommand(() => { if (_song is not null) LoopStartSec = _song.Analysis.Grid.SnapToDownbeat(LoopStartSec); }, () => HasSong);
        ResetCommand = new RelayCommand(ResetToRecommendation, () => _recommended is not null);
        PreviewCommand = new AsyncRelayCommand(PreviewAsync, () => HasSong && !IsBusy);
        PreviewTrackCommand = new AsyncRelayCommand(PreviewTrackAsync, () => HasSong && !IsBusy);
        IntroToSongStartCommand = new RelayCommand(() => IntroSourceStartSec = 0, () => DetachIntro);
        StopCommand = new RelayCommand(() => _app.Preview.Stop());
        BuildCommand = new AsyncRelayCommand(BuildAsync, () => HasSong && !IsBusy && CanBuild);
        SavePlanCommand = new RelayCommand(SavePlan, () => _existing is not null && !IsBusy);
        BackCommand = new RelayCommand(() => BackRequested?.Invoke(this, EventArgs.Empty));
        _app.Preview.PlaybackEnded += (_, _) => OnPropertyChanged(nameof(IsPlaying));
    }

    public event EventHandler<ModManifest>? Built;
    public event EventHandler<ModManifest>? PlanSaved;
    public event EventHandler? BackRequested;

    public RelayCommand BarsUpCommand { get; }
    public RelayCommand BarsDownCommand { get; }
    public RelayCommand SnapCommand { get; }
    public RelayCommand ResetCommand { get; }
    public AsyncRelayCommand PreviewCommand { get; }
    public AsyncRelayCommand PreviewTrackCommand { get; }
    public RelayCommand IntroToSongStartCommand { get; }
    public RelayCommand StopCommand { get; }
    public AsyncRelayCommand BuildCommand { get; }
    public RelayCommand SavePlanCommand { get; }
    public RelayCommand BackCommand { get; }

    public IReadOnlyList<IntroStrategy> IntroOptions { get; private set; } = Array.Empty<IntroStrategy>();

    // ------------------------------------------------------------------ loading

    public void Load(AnalyzedSong song, Slot slot, SlotScore? recommended)
    {
        _app.Preview.Stop();
        _song = song; _slot = slot; _recommended = recommended; _existing = null; _userPickedIntro = false;
        _peaks = ComputePeaks(song.Pcm, WaveformColumns);
        _modName = SuggestName(song.Song.Title);
        IntroOptions = slot.HasIntro
            ? new[] { IntroStrategy.Real, IntroStrategy.Detached, IntroStrategy.FadeIn, IntroStrategy.Silence }
            : new[] { IntroStrategy.None };
        if (recommended is not null)
            _plan = PlanFactory.FromScore(slot, recommended, song.Analysis, _app.Settings.TargetLufs);
        else
        {
            var grid = song.Analysis.Grid;
            double start = grid.DownbeatsSec.FirstOrDefault();
            int bars = Math.Max(1, (int)Math.Round(slot.LoopSeconds / grid.BarSec));
            var (strategy, trim) = PlanFactory.ChooseIntro(slot, start);
            _plan = new RenderPlan
            {
                SlotKey = slot.Key, SongFingerprint = song.Song.Fingerprint, LoopStartSec = start, LoopBars = bars,
                Rho = PlanFactory.RhoFor(slot, bars, grid.BarSec), IntroStrategy = strategy, IntroTrimSec = trim,
                TargetLufs = _app.Settings.TargetLufs,
            };
        }
        PreviewInfo = ""; PreviewAdvice = "";
        ClearTrackPreview();
        RaiseAll();
    }

    /// <summary>Re-open a built mod: same song, same slot, the manifest's plan.</summary>
    public void LoadExisting(AnalyzedSong song, Slot slot, ModManifest manifest)
    {
        Load(song, slot, null);
        _existing = manifest;
        _plan = manifest.Plan.Clone();
        _userPickedIntro = true;
        _modName = manifest.Name;
        RaiseAll();
    }

    private void RaiseAll()
    {
        OnPropertyChanged(null);   // everything
        BarsUpCommand.RaiseCanExecuteChanged(); BarsDownCommand.RaiseCanExecuteChanged(); SnapCommand.RaiseCanExecuteChanged();
        ResetCommand.RaiseCanExecuteChanged(); PreviewCommand.RaiseCanExecuteChanged(); BuildCommand.RaiseCanExecuteChanged();
        SavePlanCommand.RaiseCanExecuteChanged(); PreviewTrackCommand.RaiseCanExecuteChanged();
        IntroToSongStartCommand.RaiseCanExecuteChanged();
    }

    // ------------------------------------------------------------------ read-only context

    public bool HasSong => _song is not null && _slot is not null;
    public bool IsExisting => _existing is not null;
    public string SongTitle => _song?.Song.Title ?? "";
    public string SlotTitle => _slot?.Title ?? "";
    public string SlotSummary => _slot is null ? "" :
        (_slot.HasIntro ? $"intro {_slot.IntroSeconds:0.00} s + loop {_slot.LoopSeconds:0.00} s ({_slot.LoopFrames} frames)" : $"loop {_slot.LoopSeconds:0.00} s ({_slot.LoopFrames} frames), no intro")
        + (_slot.Measured ? "" : " — PROVISIONAL, build the catalog first");
    public bool SlotHasIntro => _slot?.HasIntro ?? false;
    public double DurationSec => _song?.Song.DurationSec ?? 0;
    public double BarSec => _song?.Analysis.Grid.BarSec ?? 1;
    public double[] Downbeats => _song?.Analysis.Grid.DownbeatsSec ?? Array.Empty<double>();
    public float[] Peaks => _peaks;
    public RenderPlan Plan => _plan;
    public bool IsPlaying => _app.Preview.IsPlaying;
    public string StretcherName => _app.Stretcher().Name;

    // ------------------------------------------------------------------ editable plan

    public double LoopStartSec
    {
        get => _plan.LoopStartSec;
        set
        {
            double v = Math.Clamp(value, 0, Math.Max(0, DurationSec - 0.1));
            if (Math.Abs(v - _plan.LoopStartSec) < 1e-6) return;
            _plan.LoopStartSec = v;
            _plan.ManualOverrides["loop_start"] = v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            if (!_userPickedIntro && _slot is not null)
            {
                var (s, t) = PlanFactory.ChooseIntro(_slot, v);
                _plan.IntroStrategy = s; _plan.IntroTrimSec = t;
            }
            PlanChanged();
        }
    }

    public int LoopBars
    {
        get => _plan.LoopBars;
        set
        {
            int v = Math.Max(1, value);
            if (v == _plan.LoopBars) return;
            _plan.LoopBars = v;
            _plan.ManualOverrides["loop_bars"] = v.ToString();
            PlanChanged();
        }
    }

    public IntroStrategy IntroStrategy
    {
        get => _plan.IntroStrategy;
        set
        {
            if (value == _plan.IntroStrategy) return;
            // Detaching needs a source position. Default to the song's own opening, which is the
            // reason to detach in the first place.
            if (value == IntroStrategy.Detached) _plan.IntroStartSec ??= 0;
            _plan.IntroStrategy = value; _userPickedIntro = true;
            _plan.ManualOverrides["intro"] = value.ToString();
            PlanChanged();
        }
    }

    /// <summary>Convenience toggle for the checkbox; the underlying state is the intro strategy.</summary>
    public bool DetachIntro
    {
        get => _plan.IntroStrategy == IntroStrategy.Detached;
        set
        {
            if (value == DetachIntro || _slot is null || !_slot.HasIntro) return;
            if (value) IntroStrategy = IntroStrategy.Detached;
            else
            {
                var (s, t) = PlanFactory.ChooseIntro(_slot, _plan.LoopStartSec);
                _plan.IntroTrimSec = t;
                IntroStrategy = s;
            }
        }
    }

    /// <summary>Where the detached intro is cut from. Only meaningful while <see cref="DetachIntro"/>.</summary>
    public double IntroSourceStartSec
    {
        get => _plan.IntroStartSec ?? 0;
        set
        {
            double max = Math.Max(0, DurationSec - IntroLengthSec);
            double v = Math.Clamp(value, 0, max);
            if (_plan.IntroStartSec is double cur && Math.Abs(cur - v) < 1e-6) return;
            _plan.IntroStartSec = v;
            _plan.ManualOverrides["intro_start"] = v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            PlanChanged();
        }
    }

    /// <summary>Manual level trim in dB on top of loudness matching. This is the knob for "this mod
    /// is quieter than that one".</summary>
    public double GainDb
    {
        get => _plan.GainDb;
        set
        {
            double v = Math.Clamp(Math.Round(value, 1), -24, 24);
            if (Math.Abs(v - _plan.GainDb) < 1e-9) return;
            _plan.GainDb = v;
            _plan.ManualOverrides["gain_db"] = v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
            PlanChanged();
        }
    }

    public string GainDbText => _plan.GainDb == 0 ? "0.0 dB" : $"{_plan.GainDb:+0.0;-0.0} dB";

    public double CrossfadeMs
    {
        get => _plan.CrossfadeMs;
        set { double v = Math.Clamp(value, 0, 200); if (Math.Abs(v - _plan.CrossfadeMs) < 1e-9) return; _plan.CrossfadeMs = v; _plan.ManualOverrides["crossfade_ms"] = v.ToString("0"); PlanChanged(); }
    }

    public bool UseLoudness
    {
        get => _plan.TargetLufs is not null;
        set { if (value == UseLoudness) return; _plan.TargetLufs = value ? (_app.Settings.TargetLufs ?? -16.0) : null; PlanChanged(); }
    }

    public double TargetLufs
    {
        get => _plan.TargetLufs ?? (_app.Settings.TargetLufs ?? -16.0);
        set { double v = Math.Clamp(value, -30, -6); if (_plan.TargetLufs is double cur && Math.Abs(cur - v) < 1e-9) return; if (_plan.TargetLufs is not null) { _plan.TargetLufs = v; PlanChanged(); } }
    }

    public string ModName
    {
        get => _modName;
        set { if (SetProperty(ref _modName, value)) { OnPropertyChanged(nameof(CanBuild)); OnPropertyChanged(nameof(Validation)); BuildCommand.RaiseCanExecuteChanged(); } }
    }

    private void PlanChanged()
    {
        if (_slot is not null && _song is not null)
            _plan.Rho = PlanFactory.RhoFor(_slot, _plan.LoopBars, _song.Analysis.Grid.BarSec);
        PreviewInfo = ""; PreviewAdvice = "";
        ClearTrackPreview();           // the assembled track no longer matches the plan
        OnPropertyChanged(null);
        BarsDownCommand.RaiseCanExecuteChanged(); BuildCommand.RaiseCanExecuteChanged();
        IntroToSongStartCommand.RaiseCanExecuteChanged();
    }

    // ------------------------------------------------------------------ derived

    public double NaturalSec => _plan.LoopBars * BarSec;
    public double LoopEndSec => _plan.LoopStartSec + NaturalSec;
    public double IntroLengthSec => _slot?.IntroSeconds ?? 0;

    /// <summary>Start of the amber intro region drawn on the waveform. For a detached intro that is
    /// wherever the user put it; otherwise it is the material immediately before the loop.</summary>
    public double IntroStartSec => _slot is null || !_slot.HasIntro ? _plan.LoopStartSec
        : DetachIntro ? IntroSourceStartSec
        : _plan.LoopStartSec - _slot.IntroSeconds;

    public double IntroEndSec => _slot is null || !_slot.HasIntro ? _plan.LoopStartSec
        : DetachIntro ? IntroSourceStartSec + _slot.IntroSeconds
        : _plan.LoopStartSec;

    public bool DetachedIntroRunsPastEnd => DetachIntro && IntroEndSec > DurationSec + 1e-6;
    public double Rho => _plan.Rho;
    public double StretchPercent => Math.Abs(_plan.Rho - 1) * 100;
    public bool StretchOverCap => StretchPercent / 100 > _app.Settings.StretchCap;
    public bool LoopRunsPastEnd => LoopEndSec > DurationSec + 1e-6;
    public bool IntroNeedsFabrication => _slot is not null && _slot.HasIntro && _plan.IntroStrategy == IntroStrategy.Real && IntroStartSec < 0;

    public SlotScore? CurrentScore
    {
        get
        {
            if (_song is null || _slot is null || LoopRunsPastEnd) return null;
            var c = CandidateForPlan();
            var w = Weights.FromSettings(_app.Settings);
            return Scoring.ScoreCandidate(_slot, c, _song.Song.DurationSec, w);
        }
    }

    public string ScoreLine => CurrentScore is { } s ? $"{s.Headline:0}% · {s.Explain()}" : "";
    public double ScoreHeadline => CurrentScore?.Headline ?? 0;
    public double ScoreLoopFit => CurrentScore?.LoopFit ?? 0;
    public double ScoreSeam => CurrentScore?.SeamQuality ?? 0;
    public double ScoreIntro => CurrentScore?.IntroFit ?? 0;
    public double ScoreCoverage => CurrentScore?.Coverage ?? 0;

    public string Validation
    {
        get
        {
            if (_slot is null || _song is null) return "";
            if (!_slot.Measured) return "This slot's length is provisional. Build the catalog (Settings) before building a mod.";
            if (LoopRunsPastEnd) return $"The loop ends at {LoopEndSec:0.0} s but the song is {DurationSec:0.0} s long. Fewer bars or an earlier start.";
            if (IntroNeedsFabrication) return $"'Real' intro needs {_slot.IntroSeconds:0.0} s of material before the loop start; only {_plan.LoopStartSec:0.0} s exists. Switch to Detached, Fade in, or move the loop start later.";
            if (DetachedIntroRunsPastEnd) return $"The detached intro ends at {IntroEndSec:0.0} s but the song is {DurationSec:0.0} s long. Move the intro start earlier.";
            if (StretchOverCap) return $"This plan stretches the song by {StretchPercent:0.0}%, beyond the {_app.Settings.StretchCap:P0} cap. It will build, but it will sound stretched.";
            if (string.IsNullOrWhiteSpace(_modName)) return "Give the mod a name.";
            return "";
        }
    }

    public bool CanBuild => _slot is not null && _slot.Measured && !LoopRunsPastEnd && !IntroNeedsFabrication
                            && !DetachedIntroRunsPastEnd && !string.IsNullOrWhiteSpace(_modName);
    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) { PreviewCommand.RaiseCanExecuteChanged(); PreviewTrackCommand.RaiseCanExecuteChanged(); BuildCommand.RaiseCanExecuteChanged(); SavePlanCommand.RaiseCanExecuteChanged(); } } }
    public string BusyText { get => _busyText; private set => SetProperty(ref _busyText, value); }
    public string PreviewInfo { get => _previewInfo; private set => SetProperty(ref _previewInfo, value); }
    /// <summary>Shown after a preview when the limiter had to work hard, which is the usual reason a
    /// volume trim does not make a track as loud as the slider suggests.</summary>
    public string PreviewAdvice { get => _previewAdvice; private set => SetProperty(ref _previewAdvice, value); }

    /// <summary>Reads the render back to the user: how much of the trim actually survived.</summary>
    private static string AdviceFor(double gainDb, double reductionDb)
    {
        if (reductionDb <= 3) return "";
        if (gainDb > 0)
            return $"The limiter is pulling {reductionDb:0.0} dB back off this render, so most of the +{gainDb:0.0} dB trim is not reaching the output. " +
                   "This source is already mastered close to full scale; a smaller trim will sound nearly as loud and stay cleaner.";
        return $"This render peaks well above the ceiling on its own, so the limiter is taking {reductionDb:0.0} dB off it. " +
               "That is normal for a loud master and is what keeps the WEM from clipping.";
    }

    private LoopCandidate CandidateForPlan()
    {
        var song = _song!;
        double start = _plan.LoopStartSec, natural = NaturalSec, end = start + natural;
        // Exact match in the precomputed candidates (start on a downbeat, same bars)?
        var match = song.Candidates.FirstOrDefault(c => c.Bars == _plan.LoopBars && Math.Abs(c.StartSec - start) < 0.002);
        if (match is not null) return match;
        double raw = FeatureExtractor.SeamDistance(song.Features, start, end);
        // Percentile against the song's own candidate seams (same spirit as the random-cut baseline).
        int worse = song.Candidates.Count(c => c.SeamRaw > raw);
        double pct = song.Candidates.Count == 0 ? 50 : 100.0 * worse / song.Candidates.Count;
        bool crosses = song.Analysis.Segments.Any(s => (start < s.StartSec && s.StartSec < end) || (start < s.EndSec && s.EndSec < end));
        return new LoopCandidate(start, _plan.LoopBars, natural, raw, pct, crosses);
    }

    private void ResetToRecommendation()
    {
        if (_song is null || _slot is null || _recommended is null) return;
        _plan = PlanFactory.FromScore(_slot, _recommended, _song.Analysis, _app.Settings.TargetLufs);
        _userPickedIntro = false;
        PlanChanged();
    }

    // ------------------------------------------------------------------ preview / build

    private async Task PreviewAsync()
    {
        if (_song is null || _slot is null) return;
        if (!_slot.Measured) { _app.Dialogs.ShowError("Catalog not built", Validation); return; }
        if (LoopRunsPastEnd || IntroNeedsFabrication || DetachedIntroRunsPastEnd) { _app.Dialogs.ShowError("Cannot preview", Validation); return; }
        _app.Preview.Stop();
        IsBusy = true; BusyText = "Rendering preview…";
        try
        {
            var plan = _plan.Clone(); var pcm = _song.Pcm; var slot = _slot; var stretcher = _app.Stretcher();
            var cache = _app.Settings.CacheDir;
            var (path, info, advice) = await Task.Run(() =>
            {
                var b = RenderPipeline.RenderBuffers(pcm, slot, plan, stretcher);
                var triple = Verify.PreviewTriple(b.Loop);
                var full = b.Intro is null ? triple : PcmBuffer.Concat(b.Intro, triple);
                Directory.CreateDirectory(cache);
                var p = Path.Combine(cache, $"preview_{Guid.NewGuid():N}.wav");
                WavIo.WritePcm16(p, full);
                var limit = b.LimiterReductionDb > 0.05 ? $" · limiter −{b.LimiterReductionDb:0.0} dB" : "";
                var trim = Math.Abs(b.GainDb) > 1e-9 ? $" · trim {b.GainDb:+0.0;-0.0} dB" : "";
                return (p, $"seam {b.SeamMetric:0.000} (lower is better) · {b.Lufs:0.0} LUFS · peak {b.PeakDbfs:0.0} dBFS{trim}{limit} · stretch via {stretcher.Name}",
                        AdviceFor(b.GainDb, b.LimiterReductionDb));
            });
            PreviewInfo = info;
            PreviewAdvice = advice;
            PlayPreviewFile(path);
        }
        catch (TmmException e) { _app.Dialogs.ShowError("Preview failed", e.Message); }
        finally { IsBusy = false; BusyText = ""; }
    }

    // ------------------------------------------------------------------ full-track preview

    /// <summary>min/max peaks of the assembled in-game track; empty until Preview full track runs.</summary>
    public float[] TrackPeaks => _trackPeaks;
    public double TrackDurationSec => _trackDurationSec;
    public IReadOnlyList<TrackSection> TrackSections => _trackSections;
    public bool HasTrackPreview => _trackPeaks.Length > 0;
    /// <summary>Section boundaries in seconds, for the dividers drawn over the strip.</summary>
    public double[] TrackBoundaries => _trackSections.Skip(1).Select(s => s.StartSec).ToArray();
    public string TrackSummary
    {
        get
        {
            if (!HasTrackPreview) return "";
            const string intro = "Intro";
            var introSection = _trackSections.FirstOrDefault(s => s.Label == intro);
            int repeats = _trackSections.Count - (introSection is null ? 0 : 1);
            string lead = introSection is null
                ? ", no intro slot"
                : $" after a {introSection.Seconds:0.0} s intro";
            return $"{repeats} loop repeats{lead} · {_trackDurationSec:0} s assembled exactly as the game plays it";
        }
    }

    private void ClearTrackPreview()
    {
        if (_trackPeaks.Length == 0 && _trackSections.Count == 0) return;
        _trackPeaks = Array.Empty<float>();
        _trackSections = Array.Empty<TrackSection>();
        _trackDurationSec = 0;
        OnPropertyChanged(nameof(TrackPeaks)); OnPropertyChanged(nameof(TrackDurationSec));
        OnPropertyChanged(nameof(TrackSections)); OnPropertyChanged(nameof(TrackBoundaries));
        OnPropertyChanged(nameof(HasTrackPreview)); OnPropertyChanged(nameof(TrackSummary));
    }

    /// <summary>
    /// Render the plan, then assemble intro + N loops the way the game does and play the whole
    /// thing. The loop-only preview cannot show the intro-to-loop handover; this one can.
    /// </summary>
    private async Task PreviewTrackAsync()
    {
        if (_song is null || _slot is null) return;
        if (!_slot.Measured) { _app.Dialogs.ShowError("Catalog not built", Validation); return; }
        if (LoopRunsPastEnd || IntroNeedsFabrication || DetachedIntroRunsPastEnd) { _app.Dialogs.ShowError("Cannot preview", Validation); return; }
        _app.Preview.Stop();
        IsBusy = true; BusyText = "Assembling the full track…";
        try
        {
            var plan = _plan.Clone(); var pcm = _song.Pcm; var slot = _slot; var stretcher = _app.Stretcher();
            var cache = _app.Settings.CacheDir;
            var (path, peaks, sections, seconds, info, advice) = await Task.Run(() =>
            {
                var b = RenderPipeline.RenderBuffers(pcm, slot, plan, stretcher);
                int repeats = TrackPreview.RepeatsFor(b.Intro?.Seconds ?? 0, b.Loop.Seconds);
                var (audio, secs) = TrackPreview.Assemble(b.Intro, b.Loop, slot.Loop.SampleRate, repeats);
                Directory.CreateDirectory(cache);
                var p = Path.Combine(cache, $"track_{Guid.NewGuid():N}.wav");
                WavIo.WritePcm16(p, audio);
                var limit = b.LimiterReductionDb > 0.05 ? $" · limiter −{b.LimiterReductionDb:0.0} dB" : "";
                var trim = Math.Abs(b.GainDb) > 1e-9 ? $" · trim {b.GainDb:+0.0;-0.0} dB" : "";
                return (p, ComputePeaks(audio, WaveformColumns), secs, audio.Seconds,
                        $"{b.Lufs:0.0} LUFS · peak {b.PeakDbfs:0.0} dBFS{trim}{limit} · seam {b.SeamMetric:0.000}",
                        AdviceFor(b.GainDb, b.LimiterReductionDb));
            });
            _trackPeaks = peaks; _trackSections = sections; _trackDurationSec = seconds;
            OnPropertyChanged(nameof(TrackPeaks)); OnPropertyChanged(nameof(TrackDurationSec));
            OnPropertyChanged(nameof(TrackSections)); OnPropertyChanged(nameof(TrackBoundaries));
            OnPropertyChanged(nameof(HasTrackPreview)); OnPropertyChanged(nameof(TrackSummary));
            PreviewInfo = info;
            PreviewAdvice = advice;
            PlayPreviewFile(path);
        }
        catch (TmmException e) { _app.Dialogs.ShowError("Track preview failed", e.Message); }
        finally { IsBusy = false; BusyText = ""; }
    }

    private void PlayPreviewFile(string path)
    {
        if (_previewPath is not null) { try { File.Delete(_previewPath); } catch { } }
        _previewPath = path;
        _app.Preview.Play(path);
        OnPropertyChanged(nameof(IsPlaying));
    }

    private async Task BuildAsync()
    {
        if (_song is null || _slot is null || !CanBuild) return;
        _app.Preview.Stop();
        IsBusy = true; BusyText = "Building…";
        var progress = new Progress<(int done, int total, string what)>(p => BusyText = p.what);
        try
        {
            var name = _modName; var songPath = _song.Song.Path; var pcm = _song.Pcm; var slot = _slot; var plan = _plan.Clone();
            var builder = _app.Builder();
            ModManifest m;
            if (_existing is not null)
            {
                var existing = _existing;
                m = await Task.Run(() =>
                {
                    _app.Registry.UpdatePlan(existing, plan);
                    return builder.Rebuild(existing, slot, _app.Settings.FfmpegExe, progress);
                });
            }
            else
                m = await Task.Run(() => builder.Build(name, songPath, pcm, slot, plan, progress));
            Built?.Invoke(this, m);
        }
        catch (TmmException e) { _app.Dialogs.ShowError("Build failed", e.Message); }
        finally { IsBusy = false; BusyText = ""; }
    }

    private void SavePlan()
    {
        if (_existing is null) return;
        _app.Registry.UpdatePlan(_existing, _plan);
        PlanSaved?.Invoke(this, _existing);
    }

    // ------------------------------------------------------------------ helpers

    private static string SuggestName(string title)
    {
        var s = Core.Pak.PakLayout.SanitizeModName(title);
        return s.Length > 40 ? s[..40] : s;
    }

    /// <summary>min/max pairs per column, mono. Cheap to draw, computed once per song.</summary>
    public static float[] ComputePeaks(PcmBuffer pcm, int columns)
    {
        var peaks = new float[columns * 2];
        int frames = pcm.Frames; if (frames == 0) return peaks;
        for (int c = 0; c < columns; c++)
        {
            int a = (int)((long)c * frames / columns), b = (int)((long)(c + 1) * frames / columns);
            if (b <= a) b = a + 1;
            float mn = 1, mx = -1;
            for (int i = a; i < b && i < frames; i++)
            {
                float v = 0;
                for (int ch = 0; ch < pcm.Channels; ch++) v += pcm.Data[i * pcm.Channels + ch];
                v /= pcm.Channels;
                if (v < mn) mn = v; if (v > mx) mx = v;
            }
            peaks[c * 2] = mn; peaks[c * 2 + 1] = mx;
        }
        return peaks;
    }
}
