using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Tmm.Core;

namespace Tmm.App.Converters;

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is bool b ? !b : value;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => value is bool b ? !b : value;
}

/// <summary>true -> Visible; parameter "invert" flips it. Nulls and empty strings count as false.</summary>
public sealed class VisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        bool v = value switch
        {
            bool b => b,
            string s => s.Length > 0,
            null => false,
            _ => true,
        };
        if (p is string ps && ps.Equals("invert", StringComparison.OrdinalIgnoreCase)) v = !v;
        return v ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class ModStateBrushConverter : IValueConverter
{
    public Brush Enabled { get; set; } = Brushes.SeaGreen;
    public Brush Disabled { get; set; } = Brushes.Gray;
    public Brush Stale { get; set; } = Brushes.Goldenrod;
    public Brush Broken { get; set; } = Brushes.IndianRed;

    public object Convert(object value, Type t, object p, CultureInfo c) => value switch
    {
        ModState.Enabled => Enabled,
        ModState.Disabled => Disabled,
        ModState.Stale => Stale,
        ModState.Broken => Broken,
        _ => Disabled,
    };
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>0..100 -> red/amber/green brush for score cells.</summary>
public sealed class ScoreBrushConverter : IValueConverter
{
    public Brush Good { get; set; } = Brushes.SeaGreen;
    public Brush Mid { get; set; } = Brushes.Goldenrod;
    public Brush Bad { get; set; } = Brushes.IndianRed;

    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        double v = value is double d ? d : 0;
        return v >= 80 ? Good : v >= 50 ? Mid : Bad;
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Seconds as m:ss.s for labels.</summary>
public sealed class SecondsConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        if (value is not double s) return "";
        var ts = TimeSpan.FromSeconds(Math.Max(0, s));
        return $"{(int)ts.TotalMinutes}:{ts.Seconds:00}.{ts.Milliseconds / 100}";
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>True when any input is true. Lets one trigger drive an animation from either hover or
/// selection, so leaving the mouse does not collapse a tile that is still selected.</summary>
public sealed class AnyTrueConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type t, object p, CultureInfo c) => values.Any(v => v is true);
    public object[] ConvertBack(object value, Type[] t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>true when the bound number is below the ConverterParameter. Drives the layout swap that
/// moves the track player onto its own row once the panel gets narrow.</summary>
public sealed class IsLessThanConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        double v = value is double d ? d : 0;
        double limit = p is string s && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var l) ? l : 0;
        return v > 0 && v < limit;
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Seconds as m:ss, for the player's clock.</summary>
public sealed class ClockConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        if (value is not double s || double.IsNaN(s) || double.IsInfinity(s)) return "0:00";
        var ts = TimeSpan.FromSeconds(Math.Max(0, s));
        return $"{(int)ts.TotalMinutes}:{ts.Seconds:00}";
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}
