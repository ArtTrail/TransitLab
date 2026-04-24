using CommunityToolkit.Mvvm.ComponentModel;

namespace TransitLab.Models;

public partial class FrameEntry : ObservableObject
{
    [ObservableProperty] private bool   _isExcluded  = false;
    [ObservableProperty] private string _filename    = "";
    [ObservableProperty] private string _background  = "";
    [ObservableProperty] private string _status      = "";
    public string Path { get; set; } = "";   // full file path (not shown in grid)
}
