using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TransitLab.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TransitLab.ViewModels;

public partial class EquipmentTargetViewModel : ViewModelBase
{
    // ── Equipment ─────────────────────────────────────────────────────────────
    [ObservableProperty] private string _cameraType  = "CCD";
    [ObservableProperty] private string _binning     = "1x1";
    [ObservableProperty] private string _filter      = "";
    [ObservableProperty] private string _filterMin   = "";
    [ObservableProperty] private string _filterMax   = "";
    [ObservableProperty] private string _pixelScale  = "";
    [ObservableProperty] private string _notes       = "";

    public string[]                     CameraTypes    { get; } = ["CCD", "DSLR"];
    public string[]                     BinningOptions { get; } = ["1x1", "2x2", "3x3", "4x4"];
    public string[]                     FilterCodes    { get; } = [
        "SU","SG","SR","SI","SZ",
        "U","B","V","R","I",
        "Y","J","H","K",
        "CBB","CV","IJ","L","MA","MB","MI","N/A",
        "RJ","STB","STHBN","STHBW","STU","STV","STY",
    ];
    public ObservableCollection<string> PixelScales   { get; } = new();
    public ObservableCollection<string> NotesList     { get; } = new();

    private static readonly Dictionary<string, (double Min, double Max)> FilterFwhm = new()
    {
        ["U"]    = (333.8,  398.8),  ["B"]    = (391.6,  480.6),  ["V"]    = (502.8,  586.8),
        ["RJ"]   = (590.0,  810.0),  ["IJ"]   = (780.0, 1020.0),
        ["R"]    = (561.7,  719.7),  ["I"]    = (721.0,  875.0),
        ["J"]    = (1040.0,1360.0),  ["H"]    = (1420.0,1780.0),  ["K"]    = (2015.0,2385.0),
        ["SU"]   = (321.8,  386.8),  ["SG"]   = (402.5,  551.5),  ["SR"]   = (553.1,  693.1),
        ["SI"]   = (697.5,  827.5),  ["SZ"]   = (841.2,  978.2),
        ["L"]    = (430.0,  700.0),  ["CV"]   = (350.0,  850.0),  ["CBB"]  = (500.0, 1000.0),
        ["N/A"]  = (404.2,  845.8),  ["Y"]    = (946.4, 1054.4),
        ["STB"]  = (459.55,478.05),  ["STY"]  = (536.7,  559.3),  ["STU"]  = (336.3,  367.7),
        ["STV"]  = (401.5,  418.5),  ["STHBW"]= (481.5,  496.5),  ["STHBN"]= (487.5,  484.5),
        ["MA"]   = (706.5,  717.5),  ["MB"]   = (748.5,  759.5),  ["MI"]   = (1003.0,1045.0),
    };

    partial void OnFilterChanged(string value)
    {
        // value can be null here: a non-editable ComboBox's SelectedItem two-way binding
        // resolves to null (and writes back to this property) if the bound value isn't
        // present in FilterCodes — e.g. a corrupted or stale saved config. Dictionary
        // lookups throw on a null key, so guard before ever reaching FilterFwhm.
        if (!string.IsNullOrEmpty(value) && FilterFwhm.TryGetValue(value, out var fwhm))
        { FilterMin = fwhm.Min.ToString("G"); FilterMax = fwhm.Max.ToString("G"); }
        DebounceLog("Filter", value);
    }

    partial void OnCameraTypeChanged(string value)  => DebounceLog("Camera type",   value);
    partial void OnBinningChanged(string value)     => DebounceLog("Binning",       value);
    partial void OnFilterMinChanged(string value)   => DebounceLog("Filter min nm", value);
    partial void OnFilterMaxChanged(string value)   => DebounceLog("Filter max nm", value);
    partial void OnPixelScaleChanged(string value)  => DebounceLog("Pixel scale",   value);
    partial void OnNotesChanged(string value)       => DebounceLog("Notes",         value);
    partial void OnPlanetNameChanged(string value)  => DebounceLog("Planet name",   value);

    // ── Star Selection ────────────────────────────────────────────────────────
    [ObservableProperty] private string _plateSolveStatus   = "";
    [ObservableProperty] private string _activeSolverLabel  = "Solver: Astrometry.net";
    [ObservableProperty] private string _aavsoCompStatus    = "";
    [ObservableProperty] private bool   _isCompQuerying;

    partial void OnIsCompQueryingChanged(bool value) =>
        FetchCompsCommand.NotifyCanExecuteChanged();

    // ── Comp star method ──────────────────────────────────────────────────────
    /// <summary>Options shown in the comp star method ComboBox.</summary>
    public string[] CompMethods { get; } = ["AAVSO VSP", "VSP + Stone", "Stone"];

    // ── Comp star detail popup ────────────────────────────────────────────────
    /// <summary>Per-star detail data from the last Gaia fetch; null until a Gaia method succeeds.</summary>
    public List<GaiaCompService.CompStarInfo>? CompStarDetails { get; private set; }

    /// <summary>True when <see cref="CompStarDetails"/> is populated — drives the "Comp Details" button.</summary>
    [ObservableProperty] private bool _hasCompStarDetails;

    partial void OnHasCompStarDetailsChanged(bool value) =>
        ShowCompStarDetailsCommand.NotifyCanExecuteChanged();

    /// <summary>Set by the view code-behind to open the details popup window.</summary>
    public Func<List<GaiaCompService.CompStarInfo>, Task>? ShowCompStarDetailsFunc { get; set; }

