using Avalonia.Controls;
using Avalonia.Interactivity;

namespace TransitLab.Views.Dialogs;

public partial class ExclusionSessionDialog : Window
{
    public ExclusionSessionDialog()
    {
        InitializeComponent();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
