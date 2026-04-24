using Avalonia.Controls;
using TransitLab.ViewModels;
using System.Collections.Specialized;

namespace TransitLab.Views;

public partial class DiagnosticsView : UserControl
{
    public DiagnosticsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is DiagnosticsViewModel vm)
                vm.LogLines.CollectionChanged += OnLogLinesChanged;
        };
    }

    private void OnLogLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;
        if (DataContext is DiagnosticsViewModel vm && vm.IsLogFrozen) return;
        var last = LogListBox.ItemCount - 1;
        if (last >= 0)
            LogListBox.ScrollIntoView(last);
    }
}
