using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;

namespace TransitLab.ViewModels;

public partial class AdvancedSettingsViewModel : ViewModelBase
{
    [ObservableProperty] private bool    _nonInteractiveRun;
    [ObservableProperty] private bool    _useNextAstroVariabilityServer;
    [ObservableProperty] private string  _multiprocessTransformationsText = "";
    [ObservableProperty] private string  _multiprocessLightcurveFitsText  = "";
    [ObservableProperty] private bool    _useEnsemblePhotometry;
    [ObservableProperty] private bool    _useExactlyTheCompsProvided;

    public string PrereleaseOnlyNote { get; } =
        "These options are unique to the EXOTIC 4.3.2 pre-release dev build. " +
        "They can be turned on regardless of which environment is currently active — TransitLab " +
        "only sends them to EXOTIC when the pre-release build is actually running a reduction, " +
        "so they're silently ignored otherwise.";

    public Action<bool, bool, int?, int?, bool, bool>? SaveCallback { get; set; }
    public Action?                                     CloseCallback { get; set; }

    [RelayCommand]
    private void Save()
    {
        int? transformations   = int.TryParse(MultiprocessTransformationsText.Trim(), out var t) && t > 0 ? t : null;
        int? lightcurveFits    = int.TryParse(MultiprocessLightcurveFitsText.Trim(),  out var l) && l > 0 ? l : null;
        SaveCallback?.Invoke(NonInteractiveRun, UseNextAstroVariabilityServer, transformations, lightcurveFits,
            UseEnsemblePhotometry, UseExactlyTheCompsProvided);
        CloseCallback?.Invoke();
    }

    [RelayCommand]
    private void Cancel() => CloseCallback?.Invoke();
}
