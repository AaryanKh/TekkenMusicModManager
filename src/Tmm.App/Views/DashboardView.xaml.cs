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
        PanelGrid.SizeChanged += OnPanelSizeChanged;
    }

    /// <summary>
    /// The player sits inline only while the gap between the song details and the action buttons is
    /// genuinely big enough for it. Two ways it can fail: the whole bar is cramped, or the details and
    /// the buttons have eaten the middle between them. Either way it gets its own full-width bar above.
    /// </summary>
    private const double BarIsCramped = 560;      // total width of the action bar, in WPF pixels
    /// <summary>Width the player needs to stay usable inline: a play button, a scrub bar long enough
    /// to aim at, and the clock. Below this it is better off with a row of its own.</summary>
    private const double PlayerNeeds = 240;
    private const double DetailsNeed = 260;       // the song details never squeeze below this
    private const double PlayerSideMargins = 56;  // 28 either side while inline

    private void OnPanelSizeChanged(object sender, SizeChangedEventArgs e) => PlacePlayer();

    private void PlacePlayer()
    {
        double bar = PanelGrid.ActualWidth;
        if (bar <= 0) return;

        double cover = PanelGrid.ColumnDefinitions[0].ActualWidth;
        double actions = PanelGrid.ColumnDefinitions[3].ActualWidth;
        double gap = bar - cover - actions - DetailsNeed - PlayerSideMargins;

        bool stack = bar < BarIsCramped || gap < PlayerNeeds;
        if (stack == _playerStacked) return;      // nothing to do; avoid re-entering layout
        _playerStacked = stack;

        if (stack)
        {
            Grid.SetRow(PlayerBox, 0);
            Grid.SetColumn(PlayerBox, 0);
            Grid.SetColumnSpan(PlayerBox, 4);
            PlayerBox.Margin = new Thickness(0, 0, 0, 14);
            PlayerBox.MinWidth = 0;
            PlayerBox.MaxWidth = double.PositiveInfinity;
        }
        else
        {
            Grid.SetRow(PlayerBox, 1);
            Grid.SetColumn(PlayerBox, 2);
            Grid.SetColumnSpan(PlayerBox, 1);
            PlayerBox.Margin = new Thickness(28, 0, 28, 0);
            PlayerBox.MinWidth = 200;
            PlayerBox.MaxWidth = 330;
        }
    }

    private bool _playerStacked;

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
