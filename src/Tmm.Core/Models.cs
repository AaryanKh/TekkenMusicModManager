using System.Text.Json.Serialization;

namespace Tmm.Core;

// Domain models. Plain records, no behaviour beyond derived properties, serializable to JSON.
//
// Naming: a *slot* is one jukebox track in the game (intro WEM + loop WEM). A *song* is the
// user's audio. A *candidate* is a loop the song can naturally produce. A *score* is how well
// one candidate fits one slot. A *mod* is one rendered song installed into one slot.

// ------------------------------------------------------------------ catalog

/// <summary>From the community spreadsheet. Whole-second lengths, IDs, titles. Index only.</summary>
public sealed record SlotIdentity(
    int No,
    string Title,
    string Game,
    int? IntroId,              // null for the loop-only slots
    int LoopId,
    int? IntroSecSheet,
    int LoopSecSheet);

/// <summary>Sample-exact facts read from a stock WEM header. Never from the spreadsheet.</summary>
public sealed record WemInfo(
    int WemId,
    int Frames,                // Vorbis fmt extension, offset 24 (Spike B length audit)
    int SampleRate,
    int Channels,
    int FormatTag,
    long SizeBytes)
{
    [JsonIgnore] public double Seconds => (double)Frames / SampleRate;
}

/// <summary>A fully measured jukebox slot. The unit the fit engine scores against.</summary>
public sealed record Slot(
    SlotIdentity Identity,
    WemInfo? Intro,
    WemInfo Loop,
    double? Lufs = null,
    // False when the frame counts were derived from the spreadsheet's whole seconds instead of a
    // measured stock WEM. Provisional slots may be *ranked* (so the UI shows something before the
    // catalog is built) but never *rendered* — the renderer refuses them.
    bool Measured = true)
{
    [JsonIgnore] public int Key => Identity.LoopId;
    [JsonIgnore] public bool HasIntro => Intro is not null;
    [JsonIgnore] public int IntroFrames => Intro?.Frames ?? 0;
    [JsonIgnore] public int LoopFrames => Loop.Frames;
    [JsonIgnore] public double IntroSeconds => Intro?.Seconds ?? 0.0;
    [JsonIgnore] public double LoopSeconds => Loop.Seconds;
    [JsonIgnore] public string Title => Identity.Title;

    /// <summary>Every WEM ID a mod for this slot overrides. Conflict detection keys on this.</summary>
    [JsonIgnore] public IReadOnlyList<int> WemIds =>
        Intro is null ? new[] { Loop.WemId } : new[] { Intro.WemId, Loop.WemId };

    /// <summary>Slot built from the sheet's whole seconds only. See <see cref="Measured"/>.</summary>
    public static Slot Provisional(SlotIdentity id, int rate = Constants.TargetSampleRate)
    {
        WemInfo? intro = id.IntroId is int iid
            ? new WemInfo(iid, (id.IntroSecSheet ?? 0) * rate, rate, Constants.TargetChannels, Wem.WemConstants.FormatWwiseVorbis, 0)
            : null;
        var loop = new WemInfo(id.LoopId, id.LoopSecSheet * rate, rate, Constants.TargetChannels, Wem.WemConstants.FormatWwiseVorbis, 0);
        return new Slot(id, intro, loop, null, Measured: false);
    }
}

// ------------------------------------------------------------------ song + analysis

public sealed record Song(
    string Path,
    string Title,
    double DurationSec,
    int SampleRate,            // after decode, i.e. TargetSampleRate
    int Channels,
    string Fingerprint);       // content hash; keys the analysis cache

public sealed record BeatGrid(
    double Bpm,
    double[] BeatsSec,
    double[] DownbeatsSec,
    int BeatsPerBar = 4,
    double Confidence = 0.0)   // low -> UI falls back to manual loop editor
{
    [JsonIgnore] public double BarSec => 60.0 / Bpm * BeatsPerBar;
    [JsonIgnore] public double BeatSec => 60.0 / Bpm;

    /// <summary>Nearest downbeat to t. Falls back to t itself when there are none.</summary>
    public double SnapToDownbeat(double t)
    {
        if (DownbeatsSec.Length == 0) return t;
        double best = DownbeatsSec[0];
        foreach (var d in DownbeatsSec)
            if (Math.Abs(d - t) < Math.Abs(best - t)) best = d;
        return best;
    }
}

public sealed record Segment(double StartSec, double EndSec, string Label);

public sealed record SongAnalysis(
    Song Song,
    BeatGrid Grid,
    IReadOnlyList<Segment> Segments,
    double FeatureHopSec);

// ------------------------------------------------------------------ loop candidates + scoring

/// <summary>A loop the song can produce with no stretching. Slot-independent.</summary>
public sealed record LoopCandidate(
    double StartSec,
    int Bars,
    double NaturalSec,         // bars * bar_sec
    double SeamRaw,            // feature distance across the wrap point (lower = better)
    double SeamPct,            // percentile vs same-song random cuts, 0..100 (higher = better)
    bool CrossesSegment);

