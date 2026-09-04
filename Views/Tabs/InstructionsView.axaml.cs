using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;

namespace TransitLab.Views.Tabs;

public partial class InstructionsView : UserControl
{
    private readonly List<TextBlock> _matches = new();
    private int _matchIndex = -1;
    private string _lastQuery = "";

    public InstructionsView()
    {
        InitializeComponent();
    }

    private void TocClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string name }) return;
        var target = this.FindControl<Control>(name);
        if (target is null) return;
        var scroller = this.FindControl<ScrollViewer>("Scroller");
        if (scroller is not null)
            scroller.Offset = new Avalonia.Vector(scroller.Offset.X, target.Bounds.Y);
    }

    private void SearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            RunFind();
    }

    private void FindNext_Click(object? sender, RoutedEventArgs e) => RunFind();

    private void OnBackToTopClick(object? sender, RoutedEventArgs e)
    {
        var scroller = this.FindControl<ScrollViewer>("Scroller");
        if (scroller is not null)
            scroller.Offset = new Avalonia.Vector(scroller.Offset.X, 0);
    }

    private void SearchClear_Click(object? sender, RoutedEventArgs e)
    {
        if (SearchBox is not null) SearchBox.Text = "";
        _matches.Clear();
        _matchIndex = -1;
        _lastQuery = "";
        if (SearchStatus is not null) SearchStatus.Text = "";
    }

    private void RunFind()
    {
        var query = SearchBox?.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(query))
        {
            if (SearchStatus is not null) SearchStatus.Text = "";
            return;
        }

        if (!query.Equals(_lastQuery, StringComparison.OrdinalIgnoreCase))
        {
            _matches.Clear();
            _matchIndex = -1;
            _lastQuery = query;
            var panel = this.FindControl<StackPanel>("ContentPanel");
            if (panel is not null)
                CollectMatches(panel, query, _matches);
        }

        if (_matches.Count == 0)
        {
            if (SearchStatus is not null) SearchStatus.Text = "No matches";
            return;
        }

        _matchIndex = (_matchIndex + 1) % _matches.Count;
        _matches[_matchIndex].BringIntoView();
        if (SearchStatus is not null)
            SearchStatus.Text = $"{_matchIndex + 1} / {_matches.Count}";
    }

    private static void CollectMatches(ILogical parent, string query, List<TextBlock> results)
    {
        foreach (var child in parent.LogicalChildren)
        {
            if (child is TextBlock tb &&
                tb.Text?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
                results.Add(tb);
            CollectMatches(child, query, results);
        }
    }
}
