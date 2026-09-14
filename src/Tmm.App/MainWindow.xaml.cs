using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Tmm.App.ViewModels;

namespace Tmm.App;

public partial class MainWindow : Window
{
    // WPF draws the client area but the caption belongs to the window manager, so a dark app still
    // gets a white title bar unless DWM is told otherwise.
    private const int UseImmersiveDarkMode = 20;        // Windows 10 20H1 and later
    private const int UseImmersiveDarkModeLegacy = 19;  // the same switch on earlier Windows 10
    private const int BorderColorAttribute = 34;        // the three below need Windows 11 22H2+
    private const int CaptionColorAttribute = 35;
    private const int TextColorAttribute = 36;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

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

    /// <summary>The handle only exists from here on, and DWM needs one.</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyDarkTitleBar();
    }

    private void ApplyDarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        int on = 1;
        // Attribute 20 is the current one; 19 is what Windows 10 builds before 20H1 understand. Asking
        // for the modern one first and falling back costs nothing where it is rejected.
        if (DwmSetWindowAttribute(hwnd, UseImmersiveDarkMode, ref on, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, UseImmersiveDarkModeLegacy, ref on, sizeof(int));

        // Windows 11 22H2+ can take exact colours, so the caption matches the app rather than settling
        // for the generic dark grey. Older builds reject these and keep the dark mode set above.
        SetCaptionColor(hwnd, CaptionColorAttribute, "BgColor");
        SetCaptionColor(hwnd, TextColorAttribute, "TextColor");
        SetCaptionColor(hwnd, BorderColorAttribute, "BorderColor");
    }

    /// <summary>Pull the colour from the theme so the caption cannot drift from the window below it.</summary>
    private void SetCaptionColor(IntPtr hwnd, int attribute, string themeKey)
    {
        if (TryFindResource(themeKey) is not Color c) return;
        int colorRef = c.R | (c.G << 8) | (c.B << 16);   // COLORREF is 0x00BBGGRR, not RGB
        DwmSetWindowAttribute(hwnd, attribute, ref colorRef, sizeof(int));
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
