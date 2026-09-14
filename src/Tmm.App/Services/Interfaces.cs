namespace Tmm.App.Services;

/// <summary>Everything a view-model needs from the window system, behind an interface so the
/// view-models compile and test without WPF.</summary>
public interface IDialogService
{
    void ShowError(string title, string message);
    void ShowInfo(string title, string message);
    bool Confirm(string title, string message);
    string? PickFile(string title, string filter);
    string? PickFolder(string title);
    /// <summary>Show candidate pictures and return the one chosen, or null if dismissed.</summary>
    Tmm.Core.Analysis.ArtCandidate? PickArt(string title, IReadOnlyList<Tmm.Core.Analysis.ArtCandidate> candidates);
}

public interface IAudioPreview
{
    bool IsPlaying { get; }
    /// <summary>How far into the current file playback has reached, in seconds.</summary>
    double PositionSec { get; }
    /// <summary>Length of the file being played, or 0 when nothing is loaded.</summary>
    double DurationSec { get; }
    event EventHandler? PlaybackEnded;
    /// <summary>Raised while playing so a playhead can follow along. Fires on the UI thread.</summary>
    event EventHandler? PositionChanged;
    void Play(string wavPath);
    /// <summary>Jump to a point in the current file. Ignored when nothing is playing.</summary>
    void Seek(double sec);
    void Stop();
}

/// <summary>Progress tuple every long operation reports.</summary>
public readonly record struct Step(int Done, int Total, string What)
{
    public double Percent => Total <= 0 ? 0 : 100.0 * Done / Total;
}
