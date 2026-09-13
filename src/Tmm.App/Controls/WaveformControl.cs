using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Tmm.App.Controls;

/// <summary>
/// Waveform with intro/loop regions and downbeat ticks. Drag anywhere to move the loop start; it
/// snaps to the nearest downbeat unless Ctrl is held. Peaks are min/max pairs per column
/// (see EditorViewModel.ComputePeaks) so drawing is O(columns), not O(samples).
/// </summary>
public sealed class WaveformControl : FrameworkElement
{
    public static readonly DependencyProperty PeaksProperty = DependencyProperty.Register(
        nameof(Peaks), typeof(float[]), typeof(WaveformControl), new FrameworkPropertyMetadata((object?)null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty DurationSecProperty = DependencyProperty.Register(
        nameof(DurationSec), typeof(double), typeof(WaveformControl), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LoopStartSecProperty = DependencyProperty.Register(
        nameof(LoopStartSec), typeof(double), typeof(WaveformControl),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty LoopEndSecProperty = DependencyProperty.Register(
        nameof(LoopEndSec), typeof(double), typeof(WaveformControl), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IntroStartSecProperty = DependencyProperty.Register(
        nameof(IntroStartSec), typeof(double), typeof(WaveformControl), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IntroEndSecProperty = DependencyProperty.Register(
        nameof(IntroEndSec), typeof(double), typeof(WaveformControl), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ShowIntroProperty = DependencyProperty.Register(
        nameof(ShowIntro), typeof(bool), typeof(WaveformControl), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty DownbeatsProperty = DependencyProperty.Register(
        nameof(Downbeats), typeof(double[]), typeof(WaveformControl), new FrameworkPropertyMetadata((object?)null, FrameworkPropertyMetadataOptions.AffectsRender));

    public float[]? Peaks { get => (float[]?)GetValue(PeaksProperty); set => SetValue(PeaksProperty, value); }
    public double DurationSec { get => (double)GetValue(DurationSecProperty); set => SetValue(DurationSecProperty, value); }
    public double LoopStartSec { get => (double)GetValue(LoopStartSecProperty); set => SetValue(LoopStartSecProperty, value); }
    public double LoopEndSec { get => (double)GetValue(LoopEndSecProperty); set => SetValue(LoopEndSecProperty, value); }
    public double IntroStartSec { get => (double)GetValue(IntroStartSecProperty); set => SetValue(IntroStartSecProperty, value); }
    /// <summary>End of the intro region. Normally the loop start, but a detached intro sits
    /// somewhere else entirely, so the region is drawn from its own start and end.</summary>
    public double IntroEndSec { get => (double)GetValue(IntroEndSecProperty); set => SetValue(IntroEndSecProperty, value); }
    public bool ShowIntro { get => (bool)GetValue(ShowIntroProperty); set => SetValue(ShowIntroProperty, value); }
    public double[]? Downbeats { get => (double[]?)GetValue(DownbeatsProperty); set => SetValue(DownbeatsProperty, value); }

    private static readonly Brush BgBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x11, 0x13, 0x18)));
    private static readonly Brush WaveBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x5B, 0x9C, 0xF6)));
    private static readonly Brush DimWaveBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x3A, 0x3F, 0x4A)));
    private static readonly Brush LoopBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x38, 0x4C, 0xC3, 0x8A)));
    private static readonly Brush IntroBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x38, 0xE5, 0xB8, 0x4C)));
    private static readonly Pen StartPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x4C, 0xC3, 0x8A)), 2));
    private static readonly Pen EndPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x4C, 0xC3, 0x8A)), 1) { DashStyle = DashStyles.Dash });
    private static readonly Pen IntroPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xE5, 0xB8, 0x4C)), 1));
    private static readonly Pen BeatPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)), 1));
    private static readonly Pen WavePen = Freeze(new Pen(WaveBrush, 1));
    private static readonly Pen DimWavePen = Freeze(new Pen(DimWaveBrush, 1));

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    private bool _dragging;

    public WaveformControl()
    {
        MinHeight = 120;
        Cursor = Cursors.SizeWE;
        ClipToBounds = true;
    }

    private double XOf(double sec) => DurationSec <= 0 ? 0 : sec / DurationSec * ActualWidth;
    private double SecOf(double x) => DurationSec <= 0 ? 0 : Math.Clamp(x / ActualWidth, 0, 1) * DurationSec;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(BgBrush, null, new Rect(0, 0, w, h));
        if (w <= 0 || h <= 0) return;

        double xs = XOf(LoopStartSec), xe = XOf(LoopEndSec);
        double xi = XOf(IntroStartSec), xie = XOf(IntroEndSec > IntroStartSec ? IntroEndSec : LoopStartSec);
        if (ShowIntro && xie > xi) dc.DrawRectangle(IntroBrush, null, new Rect(xi, 0, xie - xi, h));
        if (xe > xs) dc.DrawRectangle(LoopBrush, null, new Rect(xs, 0, xe - xs, h));

        var peaks = Peaks;
        if (peaks is not null && peaks.Length >= 2)
        {
            int cols = peaks.Length / 2;
            double mid = h / 2, scale = h * 0.46;
            for (int c = 0; c < cols; c++)
            {
                double x = (c + 0.5) * w / cols;
                double y0 = mid - peaks[c * 2 + 1] * scale, y1 = mid - peaks[c * 2] * scale;
                if (y1 - y0 < 1) { y0 = mid - 0.5; y1 = mid + 0.5; }
                bool inLoop = x >= xs && x <= xe;
                bool inIntro = ShowIntro && xie > xi && x >= xi && x <= xie;
                dc.DrawLine(inLoop || inIntro ? WavePen : DimWavePen, new Point(x, y0), new Point(x, y1));
            }
        }

        var beats = Downbeats;
        if (beats is not null && DurationSec > 0)
        {
            double minGap = 3;
            double last = -100;
            foreach (var b in beats)
            {
                double x = XOf(b);
                if (x - last < minGap) continue;
                last = x;
                dc.DrawLine(BeatPen, new Point(x, h - 10), new Point(x, h));
            }
        }

        if (ShowIntro && xie > xi)
        {
            dc.DrawLine(IntroPen, new Point(xi, 0), new Point(xi, h));
            dc.DrawLine(IntroPen, new Point(xie, 0), new Point(xie, h));
        }
        dc.DrawLine(StartPen, new Point(xs, 0), new Point(xs, h));
        dc.DrawLine(EndPen, new Point(xe, 0), new Point(xe, h));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _dragging = true;
        CaptureMouse();
        Move(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) Move(e.GetPosition(this).X);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    private void Move(double x)
    {
        double sec = SecOf(x);
        var beats = Downbeats;
        if (beats is { Length: > 0 } && (Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            double best = beats[0];
            foreach (var b in beats) if (Math.Abs(b - sec) < Math.Abs(best - sec)) best = b;
            sec = best;
        }
        SetCurrentValue(LoopStartSecProperty, sec);
    }
}
