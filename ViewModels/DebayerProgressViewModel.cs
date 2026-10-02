using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TransitLab.Services;
using System;
using System.Collections.ObjectModel;
using System.Linq;

namespace TransitLab.ViewModels;

/// <summary>
/// Drives the live session-log popup that appears over the GUI while TransitLab is
/// debayering one-shot-color data, so the user can see work is happening instead of a
/// frozen-looking window. Streams the session log (the debayer writes <c>[Debayer]</c>
/// lines to it per file), shows a spinner while running, and enables an OK button only
/// once debayering has finished. Clicking OK closes the window.
/// </summary>
public partial class DebayerProgressViewModel : ViewModelBase
{
    public ObservableCollection<string> LogLines { get; } = new();

    /// <summary>False while debayering is in progress; true once it completes. The OK
    /// button is enabled and the spinner hidden when this is true.</summary>
    [ObservableProperty] private bool _isComplete;

    [ObservableProperty] private string _statusText = "⟳  Debayering one-shot-color frames…";

    // Convenience inverse for binding a spinner's IsVisible without a converter.
    public bool IsRunning => !IsComplete;
    partial void OnIsCompleteChanged(bool value) => OnPropertyChanged(nameof(IsRunning));

    /// <summary>Wired by the view layer to close the popup window.</summary>
    public Action? CloseCallback { get; set; }

    // Seed the log with a little recent context, then stream new lines live.
    private const int SeedTailLines = 20;

    public void Connect()
    {
        var all = SessionLogService.ReadAll()
                                   .Split('\n')
                                   .Select(l => l.TrimEnd('\r'))
                                   .Where(l => l.Length > 0)
                                   .ToArray();
        foreach (var line in all.Skip(Math.Max(0, all.Length - SeedTailLines)))
            LogLines.Add(line);

        SessionLogService.LineWritten += OnLineWritten;
    }

    public void Disconnect() => SessionLogService.LineWritten -= OnLineWritten;

    private void OnLineWritten(string line)
        => Dispatcher.UIThread.Post(() => LogLines.Add(line));

    /// <summary>Called when the debayer pass has finished (success or with per-directory
    /// failures noted in the log). Enables the OK button.</summary>
    public void MarkComplete(string finalStatus)
    {
        Dispatcher.UIThread.Post(() =>
        {
            StatusText = finalStatus;
            IsComplete = true;
        });
    }

    [RelayCommand]
    private void Ok()
    {
        Disconnect();
        CloseCallback?.Invoke();
    }
}
