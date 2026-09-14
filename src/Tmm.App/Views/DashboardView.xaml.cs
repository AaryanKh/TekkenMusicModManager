using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Tmm.App.ViewModels;

namespace Tmm.App.Views;

public partial class DashboardView : UserControl
{
    public DashboardView()
    {
        InitializeComponent();
        // A ListBox keeps its selection when you click the gaps between items. For tiles that reads
        // as "stuck", so a click on anything that is not a tile clears it. Buttons in the details
        // panel live outside Body, so they are unaffected.
        Body.PreviewMouseLeftButtonDown += OnBodyClick;
        // Give the control focus on entry so Escape reaches the KeyBinding without a click first.
        Loaded += (_, _) => Focus();
    }

    private void OnBodyClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not DashboardViewModel vm || !vm.ShowTiles) return;
        if (IsInside<ListBoxItem>(e.OriginalSource as DependencyObject)) return;
        vm.SelectedRow = null;
        Focus();
    }

    private static bool IsInside<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null)
        {
            if (d is T) return true;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }
}
