using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using Tmm.App.Services;
using Tmm.App.ViewModels;
using Tmm.Core;
using Tmm.Core.Audio;

namespace Tmm.App;

public partial class App : Application
{
    public AppServices Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Numbers in plans/manifests are invariant; the UI formats for display.
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        DispatcherUnhandledException += OnUnhandled;

        string? appDir = null;
        for (int i = 0; i + 1 < e.Args.Length; i++)
            if (e.Args[i] == "--app-dir") appDir = e.Args[i + 1];

        Services = new AppServices(new WpfDialogService(), new MediaPlayerPreview(), appDir);
        // Previews from a session that crashed or was killed never got their exit cleanup.
        PreviewCache.Clear(Services.Settings.CacheDir);
        var vm = new MainViewModel(Services);
        var window = new MainWindow { DataContext = vm };
        MainWindow = window;
        window.Show();

        // A song passed on the command line (or dropped on the exe) goes straight to Import.
        var song = e.Args.FirstOrDefault(a => File.Exists(a) && ImportViewModel.IsSupported(a));
        if (song is not null) vm.DropFile(song);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Stop first: the player keeps the file it is playing open, and an open file cannot be deleted.
        Services?.Preview.Stop();
        if (Services is not null) PreviewCache.Clear(Services.Settings.CacheDir);
        base.OnExit(e);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var msg = e.Exception is TmmException t ? t.Message : e.Exception.ToString();
        MessageBox.Show(msg, "Unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
