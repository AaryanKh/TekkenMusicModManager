using Tmm.App.Mvvm;
using Tmm.App.Services;
using Tmm.Core;
using Tmm.Core.Analysis;

namespace Tmm.App.ViewModels;

/// <summary>Drop zone + file picker. Runs the analyzer off the UI thread and hands the result on.
/// If BeatGrid.Confidence is low, warns and offers to go straight to the manual editor.</summary>
public sealed class ImportViewModel : ObservableObject
{
    private static readonly string[] Extensions = { ".mp3", ".wav", ".flac", ".m4a", ".ogg", ".aac", ".wma", ".opus" };

    private readonly AppServices _app;
    private string? _songPath;
    private bool _isAnalyzing;
    private double _progress;
    private string _status = "Drop a song here, or browse.";
    private AnalyzedSong? _result;
    private CancellationTokenSource? _cts;

    public ImportViewModel(AppServices app)
    {
        _app = app;
        BrowseCommand = new RelayCommand(Browse);
        AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync, () => SongPath is not null && !IsAnalyzing);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsAnalyzing);
        ContinueCommand = new RelayCommand(() => { if (_result is not null) Analyzed?.Invoke(this, _result); }, () => _result is not null);
    }

    public event EventHandler<AnalyzedSong>? Analyzed;

    public RelayCommand BrowseCommand { get; }
    public AsyncRelayCommand AnalyzeCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ContinueCommand { get; }

    public string? SongPath
    {
        get => _songPath;
        set
        {
            if (SetProperty(ref _songPath, value))
            {
                Result = null;
                Status = value is null ? "Drop a song here, or browse." : $"Ready: {Path.GetFileName(value)}";
                OnPropertyChanged(nameof(HasSong));
                AnalyzeCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasSong => SongPath is not null;

    public bool IsAnalyzing
    {
        get => _isAnalyzing;
        private set { if (SetProperty(ref _isAnalyzing, value)) { AnalyzeCommand.RaiseCanExecuteChanged(); CancelCommand.RaiseCanExecuteChanged(); } }
    }

    public double Progress { get => _progress; private set => SetProperty(ref _progress, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    public AnalyzedSong? Result
    {
        get => _result;
        private set
        {
            if (SetProperty(ref _result, value))
            {
                OnPropertyChanged(nameof(HasResult));
                OnPropertyChanged(nameof(Summary));
                OnPropertyChanged(nameof(LowConfidence));
                ContinueCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasResult => _result is not null;
    public bool LowConfidence => _result?.LowConfidence ?? false;

    public string Summary
    {
        get
        {
            if (_result is null) return "";
            var g = _result.Analysis.Grid;
            return $"{_result.Song.DurationSec:0.0} s · {g.Bpm:0.0} BPM · beat confidence {g.Confidence:P0} · {g.DownbeatsSec.Length} bars · {_result.Candidates.Count} loop candidates · {_result.Analysis.Segments.Count} sections";
        }
    }

    public static bool IsSupported(string path) => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Called by the view's drag-and-drop handler.</summary>
    public void DropFile(string path)
    {
        if (!IsSupported(path))
        {
            _app.Dialogs.ShowError("Unsupported file", $"{Path.GetFileName(path)} is not an audio file this app can decode.\nSupported: {string.Join(", ", Extensions)}");
            return;
        }
        SongPath = path;
        if (AnalyzeCommand.CanExecute(null)) AnalyzeCommand.Execute(null);
    }

    private void Browse()
    {
        var f = _app.Dialogs.PickFile("Choose a song", "Audio files|*.mp3;*.wav;*.flac;*.m4a;*.ogg;*.aac;*.wma;*.opus|All files|*.*");
        if (f is not null) DropFile(f);
    }

    private async Task AnalyzeAsync()
    {
        if (SongPath is null) return;
        if (!Decoder.FfmpegAvailable(_app.Settings.FfmpegExe))
        {
            _app.Dialogs.ShowError("ffmpeg not found",
                "Decoding needs ffmpeg. Install it (winget install Gyan.FFmpeg) or point Settings → ffmpeg path at ffmpeg.exe.");
            return;
        }
        IsAnalyzing = true; Progress = 0; Result = null;
        _cts = new CancellationTokenSource();
        var path = SongPath;
        var progress = new Progress<(int done, int total, string what)>(p => { Progress = 100.0 * p.done / Math.Max(1, p.total); Status = p.what; });
        try
        {
            var analyzer = _app.Analyzer();
            var result = await Task.Run(() => analyzer.Analyze(path, progress, _cts.Token), _cts.Token);
            Result = result;
            Status = result.LowConfidence
                ? "Analyzed — beat tracking is unsure about this track. The manual editor is the safer path."
                : "Analyzed. Pick a slot from the ranking.";
            Analyzed?.Invoke(this, result);
        }
        catch (OperationCanceledException) { Status = "Cancelled."; }
        catch (TmmException e) { Status = "Analysis failed."; _app.Dialogs.ShowError("Analysis failed", e.Message); }
        finally { IsAnalyzing = false; _cts = null; }
    }
}
