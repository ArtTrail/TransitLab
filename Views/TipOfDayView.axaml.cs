using Avalonia.Controls;
using Avalonia.Interactivity;

namespace TransitLab.Views;

public partial class TipOfDayView : UserControl
{
    public TipOfDayView()
    {
        InitializeComponent();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
        => (TopLevel.GetTopLevel(this) as Window)?.Close();
}
