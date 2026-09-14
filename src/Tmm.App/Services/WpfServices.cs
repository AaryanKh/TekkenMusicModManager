using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
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
    private readonly DispatcherTimer _ticker;
    private bool _playing;

    public MediaPlayerPreview()
    {
        // Open() is asynchronous. Starting playback here, rather than straight after the Open call,
        // is what guarantees the file we just asked for is the one that plays — calling Play() too
        // early resumes whatever media was still loaded.
        _player.MediaOpened += (_, _) =>
        {
            if (!_playing) return;
            _player.Position = TimeSpan.Zero;
            _player.Play();
            _ticker.Start();
        };
        _player.MediaEnded += (_, _) => Finish();
        _player.MediaFailed += (_, _) => Finish();

        _ticker = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Render,
                                      (_, _) => PositionChanged?.Invoke(this, EventArgs.Empty),
                                      Dispatcher.CurrentDispatcher);
        _ticker.Stop();
    }

    public bool IsPlaying => _playing;
    public double PositionSec => _player.Position.TotalSeconds;
    public double DurationSec => _player.NaturalDuration.HasTimeSpan ? _player.NaturalDuration.TimeSpan.TotalSeconds : 0;

    public event EventHandler? PlaybackEnded;
    public event EventHandler? PositionChanged;

    public void Play(string wavPath)
    {
        // Close the previous media before opening the next. Without this the old file stays locked
        // (so its temp copy cannot be deleted) and Open races with it.
        _player.Stop();
        _player.Close();
        _playing = true;
        _player.Open(new Uri(wavPath, UriKind.Absolute));
    }

    public void Seek(double sec)
    {
        if (!_playing) return;
        double max = DurationSec;
        _player.Position = TimeSpan.FromSeconds(max > 0 ? Math.Clamp(sec, 0, max) : Math.Max(0, sec));
        PositionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        bool was = _playing;
        _playing = false;
        _ticker.Stop();
        _player.Stop();
        _player.Close();          // always release the file, even if we thought we were idle
        if (was) PlaybackEnded?.Invoke(this, EventArgs.Empty);
    }

    private void Finish()
    {
        _playing = false;
        _ticker.Stop();
        PositionChanged?.Invoke(this, EventArgs.Empty);
        PlaybackEnded?.Invoke(this, EventArgs.Empty);
    }
}
