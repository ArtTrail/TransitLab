using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using TransitLab.Models;
using TransitLab.ViewModels;
using TransitLab.Views.Controls;
using System.Linq;

namespace TransitLab.Views.Tabs;

public partial class FrameAnalysisView : UserControl
{
    // Whichever FrameViewerControl instance is currently live — the embedded one,
    // or a popped-out one while the image viewer is undocked into its own window.
    private FrameViewerControl _activeViewer = null!;

    public FrameAnalysisView()
    {
        InitializeComponent();

        // DataGrid selection → load image + fit to viewer
        FrameGrid.SelectionChanged += OnFrameGridSelectionChanged;

        _activeViewer   = Viewer;
        Viewer.PoppedOut = OnViewerPoppedOut;
    }

    // ── Pop out / redock ──────────────────────────────────────────────────────

    private void OnViewerPoppedOut(FrameViewerControl poppedControl, Window win)
    {
        _activeViewer = poppedControl;
        Viewer.IsVisible = false;
        PoppedOutPlaceholder.IsVisible = true;

        win.Closed += (_, _) => Redock();
    }

    private void OnBringBackClick(object? sender, RoutedEventArgs e)
    {
        // Closing the pop-out window triggers Redock() via the Closed handler above.
        if (_activeViewer != Viewer && TopLevel.GetTopLevel(_activeViewer) is Window win)
            win.Close();
    }

    private void Redock()
    {
        _activeViewer = Viewer;
        Viewer.IsVisible = true;
        PoppedOutPlaceholder.IsVisible = false;
        Viewer.Reactivate();
    }

    // ── DataGrid selection ────────────────────────────────────────────────────

    private async void OnFrameGridSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not FrameAnalysisViewModel vm) return;
        if (FrameGrid.SelectedItem is not FrameEntry entry) return;
        if (string.IsNullOrEmpty(entry.Path)) return;

        await vm.LoadFrameAsync(entry.Path);
        _activeViewer.FitToViewerNow();
    }

    // ── Excl checkbox click — propagate to selection ─────────────────────────
    //
    // CheckBox.PointerPressed is marked as Handled by the Button base class before
    // it bubbles to the DataGrid row, so clicking the checkbox never disturbs the
    // current row selection.  By the time Click fires, IsExcluded has already been
    // updated through the TwoWay binding, so we just read the new value and mirror
    // it onto every other row in the selection.

    private void OnExclCheckBoxClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb) return;
        if (cb.DataContext is not FrameEntry clickedEntry) return;

        // IsExcluded is already set to the new value by the TwoWay binding.
        bool newValue = clickedEntry.IsExcluded;

        // Only propagate when more than one row is selected and the clicked row
        // is actually part of that selection (guards against accidental edits when
        // a previous range selection is still highlighted).
        var selected = FrameGrid.SelectedItems.OfType<FrameEntry>().ToList();
        if (selected.Count > 1 && selected.Contains(clickedEntry))
        {
            foreach (var entry in selected)
            {
                if (!ReferenceEquals(entry, clickedEntry))
                    entry.IsExcluded = newValue;
            }
        }
    }

    // ── [?] Help button ───────────────────────────────────────────────────────

    private void OnHelpImageAnalysis_Click(object? sender, RoutedEventArgs e) =>
        ShowHelpPopup("Image Analysis",
            "Scan Files — reads all FITS images in the Lights Directory and computes the background sky level for each frame. Frames with a background significantly above the median are flagged as potential outliers.\n\n" +
            "Flag σ — the threshold (in standard deviations above the median background) at which a frame is flagged. Lower values flag more frames; 3.0 is a typical starting point.\n\n" +
            "Exclude Flagged — marks all flagged frames as excluded so EXOTIC will skip them. Review the list first to confirm the flagged frames are genuinely bad.\n\n" +
            "Restore Excluded — un-excludes all frames so you can start the exclusion process fresh.\n\n" +
            "Dark-subtract before background estimate — subtracts the dark frame from each image before computing the background. Use this if your dark library is well-matched to the science frames.\n\n" +
            "Frame list — shows each scanned frame with its background value and status. Check the Excl box on any row to manually exclude or restore that frame. To exclude a range at once: click the first row, hold Shift and click the last row to highlight the range, then click any Excl checkbox in the highlighted selection — all selected rows will be toggled together.\n\n" +
            "VSP chart (FOV / Mag / Star / VSP Chart) — loads the AAVSO Variable Star Plotter comparison chart for the host star at the chosen field-of-view and magnitude limit. Useful for visually identifying comparison star magnitudes in the field.\n\n" +
            "Image viewer — click a row in the frame list to display that frame. Use the Black / White sliders or Auto Stretch to adjust the display contrast. Zoom with +/− /Reset or mouse wheel. Click and drag to pan. Click Pop Out to open the viewer in its own resizable window — handy on a small screen.\n\n" +
            "Pick Target Star — enables click-to-pick mode. Click the target star in the image, then click 'Send to Target Star' to populate the Target Star X,Y field on the Parameters tab.\n\n" +
            "Pick Comp Stars — enables multi-click comp picking mode. Click up to the configured maximum comparison stars (default 10, set via Tools → Settings → Comp Stars), then click 'Send to Comp Stars' to append them to the Comparison Stars list on the Parameters tab.");

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
        var scroll = new ScrollViewer
        {
            Content                       = tb,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
        };
        var btn = new Button
        {
            Content             = "OK",
            HorizontalAlignment = HorizontalAlignment.Center,
            MinWidth            = 70,
            Margin              = new Thickness(0, 2, 0, 14),
        };
        var layout = new DockPanel();
        DockPanel.SetDock(btn, Dock.Bottom);
        layout.Children.Add(btn);
        layout.Children.Add(scroll);
        var dialog = new Window
        {
            Title                 = title,
            Content               = layout,
            Width                 = 480,
            MaxHeight             = 700,
            SizeToContent         = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize             = true,
            ShowInTaskbar         = false,
        };
        btn.Click += (_, _) => dialog.Close();
        _ = dialog.ShowDialog(owner);
    }
}
