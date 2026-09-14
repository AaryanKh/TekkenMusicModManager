using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Tmm.App.Controls;

/// <summary>
/// The assembled in-game track drawn end to end: the intro block, then each loop repeat, with a
/// divider and label at every handover. Read-only on purpose — this shows the rendered result,
/// whereas <see cref="WaveformControl"/> edits the plan against the source song.
/// </summary>
public sealed class TrackStripControl : FrameworkElement
{
    public static readonly DependencyProperty PeaksProperty = DependencyProperty.Register(
        nameof(Peaks), typeof(float[]), typeof(TrackStripControl), new FrameworkPropertyMetadata((object?)null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty DurationSecProperty = DependencyProperty.Register(
        nameof(DurationSec), typeof(double), typeof(TrackStripControl), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty BoundariesProperty = DependencyProperty.Register(
        nameof(Boundaries), typeof(double[]), typeof(TrackStripControl), new FrameworkPropertyMetadata((object?)null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IntroSecProperty = DependencyProperty.Register(
        nameof(IntroSec), typeof(double), typeof(TrackStripControl), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty PlayheadSecProperty = DependencyProperty.Register(
        nameof(PlayheadSec), typeof(double), typeof(TrackStripControl), new FrameworkPropertyMetadata(-1.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SeekCommandProperty = DependencyProperty.Register(
        nameof(SeekCommand), typeof(System.Windows.Input.ICommand), typeof(TrackStripControl), new PropertyMetadata(null));

    public float[]? Peaks { get => (float[]?)GetValue(PeaksProperty); set => SetValue(PeaksProperty, value); }
    public double DurationSec { get => (double)GetValue(DurationSecProperty); set => SetValue(DurationSecProperty, value); }
    /// <summary>Seconds at which each section after the first begins.</summary>
    public double[]? Boundaries { get => (double[]?)GetValue(BoundariesProperty); set => SetValue(BoundariesProperty, value); }
    /// <summary>Length of the leading intro section, 0 when the slot has none.</summary>
    public double IntroSec { get => (double)GetValue(IntroSecProperty); set => SetValue(IntroSecProperty, value); }

    /// <summary>Where playback has reached, in seconds. Negative hides the playhead.</summary>
    public double PlayheadSec { get => (double)GetValue(PlayheadSecProperty); set => SetValue(PlayheadSecProperty, value); }
    /// <summary>Invoked with the clicked position in seconds, so the strip can scrub.</summary>
    public System.Windows.Input.ICommand? SeekCommand { get => (System.Windows.Input.ICommand?)GetValue(SeekCommandProperty); set => SetValue(SeekCommandProperty, value); }

    private static readonly Brush BgBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x11, 0x13, 0x18)));
    private static readonly Brush IntroBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x30, 0xE5, 0xB8, 0x4C)));
    private static readonly Brush LabelBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF)));
    private static readonly Pen WavePen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x5B, 0x9C, 0xF6)), 1));
    private static readonly Pen IntroWavePen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xE5, 0xB8, 0x4C)), 1));
    private static readonly Pen SeamPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0x4C, 0xC3, 0x8A)), 1) { DashStyle = DashStyles.Dash });
    private static readonly Pen PlayheadPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)), 1.5));
    private static readonly Brush PlayedBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)));

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    public TrackStripControl()
    {
        MinHeight = 90;
        ClipToBounds = true;
        Cursor = System.Windows.Input.Cursors.Hand;
    }

    protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (DurationSec <= 0 || ActualWidth <= 0) return;
        double sec = Math.Clamp(e.GetPosition(this).X / ActualWidth, 0, 1) * DurationSec;
        if (SeekCommand?.CanExecute(sec) == true) SeekCommand.Execute(sec);
        e.Handled = true;
    }

    private double XOf(double sec) => DurationSec <= 0 ? 0 : sec / DurationSec * ActualWidth;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(BgBrush, null, new Rect(0, 0, w, h));
        if (w <= 0 || h <= 0) return;

        double xIntro = IntroSec > 0 ? XOf(IntroSec) : 0;
        if (xIntro > 0) dc.DrawRectangle(IntroBrush, null, new Rect(0, 0, xIntro, h));

        var peaks = Peaks;
        if (peaks is not null && peaks.Length >= 2)
        {
            int cols = peaks.Length / 2;
            double mid = h / 2, scale = h * 0.42;
            for (int c = 0; c < cols; c++)
            {
                double x = (c + 0.5) * w / cols;
                double y0 = mid - peaks[c * 2 + 1] * scale, y1 = mid - peaks[c * 2] * scale;
                if (y1 - y0 < 1) { y0 = mid - 0.5; y1 = mid + 0.5; }
                dc.DrawLine(x < xIntro ? IntroWavePen : WavePen, new Point(x, y0), new Point(x, y1));
            }
        }

        var bounds = Boundaries;
        if (bounds is not null && DurationSec > 0)
        {
            foreach (var b in bounds)
            {
                double x = XOf(b);
                if (x <= 0 || x >= w) continue;
                dc.DrawLine(SeamPen, new Point(x, 0), new Point(x, h));
            }

            // Label only what fits, so a long track does not turn into a wall of text.
            Label(dc, xIntro > 0 ? 0 : double.NaN, "intro", h);
            double last = -60;
            int n = 0;
            foreach (var b in bounds)
            {
                double x = XOf(b);
                n++;
                if (x - last < 56) continue;
                last = x;
                Label(dc, x, n == 1 && xIntro > 0 ? "loop" : "wrap", h);
            }
        }

        // Playhead last, so it sits over the seam lines it is being used to reach.
        if (PlayheadSec >= 0 && DurationSec > 0)
        {
            double xp = XOf(PlayheadSec);
            if (xp > 0) dc.DrawRectangle(PlayedBrush, null, new Rect(0, 0, Math.Min(xp, w), h));
            dc.DrawLine(PlayheadPen, new Point(xp, 0), new Point(xp, h));
        }
    }

    private static void Label(DrawingContext dc, double x, string text, double h)
    {
        if (double.IsNaN(x)) return;
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 10, LabelBrush, 96);
        dc.DrawText(ft, new Point(x + 3, h - ft.Height - 2));
    }
}
