using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Tmm.App.Services;

public sealed class WpfDialogService : IDialogService
{
    private static Window? Owner => Application.Current?.MainWindow;

    public void ShowError(string title, string message)
    {
        if (Owner is not null) MessageBox.Show(Owner, message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        else MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    public void ShowInfo(string title, string message)
    {
        if (Owner is not null) MessageBox.Show(Owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        else MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public bool Confirm(string title, string message)
    {
        var r = Owner is not null
            ? MessageBox.Show(Owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question)
            : MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question);
        return r == MessageBoxResult.Yes;
    }

    public string? PickFile(string title, string filter)
    {
        var d = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return (Owner is null ? d.ShowDialog() : d.ShowDialog(Owner)) == true ? d.FileName : null;
    }

    public string? PickFolder(string title)
    {
        // OpenFolderDialog is new in .NET 8 WPF.
        var d = new OpenFolderDialog { Title = title, Multiselect = false };
        return (Owner is null ? d.ShowDialog() : d.ShowDialog(Owner)) == true ? d.FolderName : null;
    }
}

/// <summary>Preview playback through WPF's MediaPlayer (no extra dependencies).</summary>
public sealed class MediaPlayerPreview : IAudioPreview
{
    private readonly MediaPlayer _player = new();
    private bool _playing;

    public MediaPlayerPreview()
    {
        _player.MediaEnded += (_, _) => { _playing = false; PlaybackEnded?.Invoke(this, EventArgs.Empty); };
        _player.MediaFailed += (_, _) => { _playing = false; PlaybackEnded?.Invoke(this, EventArgs.Empty); };
    }

    public bool IsPlaying => _playing;
    public event EventHandler? PlaybackEnded;

    public void Play(string wavPath)
    {
        _player.Open(new Uri(wavPath, UriKind.Absolute));
        _player.Position = TimeSpan.Zero;
        _player.Play();
        _playing = true;
    }

    public void Stop()
    {
        if (!_playing) return;
        _player.Stop();
        _player.Close();
        _playing = false;
        PlaybackEnded?.Invoke(this, EventArgs.Empty);
    }
}