public sealed record SlotScore(
    int SlotKey,
    LoopCandidate Candidate,
    double Rho,                // required stretch ratio L / natural
    double LoopFit,            // 0..100
    double SeamQuality,        // 0..100
    double IntroFit,           // 0..100
    double Coverage,           // 0..100, fraction of song used
    double Headline)           // weighted geometric mean of the first three
{
    public string Explain()
    {
        var intro = IntroFit >= 99 ? "from real material" : $"{IntroFit:0}% natural";
        return $"{Candidate.Bars} bars · stretched {Math.Abs(Rho - 1) * 100:0.0}% · " +
               $"seam better than {SeamQuality:0}% of cuts · intro {intro} · " +
               $"uses {Coverage:0}% of your track";
    }
}

// ------------------------------------------------------------------ render

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IntroStrategy
{
    /// <summary>Slot has no intro WEM.</summary>
    None,
    /// <summary>Real material from before the loop start.</summary>
    Real,
    /// <summary>Whatever lead-in exists, left-padded with silence and faded in.</summary>
    FadeIn,
    /// <summary>Pure silence.</summary>
    Silence,
    /// <summary>Real material taken from an explicit point in the song, chosen independently of the
    /// loop start. Lets a mod use the song's own opening as the intro while looping a later section.
    /// Reads <see cref="RenderPlan.IntroStartSec"/>.</summary>
    Detached,
}

/// <summary>Everything needed to reproduce a render. Stored in the mod manifest. The editor
/// mutates one of these; the dashboard's Rebuild replays one. There is no second code path.</summary>
public sealed class RenderPlan
{
    public int SlotKey { get; set; }
    public string SongFingerprint { get; set; } = "";
    public double LoopStartSec { get; set; }
    public int LoopBars { get; set; }
    public double Rho { get; set; } = 1.0;
    public IntroStrategy IntroStrategy { get; set; } = IntroStrategy.None;
    public double? IntroTrimSec { get; set; }
    /// <summary>Where the intro's material starts in the song, for <see cref="IntroStrategy.Detached"/>.
    /// Null for every other strategy, which derive the intro from the loop start instead.</summary>
    public double? IntroStartSec { get; set; }
    public double CrossfadeMs { get; set; } = Constants.DefaultSeamCrossfadeMs;
    public double? TargetLufs { get; set; }
    /// <summary>Manual level trim in dB, applied after loudness matching. Positive makes the mod
    /// louder than the rest of the game; a peak limiter keeps the result under the ceiling instead
    /// of letting it clip. 0 leaves the level where the earlier stages put it.</summary>
    public double GainDb { get; set; }
    /// <summary>Set by the trim/preview editor. Presence means "a human touched this plan".</summary>
    public Dictionary<string, string> ManualOverrides { get; set; } = new();

    public RenderPlan Clone() => new()
    {
        SlotKey = SlotKey, SongFingerprint = SongFingerprint, LoopStartSec = LoopStartSec,
        LoopBars = LoopBars, Rho = Rho, IntroStrategy = IntroStrategy, IntroTrimSec = IntroTrimSec,
        IntroStartSec = IntroStartSec, CrossfadeMs = CrossfadeMs, TargetLufs = TargetLufs, GainDb = GainDb,
        ManualOverrides = new Dictionary<string, string>(ManualOverrides),
    };
}

public sealed record RenderResult(
    RenderPlan Plan,
    string? IntroWem,
    string LoopWem,
    int IntroFrames,
    int LoopFrames,
    double SeamMetric,          // discontinuity across the wrap, lower = better
    double PeakDbfs,
    double Lufs,
    double GainDb = 0,          // the manual trim the plan asked for
    double LimiterReductionDb = 0);  // worst gain reduction the limiter had to apply, 0 = untouched

// ------------------------------------------------------------------ mods (dashboard state)

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ModState
{
    Enabled,     // pak present in ~mods
    Disabled,    // pak kept in app store, not in ~mods
    Stale,       // manifest changed since last render; needs rebuild
    Broken,      // pak missing from both places
}

public sealed class ModManifest
{
    public string ModId { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public int SlotKey { get; set; }
    public string SlotTitle { get; set; } = "";
    public string SongPath { get; set; } = "";
    public string SongFingerprint { get; set; } = "";
    public RenderPlan Plan { get; set; } = new();
    public DateTime Created { get; set; } = DateTime.UtcNow;
    public DateTime Updated { get; set; } = DateTime.UtcNow;
    public string PakName { get; set; } = "";           // <name>_P.pak
    public List<int> WemIds { get; set; } = new();      // what this pak overrides; conflict detection keys on this
    /// <summary>Last measured render facts, for the dashboard. Informational only.</summary>
    public double? SeamMetric { get; set; }
    public double? Lufs { get; set; }
}
