using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TransitLab.Models;
using TransitLab.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace TransitLab.ViewModels;

public partial class MObsViewModel : ViewModelBase
{
    // ── Status brush helpers ──────────────────────────────────────────────────
    private static readonly SolidColorBrush _brushOk   = new(Color.Parse("#a3be8c"));
    private static readonly SolidColorBrush _brushErr  = new(Color.Parse("#bf616a"));
    private static readonly SolidColorBrush _brushWarn = new(Color.Parse("#ebcb8b"));
    private static readonly SolidColorBrush _brushHint = new(Color.Parse("#8892a0"));

    private static IBrush BrushFor(string s) => s switch
    {
        _ when s.StartsWith("✓")                       => _brushOk,
        _ when s.StartsWith("✗")                       => _brushErr,
        _ when s.StartsWith("⟳") || s.StartsWith("⚠") => _brushWarn,
        _                                              => _brushHint,
    };

    // ── Settings row ──────────────────────────────────────────────────────────
    [ObservableProperty] private string  _selectedTelescope = "Cecilia";
    [ObservableProperty] private decimal _lookbackDays      = 5;
    [ObservableProperty] private string  _statusText        = "";
    [ObservableProperty] private IBrush  _statusBrush       = _brushHint;

    partial void OnStatusTextChanged(string value) => StatusBrush = BrushFor(value);

    // ── Download controls ─────────────────────────────────────────────────────
    [ObservableProperty] private string  _downloadFolder      = "";
    [ObservableProperty] private bool    _isDefaultFolder;
    [ObservableProperty] private bool    _isDownloadEnabled;
    [ObservableProperty] private bool    _isUseDataEnabled;
    [ObservableProperty] private string  _downloadStatusText  = "";
    [ObservableProperty] private IBrush  _downloadStatusBrush = _brushHint;
    [ObservableProperty] private double  _downloadProgress;
    [ObservableProperty] private double  _downloadProgressMax = 1;
    [ObservableProperty] private bool    _isDownloadRunning;

    partial void OnDownloadStatusTextChanged(string value) => DownloadStatusBrush = BrushFor(value);

    // ── Data ──────────────────────────────────────────────────────────────────
    public ObservableCollection<MObsEntry> Observations { get; } = new();
    public string[] Telescopes { get; } = ["Cecilia", "Donald", "Ben", "Ed"];

    // Injected by the View
    public Func<Task<string?>>? FolderPickerFunc { get; set; }

    // Called by MainWindowViewModel after a successful download, to populate Observation tab
    public Action<string, string>? UseDataCallback { get; set; }   // (scienceDir, darksDir)

    // Internal state
    private List<MObsService.Observation> _fetched = [];
    private MObsService.Observation?       _lastDownloaded;
    private CancellationTokenSource?       _fetchCts;
    private CancellationTokenSource?       _dlCts;

    // Saved default folder — locked in when IsDefaultFolder is checked
    private string _savedDefaultFolder = "";

    partial void OnIsDefaultFolderChanged(bool value)
    {
        if (value) _savedDefaultFolder = DownloadFolder;
    }

