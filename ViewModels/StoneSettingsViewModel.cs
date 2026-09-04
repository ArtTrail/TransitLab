using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;

namespace TransitLab.ViewModels;

public partial class StoneSettingsViewModel : ObservableObject
{
    [ObservableProperty] private int _maxCompStars = 10;

    public Action<int>? SaveCallback  { get; set; }
    public Action?      CloseCallback { get; set; }

    [RelayCommand]
    private void Save()
    {
        SaveCallback?.Invoke(Math.Clamp(MaxCompStars, 1, 25));
        CloseCallback?.Invoke();
    }

    [RelayCommand]
    private void Cancel() => CloseCallback?.Invoke();
}
