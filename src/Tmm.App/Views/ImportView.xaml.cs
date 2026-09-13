using System.Windows;
using System.Windows.Controls;
using Tmm.App.ViewModels;

namespace Tmm.App.Views;

public partial class ImportView : UserControl
{
    public ImportView() => InitializeComponent();

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (DataContext is ImportViewModel vm && e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            vm.DropFile(files[0]);
            e.Handled = true;
        }
    }
}
