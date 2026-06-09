using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TransitLab.Services;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace TransitLab.ViewModels;

public partial class PreviousVersionViewModel : ObservableObject
{
    public ObservableCollection<ReleaseInfo> Releases { get; } = new();

    [ObservableProperty] private ReleaseInfo? _selectedRelease;
    [ObservableProperty] private bool         _isLoading;
    [ObservableProperty] private bool         _isDownloading;
    [ObservableProperty] private double       _downloadProgress;
    [ObservableProperty] private string       _statusText  = "";
    [ObservableProperty] private bool         _hasStatus;

    public string PlatformLabel => $"Platform: {UpdateService.GetPlatformId()}";

    public Func<Task<string?>>? BrowseFolderFunc { get; set; }
    public Action?              CloseCallback    { get; set; }

    partial void OnSelectedReleaseChanged(ReleaseInfo? value)
        => DownloadCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    private async Task LoadReleases()
    {
        IsLoading  = true;
        StatusText = "";
        HasStatus  = false;
        Releases.Clear();

        var releases = await UpdateService.GetAllReleasesAsync();

        if (releases.Count == 0)
        {
            StatusText = "Could not retrieve release list. Check your internet connection.";
            HasStatus  = true;
        }
        else
        {
            foreach (var r in releases)
                Releases.Add(r);
        }

        IsLoading = false;
    }

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task Download()
    {
        if (SelectedRelease is null || BrowseFolderFunc is null) return;

        var folder = await BrowseFolderFunc();
        if (folder is null) return;

        IsDownloading    = true;
        DownloadProgress = 0;
        StatusText       = $"Downloading {SelectedRelease.AssetName}…";
        HasStatus        = true;

        var destPath = System.IO.Path.Combine(folder, SelectedRelease.AssetName);
        try
        {
            var progress = new Progress<(long done, long total)>(p =>
            {
                if (p.total > 0)
                    DownloadProgress = (double)p.done / p.total * 100;
            });
            await ExoticInstallService.DownloadFileAsync(SelectedRelease.DownloadUrl, destPath, progress, default);
            StatusText = $"Downloaded to {folder} — extract the ZIP to use this version.";
        }
        catch (Exception ex)
        {
            StatusText = $"Download failed: {ex.Message}";
        }

        IsDownloading = false;
    }

    private bool CanDownload()
        => SelectedRelease is { HasAsset: true } && !IsDownloading;

    [RelayCommand]
    private void Close() => CloseCallback?.Invoke();
}
