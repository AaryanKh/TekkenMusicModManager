using System.Windows;
using Tmm.App.ViewModels;

namespace Tmm.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is MainViewModel vm)
                vm.OpenFolderRequested += (_, path) =>
                {
                    if (Directory.Exists(path))
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
                    else
                        MessageBox.Show(this, $"Folder does not exist yet:\n{path}\n\nIt is created the first time a mod is enabled.", "~mods", MessageBoxButton.OK, MessageBoxImage.Information);
                };
        };
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            vm.DropFile(files[0]);
    }
}
