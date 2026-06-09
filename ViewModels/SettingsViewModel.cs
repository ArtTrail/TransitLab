using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace TransitLab.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    public IReadOnlyList<string> SoundPresets { get; } =
        System.OperatingSystem.IsMacOS()
            ? new List<string> { "Glass", "Ping", "Tink", "Funk", "Hero", "Sosumi", "None", "Custom…" }
            : System.OperatingSystem.IsWindows()
                ? new List<string> { "Tada", "Chime", "Ding", "None", "Custom…" }
                : new List<string> { "None", "Custom…" };

    [ObservableProperty] private string _selectedSound =
        System.OperatingSystem.IsMacOS() ? "Glass" : "Tada";
    [ObservableProperty] private string _customSoundPath      = "";
    [ObservableProperty] private bool   _isCustom;
    [ObservableProperty] private bool   _statusAlertsEnabled  = true;
    [ObservableProperty] private bool   _showTipsAtStartup    = true;

    partial void OnSelectedSoundChanged(string value)
        => IsCustom = value == "Custom…";

    public Func<Task<string?>>?    BrowseSoundFunc { get; set; }
    public Action<string, string>? TestSoundFunc   { get; set; }
    public Action<string, string, bool, bool>? SaveCallback { get; set; }
    public Action?                 CloseCallback   { get; set; }

    [RelayCommand]
    private async Task BrowseSound()
    {
        if (BrowseSoundFunc is null) return;
        var path = await BrowseSoundFunc();
        if (path is not null)
            CustomSoundPath = path;
    }

    [RelayCommand]
    private void TestSound() => TestSoundFunc?.Invoke(SelectedSound, CustomSoundPath);

    [RelayCommand]
    private void Save()
    {
        SaveCallback?.Invoke(SelectedSound, CustomSoundPath, StatusAlertsEnabled, ShowTipsAtStartup);
        CloseCallback?.Invoke();
    }

    [RelayCommand]
    private void Cancel() => CloseCallback?.Invoke();
}
