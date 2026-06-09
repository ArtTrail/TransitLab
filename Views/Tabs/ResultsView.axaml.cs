using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using TransitLab.ViewModels;
using System.Collections.Specialized;

namespace TransitLab.Views.Tabs;

public partial class ResultsView : UserControl
{
    public ResultsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is ResultsViewModel vm)
        {
            vm.LogLines.CollectionChanged += OnLogLinesChanged;
            vm.PropertyChanged += OnVmPropertyChanged;
        }
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ResultsViewModel.IsPromptActive) &&
            DataContext is ResultsViewModel vm && vm.IsPromptActive)
        {
            PromptTextBox?.Focus();
        }
    }

    private void OnPromptKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Enter && DataContext is ResultsViewModel vm)
        {
            vm.SendPromptCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnLogLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;
        if (DataContext is ResultsViewModel vm && vm.IsLogFrozen) return;
        var last = LogListBox.ItemCount - 1;
        if (last >= 0)
            LogListBox.ScrollIntoView(last);
    }

    // ── [?] Help buttons ──────────────────────────────────────────────────

    private void OnHelpExoticLog_Click(object? sender, RoutedEventArgs e) =>
        ShowHelpPopup("EXOTIC Log",
            "Auto prompts — when enabled, known EXOTIC interactive questions (secondary observer codes, 'Enter 1 or 2' confirmations) are detected and answered silently without interrupting the run. When disabled, every question EXOTIC asks appears in the input box below the log for you to answer manually.\n\n" +
            "Pause Log — temporarily stops the log from auto-scrolling so you can read earlier output. The run continues unaffected in the background; click again to resume auto-scrolling.\n\n" +
            "To save a copy of the log: open Tools → Diagnostics and click 'Save Log'. The diagnostics log captures the full EXOTIC output for the current session and can be exported as a .txt file.");

    private void OnHelpSubmitResults_Click(object? sender, RoutedEventArgs e) =>
        ShowHelpPopup("Submit Results",
            "AAVSO Login — enter your AAVSO username and password and click Login before uploading. 'Save password' stores your credentials locally so you don't need to re-enter them each session.\n\n" +
            "AAVSO Exoplanet (exosite) — uploads your EXOTIC report file and light curve image directly to the AAVSO Exoplanet Watch database.\n\n" +
            "  • Site — select the observatory site registered in your AAVSO account that matches where the observation was made.\n\n" +
            "  • Equipment Package — select the camera/telescope combination registered in your AAVSO account for this observation.\n\n" +
            "  • I accept the GDPR terms — must be checked before uploading.\n\n" +
            "  • Upload to AAVSO Exoplanet — submits the report and image. The button is enabled after a successful EXOTIC run.\n\n" +
            "AAVSO Variability (WebObs) —\n\n" +
            "  • Open WebObs in Browser — opens the AAVSO WebObs submission portal in your default browser. WebObs submissions require manual review and approval by AAVSO staff before they appear in the database.\n\n" +
            "  • Open Transit Post Generator — opens Exoplanet Slacker's Transit Post Generator for creating a formatted social media post about your transit observation. Courtesy of Douglas James.");

    private void ShowHelpPopup(string title, string message)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;

        var tb = new TextBlock
        {
            Text         = message,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(16, 14, 16, 10),
            MaxWidth     = 440,
            FontSize     = 15,
        };
        var btn = new Button
        {
            Content             = "OK",
            HorizontalAlignment = HorizontalAlignment.Center,
            MinWidth            = 80,
            Margin              = new Thickness(0, 2, 0, 14),
        };
        var layout = new StackPanel { Children = { tb, btn } };
        var dialog = new Window
        {
            Title                 = title,
            Content               = layout,
            Width                 = 480,
            SizeToContent         = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize             = false,
            ShowInTaskbar         = false,
        };
        btn.Click += (_, _) => dialog.Close();
        _ = dialog.ShowDialog(owner);
    }
}
