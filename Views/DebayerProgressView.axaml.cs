using Avalonia.Controls;
using TransitLab.ViewModels;
using System.Collections.Specialized;

namespace TransitLab.Views;

public partial class DebayerProgressView : UserControl
{
    public DebayerProgressView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is DebayerProgressViewModel vm)
                vm.LogLines.CollectionChanged += OnLogLinesChanged;
        };
    }

    private void OnLogLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;
        var last = LogListBox.ItemCount - 1;
        if (last >= 0)
            LogListBox.ScrollIntoView(last);
    }
}
