using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Tmm.Core.Analysis;

namespace Tmm.App.Views;

/// <summary>A grid of candidate pictures. Returns the chosen one through <see cref="Chosen"/>.</summary>
public partial class ArtPickerWindow : Window
{
    public ArtCandidate? Chosen { get; private set; }

    public ArtPickerWindow(string title, IReadOnlyList<ArtCandidate> candidates)
    {
        InitializeComponent();
        Heading.Text = title;
        List.ItemsSource = candidates;
        if (candidates.Count > 0) List.SelectedIndex = 0;
        UseButton.IsEnabled = List.SelectedItem is not null;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        => UseButton.IsEnabled = List.SelectedItem is not null;

    private void OnUse(object sender, RoutedEventArgs e) => Accept();

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (List.SelectedItem is not null) Accept();
    }

    private void Accept()
    {
        if (List.SelectedItem is not ArtCandidate c) return;
        Chosen = c;
        DialogResult = true;
    }
}
