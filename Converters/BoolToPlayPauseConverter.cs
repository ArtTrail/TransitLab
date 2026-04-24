using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace TransitLab.Converters;

public class BoolToPlayPauseConverter : IValueConverter
{
    public static readonly BoolToPlayPauseConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "⏸  Pause" : "⏵  Play";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
