using Avalonia.Controls;
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

        const double charPx  = 12.0;
        const double padding = 20.0;

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

    private async Task<string?> PickFolderAsync(string title)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return null;
        var results = await topLevel.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
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
}
