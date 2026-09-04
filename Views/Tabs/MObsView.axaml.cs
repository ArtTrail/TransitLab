using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using TransitLab.Models;
using TransitLab.ViewModels;
using System;
using System.Collections.Specialized;
using System.Threading.Tasks;

namespace TransitLab.Views.Tabs;

public partial class MObsView : UserControl
{
    public MObsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                // Wire folder pickers for ObservationViewModel (directory browse buttons)
                vm.Observation.FolderPickerFunc = PickFolderAsync;

                // Wire folder picker for MObsViewModel (download folder browse)
                vm.MObs.FolderPickerFunc = PickMObsFolderAsync;

                vm.MObs.Observations.CollectionChanged -= OnObservationsChanged;
                vm.MObs.Observations.CollectionChanged += OnObservationsChanged;
            }
        };
    }

    private void OnObservationsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add &&
            e.Action != NotifyCollectionChangedAction.Reset) return;

        Dispatcher.UIThread.Post(AutoSizeColumns, DispatcherPriority.Background);
    }

    private void AutoSizeColumns()
    {
        if (DataContext is not MainWindowViewModel vm || vm.MObs.Observations.Count == 0) return;

        const double charPx  = 15.0;
        const double padding = 30.0;

        var colDefs = new (string Header, Func<MObsEntry, string> Get, int Idx)[]
        {
            ("Object",         e => e.Object,  0),
            ("Date",           e => e.Date,    1),
            ("Science Frames", e => e.Science, 2),
            ("Cal Frames",     e => e.Cal,     3),
        };

        foreach (var (header, get, idx) in colDefs)
        {
            if (idx >= ObsGrid.Columns.Count) continue;

            double maxLen = header.Length;
            foreach (var entry in vm.MObs.Observations)
                maxLen = Math.Max(maxLen, (get(entry) ?? "").Length);

            ObsGrid.Columns[idx].Width = new DataGridLength(maxLen * charPx + padding);
        }
    }

    private async Task<string?> PickFolderAsync(string title, string startDir)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return null;

        IStorageFolder? startFolder = null;
        if (!string.IsNullOrEmpty(startDir))
            startFolder = await topLevel.StorageProvider.TryGetFolderFromPathAsync(startDir);

        var results = await topLevel.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title                  = title,
                AllowMultiple          = false,
                SuggestedStartLocation = startFolder,
            });
        return results.Count > 0 ? results[0].Path.LocalPath : null;
    }

    private async Task<string?> PickMObsFolderAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return null;

        IStorageFolder? startFolder = null;
        if (DataContext is MainWindowViewModel vm && !string.IsNullOrEmpty(vm.MObs.DownloadFolder))
            startFolder = await topLevel.StorageProvider.TryGetFolderFromPathAsync(vm.MObs.DownloadFolder);

        var results = await topLevel.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title                  = "Select MObs Download Folder",
                AllowMultiple          = false,
                SuggestedStartLocation = startFolder,
            });
        return results.Count > 0 ? results[0].Path.LocalPath : null;
    }

    // ── [?] Help buttons ──────────────────────────────────────────────────

    private void OnHelpDirectories_Click(object? sender, RoutedEventArgs e) =>
        ShowHelpPopup("Directories",
            "Lights Directory — the folder containing your science (light) FITS frames for this transit. This is the primary input for EXOTIC.\n\n" +
            "Darks Directory — folder containing dark calibration frames matched to your science frame exposure time and temperature. Leave blank if not using darks.\n\n" +
            "Flats Directory — folder containing flat-field calibration frames. Leave blank if not using flats.\n\n" +
            "Biases Directory — folder containing bias (zero-second) calibration frames. Leave blank if not using biases.\n\n" +
            "Save Plots Directory — the output folder where EXOTIC writes the reduced light curve, stellar variability plot, AAVSO report file, and FITS output. Each run creates its own subfolder here, named '{Target Name}_{Obs Date}_Results{n}_{EXOTIC version}' — {n} counts how many times that exact target + date has already been run, so re-processing the same night's data never overwrites a previous run.");

    private void OnHelpMObs_Click(object? sender, RoutedEventArgs e) =>
        ShowHelpPopup("MicroObservatory (MObs)",
            "MicroObservatory (MObs) is a network of robotic telescopes operated by the Harvard-Smithsonian Center for Astrophysics. TransitLab can fetch and download your MObs observation data directly.\n\n" +
            "Data refreshes once daily, at 13:00 UTC. To keep from overloading MicroObservatory's own servers, TransitLab reads from a shared cache instead of querying MicroObservatory live — so if a session finishes after today's 13:00 UTC refresh, it won't show up here until tomorrow's.\n\n" +
            "Lookback days — how many days back to search for available observations.\n\n" +
            "Fetch List — checks the cached observations list for matching sessions, showing object name, date, science frame count, calibration frames, and weather.\n\n" +
            "Download Directory — the local folder where selected observations will be downloaded. Check 'Set as default download directory' to reuse it automatically; a target-named subfolder is created inside it.\n\n" +
            "Download Selected — downloads the FITS frames for the row selected in the list. A progress bar tracks the download.\n\n" +
            "Use This Data — sets the Lights Directory to the downloaded folder so EXOTIC can process the frames immediately.\n\n" +
            "MObs functionality powered by Python code courtesy of Douglas James.");

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
