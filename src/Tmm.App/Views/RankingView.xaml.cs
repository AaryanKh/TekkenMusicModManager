using System.Windows.Controls;
using System.Windows.Input;
using Tmm.App.ViewModels;

namespace Tmm.App.Views;

public partial class RankingView : UserControl
{
    public RankingView() => InitializeComponent();

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is RankingViewModel vm && sender is DataGrid g && g.SelectedItem is SlotScoreRow row)
            vm.ChooseRow(row);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is RankingViewModel vm && sender is DataGrid g && g.SelectedItem is SlotScoreRow row)
        {
            vm.ChooseRow(row);
            e.Handled = true;
        }
    }
}
