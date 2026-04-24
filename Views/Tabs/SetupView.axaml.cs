using Avalonia.Controls;
using TransitLab.ViewModels;

namespace TransitLab.Views.Tabs;

public partial class SetupView : UserControl
{
    public SetupView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => SubscribeLog();
    }

    private void SubscribeLog()
    {
        if (DataContext is ExoticSetupViewModel vm)
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ExoticSetupViewModel.LogText))
                    ScrollLogToEnd();
            };
    }

    private void ScrollLogToEnd()
    {
        if (this.FindControl<TextBox>("LogBox") is { } tb)
            tb.CaretIndex = int.MaxValue;
    }
}
