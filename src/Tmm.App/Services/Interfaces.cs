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
}

public interface IAudioPreview
{
    bool IsPlaying { get; }
    event EventHandler? PlaybackEnded;
    void Play(string wavPath);
    void Stop();
}

/// <summary>Progress tuple every long operation reports.</summary>
public readonly record struct Step(int Done, int Total, string What)
{
    public double Percent => Total <= 0 ? 0 : 100.0 * Done / Total;
}
