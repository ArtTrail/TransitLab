using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TransitLab.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;

namespace TransitLab.ViewModels;

public partial class DiagnosticsViewModel : ViewModelBase
{
    public ObservableCollection<string> LogLines { get; } = new();

    [ObservableProperty] private bool _isLogFrozen;

    private readonly Queue<string> _pending = new();

    public Func<Task<string?>>? SaveFileFunc  { get; set; }
    public Action?              CloseCallback { get; set; }

    // ── Live feed ─────────────────────────────────────────────────────────────

    public void Connect()
    {
        // Seed with everything already in the log this session
        var existing = SessionLogService.ReadAll();
        foreach (var line in existing.Split('\n'))
            LogLines.Add(line.TrimEnd('\r'));

        SessionLogService.LineWritten += OnLineWritten;
    }

    public void Disconnect() => SessionLogService.LineWritten -= OnLineWritten;

    private void OnLineWritten(string line)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (IsLogFrozen)
                _pending.Enqueue(line);
            else
                LogLines.Add(line);
        });
    }

    partial void OnIsLogFrozenChanged(bool value)
    {
        if (value) return;
        // Flush any lines that arrived while paused
        while (_pending.TryDequeue(out var line))
            LogLines.Add(line);
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task SaveLog()
    {
        if (SaveFileFunc is null) return;
        var path = await SaveFileFunc();
        if (path is null) return;
        try { await File.WriteAllTextAsync(path, string.Join(Environment.NewLine, LogLines)); }
        catch { }
    }

    [RelayCommand]
    private void Close() => CloseCallback?.Invoke();
}
