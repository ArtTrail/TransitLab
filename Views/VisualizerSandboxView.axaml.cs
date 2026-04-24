using Avalonia.Controls;
using TransitLab.ViewModels;

namespace TransitLab.Views;

public partial class VisualizerSandboxView : Window
{
    public VisualizerSandboxView()
    {
        InitializeComponent();
    }

    private void OnResetClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is TransitViewModel vm)
            vm.ResetVisSandbox();
    }
}
