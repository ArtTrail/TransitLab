using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TransitLab.Models;
using TransitLab.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace TransitLab.ViewModels;

public partial class ExclusionSessionDialogViewModel : ViewModelBase
{
    // ── State ─────────────────────────────────────────────────────────────────
    public ObservableCollection<ExclusionSessionRow> Rows { get; } = new();

    [ObservableProperty] private ExclusionSessionRow? _selectedRow;
    [ObservableProperty] private string _detailText  = "Select a session above to preview its files.";
    [ObservableProperty] private string _statusText  = "";

    // Two-stage confirm state
    [ObservableProperty] private string _deleteLabel = "Delete Selected Files";
    [ObservableProperty] private string _clearLabel  = "Clear List";
    private bool _deletePending;
    private bool _clearPending;

    // ── Injected callbacks ─────────────────────────────────────────────────────
    private readonly Action<List<ExclusionSession>> _saveSessions;
    private readonly Action                          _onRestored;

    // ── Constructor ───────────────────────────────────────────────────────────
    public ExclusionSessionDialogViewModel(
        IEnumerable<ExclusionSession> sessions,
        Action<List<ExclusionSession>> saveSessions,
        Action onRestored)
    {
        _saveSessions = saveSessions;
        _onRestored   = onRestored;

        foreach (var s in sessions)
            Rows.Add(new ExclusionSessionRow(s));
    }

    // ── Detail panel ──────────────────────────────────────────────────────────
    partial void OnSelectedRowChanged(ExclusionSessionRow? value)
    {
        if (value is null)
        {
            DetailText = "Select a session above to preview its files.";
            return;
        }
        var s      = value.Session;
        var exists = value.FolderExists;
        var lines  = new System.Text.StringBuilder();
        lines.AppendLine(exists
            ? $"Folder:  {s.ExclFolder}"
            : $"Folder:  {s.ExclFolder}  \u2014 NOT FOUND");
        lines.AppendLine($"Restores to:  {s.FitsDir}");
        lines.AppendLine($"Files ({value.FileCount}):");
        foreach (var f in s.Files)
            lines.AppendLine($"  {f}");
        DetailText = lines.ToString().TrimEnd();
    }

    // ── Select All / None ─────────────────────────────────────────────────────
    [RelayCommand]
    private void SelectAll()
    {
        foreach (var r in Rows) r.IsSelected = true;
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var r in Rows) r.IsSelected = false;
    }

    // ── Restore Selected ──────────────────────────────────────────────────────
    [RelayCommand]
    private void RestoreSelected()
    {
        ResetConfirmStates();
        var selected = Rows.Where(r => r.IsSelected).ToList();
        if (selected.Count == 0) { StatusText = "No sessions selected."; return; }

        int total = 0;
        foreach (var row in selected)
        {
            if (ExclusionService.RestoreSession(row.Session))
            {
                total += row.Session.Files.Count;
                Rows.Remove(row);
            }
        }
        SaveCurrentSessions();
        _onRestored();
        StatusText  = total > 0
            ? $"\u2713  {total} file(s) restored."
            : "No files found (folders may already be missing).";
        DetailText  = "Select a session above to preview its files.";
    }

    // ── Delete Selected Files (two-stage) ─────────────────────────────────────
    [RelayCommand]
    private void DeleteSelectedFiles()
    {
        ResetClearConfirm();
        var selected = Rows.Where(r => r.IsSelected).ToList();
        if (selected.Count == 0) { StatusText = "No sessions selected."; return; }

        if (!_deletePending)
        {
            _deletePending = true;
            DeleteLabel    = "\u26a0 Click again to confirm delete";
            StatusText     = $"This will permanently delete files in {selected.Count} session(s). Click again to confirm.";
            // Auto-reset after 4 s without blocking the command (button stays enabled)
            _ = Task.Delay(4000).ContinueWith(_ =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (_deletePending) { _deletePending = false; DeleteLabel = "Delete Selected Files"; StatusText = ""; }
                }));
            return;
        }

        _deletePending = false;
        DeleteLabel    = "Delete Selected Files";
        foreach (var row in selected.ToList())
        {
            ExclusionService.DeleteSession(row.Session);
            Rows.Remove(row);
        }
        SaveCurrentSessions();
        StatusText = $"\u2713  {selected.Count} exclusion folder(s) permanently deleted.";
        DetailText = "Select a session above to preview its files.";
    }

    // ── Clear List (two-stage) ────────────────────────────────────────────────
    [RelayCommand]
    private void ClearList()
    {
        ResetDeleteConfirm();
        if (Rows.Count == 0) { StatusText = "List is already empty."; return; }

        if (!_clearPending)
        {
            _clearPending = true;
            ClearLabel    = "\u26a0 Click again to confirm clear";
            StatusText    = "This removes all sessions from the list (files on disk are NOT deleted). Click again to confirm.";
            // Auto-reset after 4 s without blocking the command (button stays enabled)
            _ = Task.Delay(4000).ContinueWith(_ =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (_clearPending) { _clearPending = false; ClearLabel = "Clear List"; StatusText = ""; }
                }));
            return;
        }

        _clearPending = false;
        ClearLabel    = "Clear List";
        Rows.Clear();
        SaveCurrentSessions();
        StatusText = "\u2713  Session list cleared.";
        DetailText = "Select a session above to preview its files.";
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private void SaveCurrentSessions() =>
        _saveSessions(Rows.Select(r => r.Session).ToList());

    private void ResetDeleteConfirm() { _deletePending = false; DeleteLabel = "Delete Selected Files"; }
    private void ResetClearConfirm()  { _clearPending  = false; ClearLabel  = "Clear List"; }
    private void ResetConfirmStates() { ResetDeleteConfirm(); ResetClearConfirm(); }
}
