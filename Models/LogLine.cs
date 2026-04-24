using Avalonia.Media;

namespace TransitLab.Models;

public class LogLine
{
    public string Text  { get; init; } = "";
    public IBrush Color { get; init; } = Brushes.White;
}
