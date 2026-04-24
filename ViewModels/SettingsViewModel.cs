using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace TransitLab.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    public static IReadOnlyList<string> SoundPresets { get; } = ["Tada", "Chime", "Ding", "None", "Custom…"];

    [ObservableProperty] private string _selectedSound        = "Tada";
    [ObservableProperty] private string _customSoundPath      = "";
    [ObservableProperty] private bool   _isCustom;
    [ObservableProperty] private bool   _statusAlertsEnabled  = true;

    partial void OnSelectedSoundChanged(string value)
        => IsCustom = value == "Custom…";

    public Func<Task<string?>>?    BrowseSoundFunc { get; set; }
    public Action<string, string>? TestSoundFunc   { get; set; }
    public Action<string, string, bool>? SaveCallback { get; set; }
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
        SaveCallback?.Invoke(SelectedSound, CustomSoundPath, StatusAlertsEnabled);
        CloseCallback?.Invoke();
    }

    [RelayCommand]
    private void Cancel() => CloseCallback?.Invoke();
}
