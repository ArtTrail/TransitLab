using Avalonia.Controls;
using TransitLab.ViewModels;

namespace TransitLab.Views;

public partial class PreviousVersionView : UserControl
{
    public PreviousVersionView()
    {
        InitializeComponent();
        DataContextChanged += async (_, _) =>
        {
            if (DataContext is PreviousVersionViewModel vm)
                await vm.LoadReleasesCommand.ExecuteAsync(null);
        };
    }
}
