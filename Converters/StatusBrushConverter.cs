using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;

namespace TransitLab.Converters;

public class StatusBrushConverter : IValueConverter
{
    private static readonly IBrush BrushOk   = new SolidColorBrush(Color.Parse("#a3be8c"));
    private static readonly IBrush BrushErr  = new SolidColorBrush(Color.Parse("#bf616a"));
    private static readonly IBrush BrushWarn = new SolidColorBrush(Color.Parse("#ebcb8b"));
    private static readonly IBrush BrushHint = new SolidColorBrush(Color.Parse("#8892a0"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = value as string ?? "";
        return s switch
        {
            _ when s.StartsWith("✓") => BrushOk,
            _ when s.StartsWith("✗") => BrushErr,
            _ when s.StartsWith("⟳") || s.StartsWith("⚠") => BrushWarn,
            _ => BrushHint,
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
