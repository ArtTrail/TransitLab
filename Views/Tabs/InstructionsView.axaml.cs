using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;

namespace TransitLab.Views.Tabs;

public partial class InstructionsView : UserControl
{
    private readonly List<TextBlock> _matches = new();
    private int _matchIndex = -1;
    private string _lastQuery = "";

    // Tracks whichever match TextBlock currently has its Text swapped out for highlighted
    // Inlines, so it can be restored to plain text before highlighting a different one.
    private TextBlock? _highlightedBlock;
    private string?    _highlightedOriginalText;

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
        ClearHighlight();
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
            ClearHighlight();
            _matches.Clear();
            _matchIndex = -1;
            _lastQuery = query;
            var panel = this.FindControl<StackPanel>("ContentPanel");
            if (panel is not null)
                CollectMatches(panel, query, _matches);
        }

        if (_matches.Count == 0)
        {
            ClearHighlight();
            if (SearchStatus is not null) SearchStatus.Text = "No matches";
            return;
        }

        ClearHighlight();

        _matchIndex = (_matchIndex + 1) % _matches.Count;
        var target = _matches[_matchIndex];
        target.BringIntoView();
        HighlightMatch(target, query);
        if (SearchStatus is not null)
            SearchStatus.Text = $"{_matchIndex + 1} / {_matches.Count}";
    }

    // Swaps the TextBlock's plain Text for three Runs (before/match/after), so only the
    // matched substring itself gets a contrasting highlight rather than the whole paragraph.
    private void HighlightMatch(TextBlock target, string query)
    {
        var text = target.Text ?? "";
        var idx = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return; // shouldn't happen — CollectMatches already confirmed a match

        _highlightedBlock = target;
        _highlightedOriginalText = text;

        var before = text[..idx];
        var match  = text.Substring(idx, query.Length);
        var after  = text[(idx + query.Length)..];

        this.TryFindResource("BrushWarn", out var bgResource);
        this.TryFindResource("BrushBg",   out var fgResource);
        var highlightBg = bgResource as IBrush;
        var highlightFg = fgResource as IBrush;

        target.Text = null;
        target.Inlines ??= new InlineCollection();
        target.Inlines.Clear();
        target.Inlines.Add(new Run(before));
        target.Inlines.Add(new Run(match) { Background = highlightBg, Foreground = highlightFg });
        target.Inlines.Add(new Run(after));
    }

    // Restores the previously-highlighted block's plain Text — called before moving to a
    // different match, starting a new search, or clicking Clear, so at most one match is
    // ever visibly highlighted at a time.
    private void ClearHighlight()
    {
        if (_highlightedBlock is not null && _highlightedOriginalText is not null)
        {
            _highlightedBlock.Inlines?.Clear();
            _highlightedBlock.Text = _highlightedOriginalText;
        }
        _highlightedBlock = null;
        _highlightedOriginalText = null;
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
