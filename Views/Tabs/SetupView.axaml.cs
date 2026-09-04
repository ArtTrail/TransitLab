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

    private void OnHelpEnvironments_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        ShowHelpPopup("EXOTIC Environments",
            "Each card (Stable and Pre-release) is a fully separate, isolated Python environment — installing, uninstalling, or removing one never affects the other. They also use independently-detected/installed base Python interpreters: Stable uses Python 3.10 (its own status row/buttons above the cards), Pre-release uses Python 3.12+ (its own status row/buttons inside its card) — some pre-release branches require a newer Python than Stable's releases do.\n\n" +
            "Install / Reinstall — creates the environment if it doesn't exist yet, then installs (or reinstalls) EXOTIC into it. Safe to click even when EXOTIC is already installed — it updates it in place. For Pre-release, this always (re)installs from whichever branch URL is currently selected above, so it's also how you pull in new commits from that branch without uninstalling first.\n\n" +
            "Uninstall — removes only the EXOTIC package from the environment. The environment itself (its Python interpreter and any other installed packages) is left in place, so a later Install/Reinstall is quick.\n\n" +
            "Remove — deletes the entire environment outright, including EXOTIC and Python itself. Use this only if the environment seems broken — a later Install/Reinstall has to rebuild it from scratch, which is slower than recovering from Uninstall.\n\n" +
            "● (radio button) — picks which environment runs when you click Save & Run. Switch this at any time without reinstalling anything.");

    private void ShowHelpPopup(string title, string message)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;

        var tb = new TextBlock
        {
            Text         = message,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Avalonia.Thickness(16, 14, 16, 10),
            MaxWidth     = 440,
            FontSize     = 15,
        };
        var btn = new Button
        {
            Content             = "OK",
            HorizontalAlignment = HorizontalAlignment.Center,
            MinWidth            = 70,
            Margin              = new Avalonia.Thickness(0, 2, 0, 14),
        };
        var layout = new StackPanel { Children = { tb, btn } };
        var dialog = new Window
        {
            Title                 = title,
            Content               = layout,
            Width                 = 490,
            SizeToContent         = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize             = false,
            ShowInTaskbar         = false,
        };
        btn.Click += (_, _) => dialog.Close();
        _ = dialog.ShowDialog(owner);
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
                        Width               = 70,
                    }
                }
            }
        };

        var btn = ((StackPanel)dlg.Content).Children[1] as Button;
        if (btn is not null) btn.Click += (_, _) => dlg.Close();

        await dlg.ShowDialog(owner);
    }
}
