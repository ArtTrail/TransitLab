using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using System.Threading.Tasks;
using TransitLab.ViewModels;

namespace TransitLab.Views.Tabs;

public partial class SetupView : UserControl
{
    public SetupView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => SubscribeViewModel();
    }

    private void SubscribeViewModel()
    {
        if (DataContext is not ExoticSetupViewModel vm) return;

        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ExoticSetupViewModel.LogText))
                ScrollLogToEnd();
        };

        vm.ShowWarningAsync = ShowWarningDialogAsync;
    }

    private void ScrollLogToEnd()
    {
        if (this.FindControl<TextBox>("LogBox") is { } tb)
            tb.CaretIndex = int.MaxValue;
    }

    private async Task ShowWarningDialogAsync(string message)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;

        var dlg = new Window
        {
            Title          = "Python Version Warning",
            Width          = 460,
            Height         = 200,
            CanResize      = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content        = new StackPanel
            {
                Margin  = new Avalonia.Thickness(24),
                Spacing = 16,
                Children =
                {
                    new TextBlock
                    {
                        Text            = message,
                        TextWrapping    = TextWrapping.Wrap,
                        FontSize        = 13,
                    },
                    new Button
                    {
                        Content             = "OK",
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Width               = 80,
                    }
                }
            }
        };

        var btn = ((StackPanel)dlg.Content).Children[1] as Button;
        if (btn is not null) btn.Click += (_, _) => dlg.Close();

        await dlg.ShowDialog(owner);
    }
}
