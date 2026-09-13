using Tmm.Core.Audio;
using Tmm.Core.LoopFit;

namespace Tmm.Core.Analysis;

/// <summary>Everything the UI needs after "Analyze": the decoded audio, the analysis and the
/// slot-independent candidates. Candidates are computed once here; ranking is a sorted lookup.</summary>
public sealed record AnalyzedSong(Song Song, PcmBuffer Pcm, SongAnalysis Analysis, Features Features,
                                  IReadOnlyList<LoopCandidate> Candidates)
{
    public bool LowConfidence => Analysis.Grid.Confidence < 0.5;
}

public interface ISongAnalyzer
{
    AnalyzedSong Analyze(string path, IProgress<(int done, int total, string what)>? progress = null, CancellationToken ct = default);
}

/// <summary>One call: path -> AnalyzedSong. Baseline DSP; see <see cref="AutocorrelationBeatTracker"/>.</summary>
public sealed class SongAnalyzer : ISongAnalyzer
{
    private readonly string _ffmpeg;
    private readonly IBeatTracker _beats;

    public SongAnalyzer(string ffmpeg = "ffmpeg", IBeatTracker? beats = null)
    {
        _ffmpeg = ffmpeg;
        _beats = beats ?? new AutocorrelationBeatTracker();
    }

    public AnalyzedSong Analyze(string path, IProgress<(int, int, string)>? progress = null, CancellationToken ct = default)
    {
        progress?.Report((0, 4, "Decoding"));
        var (song, pcm) = Decoder.Decode(path, _ffmpeg, ct: ct);
        ct.ThrowIfCancellationRequested();
        if (song.DurationSec < 5)
            throw new AnalysisException($"{song.Title} is only {song.DurationSec:0.0}s long; nothing to loop.");

        progress?.Report((1, 4, "Extracting features"));
        var mono = pcm.ToMono();
        var feats = FeatureExtractor.Extract(mono, song.SampleRate);
        ct.ThrowIfCancellationRequested();

        progress?.Report((2, 4, "Tracking beats"));
        var grid = _beats.Track(feats, song.DurationSec);
        var segs = Structure.Segment(feats, grid, song.DurationSec);
        var analysis = new SongAnalysis(song, grid, segs, feats.HopSec);
        ct.ThrowIfCancellationRequested();

        progress?.Report((3, 4, "Generating loop candidates"));
        var cands = Candidates.Generate(analysis, feats);
        progress?.Report((4, 4, "Done"));
        return new AnalyzedSong(song, pcm, analysis, feats, cands);
    }
}
