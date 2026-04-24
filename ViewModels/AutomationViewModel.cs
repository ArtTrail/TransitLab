using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TransitLab.Services;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TransitLab.ViewModels;

public partial class AutomationViewModel : ViewModelBase
{
    [ObservableProperty] private string _monitorFolder    = "";
    [ObservableProperty] private string _startTime        = "";   // "HH:mm" 24-hour
    [ObservableProperty] private int    _durationMinutes  = 1;
    [ObservableProperty] private bool   _isArmed          = false;
    [ObservableProperty] private string _automationStatus = "Not armed.";

    // Poll every 10 seconds; require 3 consecutive identical counts (~30 sec of stability)
    private const int PollIntervalSeconds = 10;
    private const int StabilityChecks     = 3;

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
        SessionLogService.Write($"[Automation] Armed — folder: {MonitorFolder}, start: {(string.IsNullOrEmpty(StartTime) ? "immediate" : StartTime)}, window: {DurationMinutes} min");
        _ = MonitorLoopAsync(_cts.Token);
    }

    private bool CanArm() => !IsArmed;

    [RelayCommand(CanExecute = nameof(CanDisarm))]
    private void DisarmAutomation()
    {
        _cts?.Cancel();
        IsArmed          = false;
        AutomationStatus = "Disarmed.";
        SessionLogService.Write("[Automation] Disarmed by user");
    }

    private bool CanDisarm() => IsArmed;

    // ── Monitoring loop ───────────────────────────────────────────────────────

    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        // Wait until start time (one large delay; status updated once)
        if (!string.IsNullOrWhiteSpace(StartTime) &&
            TimeSpan.TryParseExact(StartTime, @"hh\:mm", null, out var start))
        {
            var now    = DateTime.Now;
            var target = DateTime.Today.Add(start);
            if (target <= now) target = target.AddDays(1);
            var wait     = target - now;
            int waitMins = (int)wait.TotalMinutes;
            AutomationStatus = $"Armed — starting at {start:hh\\:mm} (in {waitMins} min)";
            SessionLogService.Write($"[Automation] Waiting {waitMins} min until start time {start:hh\\:mm}");
            try { await Task.Delay(wait, ct); }
            catch (OperationCanceledException) { return; }
        }

        var deadline     = DateTime.Now.AddMinutes(DurationMinutes);
        int stableChecks = 0;
        int lastCount    = -1;

        SessionLogService.Write($"[Automation] Monitoring started — window closes in {DurationMinutes} min");

        while (!ct.IsCancellationRequested && DateTime.Now < deadline)
        {
            int     count     = CountFitsFiles();
            string  remaining = FmtRemaining(deadline - DateTime.Now);

            if (count > 0 && count == lastCount)
            {
                stableChecks++;
                AutomationStatus = $"Files stable {stableChecks}/{StabilityChecks}  ({count} files)  —  {remaining} remaining";

                if (stableChecks >= StabilityChecks)
                {
                    AutomationStatus = $"✓  Stability reached ({count} files). Starting automation sequence…";
                    SessionLogService.Write($"[Automation] Stability reached — {count} FITS files stable for {StabilityChecks * PollIntervalSeconds} sec. Starting sequence.");

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
                        IsArmed = false;
                        _cts?.Dispose();
                        _cts = null;
                    });
                    return;
                }
            }
            else
            {
                if (stableChecks > 0)
                    SessionLogService.Write($"[Automation] File count changed ({lastCount} → {count}) — resetting stability counter");
                stableChecks = 0;
                AutomationStatus = count == 0
                    ? $"Waiting for FITS files…  —  {remaining} remaining"
                    : $"Files still arriving ({count} files)  —  {remaining} remaining";
            }
            lastCount = count;

            try { await Task.Delay(TimeSpan.FromSeconds(PollIntervalSeconds), ct); }
            catch (OperationCanceledException) { return; }
        }

        // Window expired
        if (!ct.IsCancellationRequested)
        {
            AutomationStatus = $"⚠  Monitoring window expired ({DurationMinutes} min) — no stable files detected.";
            SessionLogService.Write($"[Automation] Window expired — no trigger. Last file count: {lastCount}");
            Avalonia.Threading.Dispatcher.UIThread.Post(() => IsArmed = false);
        }
    }

    private static string FmtRemaining(TimeSpan t)
    {
        int total = Math.Max(0, (int)t.TotalSeconds);
        return $"{total / 60:D2}:{total % 60:D2}";
    }

    private int CountFitsFiles()
    {
        if (string.IsNullOrEmpty(MonitorFolder) || !Directory.Exists(MonitorFolder))
            return 0;
        try
        {
            return Directory.GetFiles(MonitorFolder, "*.fits", SearchOption.TopDirectoryOnly).Length
                 + Directory.GetFiles(MonitorFolder, "*.fit",  SearchOption.TopDirectoryOnly).Length;
        }
        catch { return 0; }
    }
}
