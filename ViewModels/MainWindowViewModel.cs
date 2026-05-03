using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TransitLab.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace TransitLab.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    public string Version { get; } = "2.6.3";
    public string Title   { get; } = "TransitLab  v2.6.3";

    public ExoticSetupViewModel     ExoticSetup     { get; } = new();
    public MObsViewModel            MObs            { get; } = new();
    public ObservationViewModel     Observation     { get; } = new();
    public EquipmentTargetViewModel EquipmentTarget { get; } = new();
    public FrameAnalysisViewModel   FrameAnalysis   { get; } = new();
    public TransitViewModel         Transit         { get; } = new();
    public ResultsViewModel         Results         { get; } = new();
    public HistoryViewModel         History         { get; } = new();
    public AutomationViewModel      Automation      { get; } = new();

    /// <summary>Wired by MainWindow.axaml.cs to play the completion sound.</summary>
    public Action<string, string>? PlaySoundAction { get; set; }

    /// <summary>Wired by MainWindow.axaml.cs to play a Device Connect/Disconnect status sound.</summary>
    public Action<bool>? PlayStatusSoundAction { get; set; }

    /// <summary>Which preset (or "Custom…"/"None") to play when results appear.</summary>
    public string CompletionSound
    {
        get => _cfg.CompletionSound;
        set { _cfg.CompletionSound = value; ConfigService.Save(_cfg); }
    }

    /// <summary>Path to the user's custom sound file (used only when CompletionSound == "Custom…").</summary>
    public string CompletionSoundPath
    {
        get => _cfg.CompletionSoundPath;
        set { _cfg.CompletionSoundPath = value; ConfigService.Save(_cfg); }
    }

    public bool StatusAlertsEnabled
    {
        get => _cfg.StatusAlertsEnabled;
        set { _cfg.StatusAlertsEnabled = value; ConfigService.Save(_cfg); }
    }

    // Injected by MainWindow code-behind
    public Func<string, string, Task<string?>>?  SaveFilePickerFunc      { get; set; }
    public Func<string, Task<string?>>?           OpenFilePickerFunc      { get; set; }
    public Func<string, Task<string?>>?           BrowseExoticFunc        { get; set; }
    public Action<int>?                           SelectTabFunc           { get; set; }
    public Func<string, string, Task>?            ShowErrorFunc           { get; set; }
    public Func<string, string, Task<bool>>?      ShowConfirmFunc         { get; set; }
    public Func<Task<string?>>?                   BrowseUpdateFolderFunc  { get; set; }
    public Func<string, string, Task>?            ShowInfoFunc            { get; set; }

    // ── Update checker ────────────────────────────────────────────────────────
    [ObservableProperty] private bool   _isUpdateAvailable   = false;
    [ObservableProperty] private bool   _isUpdateDownloading = false;
    [ObservableProperty] private bool   _isUpdateDone        = false;
    [ObservableProperty] private string _updateVersionText   = "";
    [ObservableProperty] private string _updateStatusText    = "";
    [ObservableProperty] private double _updateProgress      = 0;

    private UpdateInfo? _pendingUpdate;

    public async Task RunStartupUpdateCheckAsync()
    {
        var info = await UpdateService.CheckAsync(Version);
        if (info is null) return;
        _pendingUpdate      = info;
        UpdateVersionText   = $"TransitLab v{info.Version} is available";
        IsUpdateAvailable   = true;
    }

    [RelayCommand]
    private void SkipUpdate() => IsUpdateAvailable = false;

    [RelayCommand]
    private async Task DownloadUpdate()
    {
        if (_pendingUpdate is null || BrowseUpdateFolderFunc is null) return;

        var folder = await BrowseUpdateFolderFunc();
        if (folder is null) return;

        var destPath = System.IO.Path.Combine(folder, _pendingUpdate.AssetName);
        IsUpdateAvailable   = false;
        IsUpdateDownloading = true;
        UpdateStatusText    = "Downloading…";

        try
        {
            var progress = new Progress<(long done, long total)>(t =>
            {
                if (t.total > 0)
                {
                    UpdateProgress   = (double)t.done / t.total * 100;
                    UpdateStatusText = $"Downloading… {t.done / 1_048_576.0:F1} MB / {t.total / 1_048_576.0:F1} MB";
                }
            });
            await ExoticInstallService.DownloadFileAsync(_pendingUpdate.DownloadUrl, destPath, progress, default);
            UpdateStatusText    = $"Downloaded to {destPath} — extract to update.";
            IsUpdateDownloading = false;
            IsUpdateDone        = true;
        }
        catch (Exception ex)
        {
            UpdateStatusText    = $"Download failed: {ex.Message}";
            IsUpdateDownloading = false;
            IsUpdateDone        = true;
        }
    }

    [RelayCommand]
    private void DismissUpdateDone() => IsUpdateDone = false;

    [RelayCommand]
    private async Task CheckForUpdates()
    {
        var info = await UpdateService.CheckAsync(Version);
        if (info is null)
        {
            if (ShowInfoFunc is not null)
                await ShowInfoFunc("Check for Updates", "TransitLab is up to date.");
            return;
        }
        _pendingUpdate    = info;
        UpdateVersionText = $"TransitLab v{info.Version} is available";
        IsUpdateDone      = false;
        IsUpdateAvailable = true;
    }

    // Set true while RunAutomationSequenceAsync is running — suppresses all confirm dialogs
    private bool _isAutomationRunning = false;

    // Recent Sessions
    public ObservableCollection<SessionRecord> RecentSessions { get; } = new();

    // EXOTIC process state
    [ObservableProperty] private bool   isExoticRunning = false;
    [ObservableProperty] private string exoticStatusText = "";

    // True while EXOTIC is not running AND FITS header has been read (or no FitsDir set yet)
    public bool CanSaveAndRun => !IsExoticRunning && !Observation.FitsDirNeedsHeaderRead;

    partial void OnIsExoticRunningChanged(bool value) => OnPropertyChanged(nameof(CanSaveAndRun));

    private CancellationTokenSource? _exoticCts;
    private Process?                  _exoticProcess;
    private ExclusionSession?         _activeExclSession;

    private AppConfig _cfg;

    public MainWindowViewModel()
    {
        _cfg = ConfigService.Load();
        Observation.EquipmentTarget    = EquipmentTarget;
        Observation.ConfigSaveCallback = SaveObservationLists;
        FrameAnalysis.FitsDirFunc      = () => Observation.FitsDir;
        FrameAnalysis.TargetNameFunc   = () =>
        {
            var hn = EquipmentTarget.HostStarName;
            return string.IsNullOrWhiteSpace(hn) ? EquipmentTarget.PlanetName : hn;
        };
        // Push target name into the VSP star field whenever it becomes available
        EquipmentTarget.TargetNameChanged = name =>
        {
            if (!string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(FrameAnalysis.VspStar))
                FrameAnalysis.VspStar = name;
        };

        // Wire transit visualizer to equipment target
        Transit.Connect(EquipmentTarget);

        // Wire sound notification when light curve results appear
        Results.LightCurveReadySound = () => { if (CompletionSound != "None") PlaySoundAction?.Invoke(CompletionSound, CompletionSoundPath); };

        // Wire operation status sounds (NEA fetch, plate solve, AAVSO comp fetch)
        EquipmentTarget.PlayStatusSoundAction = success => { if (StatusAlertsEnabled) PlayStatusSoundAction?.Invoke(success); };
        Observation.AutoScanAndGetFitsFunc = FrameAnalysis.ScanExcludeAndGetFirstAsync;
        FrameAnalysis.EquipmentTarget = EquipmentTarget;
        FrameAnalysis.DarksDirFunc = () => Observation.DarksDir;
        EquipmentTarget.FitsDirFunc   = () => Observation.FitsDir;
        EquipmentTarget.GetFirstNonExcludedFitsFunc = FrameAnalysis.ScanExcludeAndGetFirstAsync;
        MObs.UseDataCallback = (scienceDir, darksDir) =>
        {
            Observation.FitsDir  = scienceDir;
            Observation.DarksDir = darksDir;
            Observation.SaveDir  = scienceDir;  // override parent-default; save plots into the science folder
            SelectTabFunc?.Invoke(0); // stay on Data=0
        };

        // Propagate FitsDirNeedsHeaderRead changes to CanSaveAndRun
        Observation.FitsDirNeedsHeaderReadChanged = () => OnPropertyChanged(nameof(CanSaveAndRun));

        // Clear all session fields when the user browses a new FITS directory
        Observation.ClearSessionFieldsCallback = () =>
        {
            Observation.ObsDate   = "";
            Observation.Latitude  = "";
            Observation.Longitude = "";
            Observation.Elevation = "";
            EquipmentTarget.PlanetName         = "";
            EquipmentTarget.HostStarName       = "";
            EquipmentTarget.TargetRa           = "";
            EquipmentTarget.TargetDec          = "";
            EquipmentTarget.RpRs               = "";
            EquipmentTarget.RpRsUnc            = "";
            EquipmentTarget.ARs                = "";
            EquipmentTarget.ARsUnc             = "";
            EquipmentTarget.Inclination        = "";
            EquipmentTarget.InclinationUnc     = "";
            EquipmentTarget.Eccentricity       = "";
            EquipmentTarget.ArgPeriastron      = "";
            EquipmentTarget.OrbitalPeriod      = "";
            EquipmentTarget.OrbitalPeriodUnc   = "";
            EquipmentTarget.MidTransitTime     = "";
            EquipmentTarget.MidTransitTimeUnc  = "";
            EquipmentTarget.Teff               = "";
            EquipmentTarget.TeffPlus           = "";
            EquipmentTarget.TeffMinus          = "";
            EquipmentTarget.Logg               = "";
            EquipmentTarget.LoggPlus           = "";
            EquipmentTarget.LoggMinus          = "";
            EquipmentTarget.Metallicity        = "";
            EquipmentTarget.MetallicityPlus    = "";
            EquipmentTarget.MetallicityMinus   = "";
            EquipmentTarget.StarDistance       = "";
            EquipmentTarget.PmRa               = "";
            EquipmentTarget.PmDec              = "";
            EquipmentTarget.TargetXY           = "";
            EquipmentTarget.CompXY             = "";
        };
        ApplyConfig(_cfg);
        RefreshRecentSessions();

        // Wire Results callbacks
        Results.ObscodeFunc          = () => Observation.AavsoCode;
        Results.RecordSubmissionFunc = RecordSubmissionFromFinalParams;

        // Load and wire history persistence
        foreach (var h in HistoryService.Load()) History.Entries.Add(h);
        History.SaveHistoryFunc = entries => HistoryService.Save(entries);

        // Wire Setup wizard — when EXOTIC is found/installed, store the runtime paths
        ExoticSetup.PythonFoundCallback = pythonExe =>
        {
            _cfg.PythonExePath        = pythonExe;
            Observation.PythonExePath = pythonExe;
            ConfigService.Save(_cfg);
        };
        ExoticSetup.ExoticExeFoundCallback = exe =>
        {
            _cfg.ExoticExePath        = exe;
            Observation.ExoticExePath = exe;
            ConfigService.Save(_cfg);
        };

        // Wire automation sequence callback
        Automation.RunSequenceFunc = RunAutomationSequenceAsync;
    }

    // ── Config round-trip ─────────────────────────────────────────────────────

    /// <summary>
    /// Updates plate solver settings in the live ViewModels and optionally persists to config.json.
    /// Called from ApplyConfig (save=false) and from the Plate Solve Setup dialog (save=true).
    /// </summary>
    public void ApplyPlateSolverSettings(
        string solver, string astapPath, string catalogDir, int searchRadius, int downsample,
        bool solveAllFrames = false, bool save = true)
    {
        _cfg.PlateSolver        = solver;
        _cfg.AstapExePath       = astapPath;
        _cfg.AstapCatalogDir    = catalogDir;
        _cfg.AstapSearchRadius  = searchRadius;
        _cfg.AstapDownsample    = downsample;
        _cfg.AstapSolveAllFrames = solveAllFrames;

        var config = new PlateSolveService.SolverConfig(solver, astapPath, catalogDir, searchRadius, downsample, solveAllFrames);
        EquipmentTarget.PlateSolverConfig  = config;
        EquipmentTarget.ActiveSolverLabel  =
            solver == "ASTAP" ? "Solver: ASTAP" : "Solver: Astrometry.net";

        if (save) ConfigService.Save(_cfg);
    }

    /// <summary>Exposes current solver config for the Plate Solve Setup dialog.</summary>
    public (string Solver, string AstapPath, string CatalogDir, int SearchRadius, int Downsample, bool SolveAllFrames) GetPlateSolverConfig()
        => (_cfg.PlateSolver, _cfg.AstapExePath, _cfg.AstapCatalogDir, _cfg.AstapSearchRadius, _cfg.AstapDownsample, _cfg.AstapSolveAllFrames);

    /// <summary>Persists the live Automation settings to config.json. Called from the Settings Save callback.</summary>
    public void SaveAutomationConfig()
    {
        _cfg.AutoMonitorFolder    = Automation.MonitorFolder;
        _cfg.AutoStartTime        = Automation.StartTime;
        _cfg.AutoDurationMinutes  = Automation.DurationMinutes;
        ConfigService.Save(_cfg);
    }

    // ── Automation sequence ───────────────────────────────────────────────────

    /// <summary>
    /// Full automation sequence triggered when the monitor folder reaches file stability:
    /// clear darks → disable dark subtract → scan frames → auto-exclude flagged →
    /// read FITS header (→ plate solve → NEA → AAVSO comps) → auto-select target →
    /// auto-select comps → Save &amp; Run EXOTIC.
    /// </summary>
    public async Task RunAutomationSequenceAsync()
    {
        _isAutomationRunning = true;
        try
        {
        await RunAutomationSequenceInternalAsync();
        }
        finally
        {
            _isAutomationRunning = false;
        }
    }

    private async Task RunAutomationSequenceInternalAsync()
    {
        SessionLogService.Write("[Automation] ── Sequence start ──");

        // 1. Set FITS directory from the monitored folder
        if (!string.IsNullOrEmpty(Automation.MonitorFolder))
        {
            Observation.FitsDir = Automation.MonitorFolder;
            SessionLogService.Write($"[Automation] FitsDir set to monitor folder: {Automation.MonitorFolder}");
        }

        // 2. Clear darks directory (images are pre-calibrated by FITS Calibrator)
        Observation.DarksDir = "";
        FrameAnalysis.IsDarkSubtract = false;
        SessionLogService.Write("[Automation] Cleared DarksDir, disabled dark subtract");

        // Wait for any scan triggered by the IsDarkSubtract toggle to start and finish
        // (HandleDarkSubtractToggledAsync fires fire-and-forget and calls ScanFiles when
        // Frames.Count > 0, setting IsScanRunning=true before the explicit scan below runs)
        await Task.Delay(200);
        while (FrameAnalysis.IsScanRunning)
            await Task.Delay(100);

        // 3. Scan frames
        Automation.AutomationStatus = "Running sequence: scanning frames…";
        SessionLogService.Write("[Automation] Scanning frames…");
        await FrameAnalysis.ScanFilesCommand.ExecuteAsync(null);

        // Wait for Dispatcher.Post (Frames.Add + IsScanRunning=false) to flush on the UI thread
        while (FrameAnalysis.IsScanRunning)
            await Task.Delay(100);

        SessionLogService.Write($"[Automation] Scan complete — {FrameAnalysis.Frames.Count} frames found");

        // 4. Auto-exclude flagged frames
        FrameAnalysis.AutoExcludeFlaggedCommand.Execute(null);
        SessionLogService.Write($"[Automation] Auto-excluded flagged frames — {FrameAnalysis.FlaggedStatus}");

        int nonExcluded = FrameAnalysis.Frames.Count(f => !f.IsExcluded);
        SessionLogService.Write($"[Automation] {nonExcluded} non-excluded frames available for reduction");
        if (nonExcluded == 0)
        {
            Automation.AutomationStatus = "⚠  Sequence aborted — no usable frames after exclusion (check Flag Sigma setting)";
            SessionLogService.Write("[Automation] Sequence aborted — no non-excluded frames");
            return;
        }

        // 5. Read FITS header — this fires plate solve and NEA as background tasks; returns immediately
        Automation.AutomationStatus = "Running sequence: reading FITS header…";
        SessionLogService.Write("[Automation] Reading FITS header…");
        await Observation.ReadFitsHeaderCommand.ExecuteAsync(null);

        // 6. Wait for plate solve AND NEA to both complete — IsAutoTargetEnabled is the signal
        //    (only becomes true when _wcsReady AND TargetRa/Dec are populated)
        Automation.AutomationStatus = "Running sequence: waiting for plate solve + NEA…";
        SessionLogService.Write("[Automation] Waiting for plate solve and NEA to complete…");

        const int plateSolveTimeoutSec = 300;  // 5-minute ceiling for slow solvers
        var psDeadline = DateTime.Now.AddSeconds(plateSolveTimeoutSec);
        while (!EquipmentTarget.IsAutoTargetEnabled && DateTime.Now < psDeadline)
        {
            SessionLogService.Write($"[Automation] … plate solve/NEA still running — PS: {EquipmentTarget.PlateSolveStatus}  |  target: {EquipmentTarget.AutoTargetStatus}");
            Automation.AutomationStatus = $"Running sequence: {EquipmentTarget.PlateSolveStatus}";
            await Task.Delay(2000);
        }

        if (!EquipmentTarget.IsAutoTargetEnabled)
        {
            Automation.AutomationStatus = $"⚠  Sequence aborted — plate solve or NEA did not complete in {plateSolveTimeoutSec / 60} min  ({EquipmentTarget.AutoTargetStatus})";
            SessionLogService.Write($"[Automation] Sequence aborted — timeout waiting for IsAutoTargetEnabled. PS: {EquipmentTarget.PlateSolveStatus}  NEA: {EquipmentTarget.AutoTargetStatus}");
            return;
        }

        SessionLogService.Write($"[Automation] Plate solve + NEA complete — {EquipmentTarget.AutoTargetStatus}");

        // 7. Auto-select target star using WCS
        Automation.AutomationStatus = "Running sequence: auto-selecting target star…";
        SessionLogService.Write("[Automation] Auto-selecting target star…");
        await EquipmentTarget.AutoSelectTargetCommand.ExecuteAsync(null);
        SessionLogService.Write($"[Automation] Target select result: {EquipmentTarget.AutoTargetStatus}");

        if (string.IsNullOrEmpty(EquipmentTarget.TargetXY))
        {
            Automation.AutomationStatus = $"⚠  Sequence aborted — could not auto-select target star ({EquipmentTarget.AutoTargetStatus})";
            SessionLogService.Write("[Automation] Sequence aborted — TargetXY empty after auto-select");
            return;
        }

        // 8. Wait for AAVSO comp fetch to finish (AavsoCompStatus stops starting with "⟳")
        Automation.AutomationStatus = "Running sequence: waiting for AAVSO comp fetch…";
        SessionLogService.Write($"[Automation] Waiting for AAVSO comp fetch — current: {EquipmentTarget.AavsoCompStatus}");

        var compDeadline = DateTime.Now.AddSeconds(90);  // covers first attempt (30s) + 10s retry delay + second attempt (30s)
        while (EquipmentTarget.AavsoCompStatus.StartsWith("⟳") && DateTime.Now < compDeadline)
            await Task.Delay(1000);

        SessionLogService.Write($"[Automation] AAVSO comp status: {EquipmentTarget.AavsoCompStatus}");

        // 9. Auto-select comp stars
        if (EquipmentTarget.IsAutoCompsEnabled)
        {
            Automation.AutomationStatus = "Running sequence: auto-selecting comp stars…";
            SessionLogService.Write("[Automation] Auto-selecting comp stars…");
            await EquipmentTarget.AutoSelectCompsCommand.ExecuteAsync(null);
            SessionLogService.Write($"[Automation] Comp select result: {EquipmentTarget.AavsoCompStatus}");
        }
        else
        {
            SessionLogService.Write($"[Automation] Skipping auto-comps — not available: {EquipmentTarget.AavsoCompStatus}");
        }

        // 10. Save & Run EXOTIC
        Automation.AutomationStatus = "Running sequence: launching EXOTIC…";
        SessionLogService.Write("[Automation] Triggering Save & Run EXOTIC…");
        await SaveAndRunCommand.ExecuteAsync(null);

        SessionLogService.Write("[Automation] ── Sequence complete ──");
    }

    private void SaveObservationLists()
    {
        _cfg.AavsoCode      = [.. Observation.LiveAavsoCodes];
        _cfg.SecondaryCodes = [.. Observation.LiveSecondaryCodes];
        _cfg.Observatories  = [.. Observation.LiveObservatories];
        ConfigService.Save(_cfg);
    }

    private void ApplyConfig(AppConfig cfg)
    {
        // Observer code collections
        Observation.LoadFromConfig(cfg.AavsoCode, cfg.SecondaryCodes, cfg.Observatories);

        // EXOTIC runtime paths (used by reduction launch and plate solve)
        Observation.PythonExePath = cfg.PythonExePath;
        Observation.ExoticExePath = cfg.ExoticExePath;

        // Plate solver config
        ApplyPlateSolverSettings(cfg.PlateSolver, cfg.AstapExePath, cfg.AstapCatalogDir, cfg.AstapSearchRadius, cfg.AstapDownsample, cfg.AstapSolveAllFrames, save: false);

        // Directories
        // SaveDirUserSet intentionally not restored — always start fresh so Save Plots
        // auto-follows the FITS directory parent at the start of each session.
        Observation.FitsDir        = cfg.LastUi.FitsDir;
        Observation.SaveDir        = cfg.LastUi.SaveDir;
        Observation.DarksDir  = cfg.LastUi.DarksDir;
        Observation.FlatsDir  = cfg.LastUi.FlatsDir;
        Observation.BiasDir   = cfg.LastUi.BiasDir;

        // Observer identity
        Observation.AavsoCode = cfg.LastUi.AavsoCode;

        // Equipment defaults
        EquipmentTarget.CameraType = cfg.LastUi.CameraType;
        EquipmentTarget.Binning    = cfg.LastUi.Binning;
        EquipmentTarget.Filter     = cfg.LastUi.Filter;
        EquipmentTarget.FilterMin  = cfg.LastUi.FilterMin;
        EquipmentTarget.FilterMax  = cfg.LastUi.FilterMax;
        EquipmentTarget.PixelScale = cfg.LastUi.PixelScale;
        EquipmentTarget.Notes      = cfg.LastUi.Notes;

        // Session fields (ObsDate, lat/lon/elev, planet params, star coords) are not
        // restored — the user must re-read the FITS header to populate them each session.

        // MObs
        if (!string.IsNullOrEmpty(cfg.MobsDownloadDir))
            MObs.DownloadFolder = cfg.MobsDownloadDir;
        MObs.IsDefaultFolder = cfg.MobsIsDefaultFolder;

        // Frame Analysis
        FrameAnalysis.FlagSigma = (decimal)cfg.FlagSigma;

        // Automation
        Automation.MonitorFolder    = cfg.AutoMonitorFolder;
        Automation.StartTime        = cfg.AutoStartTime;
        Automation.DurationMinutes  = cfg.AutoDurationMinutes;

        // Results / AAVSO credentials
        Results.AavsoUsername     = cfg.AavsoUsername;
        Results.SavePassword      = cfg.SavePassword;
        Results.AavsoPassword     = cfg.SavePassword ? cfg.AavsoPassword : "";
        Results.AutoAnswerPrompts = cfg.AutoAnswerPrompts;
    }

    public void SaveOnExit()
    {
        SnapshotToConfig();
        ConfigService.Save(_cfg);
    }

    private void SnapshotToConfig()
    {
        // Directories and stable equipment defaults are preserved across sessions.
        // All session-specific fields (date, lat/lon, planet params, star coords) are
        // intentionally NOT saved so the user must re-read the FITS header each session.
        _cfg.LastUi = new LastUi
        {
            // Directories
            FitsDir        = Observation.FitsDir,
            SaveDir        = Observation.SaveDir,
            SaveDirUserSet = Observation.SaveDirUserSet,
            DarksDir  = Observation.DarksDir,
            FlatsDir  = Observation.FlatsDir,
            BiasDir   = Observation.BiasDir,

            // Observer identity
            AavsoCode = Observation.AavsoCode,

            // Equipment defaults (stable between sessions)
            CameraType = EquipmentTarget.CameraType,
            Binning    = EquipmentTarget.Binning,
            Filter     = EquipmentTarget.Filter,
            FilterMin  = EquipmentTarget.FilterMin,
            FilterMax  = EquipmentTarget.FilterMax,
            PixelScale = EquipmentTarget.PixelScale,
            Notes      = EquipmentTarget.Notes,
        };

        _cfg.AavsoCode      = [.. Observation.LiveAavsoCodes];
        _cfg.SecondaryCodes = [.. Observation.LiveSecondaryCodes];
        _cfg.Observatories  = [.. Observation.LiveObservatories];
        _cfg.MobsDownloadDir       = MObs.DownloadFolder;
        _cfg.MobsIsDefaultFolder   = MObs.IsDefaultFolder;
        _cfg.FlagSigma             = (double)FrameAnalysis.FlagSigma;
        _cfg.AutoMonitorFolder    = Automation.MonitorFolder;
        _cfg.AutoStartTime        = Automation.StartTime;
        _cfg.AutoDurationMinutes  = Automation.DurationMinutes;
        _cfg.AavsoUsername         = Results.AavsoUsername;
        _cfg.SavePassword          = Results.SavePassword;
        _cfg.AavsoPassword         = Results.SavePassword ? Results.AavsoPassword : "";
        _cfg.AutoAnswerPrompts     = Results.AutoAnswerPrompts;
    }

    // ── inits.json builder ────────────────────────────────────────────────────

    private JsonObject BuildInits()
    {
        static JsonNode? N(string s)  => string.IsNullOrWhiteSpace(s) ? null : JsonValue.Create(s);
        static JsonNode? NF(string s) => double.TryParse(s, out var d) ? JsonValue.Create(d) : null;
        // EXOTIC expects RA in decimal hours in inits.json, not decimal degrees.
        // It multiplies the value by 15 internally to get degrees, so we must divide here.
        static JsonNode? NRA(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (double.TryParse(s, System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture, out var deg))
                return JsonValue.Create((deg / 15.0).ToString(
                    "G10", System.Globalization.CultureInfo.InvariantCulture));
            return JsonValue.Create(s); // pass through if not parseable
        }

        var et = EquipmentTarget;
        var ob = Observation;

        var pp = new JsonObject
        {
            ["Target Star RA"]  = NRA(et.TargetRa),
            ["Target Star Dec"] = N(et.TargetDec),
            ["Planet Name"]     = N(et.PlanetName),
            ["Host Star Name"]  = N(et.HostStarName),
            ["Orbital Period (days)"]           = NF(et.OrbitalPeriod),
            ["Orbital Period Uncertainty"]      = NF(et.OrbitalPeriodUnc),
            ["Published Mid-Transit Time (BJD-UTC)"]       = NF(et.MidTransitTime),
            ["Mid-Transit Time Uncertainty"]    = NF(et.MidTransitTimeUnc),
            ["Ratio of Planet to Stellar Radius (Rp/Rs)"]             = NF(et.RpRs),
            ["Ratio of Planet to Stellar Radius (Rp/Rs) Uncertainty"] = NF(et.RpRsUnc),
            ["Ratio of Distance to Stellar Radius (a/Rs)"]            = NF(et.ARs),
            ["Ratio of Distance to Stellar Radius (a/Rs) Uncertainty"]= string.IsNullOrWhiteSpace(et.ARsUnc) ? JsonValue.Create(0.1) : NF(et.ARsUnc),
            ["Orbital Inclination (deg)"]                             = NF(et.Inclination),
            ["Orbital Inclination (deg) Uncertainty"]                 = NF(et.InclinationUnc),
            ["Orbital Eccentricity (0 if null)"]                      = NF(et.Eccentricity),
            ["Argument of Periastron (deg)"]  = NF(string.IsNullOrWhiteSpace(et.ArgPeriastron) ? "0" : et.ArgPeriastron),
            ["Star Effective Temperature (K)"]                        = NF(et.Teff),
            ["Star Effective Temperature (+) Uncertainty"]            = NF(et.TeffPlus),
            ["Star Effective Temperature (-) Uncertainty"]            = NF(et.TeffMinus),
            ["Star Metallicity ([FE/H])"]                             = NF(et.Metallicity),
            ["Star Metallicity (+) Uncertainty"]                      = NF(et.MetallicityPlus),
            ["Star Metallicity (-) Uncertainty"]                      = NF(et.MetallicityMinus),
            ["Star Surface Gravity (log(g))"]                         = NF(et.Logg),
            ["Star Surface Gravity (+) Uncertainty"]                  = NF(et.LoggPlus),
            ["Star Surface Gravity (-) Uncertainty"]                  = NF(et.LoggMinus),
            ["Star Distance (pc)"]                                    = NF(et.StarDistance),
            ["Star Proper Motion RA (mas/yr)"]                        = NF(et.PmRa),
            ["Star Proper Motion DEC (mas/yr)"]                       = NF(et.PmDec),
        };

        var ui = new JsonObject
        {
            ["Directory with FITS files"]  = N(ob.FitsDir),
            ["Directory to Save Plots"]    = N(ob.SaveDir),
            ["AAVSO Observer Code (blank if none)"]      = N(ob.AavsoCode),
            ["Secondary Observer Codes (blank if none)"] = N(ob.SecondaryCode),
            ["Observation date"]           = N(ob.ObsDate),
            ["Obs. Latitude"]              = N(ob.Latitude),
            ["Obs. Longitude"]             = N(ob.Longitude),
            ["Obs. Elevation (meters; Note: leave blank if unknown)"] = NF(ob.Elevation),
            ["Camera Type (CCD or DSLR)"]  = N(et.CameraType),
            ["Pixel Binning"]              = N(et.Binning),
            ["Filter Name (aavso.org/filters)"] = N(et.Filter),
            ["Observing Notes"]            = N(et.Notes),
            ["Plate Solution? (y/n)"]      = JsonValue.Create("n"),
            ["Add Comparison Stars from AAVSO? (y/n)"] = JsonValue.Create("y"),
            ["Target Star X & Y Pixel"]    = N(et.TargetXY),
            ["Comparison Star(s) X & Y Pixel"] = N(et.CompXY),
            ["Demosaic Format"]            = null,
            ["Demosaic Output"]            = null,
            ["Directory of Flats"]         = N(ob.FlatsDir),
            ["Directory of Darks"]         = N(ob.DarksDir),
            ["Directory of Biases"]        = N(ob.BiasDir),
        };

        var optional = new JsonObject
        {
            ["Filter Minimum Wavelength (nm)"] = NF(et.FilterMin),
            ["Filter Maximum Wavelength (nm)"] = NF(et.FilterMax),
            ["Pixel Scale (Ex: 5.21 arcsecs/pixel)"] = N(et.PixelScale),
        };

        return new JsonObject
        {
            ["user_info"]            = ui,
            ["planetary_parameters"] = pp,
            ["optional_info"]        = optional,
        };
    }

    private string InitsDefaultPath()
    {
        var saveDir = Observation.SaveDir;
        if (!string.IsNullOrWhiteSpace(saveDir) && Directory.Exists(saveDir))
            return saveDir;
        if (!string.IsNullOrWhiteSpace(_cfg.LastInitsDir) && Directory.Exists(_cfg.LastInitsDir))
            return _cfg.LastInitsDir;
        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    private void WriteInits(string path)
    {
        var opts = new JsonSerializerOptions { WriteIndented = true };
        var json = BuildInits().ToJsonString(opts);
        File.WriteAllText(path, json);
        _cfg.LastInitsDir = Path.GetDirectoryName(path) ?? _cfg.LastInitsDir;
        SnapshotToConfig();
        ConfigService.Save(_cfg);
    }

    private void ApplyInits(JsonObject d)
    {
        var ui = d["user_info"]            as JsonObject ?? new JsonObject();
        var pp = d["planetary_parameters"] as JsonObject ?? new JsonObject();
        var op = d["optional_info"]        as JsonObject ?? new JsonObject();

        static string S(JsonNode? n) => n?.GetValue<string?>() ?? "";
        static string SF(JsonNode? n)
        {
            if (n is null) return "";
            try { return n.GetValue<double>().ToString(); } catch { return ""; }
        }

        Observation.FitsDir       = S(ui["Directory with FITS files"]);
        Observation.SaveDir       = S(ui["Directory to Save Plots"]);
        Observation.DarksDir      = S(ui["Directory of Darks"]);
        Observation.FlatsDir      = S(ui["Directory of Flats"]);
        Observation.BiasDir       = S(ui["Directory of Biases"]);
        Observation.AavsoCode     = S(ui["AAVSO Observer Code (blank if none)"]);
        Observation.SecondaryCode = S(ui["Secondary Observer Codes (blank if none)"]);
        Observation.ObsDate       = S(ui["Observation date"]);
        Observation.Latitude      = SF(ui["Obs. Latitude"]);
        Observation.Longitude     = SF(ui["Obs. Longitude"]);
        Observation.Elevation     = SF(ui["Obs. Elevation (meters; Note: leave blank if unknown)"]);

        EquipmentTarget.CameraType  = S(ui["Camera Type (CCD or DSLR)"]);
        EquipmentTarget.Binning     = S(ui["Pixel Binning"]);
        EquipmentTarget.Filter      = S(ui["Filter Name (aavso.org/filters)"]);
        EquipmentTarget.Notes       = S(ui["Observing Notes"]);
        EquipmentTarget.TargetXY    = S(ui["Target Star X & Y Pixel"]);
        EquipmentTarget.CompXY      = S(ui["Comparison Star(s) X & Y Pixel"]);
        EquipmentTarget.FilterMin   = SF(op["Filter Minimum Wavelength (nm)"]);
        EquipmentTarget.FilterMax   = SF(op["Filter Maximum Wavelength (nm)"]);
        EquipmentTarget.PixelScale  = S(op["Pixel Scale (Ex: 5.21 arcsecs/pixel)"]);

        EquipmentTarget.PlanetName       = S(pp["Planet Name"]);
        EquipmentTarget.TargetRa         = SF(pp["Target Star RA"]);
        EquipmentTarget.TargetDec        = SF(pp["Target Star Dec"]);
        EquipmentTarget.HostStarName     = S(pp["Host Star Name"]);
        EquipmentTarget.OrbitalPeriod    = SF(pp["Orbital Period (days)"]);
        EquipmentTarget.OrbitalPeriodUnc = SF(pp["Orbital Period Uncertainty"]);
        EquipmentTarget.MidTransitTime   = SF(pp["Published Mid-Transit Time (BJD-UTC)"]);
        EquipmentTarget.MidTransitTimeUnc= SF(pp["Mid-Transit Time Uncertainty"]);
        EquipmentTarget.RpRs             = SF(pp["Ratio of Planet to Stellar Radius (Rp/Rs)"]);
        EquipmentTarget.RpRsUnc          = SF(pp["Ratio of Planet to Stellar Radius (Rp/Rs) Uncertainty"]);
        EquipmentTarget.ARs              = SF(pp["Ratio of Distance to Stellar Radius (a/Rs)"]);
        EquipmentTarget.ARsUnc           = SF(pp["Ratio of Distance to Stellar Radius (a/Rs) Uncertainty"]);
        EquipmentTarget.Inclination      = SF(pp["Orbital Inclination (deg)"]);
        EquipmentTarget.InclinationUnc   = SF(pp["Orbital Inclination (deg) Uncertainty"]);
        EquipmentTarget.Eccentricity     = SF(pp["Orbital Eccentricity (0 if null)"]);
        EquipmentTarget.ArgPeriastron    = SF(pp["Argument of Periastron (deg)"]);
        EquipmentTarget.Teff             = SF(pp["Star Effective Temperature (K)"]);
        EquipmentTarget.TeffPlus         = SF(pp["Star Effective Temperature (+) Uncertainty"]);
        EquipmentTarget.TeffMinus        = SF(pp["Star Effective Temperature (-) Uncertainty"]);
        EquipmentTarget.Metallicity      = SF(pp["Star Metallicity ([FE/H])"]);
        EquipmentTarget.MetallicityPlus  = SF(pp["Star Metallicity (+) Uncertainty"]);
        EquipmentTarget.MetallicityMinus = SF(pp["Star Metallicity (-) Uncertainty"]);
        EquipmentTarget.Logg             = SF(pp["Star Surface Gravity (log(g))"]);
        EquipmentTarget.LoggPlus         = SF(pp["Star Surface Gravity (+) Uncertainty"]);
        EquipmentTarget.LoggMinus        = SF(pp["Star Surface Gravity (-) Uncertainty"]);
        EquipmentTarget.StarDistance     = SF(pp["Star Distance (pc)"]);
        EquipmentTarget.PmRa             = SF(pp["Star Proper Motion RA (mas/yr)"]);
        EquipmentTarget.PmDec            = SF(pp["Star Proper Motion DEC (mas/yr)"]);
    }

    // ── Toolbar commands ──────────────────────────────────────────────────────

    [RelayCommand]
    private async Task SaveAndRun()
    {
        if (IsExoticRunning) return;

        if (Observation.FitsDirNeedsHeaderRead)
        {
            if (ShowErrorFunc is not null)
                await ShowErrorFunc("Read FITS Header Required",
                    "The FITS Files Directory has changed since the last header read.\n\n" +
                    "Please click 'Read FITS Header' on the Parameters tab to update observation data and trigger a plate solve before running.");
            return;
        }

        // Switch to Results tab immediately so the user lands there for validation messages and the live log
        SelectTabFunc?.Invoke(3);

        // ── Validate all required fields before writing inits.json ───────────────
        var missing = new System.Text.StringBuilder();
        void Require(string label, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                missing.AppendLine($"  • {label}");
        }

        var ob = Observation;
        var et = EquipmentTarget;

        // Observation tab
        Require("FITS Files Directory",              ob.FitsDir);
        Require("Save Plots Directory",              ob.SaveDir);
        Require("Observation Date",                  ob.ObsDate);
        Require("Observatory Latitude",              ob.Latitude);
        Require("Observatory Longitude",             ob.Longitude);

        // Equipment & Target tab
        Require("Camera Type",                       et.CameraType);
        Require("Pixel Binning",                     et.Binning);
        Require("Filter",                            et.Filter);
        Require("Filter Min Wavelength (nm)",        et.FilterMin);
        Require("Filter Max Wavelength (nm)",        et.FilterMax);
        Require("Pixel Scale (arcsec/px)",           et.PixelScale);
        Require("Target Star X,Y  (select on Image Analysis tab)",        et.TargetXY);
        Require("Comparison Star(s) X,Y  (select on Image Analysis tab)", et.CompXY);

        // Planet / stellar parameters
        Require("Planet Name",                       et.PlanetName);
        Require("Target Star RA",                    et.TargetRa);
        Require("Target Star Dec",                   et.TargetDec);
        Require("Host Star Name",                    et.HostStarName);
        Require("Orbital Period",                    et.OrbitalPeriod);
        Require("Mid-Transit Time",                  et.MidTransitTime);
        Require("Rp/Rs",                             et.RpRs);
        Require("a/Rs",                              et.ARs);
        Require("Orbital Inclination",               et.Inclination);
        Require("Star Effective Temperature (Teff)", et.Teff);
        Require("Star Metallicity",                  et.Metallicity);
        Require("Star Surface Gravity (log g)",      et.Logg);

        if (missing.Length > 0)
        {
            var missingFlat = missing.ToString().Trim().Replace(Environment.NewLine, ", ").TrimStart(',', ' ');
            SessionLogService.Write($"[Run] Validation failed — missing fields: {missingFlat}");
            if (ShowErrorFunc is not null)
                await ShowErrorFunc("Cannot Run EXOTIC",
                    "The following required fields are empty:\n\n" + missing.ToString().TrimEnd() +
                    "\n\nTip: Read FITS Header (Parameters tab) populates observation data and triggers a plate solve. Fetch from NEA populates planet and stellar parameters.");
            return;
        }

        // Rp/Rs sanity check — outside 0.01–0.35 almost certainly indicates a data error
        if (double.TryParse(et.RpRs, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double rprsCheck) &&
            (rprsCheck < 0.01 || rprsCheck > 0.35))
        {
            SessionLogService.Write($"[Run] Validation failed — suspicious Rp/Rs = {rprsCheck:G6} (expected 0.01–0.35)");
            if (ShowErrorFunc is not null)
                await ShowErrorFunc("Suspicious Rp/Rs Value",
                    $"Rp/Rs = {rprsCheck:G6} is outside the expected range (0.01 – 0.35) for a transiting exoplanet.\n\n" +
                    "This value will likely produce an invalid result. Please verify and correct the Rp/Rs field before running.");
            return;
        }

        var ts   = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var dir  = InitsDefaultPath();
        var path = Path.Combine(dir, $"inits_{ts}.json");
        WriteInits(path);
        RecordSession(path);
        SessionLogService.Write($"[Run] Starting EXOTIC — planet: \"{et.PlanetName}\", FITS: {ob.FitsDir}, inits: {path}");

        // ── Confirm excluded frames with the user ─────────────────────────────
        var excludedInfo = FrameAnalysis.GetExcludedFrameInfo();
        if (excludedInfo.Count > 0 && ShowConfirmFunc is not null && !_isAutomationRunning)
        {
            var list = string.Join("\n", excludedInfo.Select(x => $"  Frame {x.Number}:  {x.Filename}"));
            var proceed = await ShowConfirmFunc("Excluded Frames",
                $"{excludedInfo.Count} frame{(excludedInfo.Count == 1 ? "" : "s")} will be temporarily moved out of the FITS directory before EXOTIC runs:\n\n{list}");
            if (!proceed) return;
        }
        else if (excludedInfo.Count > 0 && _isAutomationRunning)
        {
            SessionLogService.Write($"[Automation] Auto-accepting excluded frames confirmation ({excludedInfo.Count} frames will be moved)");
        }

        // ── Move excluded files out before EXOTIC sees them ───────────────────
        var excludedPaths = FrameAnalysis.GetExcludedPaths();
        var fitsDir       = Observation.FitsDir;
        _activeExclSession = null;
        if (excludedPaths.Count > 0 && Directory.Exists(fitsDir))
        {
            _activeExclSession = ExclusionService.MoveExcluded(excludedPaths, fitsDir);
            if (_activeExclSession is not null)
                FrameAnalysis.ScanStatus = $"⟳  {excludedPaths.Count} excluded file(s) moved aside — running EXOTIC…";
        }

        try
        {
            await LaunchExoticAsync(path);
        }
        finally
        {
            // ── Restore excluded files after EXOTIC finishes ──────────────────
            if (_activeExclSession is not null)
            {
                ExclusionService.RestoreSession(_activeExclSession);
                FrameAnalysis.ScanStatus = $"✓  Excluded files restored to {Path.GetFileName(fitsDir)}";
                _activeExclSession = null;
            }

            // ── Scan save_dir for output files after EXOTIC finishes ──────────
            var saveDir = Observation.SaveDir;
            if (!string.IsNullOrEmpty(saveDir))
            {
                FixAavsoSecondaryObscodes(saveDir);
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    Results.LoadOutputImages(saveDir);
                    Results.ScanOutputFiles(saveDir);
                });
            }
        }
    }

    // ── AAVSO output post-processing ──────────────────────────────────────────

    /// <summary>
    /// EXOTIC 4.x writes #SECONDARY_OBSCODES=1,MOBS (count prefix) which AAVSO rejects.
    /// Strip the leading count so it becomes #SECONDARY_OBSCODES=MOBS.
    /// </summary>
    private static void FixAavsoSecondaryObscodes(string saveDir)
    {
        try
        {
            foreach (var file in Directory.GetFiles(saveDir, "AAVSO_*.txt"))
            {
                var lines   = File.ReadAllLines(file);
                bool changed = false;
                for (int i = 0; i < lines.Length; i++)
                {
                    var m = System.Text.RegularExpressions.Regex.Match(
                        lines[i], @"^#SECONDARY_OBSCODES=\d+,(.+)$");
                    if (m.Success)
                    {
                        lines[i] = $"#SECONDARY_OBSCODES={m.Groups[1].Value}";
                        changed   = true;
                    }
                }
                if (changed) File.WriteAllLines(file, lines);
            }
        }
        catch { }
    }

    // ── Recent Sessions ───────────────────────────────────────────────────────

    private void RefreshRecentSessions()
    {
        RecentSessions.Clear();
        foreach (var s in _cfg.SessionHistory)
            RecentSessions.Add(s);
    }

    [RelayCommand]
    private void LoadRecentSession(SessionRecord? record)
    {
        if (record is null || !File.Exists(record.Path)) return;
        try
        {
            var json = File.ReadAllText(record.Path);
            var doc  = JsonNode.Parse(json) as JsonObject ?? new JsonObject();
            ApplyInits(doc);
        }
        catch (Exception ex) { SessionLogService.Write($"[Session] ERROR loading recent session \"{Path.GetFileName(record.Path)}\": {ex.Message}"); }
    }

    // ── EXOTIC launch + output streaming ──────────────────────────────────────

    private async Task LaunchExoticAsync(string initsPath)
    {
        _exoticCts = new CancellationTokenSource();
        var runtime = await ExoticRuntimeService.ResolveAsync(
            _cfg.PythonExePath,
            _cfg.ExoticExePath,
            _exoticCts.Token);

        if (runtime is null)
        {
            if (BrowseExoticFunc is not null)
            {
                var selectedPath = await BrowseExoticFunc("Locate python.exe or exotic.exe for the EXOTIC environment");
                if (selectedPath is null)
                {
                    _exoticCts = null;
                    return;
                }
                if (ExoticRuntimeService.LooksLikePython(selectedPath))
                    _cfg.PythonExePath = selectedPath;
                else
                    _cfg.ExoticExePath = selectedPath;
                ConfigService.Save(_cfg);

                runtime = await ExoticRuntimeService.ResolveAsync(
                    _cfg.PythonExePath,
                    _cfg.ExoticExePath,
                    _exoticCts.Token);
            }
            if (runtime is null)
            {
                if (ShowErrorFunc is not null)
                    await ShowErrorFunc(
                        "EXOTIC not found",
                        "TransitLab could not find a Python environment that can import EXOTIC. Open Tools → Python & EXOTIC Setup and run Check System.");
                _exoticCts = null;
                return;
            }
        }

        _cfg.PythonExePath        = runtime.PythonExePath;
        Observation.PythonExePath = runtime.PythonExePath;
        if (!string.IsNullOrWhiteSpace(runtime.ExoticExePath))
        {
            _cfg.ExoticExePath        = runtime.ExoticExePath;
            Observation.ExoticExePath = runtime.ExoticExePath;
        }
        ConfigService.Save(_cfg);

        // Switch to Results tab (index 3: Data=0, ImageAnalysis=1, Parameters=2, Results=3, Visualizer=4, History=5)
        SelectTabFunc?.Invoke(3);

        Results.ClearLog();
        Results.ClearOutputImages();
        IsExoticRunning   = true;
        ExoticStatusText  = "Running…";

        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PYTHONIOENCODING"] = "utf-8",
            ["PYTHONUNBUFFERED"]  = "1",
        };
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            env[e.Key!.ToString()!] = e.Value?.ToString() ?? "";
        env["PYTHONIOENCODING"] = "utf-8";
        env["PYTHONUNBUFFERED"]  = "1";
        var log = new StringBuilder();

        AppendLog($"▶  {runtime.PythonExePath} -u -m exotic.exotic -red \"{initsPath}\"\n\n");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = runtime.PythonExePath,
                ArgumentList           = { "-u", "-m", "exotic.exotic", "-red", initsPath },
                WorkingDirectory       = System.IO.Path.GetDirectoryName(initsPath) ?? System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                RedirectStandardInput  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding  = Encoding.UTF8,
            };
            foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;

            using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _exoticProcess = proc;
            proc.Start();

            // Stderr: simple background thread
            var stderrTask = Task.Run(() =>
            {
                string? line;
                while ((line = proc.StandardError.ReadLine()) is not null)
                {
                    CheckCoordinateMismatch(line);
                    AppendLog(line + "\n");
                }
            });

            // Stdout: interactive — surfaces EXOTIC prompts to the user in the Results tab
            await ReadStdoutInteractiveAsync(proc, _exoticCts.Token);

            await stderrTask;
            await Task.Run(() => proc.WaitForExit());

            var rc = proc.ExitCode;
            AppendLog(rc == 0
                ? "\n✓  EXOTIC finished successfully.\n"
                : $"\n✗  EXOTIC exited with code {rc}.\n");
        }
        catch (OperationCanceledException)
        {
            AppendLog("\n⏹  EXOTIC run cancelled.\n");
        }
        finally
        {
            _exoticProcess   = null;
            IsExoticRunning  = false;
            ExoticStatusText = "";
        }
    }

    [RelayCommand]
    private void CancelExotic()
    {
        try { _exoticProcess?.Kill(entireProcessTree: true); } catch { }
        Results.CancelPrompt();
        _exoticCts?.Cancel();
    }

    /// <summary>
    /// Reads EXOTIC's stdout char-by-char via a Channel. Lines are flushed to the log
    /// immediately. After a 250 ms silence, if the accumulated buffer looks like a prompt
    /// (ends with ": ") it is surfaced to the user via <see cref="ResultsViewModel.RequestPromptAsync"/>
    /// and the user's response is written to stdin.
    /// </summary>
    private async Task ReadStdoutInteractiveAsync(Process proc, CancellationToken outerCt)
    {
        var channel = Channel.CreateUnbounded<char>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var buf = new StringBuilder();

        // Background thread: push chars into the channel
        var readerThread = new Thread(() =>
        {
            try
            {
                int c;
                while ((c = proc.StandardOutput.Read()) != -1)
                    channel.Writer.TryWrite((char)c);
            }
            finally { channel.Writer.Complete(); }
        }) { IsBackground = true, Name = "exotic-stdout" };
        readerThread.Start();

        while (true)
        {
            char ch;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
                cts.CancelAfter(250);
                ch = await channel.Reader.ReadAsync(cts.Token);
            }
            catch (ChannelClosedException)
            {
                break; // stdout EOF — process has finished writing
            }
            catch (OperationCanceledException) when (outerCt.IsCancellationRequested)
            {
                break; // user cancelled the run
            }
            catch (OperationCanceledException)
            {
                // 250 ms timeout — no chars arrived; check for a prompt
                if (buf.Length > 0)
                {
                    var text = buf.ToString();
                    AppendLog(text);
                    buf.Clear();

                    if (IsPromptLike(text))
                    {
                        string response;
                        var stripped = Regex.Replace(text, @"\x1B\[[^m]*m", "");
                        if (stripped.Contains("Secondary Observer"))
                        {
                            // Always auto-send secondary codes (value already in the launcher form)
                            response = Observation.SecondaryCode ?? "";
                        }
                        else if (Results.AutoAnswerPrompts && stripped.Contains("Enter 1 or 2"))
                        {
                            // Auto mode: use launcher's defined parameters, no prompt needed
                            response = "1";
                        }
                        else if (stripped.Contains("alternate image extension"))
                        {
                            // No alternate extension needed — avoids re-entering calibration loop
                            response = "n";
                        }
                        else if (stripped.Contains("directory path where Darks"))
                        {
                            response = Observation.DarksDir ?? "";
                        }
                        else if (stripped.Contains("directory path where Flats"))
                        {
                            response = Observation.FlatsDir ?? "";
                        }
                        else if (stripped.Contains("directory path where Bias"))
                        {
                            response = Observation.BiasDir ?? "";
                        }
                        else
                        {
                            response = await Results.RequestPromptAsync(text);
                        }

                        try { proc.StandardInput.WriteLine(response); } catch { }
                    }
                }
                // If the channel writer is done and the buffer is drained, we're finished
                if (channel.Reader.Completion.IsCompleted) break;
                continue;
            }

            buf.Append(ch);
            if (ch == '\n')
            {
                var line = buf.ToString();
                CheckCoordinateMismatch(line);
                AppendLog(line);
                buf.Clear();
            }
        }

        // Flush any partial line that did not end with \n
        if (buf.Length > 0) AppendLog(buf.ToString());

        try { proc.StandardInput.Close(); } catch { }
    }

    private void CheckCoordinateMismatch(string line)
    {
        if (!line.Contains("does not match the target") &&
            !line.Contains("pixel position of your target")) return;

        var m = Regex.Match(line, @"\[\s*(\d+)\s*,\s*(\d+)\s*\]");
        if (m.Success &&
            int.TryParse(m.Groups[1].Value, out var nx) &&
            int.TryParse(m.Groups[2].Value, out var ny))
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                EquipmentTarget.TargetXY = $"[{nx}, {ny}]");
        }
    }

    private static bool IsPromptLike(string text)
    {
        // Strip ANSI colour codes before checking
        var stripped = Regex.Replace(text, @"\x1B\[[^m]*m", "");
        var t = stripped.TrimEnd('\r', '\n').TrimEnd();
        return t.EndsWith(": ") || t.EndsWith(":");
    }

    private void AppendLog(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (!string.IsNullOrWhiteSpace(trimmed))
                SessionLogService.Write($"[EXOTIC] {trimmed}");
        }
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            Results.AppendLog(text));
    }

    private void RecordSession(string path)
    {
        var planet = EquipmentTarget.PlanetName.Trim();
        var label  = string.IsNullOrEmpty(planet)
            ? $"Session  {DateTime.Now:yyyy-MM-dd HH:mm}"
            : $"{planet}  {DateTime.Now:yyyy-MM-dd}";
        _cfg.SessionHistory.Insert(0, new SessionRecord { Label = label, Path = path });
        if (_cfg.SessionHistory.Count > 10) _cfg.SessionHistory.RemoveAt(10);
        ConfigService.Save(_cfg);
        RefreshRecentSessions();
    }

    [RelayCommand]
    private async Task SaveInitsOnly()
    {
        if (SaveFilePickerFunc is null) return;
        var ts  = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var dir = InitsDefaultPath();
        var path = await SaveFilePickerFunc($"inits_{ts}.json", dir);
        if (path is null) return;
        WriteInits(path);
    }

    [RelayCommand]
    private async Task LoadInits()
    {
        if (OpenFilePickerFunc is null) return;
        var path = await OpenFilePickerFunc(
            string.IsNullOrEmpty(_cfg.LastInitsDir) ? InitsDefaultPath() : _cfg.LastInitsDir);
        if (path is null) return;
        try
        {
            var json = File.ReadAllText(path);
            var doc  = JsonNode.Parse(json) as JsonObject ?? new JsonObject();
            ApplyInits(doc);
            _cfg.LastInitsDir = Path.GetDirectoryName(path) ?? _cfg.LastInitsDir;
            ConfigService.Save(_cfg);
        }
        catch (Exception ex) { SessionLogService.Write($"[Inits] ERROR loading {Path.GetFileName(path)}: {ex.Message}"); }
    }

    private void RecordSubmissionFromFinalParams()
    {
        var saveDir = Observation.SaveDir;
        if (string.IsNullOrEmpty(saveDir)) return;

        // Find FinalParams_*.json
        var tempDir    = Path.Combine(saveDir, "temp");
        var candidates = Directory.Exists(tempDir)
            ? Directory.GetFiles(tempDir, "FinalParams_*.json")
            : [];
        if (candidates.Length == 0)
            candidates = Directory.GetFiles(saveDir, "FinalParams_*.json");
        if (candidates.Length == 0) return;

        var latest = candidates.OrderByDescending(File.GetLastWriteTime).First();
        try
        {
            var json = File.ReadAllText(latest);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("FINAL PLANETARY PARAMETERS", out var pp)) return;

            static (string Val, string Unc) ParseVU(JsonElement el, string key)
            {
                if (!el.TryGetProperty(key, out var v)) return ("", "");
                var s   = v.ToString().Trim();
                var idx = s.IndexOf("+/-");
                if (idx < 0) return (s.Split(' ')[0], "");
                return (s[..idx].Trim(), s[(idx + 3)..].Trim().Split(' ')[0]);
            }

            var (tmidV,  tmidU)  = ParseVU(pp, "Mid-Transit Time (Tmid)");
            var (depthV, depthU) = ParseVU(pp, "Transit depth (Rp/Rs)^2");
            var (incV,   _)      = ParseVU(pp, "Orbital Inclination (inc)");
            var (durV,   _)      = ParseVU(pp, "Transit Duration (day)");
            var scatter = pp.TryGetProperty(
                "Scatter in the residuals of the lightcurve fit is", out var sv)
                ? sv.ToString().Replace("%", "").Trim() : "";

            string rpRs = depthV, rpRsUnc = depthU;
            if (double.TryParse(depthV, out var dv) &&
                double.TryParse(depthU, out var du) && dv > 0.1)
            {
                rpRs    = (dv / 100.0).ToString("F6");
                rpRsUnc = (du / 100.0).ToString("F6");
            }

            string snr = "";
            if (double.TryParse(rpRs, out var rv) &&
                double.TryParse(rpRsUnc, out var ru) && ru > 0)
                snr = (rv / ru).ToString("F1");

            var obsDate = Observation.ObsDate;
            if (string.IsNullOrEmpty(obsDate) &&
                !string.IsNullOrEmpty(tmidV) && double.TryParse(tmidV, out var jd))
            {
                try
                {
                    var dt = new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc)
                                 .AddDays(jd - 2451545.0);
                    obsDate = dt.ToString("dd-MMM-yyyy").ToUpper();
                }
                catch { }
            }

            var entry = new Models.HistoryEntry
            {
                Planet    = EquipmentTarget.PlanetName.Trim(),
                Obs       = Observation.AavsoCode,
                ObsDate   = obsDate,
                Submitted = DateTime.Today,
                Tmid      = tmidV,
                TmidUnc   = tmidU,
                RpRs      = rpRs,
                RpRsUnc   = rpRsUnc,
                Snr       = snr,
                Depth     = depthV,
                Inc       = incV,
                Duration  = durV,
                Scatter   = scatter,
            };

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                History.Entries.Insert(0, entry);
                HistoryService.Save(History.Entries);
            });
        }
        catch (Exception ex) { SessionLogService.Write($"[History] ERROR recording submission from FinalParams: {ex.Message}"); }
    }

    [RelayCommand]
    private void ClearAllFields()
    {
        Observation.FitsDir        = "";
        Observation.SaveDir        = "";
        Observation.SaveDirUserSet = false;
        Observation.DarksDir      = "";
        Observation.FlatsDir      = "";
        Observation.BiasDir       = "";
        Observation.ObsDate       = "";

        EquipmentTarget.PlanetName    = "";
        EquipmentTarget.TargetRa      = "";
        EquipmentTarget.TargetDec     = "";
        EquipmentTarget.HostStarName  = "";
        EquipmentTarget.OrbitalPeriod = "";
        EquipmentTarget.OrbitalPeriodUnc  = "";
        EquipmentTarget.MidTransitTime    = "";
        EquipmentTarget.MidTransitTimeUnc = "";
        EquipmentTarget.RpRs          = "";
        EquipmentTarget.RpRsUnc       = "";
        EquipmentTarget.ARs           = "";
        EquipmentTarget.ARsUnc        = "";
        EquipmentTarget.Inclination   = "";
        EquipmentTarget.InclinationUnc= "";
        EquipmentTarget.Eccentricity  = "";
        EquipmentTarget.ArgPeriastron = "";
        EquipmentTarget.Teff          = "";
        EquipmentTarget.TeffPlus      = "";
        EquipmentTarget.TeffMinus     = "";
        EquipmentTarget.Metallicity   = "";
        EquipmentTarget.MetallicityPlus  = "";
        EquipmentTarget.MetallicityMinus = "";
        EquipmentTarget.Logg          = "";
        EquipmentTarget.LoggPlus      = "";
        EquipmentTarget.LoggMinus     = "";
        EquipmentTarget.StarDistance  = "";
        EquipmentTarget.PmRa          = "";
        EquipmentTarget.PmDec         = "";
        EquipmentTarget.TargetXY      = "";
        EquipmentTarget.CompXY        = "";
        EquipmentTarget.Notes         = "";
        EquipmentTarget.PixelScale    = "";
    }
}