    partial void OnDownloadFolderChanged(string value)
    {
        if (IsDefaultFolder && !string.IsNullOrEmpty(_savedDefaultFolder) && value != _savedDefaultFolder)
            DownloadFolder = _savedDefaultFolder;
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task FetchList()
    {
        _fetchCts?.Cancel();
        _fetchCts = new CancellationTokenSource();
        var ct = _fetchCts.Token;

        Observations.Clear();
        IsDownloadEnabled = false;
        IsUseDataEnabled  = false;
        StatusText        = "⟳  Fetching observations…";

        try
        {
            var results = await Task.Run(
                () => MObsService.FetchListAsync(SelectedTelescope, (int)LookbackDays, ct), ct);

            _fetched = results;
            foreach (var obs in results)
            {
                var nCal  = obs.CalibrationFiles.Count;
                var calStr = obs.CalFallbackDate is not null ? $"{nCal}*" : $"{nCal}";
                Observations.Add(new MObsEntry
                {
                    Object     = obs.ObjectName,
                    Date       = obs.DateDisplay,
                    Science    = obs.ScienceFiles.Count.ToString(),
                    Cal        = calStr,
                    Weather    = obs.Weather,
                });
            }

            var n = results.Count;
            StatusText        = n == 0
                ? "No observations found for the selected telescope / lookback period."
                : $"✓  {n} observation{(n == 1 ? "" : "s")} found.  (* = calibrations from a prior date)";
            IsDownloadEnabled = n > 0;
        }
        catch (OperationCanceledException)
        {
            StatusText = "Fetch cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = $"✗  Fetch failed: {ex.Message}";
            Services.SessionLogService.Write($"[MObs] Fetch failed (telescope={SelectedTelescope}): {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task Browse()
    {
        if (FolderPickerFunc is null) return;
        var path = await FolderPickerFunc();
        if (path is not null)
        {
            DownloadFolder = path;
            if (IsDefaultFolder) _savedDefaultFolder = path;
        }
    }

    [ObservableProperty] private MObsEntry? _selectedEntry;

    [RelayCommand]
    private async Task Download()
    {
        // Use the collection index (not the visual/sort index) to look up the fetched observation.
        // DataGrid.SelectedIndex reflects display order after user sorting, not collection order.
        var idx = SelectedEntry is null ? -1 : Observations.IndexOf(SelectedEntry);
        if (idx < 0 || idx >= _fetched.Count)
        {
            DownloadStatusText = "⚠  Select an observation first.";
            return;
        }
        if (string.IsNullOrWhiteSpace(DownloadFolder))
        {
            DownloadStatusText = "⚠  Set a download folder first.";
            return;
        }

        var obs = _fetched[idx];

        _dlCts?.Cancel();
        _dlCts = new CancellationTokenSource();
        var ct = _dlCts.Token;

        int total = obs.ScienceFiles.Count + obs.CalibrationFiles.Count;
        DownloadProgress    = 0;
        DownloadProgressMax = Math.Max(total, 1);
        IsDownloadEnabled   = false;
        IsUseDataEnabled    = false;
        IsDownloadRunning   = true;
        DownloadStatusText  = "Starting download…";

        bool downloadDone = false;
        var progress = new Progress<(int done, int total, string filename)>(p =>
        {
            if (downloadDone) return;
            DownloadProgress   = p.done;
            DownloadStatusText = $"Downloading {p.done}/{p.total}  {p.filename}";
        });

        try
        {
            var result = await MObsService.DownloadAsync(obs, DownloadFolder, progress, ct);
            downloadDone    = true;
            _lastDownloaded = obs;

            DownloadProgress = DownloadProgressMax;
            var parts = new List<string> { $"{result.ScienceOk} science" };
            if (result.ScienceFail > 0) parts.Add($"({result.ScienceFail} failed)");
            parts.Add($"· {result.CalOk} darks");
            if (result.CalFail > 0) parts.Add($"({result.CalFail} failed)");
            var summary = string.Join(" ", parts);
            DownloadStatusText = "✓  Download complete — " + summary;
            if (result.ScienceFail > 0 || result.CalFail > 0)
                Services.SessionLogService.Write($"[MObs] Download finished with errors — {summary}  (object={obs.ObjectName}, date={obs.DateDisplay})");
            else
                Services.SessionLogService.Write($"[MObs] Download complete — {summary}  (object={obs.ObjectName}, date={obs.DateDisplay})");
            IsDownloadEnabled  = true;
            IsDownloadRunning  = false;
            IsUseDataEnabled   = true;
        }
        catch (OperationCanceledException)
        {
            DownloadStatusText = "Download cancelled.";
            IsDownloadEnabled  = true;
            IsDownloadRunning  = false;
        }
        catch (Exception ex)
        {
            DownloadStatusText = $"✗  {ex.Message}";
            Services.SessionLogService.Write($"[MObs] Download error (object={obs.ObjectName}): {ex.Message}");
            IsDownloadEnabled  = true;
            IsDownloadRunning  = false;
        }
    }

    [RelayCommand]
    private void CancelDownload()
    {
        _dlCts?.Cancel();
    }

    [RelayCommand]
    private void UseData()
    {
        if (_lastDownloaded is null)
        {
            DownloadStatusText = "⚠  Download a file set first.";
            return;
        }
        var obs        = _lastDownloaded;
        var dateToken  = obs.Date.ToString("yyyy-MM-dd");
        var folderName = $"{obs.ObjectName}_{dateToken}".Replace(" ", "_");
        var scienceDir = System.IO.Path.Combine(DownloadFolder, folderName);
        var darksDir   = System.IO.Path.Combine(scienceDir, "Darks");
        UseDataCallback?.Invoke(scienceDir, darksDir);
    }
}
