using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TransitLab.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace TransitLab.ViewModels;

public partial class AutomationViewModel : ViewModelBase
{
    [ObservableProperty] private string _monitorFolder    = "";
    [ObservableProperty] private string _startTime        = "";   // "HH:mm" 24-hour
    [ObservableProperty] private bool   _isArmed          = false;
    [ObservableProperty] private string _automationStatus = "Not armed.";
    [ObservableProperty] private string _countdownText    = "";


    partial void OnIsArmedChanged(bool value)
    {
        ArmAutomationCommand.NotifyCanExecuteChanged();
        DisarmAutomationCommand.NotifyCanExecuteChanged();
    }

    // ── Injected callbacks ────────────────────────────────────────────────────
    public Func<Task<string?>>? BrowseFolderFunc { get; set; }
    public Func<Task>?          RunSequenceFunc  { get; set; }
    public Action?              CloseCallback    { get; set; }

    [RelayCommand]
    private void CloseSettings() => CloseCallback?.Invoke();

    // ── Background monitoring ─────────────────────────────────────────────────
    private CancellationTokenSource? _cts;

    [RelayCommand]
    private async Task BrowseFolder()
    {
        if (BrowseFolderFunc is null) return;
        var path = await BrowseFolderFunc();
        if (path is not null) MonitorFolder = path;
    }

    [RelayCommand(CanExecute = nameof(CanArm))]
    private void ArmAutomation()
    {
        if (string.IsNullOrWhiteSpace(MonitorFolder))
        {
            AutomationStatus = "⚠  Set the monitor folder first.";
            return;
        }
        IsArmed          = true;
        _cts             = new CancellationTokenSource();
        AutomationStatus = "Armed — waiting for start time…";
        SessionLogService.Write($"[Automation] Armed — folder: {MonitorFolder}, start: {(string.IsNullOrEmpty(StartTime) ? "immediate" : StartTime)}");
        _ = MonitorLoopAsync(_cts.Token);
    }

    private bool CanArm() => !IsArmed;

    [RelayCommand(CanExecute = nameof(CanDisarm))]
    private void DisarmAutomation()
    {
        _cts?.Cancel();
        IsArmed          = false;
        CountdownText    = "";
        AutomationStatus = "Disarmed.";
        SessionLogService.Write("[Automation] Disarmed by user");
    }

    private bool CanDisarm() => IsArmed;

    // ── Monitoring loop ───────────────────────────────────────────────────────

    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        // Wait until start time, ticking the countdown every second
        if (!string.IsNullOrWhiteSpace(StartTime) &&
            TimeSpan.TryParseExact(StartTime, @"hh\:mm", null, out var start))
        {
            var now    = DateTime.Now;
            var target = DateTime.Today.Add(start);
            if (target <= now) target = target.AddDays(1);

            SessionLogService.Write($"[Automation] Waiting until start time {start:hh\\:mm}");

            while (!ct.IsCancellationRequested && DateTime.Now < target)
            {
                var remaining = target - DateTime.Now;
                AutomationStatus = $"Armed — starting at {start:hh\\:mm}";
                CountdownText    = $"Starts in  {(int)remaining.TotalMinutes:D2}:{remaining.Seconds:D2}";
                try { await Task.Delay(1000, ct); }
                catch (OperationCanceledException) { return; }
            }

            if (ct.IsCancellationRequested) return;
        }

        CountdownText    = "Running…";
        AutomationStatus = "Starting automation sequence…";
        SessionLogService.Write("[Automation] Start time reached — running sequence");

        if (RunSequenceFunc is not null)
        {
            try { await RunSequenceFunc(); }
            catch (Exception ex)
            {
                AutomationStatus = $"✗  Sequence error: {ex.Message}";
                SessionLogService.Write($"[Automation] Sequence error: {ex.Message}");
            }
        }
        else
        {
            AutomationStatus = "⚠  Sequence callback not wired.";
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            IsArmed       = false;
            CountdownText = "";
            _cts?.Dispose();
            _cts = null;
        });
    }

}
