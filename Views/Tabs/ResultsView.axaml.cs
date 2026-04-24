using Avalonia.Controls;
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
}