    [RelayCommand(CanExecute = nameof(HasCompStarDetails))]
    private async Task ShowCompStarDetails()
    {
        if (CompStarDetails is { Count: > 0 } && ShowCompStarDetailsFunc is not null)
            await ShowCompStarDetailsFunc(CompStarDetails);
    }

    /// <summary>Currently selected comp star method.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CompMethodDescription))]
    private string _selectedCompMethod = "AAVSO VSP";

    /// <summary>One-line description shown beneath the comp method ComboBox.</summary>
    public string CompMethodDescription => SelectedCompMethod switch
    {
        "AAVSO VSP"   => "Queries the AAVSO Variable Star Plotter for this target's published comparison sequence. Fast and reliable when an AAVSO sequence exists. No PSF validation.",
        "Stone"       => "Gaia DR3 + APASS + Gaia GSPC pipeline without the VSP query. Scored by color match, RUWE, and isolation. Quality gates: RUWE < 1.1 (tiered fallback to < 1.2 / < 1.4), Gaia FOE > 200, VSX variable cross-check. Includes PSF validation.",
        _             => "Gaia DR3 + APASS + Gaia GSPC, scored by color match, RUWE, and isolation. Quality gates: RUWE < 1.1 (tiered fallback to < 1.2 / < 1.4), Gaia FOE > 200, VSX variable cross-check. VSP stars fill first slots; Stone fills remaining. Includes PSF validation. Recommended.",
    };

    /// <summary>Set by MainWindowViewModel when plate solver config changes.</summary>
    public PlateSolveService.SolverConfig? PlateSolverConfig { get; set; }

    /// <summary>Called with true=success/false=failure after NEA fetch, plate solve, or AAVSO comp fetch completes.</summary>
    public Action<bool>? PlayStatusSoundAction { get; set; }
    [ObservableProperty] private string _targetXY           = "";
    [ObservableProperty] private string _compXY             = "";
    [ObservableProperty] private string _autoTargetStatus   = "⚠  Fetch planet parameters first";
    [ObservableProperty] private bool   _isAutoTargetEnabled;
    [ObservableProperty] private bool   _isPsCancelVisible;
    [ObservableProperty] private bool   _isPsRetryEnabled;
    [ObservableProperty] private bool   _isCompsRetryEnabled;

    // ── Planet Parameters ─────────────────────────────────────────────────────
    // Skips transit fitting entirely and runs EXOTIC's stellar-variability-only reduction
    // instead — requires the EXOTIC 4.3.2 pre-release dev build (build 80+); written to
    // inits.json's optional_info as "stellar_variability_only" (a plain JSON bool).
    [ObservableProperty] private bool   _stellarVariabilityOnly = false;

    /// <summary>Pushed in from MainWindowViewModel/config; suppresses the warning popup below.</summary>
    public bool SuppressStellarVariabilityWarning { get; set; } = false;

    /// <summary>Shows the "not for transit data" warning popup; persisting "don't show again" is handled by the caller.</summary>
    public Func<Task>? ShowStellarVariabilityWarningFunc { get; set; }

    partial void OnStellarVariabilityOnlyChanged(bool value)
    {
        if (value && !SuppressStellarVariabilityWarning)
            _ = ShowStellarVariabilityWarningFunc?.Invoke();
    }
    [ObservableProperty] private string _planetName     = "";
    [ObservableProperty] private string _neaStatus      = "";
    public ObservableCollection<string> RecentPlanets  { get; } = new();

    [ObservableProperty] private string _targetRa          = "";
    [ObservableProperty] private string _targetDec         = "";
    [ObservableProperty] private string _hostStarName      = "";
    [ObservableProperty] private string _orbitalPeriod     = "";
    [ObservableProperty] private string _orbitalPeriodUnc  = "";
    [ObservableProperty] private string _midTransitTime    = "";
    [ObservableProperty] private string _midTransitTimeUnc = "";
    [ObservableProperty] private string _rpRs              = "";
    [ObservableProperty] private string _rpRsUnc           = "";
    [ObservableProperty] private string _aRs               = "";
    [ObservableProperty] private string _aRsUnc            = "";
    [ObservableProperty] private string _inclination       = "";
    [ObservableProperty] private string _inclinationUnc    = "";
    [ObservableProperty] private string _eccentricity      = "";
    [ObservableProperty] private string _argPeriastron     = "";
    [ObservableProperty] private string _teff              = "";
    [ObservableProperty] private string _teffPlus          = "";
    [ObservableProperty] private string _teffMinus         = "";
    [ObservableProperty] private string _metallicity       = "";
    [ObservableProperty] private string _metallicityPlus   = "";
    [ObservableProperty] private string _metallicityMinus  = "";
    [ObservableProperty] private string _logg              = "";
    [ObservableProperty] private string _loggPlus          = "";
    [ObservableProperty] private string _loggMinus         = "";
    [ObservableProperty] private string _starDistance      = "";
    [ObservableProperty] private string _pmRa              = "";
    [ObservableProperty] private string _pmDec             = "";

    // ── Image quality (populated after Stone comp query) ──────────────────────
    /// <summary>Wire this to Results.AppendLog to stream Stone Method progress into the log.</summary>
    public Action<string>? CompLogAction { get; set; }

    /// <summary>Maximum comparison stars for Stone / VSP + Stone. Set by MainWindowViewModel from config.</summary>
    [ObservableProperty] private int _maxCompStars = 10;

