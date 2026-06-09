using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TransitLab.Services;

namespace TransitLab.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
    private readonly Dictionary<string, CancellationTokenSource> _debounceMap = new();

    /// <summary>
    /// Logs a field value after 1.2 s of inactivity for that label, so rapid keystrokes
    /// collapse to a single entry showing the completed value.
    /// </summary>
    protected void DebounceLog(string label, string value, int delayMs = 1200)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (_debounceMap.TryGetValue(label, out var old))
        {
            old.Cancel();
            old.Dispose();
        }
        var cts = new CancellationTokenSource();
        _debounceMap[label] = cts;
        _ = Task.Delay(delayMs, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
                SessionLogService.Write($"[Field] {label}: {value}");
        }, TaskScheduler.Default);
    }
}
