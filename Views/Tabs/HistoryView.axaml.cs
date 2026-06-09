using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace TransitLab.Views.Tabs;

public partial class HistoryView : UserControl
{
    public HistoryView()
    {
        InitializeComponent();
    }

    // ── [?] Help button ───────────────────────────────────────────────────────

    private void OnHelpHistory_Click(object? sender, RoutedEventArgs e) =>
        ShowHelpPopup("History",
            "The History table records the fitted parameters from each EXOTIC run that was successfully submitted to AAVSO Exoplanet Watch. Columns are sortable by clicking the header.\n\n" +
            "Editing — double-click any cell to edit it. Press Enter or Tab to confirm, or Escape to cancel. Changes are saved automatically.\n\n" +
            "Export CSV / Export XLSX — exports all records to a spreadsheet file for external analysis or archiving.\n\n" +
            "Delete Selected — permanently removes the selected row from the history.\n\n" +
            "Import FinalParams JSON — imports fit results from an EXOTIC FinalParams_*.json output file and adds a new record to the table. Use this to backfill runs that were not automatically recorded.\n\n" +
            "Import CSV / XLSX — imports records from a previously exported history file, allowing you to restore or merge history from another machine or session.\n\n" +
            "Table columns:\n" +
            "  • Planet — exoplanet name\n" +
            "  • Obs — AAVSO observer code\n" +
            "  • Obs Date — calendar date of the observation\n" +
            "  • Submitted — date the result was submitted to AAVSO (dd-MMM-yyyy)\n" +
            "  • Tmid (BJD_TDB) — fitted mid-transit time and its 1σ uncertainty\n" +
            "  • (Rp/R*)² — fitted transit depth and uncertainty\n" +
            "  • SNR — signal-to-noise ratio of the transit detection\n" +
            "  • Depth % — transit depth as a percentage\n" +
            "  • Inc ° — fitted orbital inclination\n" +
            "  • Dur (d) — transit duration in days\n" +
            "  • Scatter % — residual scatter of the light curve fit");

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
