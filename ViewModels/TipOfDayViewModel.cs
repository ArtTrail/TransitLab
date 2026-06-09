using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TransitLab.Services;
using System;

namespace TransitLab.ViewModels;

public partial class TipOfDayViewModel : ObservableObject
{
    [ObservableProperty] private string _tipText      = "";
    [ObservableProperty] private string _tipCounter   = "";
    [ObservableProperty] private bool   _showAtStartup;

    private int _index;

    public int CurrentIndex => _index;

    public TipOfDayViewModel(int startIndex, bool showAtStartup)
    {
        _index         = Math.Abs(startIndex) % TipService.Tips.Length;
        _showAtStartup = showAtStartup;
        Refresh();
    }

    [RelayCommand]
    private void Next()
    {
        _index = (_index + 1) % TipService.Tips.Length;
        Refresh();
    }

    [RelayCommand]
    private void Previous()
    {
        _index = (_index - 1 + TipService.Tips.Length) % TipService.Tips.Length;
        Refresh();
    }

    private void Refresh()
    {
        TipText    = TipService.Tips[_index];
        TipCounter = $"Tip {_index + 1} of {TipService.Tips.Length}";
    }
}
