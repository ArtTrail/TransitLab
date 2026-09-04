using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TransitLab.Services;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TransitLab.ViewModels;

public partial class LdtkLibraryDownloadViewModel : ViewModelBase
{
    [ObservableProperty] private string  _headlineText  = "Checking ldtk library size…";
    [ObservableProperty] private string  _logText       = "";
    [ObservableProperty] private bool    _isIndeterminate = true;
    [ObservableProperty] private double  _progressValue   = 0;
    [ObservableProperty] private double  _progressMax     = 1;
    [ObservableProperty] private bool    _isProgressVisible = true;

    [ObservableProperty] private bool    _isConfirmVisible  = false;
    [ObservableProperty] private string  _confirmText       = "";

    [ObservableProperty] private bool    _isDone            = false;
    [ObservableProperty] private bool    _canCancel         = true;

    public Action? CloseCallback { get; set; }

    private CancellationTokenSource? _cts;
    private Dictionary<string, List<(string Name, long Size)>>? _listing;
    private string? _mirrorLabel;

    private void Log(string msg) => LogText += (LogText.Length > 0 ? "\n" : "") + msg;

    public async Task StartAsync()
    {
        _cts = new CancellationTokenSource();
        var progress = new Progress<string>(Log);
        try
        {
            var (listing, totalBytes, mirrorLabel) =
                await ExoticInstallService.CrawlLdtkMirrorAsync(progress, _cts.Token);
            _listing     = listing;
            _mirrorLabel = mirrorLabel;

            var fileCount = listing.Count > 0 ? System.Linq.Enumerable.Sum(listing.Values, v => v.Count) : 0;
            var sizeMb    = totalBytes / 1024.0 / 1024.0;
            var sizeText  = sizeMb >= 1024 ? $"{sizeMb / 1024.0:F2} GB" : $"{sizeMb:F0} MB";

            HeadlineText     = "Ready to download";
            ConfirmText      = $"This will download {fileCount:N0} files (~{sizeText}) from the {mirrorLabel} mirror " +
                                "and install them into EXOTIC's limb-darkening cache (~/.ldtk).\n\n" +
                                "Once installed, EXOTIC will never need to contact the original FTP server again — " +
                                "useful if your network blocks it.\n\n" +
                                "This is a one-time download; already-cached files are skipped on future runs.";
            IsConfirmVisible = true;
            IsProgressVisible = false;
        }
        catch (OperationCanceledException)
        {
            HeadlineText = "Cancelled.";
            IsProgressVisible = false;
        }
        catch (Exception ex)
        {
            HeadlineText = "Could not check the ldtk library size.";
            Log($"ERROR: {ex.Message}");
            IsProgressVisible = false;
        }
    }

    [RelayCommand]
    private async Task Confirm()
    {
        if (_listing is null || _mirrorLabel is null) return;

        IsConfirmVisible   = false;
        IsProgressVisible  = true;
        IsIndeterminate    = false;
        HeadlineText       = "Downloading ldtk library…";

        var totalFiles = System.Linq.Enumerable.Sum(_listing.Values, v => v.Count);
        ProgressMax = totalFiles;
        ProgressValue = 0;

        var fileProgress = new Progress<(int done, int total, string currentFile)>(p =>
        {
            ProgressValue = p.done;
            ProgressMax   = p.total;
        });
        var logProgress = new Progress<string>(Log);

        try
        {
            await ExoticInstallService.DownloadLdtkFilesAsync(
                _listing, _mirrorLabel, fileProgress, logProgress, maxConcurrency: 8, ct: _cts!.Token);

            HeadlineText = "Locating Python to finish setup…";
            var python = await ExoticInstallService.FindPythonAsync(_cts.Token);
            if (python is null)
            {
                Log("Python was not found. The downloaded files are in place, but EXOTIC needs " +
                    "Tools → Python & EXOTIC Setup run at least once before this cache can be used.");
            }
            else
            {
                await ExoticInstallService.WriteLdtkServerFileListAsync(python.ExePath, _listing, logProgress, _cts.Token);
            }

            HeadlineText  = "Done!";
            IsDone        = true;
            CanCancel     = false;
            IsIndeterminate = false;
        }
        catch (OperationCanceledException)
        {
            HeadlineText = "Cancelled.";
        }
        catch (Exception ex)
        {
            HeadlineText = "Download failed.";
            Log($"ERROR: {ex.Message}");
        }
    }

    [RelayCommand]
    private void CancelConfirm() => CloseCallback?.Invoke();

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
        CloseCallback?.Invoke();
    }

    [RelayCommand]
    private void Close() => CloseCallback?.Invoke();
}