    private GaiaCompService.ImageQualityInfo? _lastImageQuality;

    [ObservableProperty] private bool _hasImageQuality;

    public string QualityFwhm =>
        _lastImageQuality?.FwhmMeanPx is double fwhm
            ? $"{fwhm:F1}px  ({_lastImageQuality.FwhmUniformity})"
            : "—";

    public string QualityColorMatch =>
        _lastImageQuality is null ? "—" :
        _lastImageQuality.ColorMatchActive ? "✓  active" : "✗  disabled";

    public string QualityPrecision =>
        _lastImageQuality?.PrecisionMmag is double p ? $"~{p:F1} mmag" : "—";

    public string QualityGrade => _lastImageQuality?.OverallGrade ?? "—";

    public IBrush QualityGradeColor => _lastImageQuality?.OverallGrade switch
    {
        "good"       => new SolidColorBrush(Color.FromRgb(0xA3, 0xBE, 0x8C)),
        "acceptable" => new SolidColorBrush(Color.FromRgb(0xEB, 0xCB, 0x8B)),
        "marginal"   => new SolidColorBrush(Color.FromRgb(0xD0, 0x87, 0x70)),
        "poor"       => new SolidColorBrush(Color.FromRgb(0xBF, 0x61, 0x6A)),
        _            => new SolidColorBrush(Color.FromRgb(0xEC, 0xEF, 0xF4)),
    };

    private void SetImageQuality(GaiaCompService.ImageQualityInfo? q)
    {
        _lastImageQuality = q;
        HasImageQuality   = q is not null;
        OnPropertyChanged(nameof(QualityFwhm));
        OnPropertyChanged(nameof(QualityColorMatch));
        OnPropertyChanged(nameof(QualityPrecision));
        OnPropertyChanged(nameof(QualityGrade));
        OnPropertyChanged(nameof(QualityGradeColor));
    }

    // ── Injected ──────────────────────────────────────────────────────────────
    /// <summary>Set to true by MainWindowViewModel while an automation sequence is running; suppresses blocking dialogs.</summary>
    public bool IsAutomationRunning { get; set; }

    /// <summary>Called whenever a new target host star name becomes available (e.g. from NEA).</summary>
    public Action<string>? TargetNameChanged { get; set; }

    public Func<string, Task<string?>>?  FolderPickerFunc            { get; set; }
    public Func<string, string, Task>?   ShowInfoFunc                { get; set; }
    /// <summary>Returns the FITS directory path.</summary>
    public Func<string>?                 FitsDirFunc                 { get; set; }
    /// <summary>Returns the first non-excluded FITS file path (may scan first).</summary>
    public Func<Task<string?>>?          GetFirstNonExcludedFitsFunc { get; set; }

    // ── WCS state ─────────────────────────────────────────────────────────────
    private bool   _wcsReady    = false;
    private string _wcsFitsPath = "";
    private CancellationTokenSource? _aavsoCompCts;
    private bool   _isNeaRunning = false;  // true while FetchFromNeaAsync is in progress
    // Set when StartAavsoCompFetchAsync defers because NEA is in progress.
    // FetchFromNeaAsync checks this flag on completion and re-triggers the fetch.
    private bool   _compFetchDeferredForNea = false;

    /// <summary>Called by ObservationViewModel at the start of ReadFitsHeader to invalidate the previous plate solve.</summary>
    public void ResetWcs()
    {
        _wcsReady    = false;
        _wcsFitsPath = "";
        UpdateAutoTargetEnabled();
        FetchCompsCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Clears comp-star pixel positions and details. Pixel coordinates are image-specific,
    /// so this must run whenever a new FITS/target invalidates the previous plate solve.
    /// </summary>
    public void ResetCompSelection()
    {
        CompXY             = "";
        CompStarDetails    = null;
        HasCompStarDetails = false;
    }

    /// <summary>Called by ObservationViewModel when a plate solve succeeds or WCS is already present.</summary>
    public void NotifyWcsReady(string fitsPath, string saveDir = "", string exoticExePath = "")
    {
        _wcsReady            = true;
        _wcsFitsPath         = fitsPath;
        if (!string.IsNullOrEmpty(fitsPath))    _psFitsPath  = fitsPath;
        if (!string.IsNullOrEmpty(saveDir))     _psSaveDir   = saveDir;
        if (!string.IsNullOrEmpty(exoticExePath)) _psExoticExe = exoticExePath;
        UpdateAutoTargetEnabled();
        FetchCompsCommand.NotifyCanExecuteChanged();
        AavsoCompStatus = "Plate solve complete — select a method and click Fetch Comps.";
    }

    [RelayCommand]
    private Task RetryComps() => StartAavsoCompFetchAsync(_wcsFitsPath);

    private bool CanFetchComps() => _wcsReady && !IsCompQuerying;

    [RelayCommand(CanExecute = nameof(CanFetchComps))]
    private Task FetchComps()
    {
        Services.SessionLogService.Write($"[Comp] User clicked Fetch Comps (method: {SelectedCompMethod})");
        return StartAavsoCompFetchAsync(_wcsFitsPath);
    }

    [RelayCommand]
    private void CancelComps()
    {
        _aavsoCompCts?.Cancel();
        _compFetchDeferredForNea = false;
        AavsoCompStatus = "Cancelled.";
    }

    public async Task StartAavsoCompFetchAsync(string fitsPath)
    {
        _aavsoCompCts?.Cancel();
        _aavsoCompCts = new CancellationTokenSource();
        var ct = _aavsoCompCts.Token;

        // ── Determine RA/Dec ──────────────────────────────────────────────────
        double ra = 0, dec = 0;
        bool hasCoords = false;

        // 1. Planet parameters
        if (NumericParseService.TryParse(TargetRa, out var pRa) &&
            NumericParseService.TryParse(TargetDec, out var pDec))
        {
            ra = pRa; dec = pDec; hasCoords = true;
        }

        // 2. Derive from WCS (image centre), but only if NEA isn't still running.
        //    If NEA is in progress, defer this fetch — NEA will re-trigger it with accurate coords.
        if (!hasCoords)
        {
            if (_isNeaRunning && !ct.IsCancellationRequested)
            {
                AavsoCompStatus = "⟳  Waiting for NEA query to complete…";
                _compFetchDeferredForNea = true;
                return;
            }

            try
            {
                var hdr = await Task.Run(() => FitsHeaderService.Read(fitsPath));
                var wcs = WcsService.ReadWcs(hdr);
                if (wcs is not null)
                {
                    ra  = wcs.Crval1;
                    dec = wcs.Crval2;
                    hasCoords = true;
                }
            }
            catch (Exception ex)
            {
                Services.SessionLogService.Write($"[AavsoComp] WARNING — could not read WCS from FITS: {ex.Message}");
            }
        }

        if (!hasCoords)
        {
            if (!ct.IsCancellationRequested)
            {
                AavsoCompStatus = "⚠  Comp fetch skipped — fetch planet parameters or plate solve first";
                Services.SessionLogService.Write("[CompFetch] Skipped — no RA/Dec available");
            }
            return;
        }

        if (ct.IsCancellationRequested) return;

        // ── Route to the selected method ──────────────────────────────────────
        switch (SelectedCompMethod)
        {
            case "VSP + Stone": await RunGaiaCompFetchAsync(fitsPath, ra, dec, useVsp: true,  ct); break;
            case "Stone":       await RunGaiaCompFetchAsync(fitsPath, ra, dec, useVsp: false, ct); break;
            default:            await RunVspCompFetchAsync (fitsPath, ra, dec, ct);                 break;
        }
    }

    // ── AAVSO VSP — existing behaviour ───────────────────────────────────────

    private async Task RunVspCompFetchAsync(string fitsPath, double ra, double dec, CancellationToken ct)
    {
        ResetCompSelection();
        AavsoCompStatus    = "⟳  Fetching AAVSO comparison stars…";
        IsCompQuerying     = true;
        try
        {
            TryParseXY(TargetXY, out var targetPx, out var targetPy);
            var result = await AavsoCompService.FetchAsync(fitsPath, ra, dec, Filter, targetPx, targetPy, ct);
            if (ct.IsCancellationRequested) return;

            if (result.Pairs.Count == 0)
            {
                Services.SessionLogService.Write($"[AavsoComp] Attempt 1 failed ({result.StatusMessage}) — retrying in 10 s");
                AavsoCompStatus = $"⟳  Retrying AAVSO fetch in 10 s… ({result.StatusMessage})";
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                if (ct.IsCancellationRequested) return;

                AavsoCompStatus = "⟳  Retrying AAVSO comparison star fetch…";
                result = await AavsoCompService.FetchAsync(fitsPath, ra, dec, Filter, targetPx, targetPy, ct);
                if (ct.IsCancellationRequested) return;
                Services.SessionLogService.Write($"[AavsoComp] Retry: {result.StatusMessage}");
            }
            else
            {
                Services.SessionLogService.Write($"[AavsoComp] {result.StatusMessage}");
            }

            AavsoCompStatus = result.StatusMessage;
            if (result.Pairs.Count > 0) { SetCompStars(result.Pairs); PlayStatusSoundAction?.Invoke(true); }
            else                          PlayStatusSoundAction?.Invoke(false);
        }
        catch (OperationCanceledException) { /* superseded */ }
        catch (Exception ex)
        {
            AavsoCompStatus = $"✗  AAVSO fetch failed: {ex.Message}";
            Services.SessionLogService.Write($"[AavsoComp] ERROR — {ex.GetType().Name}: {ex.Message}");
            PlayStatusSoundAction?.Invoke(false);
        }
        finally
        {
            IsCompQuerying = false;
        }
    }

    // ── Gaia + VSP — full scored pipeline ────────────────────────────────────

    private async Task RunGaiaCompFetchAsync(string fitsPath, double ra, double dec, bool useVsp, CancellationToken ct)
    {
        ResetCompSelection();
        SetImageQuality(null);   // clear previous quality panel
        AavsoCompStatus    = "⟳  Stone method: querying Gaia DR3…";
        IsCompQuerying     = true;
        try
        {
            // Prefer the user-selected filter; fall back to the FITS header FILTER keyword;
            // last resort "V" so that VSP always has a band to query against.
            var filter = EquipmentTarget_Filter;
            if (string.IsNullOrWhiteSpace(filter) && !string.IsNullOrEmpty(fitsPath))
            {
                try
                {
                    var hdr = await Task.Run(() => FitsHeaderService.Read(fitsPath), ct);
                    filter  = hdr.Get("FILTER") ?? "";
                }
                catch { /* ignore — will fall through to default */ }
            }
            if (string.IsNullOrWhiteSpace(filter)) filter = "V";

            // Progress<string> captures the current (UI) SynchronizationContext, so
            // Report() calls from background tasks marshal back to the UI thread safely.
            var progress    = new Progress<string>(msg => AavsoCompStatus = msg);
            var logProgress = new Progress<string>(msg => CompLogAction?.Invoke(msg + "\n"));

            var result = await GaiaCompService.FetchAsync(
                fitsPath, ra, dec, filter,
                aavsoCode: null,
                useVsp: useVsp,
                progress: progress,
                logProgress: logProgress,
                maxCompStars: MaxCompStars,
                ct);
            if (ct.IsCancellationRequested) return;

            AavsoCompStatus = result.StatusMessage;
            Services.SessionLogService.Write($"[GaiaComp] {result.StatusMessage}");
            SetImageQuality(result.Quality);

            if (result.Pairs.Count > 0)
            {
                SetCompStars(result.Pairs, MaxCompStars);
                // Populate detail popup data when the Gaia pipeline returns per-star info
                if (result.Stars is { Count: > 0 })
                {
                    CompStarDetails    = result.Stars;
                    HasCompStarDetails = true;
                }
                // "poor" here can mean every candidate failed the PSF/SNR quality bar and this
                // is a forced fallback selection (see GaiaCompService) — that's not a genuine
                // success, so don't play the success cue for it even though comps were returned.
                PlayStatusSoundAction?.Invoke(result.Quality?.OverallGrade != "poor");
            }
            else
            {
                PlayStatusSoundAction?.Invoke(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* superseded */ }
        catch (OperationCanceledException)
        {
            AavsoCompStatus = "✗  Gaia query timed out — check your internet connection";
            Services.SessionLogService.Write("[GaiaComp] Timed out waiting for Gaia/APASS response");
            PlayStatusSoundAction?.Invoke(false);
        }
        catch (Exception ex)
        {
            AavsoCompStatus = $"✗  Gaia fetch failed: {ex.Message}";
            Services.SessionLogService.Write($"[GaiaComp] ERROR — {ex.GetType().Name}: {ex.Message}");
            PlayStatusSoundAction?.Invoke(false);
        }
        finally
        {
            IsCompQuerying = false;
        }
    }

    // Helper: expose the filter code from this VM (Filter is declared in the same partial class)
    private string EquipmentTarget_Filter => Filter;

    // ── Reactive enable-flag helpers ──────────────────────────────────────────
    partial void OnTargetRaChanged(string value)
    {
        UpdateAutoTargetEnabled();
        // New planet loaded — pixel coords and CSV star list from a previous target are stale.
        // CompXY is NOT cleared here to avoid racing with the concurrent AAVSO auto-fetch;
        // it is cleared at the start of StartAavsoCompFetchAsync instead.
        if (!string.IsNullOrWhiteSpace(value))
        {
            TargetXY = "";
            // NOTE: comp fetch re-trigger is handled at the end of FetchFromNeaAsync, after
            // both TargetRa AND TargetDec are fully set.  Triggering here would race against
            // TargetDec being set on the very next line of FetchFromNeaAsync.
        }
    }

    partial void OnTargetDecChanged(string value) => UpdateAutoTargetEnabled();

    private void UpdateAutoTargetEnabled()
    {
        bool hasRa  = NumericParseService.TryParse(TargetRa, out _);
        bool hasDec = NumericParseService.TryParse(TargetDec, out _);

        if (hasRa && hasDec && _wcsReady)
        {
            IsAutoTargetEnabled = true;
            AutoTargetStatus    = "✓  Planet parameters and plate solution ready";
        }
        else if (!hasRa || !hasDec)
        {
            IsAutoTargetEnabled = false;
            AutoTargetStatus    = "⚠  Fetch planet parameters first";
        }
        else
        {
            IsAutoTargetEnabled = false;
            AutoTargetStatus    = "⚠  Awaiting plate solve…";
        }
    }

    partial void OnIsAutoTargetEnabledChanged(bool value) => AutoSelectTargetCommand.NotifyCanExecuteChanged();

    // ── Plate solve state ─────────────────────────────────────────────────────
    private CancellationTokenSource? _psCts;
    private string _psFitsPath    = "";
    private string _psSaveDir     = "";
    private string _psExoticExe   = "";
    private string _psPythonExe   = "";

    // MicroObservatory FITS headers report OBSERVAT='Whipple Observatory' (the facility
    // hosting every MObs telescope) and TELESCOP as one of its four named instruments —
    // checked directly against the file being solved, not just the folder it came from,
    // so this catches MObs data regardless of how the FITS directory was selected.
    private static readonly string[] MobsTelescopes = ["Cecilia", "Donald", "Ben", "Ed"];

    private static bool LooksLikeMobsHeader(FitsHeaderService.FitsHeader hdr)
    {
        var observat = hdr.Get("OBSERVAT") ?? "";
        var telescop = hdr.Get("TELESCOP") ?? "";
        return observat.Contains("Whipple", StringComparison.OrdinalIgnoreCase)
            || MobsTelescopes.Contains(telescop, StringComparer.OrdinalIgnoreCase);
    }

    // Unistellar FITS headers report ORIGIN='Unistellar' and TELESCOP='eVscope v2.0' (or
    // similar). Checked independently of BAYERPAT/debayer state — confirmed via live testing
    // (a real Unistellar user's frames) that ASTAP still fails to solve even after debayering
    // with a correct pixel scale, so this warning applies whether or not the data has been
    // debayered yet.
    private static bool LooksLikeUnistellarHeader(FitsHeaderService.FitsHeader hdr)
    {
        var origin = hdr.Get("ORIGIN") ?? "";
        var telescop = hdr.Get("TELESCOP") ?? "";
        return origin.Contains("Unistellar", StringComparison.OrdinalIgnoreCase)
            || telescop.Contains("evscope", StringComparison.OrdinalIgnoreCase);
    }

    public async Task StartPlateSolveAsync(string fitsPath, string saveDir, string exoticExePath, string pythonExePath = "")
    {
        // ASTAP is known to struggle with data from certain sources — warn once per solve
        // attempt rather than gating/blocking, so the user can still proceed if they want to.
        if (PlateSolverConfig?.Solver == "ASTAP" && ShowInfoFunc is not null)
        {
            try
            {
                var hdr = await Task.Run(() => FitsHeaderService.Read(fitsPath));
                bool isMobs = LooksLikeMobsHeader(hdr);
                bool isUnistellar = LooksLikeUnistellarHeader(hdr);
                if (isMobs || isUnistellar)
                {
                    string reason = (isMobs, isUnistellar) switch
                    {
                        (true, true) => "MicroObservatory's small image dimensions and Unistellar eVscope's field distortion can both overwhelm ASTAP's quad-matching.",
                        (true, false) => "ASTAP can struggle to plate solve MicroObservatory's small image frames — it may fail outright or produce an incorrect solution.",
                        _ => "Unistellar eVscope frames have a known field distortion that ASTAP's quad-matching can't handle — confirmed to still fail even after debayering with a correct pixel scale, so this isn't a data-quality issue debayering alone can fix.",
                    };
                    _ = ShowInfoFunc("ASTAP May Struggle With This Data",
                        $"{reason}\n\nFor more reliable results, consider switching solvers in Plate Solve Setup: " +
                        "Astrometry.net if you're running EXOTIC 4.3.1, or NextAstro if you're running the EXOTIC 4.3.2 pre-release build.");
                }
            }
            catch { /* header unreadable — skip the warning, the solve attempt itself will report the real error */ }
        }

        _psFitsPath  = fitsPath;
        _psSaveDir   = saveDir;
        _psExoticExe = exoticExePath;
        _psPythonExe = pythonExePath;

        _psCts?.Cancel();
        _psCts = new CancellationTokenSource();
        var ct = _psCts.Token;

        IsPsCancelVisible = true;
        IsPsRetryEnabled  = false;

        var progress = new Progress<string>(msg => PlateSolveStatus = msg);
        PlateSolveService.Result result;
        try
        {
            // Inject the current pixel scale so ASTAP receives a computed FOV
            // instead of -fov 0 (auto-iterate), which risks spurious low-quad solutions.
            var config = PlateSolverConfig;
            if (config is not null &&
                NumericParseService.TryParse(PixelScale, out var ps) && ps > 0)
            {
                config = config with { PixelScaleArcsec = ps };
            }
            // Inject target RA/Dec as solve hints — used by NextAstro (and passed through
            // harmlessly to Astrometry.net, which already solves blind without them).
            if (config is not null &&
                NumericParseService.TryParse(TargetRa, out var ra) &&
                NumericParseService.TryParse(TargetDec, out var dec))
            {
                config = config with { Ra = ra, Dec = dec };
            }
            result = await PlateSolveService.SolveAsync(fitsPath, saveDir, exoticExePath, config, progress, ct, pythonExePath);
        }
        catch (OperationCanceledException)
        {
            PlateSolveStatus  = "Plate solve cancelled.";
            IsPsCancelVisible = false;
            IsPsRetryEnabled  = true;
            return;
        }
        catch (Exception ex)
        {
            PlateSolveStatus  = $"✗  {ex.Message}";
            IsPsCancelVisible = false;
            IsPsRetryEnabled  = true;
            PlayStatusSoundAction?.Invoke(false);
            return;
        }

        PlateSolveStatus  = !result.Success            ? $"✗  {result.Message}"
                           : result.IsPartialSuccess    ? $"⚠  {result.Message}"
                           :                              $"✓  {result.Message}";
        IsPsCancelVisible = false;
        IsPsRetryEnabled  = !result.Success;
        PlayStatusSoundAction?.Invoke(result.Success);

        if (result.Success)
        {
            // When Astrometry.net returns a WCS-derived pixel scale that differs from the
            // configured value by more than 5 %, auto-correct PixelScale so subsequent ASTAP
            // calls receive the correct FOV hint — wrong scale was the root cause of ASTAP's
            // consistent quad-matching failures on mismatched telescope/camera combos.
            if (result.WcsPixelScaleArcsec > 0 &&
                NumericParseService.TryParse(PixelScale, out var currentScale) &&
                currentScale > 0)
            {
                double diffPct = Math.Abs(result.WcsPixelScaleArcsec - currentScale) / currentScale * 100.0;
                if (diffPct > 5.0)
                {
                    string newScale = result.WcsPixelScaleArcsec.ToString(
                        "F2", System.Globalization.CultureInfo.InvariantCulture);
                    PixelScale = newScale;
                    PlateSolveStatus =
                        $"✓  WCS written — pixel scale auto-corrected to {newScale}\"/px " +
                        $"(was {currentScale:F2}\"/px).";
                    Services.SessionLogService.Write(
                        $"[PlateSolve] Pixel scale auto-corrected: {currentScale:F3}→" +
                        $"{result.WcsPixelScaleArcsec:F3}\"/px ({diffPct:F1}% off).");
                }
            }

            // ASTAP all-frames: use the first file that was actually solved, not the original
            // fitsPath which may be a bad/dark frame that ASTAP could never plate-solve.
            // AavsoCompService and GaiaCompService both read WCS from the FITS file to convert
            // comp star RA/Dec → pixel coords, so _wcsFitsPath must point to a solved file.
            var wcsNotifyPath = !string.IsNullOrEmpty(result.FirstSolvedPath)
                ? result.FirstSolvedPath
                : fitsPath;
            NotifyWcsReady(wcsNotifyPath);
        }
    }

    // ── Commands ──────────────────────────────────────────────────────────────
    [RelayCommand(CanExecute = nameof(IsAutoTargetEnabled))]
    private async Task AutoSelectTarget()
    {
        Services.SessionLogService.Write("[UI] User clicked Auto Select Target");
        AutoTargetStatus = "Searching…";

        if (!NumericParseService.TryParse(TargetRa, out var ra) ||
            !NumericParseService.TryParse(TargetDec, out var dec))
        {
            AutoTargetStatus = "⚠  RA/Dec must be decimal degrees";
            return;
        }

        // Find the plate-solved FITS file
        var fitsPath = _wcsFitsPath;
        if (string.IsNullOrEmpty(fitsPath) || !File.Exists(fitsPath))
        {
            if (GetFirstNonExcludedFitsFunc is not null)
                fitsPath = await GetFirstNonExcludedFitsFunc() ?? "";
            if (string.IsNullOrEmpty(fitsPath))
            {
                AutoTargetStatus = "⚠  No FITS file found — set FITS directory";
                return;
            }
        }

        try
        {
            var hdr = await Task.Run(() => FitsHeaderService.Read(fitsPath));
            var wcs = WcsService.ReadWcs(hdr);
            if (wcs is null)
            {
                AutoTargetStatus = "⚠  No plate solution — plate-solve FITS first";
                return;
            }

            var naxis1 = hdr.GetInt("NAXIS1") ?? 0;
            var naxis2 = hdr.GetInt("NAXIS2") ?? 0;

            var pixel = WcsService.SkyToPixel(wcs, ra, dec);
            if (pixel is null)
            {
                AutoTargetStatus = "⚠  WCS coordinate conversion failed";
                return;
            }

            var (px, py) = pixel.Value;
            if (naxis1 > 0 && naxis2 > 0 && (px < 1 || px > naxis1 || py < 1 || py > naxis2))
            {
                AutoTargetStatus = $"⚠  [{px}, {py}] is outside image bounds ({naxis1}×{naxis2})";
                return;
            }

            TargetXY         = $"[{px}, {py}]";
            AutoTargetStatus = $"✓  [{px}, {py}]";
            PlayStatusSoundAction?.Invoke(true);
        }
        catch (Exception ex)
        {
            AutoTargetStatus = $"✗  {ex.Message}";
            PlayStatusSoundAction?.Invoke(false);
        }
    }

    private void SetCompStars(List<(int X, int Y)> pairs, int maxCap = 10)
    {
        var deduped = new List<(int X, int Y)>();
        foreach (var (x, y) in pairs)
        {
            if (deduped.Any(e => Math.Abs(e.X - x) <= 5 && Math.Abs(e.Y - y) <= 5)) continue;
            deduped.Add((x, y));
            if (deduped.Count == maxCap) break;
        }
        CompXY = "[" + string.Join(", ", deduped.Select(p => $"[{p.X}, {p.Y}]")) + "]";
    }

    private static bool TryParseXY(string raw, out int x, out int y)
    {
        x = y = 0;
        raw = raw.Trim();
        if (string.IsNullOrEmpty(raw)) return false;
        try
        {
            var arr = JsonSerializer.Deserialize<int[]>(raw);
            if (arr is not null && arr.Length >= 2) { x = arr[0]; y = arr[1]; return true; }
        }
        catch { /* fall through */ }
        return false;
    }

    [RelayCommand] private void CancelPlateSolve() => _psCts?.Cancel();

    [RelayCommand] private async Task RetryPlateSolve()
    {
        if (string.IsNullOrEmpty(_psFitsPath)) return;
        var runtime = await ExoticRuntimeService.ResolveAsync(_psPythonExe, _psExoticExe);
        if (runtime is null)
        {
            PlateSolveStatus = "⚠  EXOTIC not found — install EXOTIC and retry";
            return;
        }
        _psPythonExe = runtime.PythonExePath;
        if (!string.IsNullOrWhiteSpace(runtime.ExoticExePath))
            _psExoticExe = runtime.ExoticExePath;
        await StartPlateSolveAsync(_psFitsPath, _psSaveDir, _psExoticExe, _psPythonExe);
    }

    [RelayCommand] private async Task FetchFromNea() => await FetchFromNeaAsync();

    private CancellationTokenSource? _neaCts;

    public async Task FetchFromNeaAsync()
    {
        var planet = PlanetName.Trim();
        if (string.IsNullOrEmpty(planet))
        {
            NeaStatus = "⚠  Enter a planet name first.";
            return;
        }

        _neaCts?.Cancel();
        _neaCts = new CancellationTokenSource();
        var ct = _neaCts.Token;

        _isNeaRunning = true;
        NeaStatus = "⟳  Querying NASA Exoplanet Archive…";
        Services.SessionLogService.Write($"[NEA] Querying: \"{planet}\"");

        NeaService.PlanetData? data;
        try
        {
            data = await NeaService.FetchAsync(planet, ct);
        }
        catch (OperationCanceledException) { _isNeaRunning = false; return; }
        catch (Exception ex)
        {
            _isNeaRunning = false;
            NeaStatus = $"✗  {ex.Message}";
            Services.SessionLogService.Write($"[NEA] ERROR — {ex.GetType().Name}: {ex.Message}");
            PlayStatusSoundAction?.Invoke(false);
            if (_wcsReady && !string.IsNullOrEmpty(_wcsFitsPath))
                _ = StartAavsoCompFetchAsync(_wcsFitsPath);
            return;
        }

        if (data is null)
        {
            _isNeaRunning = false;
            NeaStatus = $"✗  \"{planet}\" not found in NASA Exoplanet Archive.";
            Services.SessionLogService.Write($"[NEA] Not found: \"{planet}\"");
            PlayStatusSoundAction?.Invoke(false);
            if (_wcsReady && !string.IsNullOrEmpty(_wcsFitsPath))
                _ = StartAavsoCompFetchAsync(_wcsFitsPath);
            return;
        }

        PlanetName        = data.PlanetName;
        HostStarName      = data.HostStarName;
        TargetRa          = data.Ra;
        TargetDec         = data.Dec;
        OrbitalPeriod     = data.OrbitalPeriod;
        OrbitalPeriodUnc  = data.OrbitalPeriodUnc;
        MidTransitTime    = data.MidTransitTime;
        MidTransitTimeUnc = data.MidTransitTimeUnc;
        RpRs              = data.RpRs;
        RpRsUnc           = data.RpRsUnc;
        ARs               = data.ARs;
        ARsUnc            = data.ARsUnc;
        Inclination       = data.Inclination;
        InclinationUnc    = data.InclinationUnc;
        Eccentricity      = data.Eccentricity;
        ArgPeriastron     = data.ArgPeriastron;
        Teff              = data.Teff;
        TeffPlus          = data.TeffPlus;
        TeffMinus         = data.TeffMinus;
        Metallicity       = data.Metallicity;
        MetallicityPlus   = data.MetallicityPlus;
        MetallicityMinus  = data.MetallicityMinus;
        Logg              = data.Logg;
        LoggPlus          = data.LoggPlus;
        LoggMinus         = data.LoggMinus;
        StarDistance      = data.StarDistance;
        PmRa              = data.PmRa;
        PmDec             = data.PmDec;

        _isNeaRunning = false;
        var source = data.FromNextAstroCache ? "NextAstro cache (NASA Archive unreachable)" : "NEA";
        NeaStatus = string.IsNullOrEmpty(data.RpRsWarning)
            ? $"✓  Loaded from {source} — {data.PlanetName}  ({DateTime.Now:HH:mm:ss})"
            : data.RpRsWarning;
        PlayStatusSoundAction?.Invoke(true);
        Services.SessionLogService.Write($"[NEA] ✓  Loaded from {source}: {data.PlanetName}");
        if (!string.IsNullOrEmpty(data.RpRsWarning))
            Services.SessionLogService.Write($"[NEA] {data.RpRsWarning}");
        if (!string.IsNullOrEmpty(data.DefaultsApplied))
            Services.SessionLogService.Write($"[NEA] Defaults applied: {data.DefaultsApplied.Replace(Environment.NewLine, " | ")}");

        // Notify so the VSP star field can auto-fill (uses host star name, fallback to planet name)
        var targetName = string.IsNullOrWhiteSpace(data.HostStarName) ? data.PlanetName : data.HostStarName;
        if (!string.IsNullOrWhiteSpace(targetName))
            TargetNameChanged?.Invoke(targetName);

        // Alert user to any defaulted parameters so they can review before running
        if (!string.IsNullOrEmpty(data.DefaultsApplied) && ShowInfoFunc is not null)
            _ = ShowInfoFunc("NEA Parameters — Defaults Applied",
                "One or more parameters were missing from the NASA Exoplanet Archive and have been estimated. " +
                "Please review the values below and edit them on the Equipment & Target tab if you have better data before running EXOTIC.\n\n" +
                data.DefaultsApplied);

        // If a comp fetch was deferred while NEA was in progress (rare — only possible when
        // a single-frame plate solve completes before NEA responds), re-trigger it now that
        // accurate planet RA/Dec are available.
        if (_compFetchDeferredForNea && _wcsReady && !string.IsNullOrEmpty(_wcsFitsPath))
        {
            _compFetchDeferredForNea = false;
            _ = StartAavsoCompFetchAsync(_wcsFitsPath);
        }
        else
        {
            _compFetchDeferredForNea = false;
        }
    }

    [RelayCommand] private void SavePixelScale()
    { if (!string.IsNullOrWhiteSpace(PixelScale) && !PixelScales.Contains(PixelScale)) PixelScales.Add(PixelScale); }

    [RelayCommand] private void RemovePixelScale() => PixelScales.Remove(PixelScale);

    [RelayCommand] private void SaveNote()
    { if (!string.IsNullOrWhiteSpace(Notes) && !NotesList.Contains(Notes)) NotesList.Add(Notes); }

    [RelayCommand] private void RemoveNote() => NotesList.Remove(Notes);
}
