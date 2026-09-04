using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TransitLab;
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
    public string Version { get; } = AppInfo.Version;
    public string Title   { get; } = $"TransitLab  v{AppInfo.Version}";

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

    // ── Advanced EXOTIC options (Settings → Advanced) ───────────────────────────
    // All four are CLI switches that only exist in the EXOTIC 4.3.2 pre-release dev build.
    // LaunchExoticAsync only appends them when the active runtime resolves to that build,
    // so these can be freely toggled on even while Stable is active — they're just ignored.
    public bool NonInteractiveRun
    {
        get => _cfg.NonInteractiveRun;
        set { _cfg.NonInteractiveRun = value; ConfigService.Save(_cfg); }
    }

    public bool UseNextAstroVariabilityServer
    {
        get => _cfg.UseNextAstroVariabilityServer;
        set { _cfg.UseNextAstroVariabilityServer = value; ConfigService.Save(_cfg); }
    }

    public int? MultiprocessTransformations
    {
        get => _cfg.MultiprocessTransformations;
        set { _cfg.MultiprocessTransformations = value; ConfigService.Save(_cfg); }
    }

    public int? MultiprocessLightcurveFits
    {
        get => _cfg.MultiprocessLightcurveFits;
        set { _cfg.MultiprocessLightcurveFits = value; ConfigService.Save(_cfg); }
    }

    public bool UseEnsemblePhotometry
    {
        get => _cfg.UseEnsemblePhotometry;
        set { _cfg.UseEnsemblePhotometry = value; ConfigService.Save(_cfg); }
    }

    public bool UseExactlyTheCompsProvided
    {
        get => _cfg.UseExactlyTheCompsProvided;
        set { _cfg.UseExactlyTheCompsProvided = value; ConfigService.Save(_cfg); }
    }

    public bool ShowTipsAtStartup
    {
        get => _cfg.ShowTipsAtStartup;
        set { _cfg.ShowTipsAtStartup = value; ConfigService.Save(_cfg); }
    }

    public int NextTipIndex
    {
        get => _cfg.NextTipIndex;
        set { _cfg.NextTipIndex = value; ConfigService.Save(_cfg); }
    }

    public bool HasSeenV270WhatsNew
    {
        get => _cfg.HasSeenV270WhatsNew;
        set { _cfg.HasSeenV270WhatsNew = value; ConfigService.Save(_cfg); }
    }

    public bool HasSeenV271WhatsNew
    {
        get => _cfg.HasSeenV271WhatsNew;
        set { _cfg.HasSeenV271WhatsNew = value; ConfigService.Save(_cfg); }
    }

    /// <summary>Last folder used to save a diagnostics log — tracked independently of the Data tab's directories.</summary>
    public string LastDiagnosticsLogDir
    {
        get => _cfg.LastDiagnosticsLogDir;
        set { _cfg.LastDiagnosticsLogDir = value; ConfigService.Save(_cfg); }
    }

    public bool SuppressStellarVariabilityWarning
    {
        get => _cfg.SuppressStellarVariabilityWarning;
        set
        {
            _cfg.SuppressStellarVariabilityWarning = value;
            ConfigService.Save(_cfg);
            EquipmentTarget.SuppressStellarVariabilityWarning = value;
        }
    }

    /// <summary>Maximum comp stars for the Stone / VSP + Stone pipeline (1–25, default 10).</summary>
    public int MaxCompStars
    {
        get => _cfg.MaxCompStars;
        set
        {
            _cfg.MaxCompStars = Math.Clamp(value, 1, 25);
            ConfigService.Save(_cfg);
            EquipmentTarget.MaxCompStars = _cfg.MaxCompStars;
        }
    }

    // Injected by MainWindow code-behind
    public Func<string, string, Task<string?>>?  SaveFilePickerFunc      { get; set; }
    public Func<string, Task<string?>>?           OpenFilePickerFunc      { get; set; }
    public Func<string, Task<string?>>?           OpenAavsoFilePickerFunc { get; set; }
    public Func<string, Task<string?>>?           BrowseExoticFunc        { get; set; }
    public Action<int>?                           SelectTabFunc           { get; set; }
    public Func<string, string, Task>?            ShowErrorFunc           { get; set; }
    public Func<string, string, Task<bool>>?      ShowConfirmFunc         { get; set; }
    public Func<Task<string?>>?                   BrowseUpdateFolderFunc  { get; set; }
    public Func<string, string, Task>?            ShowInfoFunc            { get; set; }
    /// <summary>Closes the main window through its normal Closing path (SaveOnExit still runs) — used as a fallback if the installer's own close-and-reinstall doesn't complete promptly during self-update.</summary>
    public Action?                                RequestAppExitAction    { get; set; }

    // ── Update checker ────────────────────────────────────────────────────────
    [ObservableProperty] private bool   _isUpdateAvailable   = false;
    [ObservableProperty] private bool   _isUpdateDownloading = false;
    [ObservableProperty] private bool   _isUpdateDone        = false;
    [ObservableProperty] private bool   _canSelfUpdate       = false;
    [ObservableProperty] private string _updateVersionText   = "";
    [ObservableProperty] private string _updateStatusText    = "";
    [ObservableProperty] private double _updateProgress      = 0;

    private UpdateInfo? _pendingUpdate;
    private UpdateInfo? _pendingInstallerUpdate;
    private System.Threading.Timer? _dailyUpdateCheckTimer;
    private DateTime _lastAutoUpdateCheckUtcDate;

    public async Task RunStartupUpdateCheckAsync()
    {
        _lastAutoUpdateCheckUtcDate = DateTime.UtcNow.Date;
        await RunAutomaticUpdateCheckAsync();
        StartDailyUpdateCheckTimer();
    }

    // Polls every 30 minutes rather than trying to fire a single timer exactly at 00:00 UTC —
    // a long-lived periodic Timer drifts and doesn't reliably survive sleep/resume, but a poll
    // that just asks "has the UTC calendar date changed since the last automatic check?" is
    // robust to both and still lands within 30 minutes of midnight, for a user who leaves
    // TransitLab open indefinitely.
    private void StartDailyUpdateCheckTimer()
    {
        var interval = TimeSpan.FromMinutes(30);
        _dailyUpdateCheckTimer = new System.Threading.Timer(_ =>
        {
            if (DateTime.UtcNow.Date <= _lastAutoUpdateCheckUtcDate) return;
            _lastAutoUpdateCheckUtcDate = DateTime.UtcNow.Date;
            Avalonia.Threading.Dispatcher.UIThread.Post(async () => await RunAutomaticUpdateCheckAsync());
        }, null, interval, interval);
    }

    // Shared by the launch-time check and the daily re-check — both are silent when there's
    // nothing new or when the user already clicked Skip for this exact version (persisted in
    // config, since "Skip" means "don't ask about this version again," not "not right now").
    // The manual Check for Updates button (below) deliberately does NOT consult the skipped
    // version, and does report "up to date" — an explicit ask should always tell the truth.
    private async Task RunAutomaticUpdateCheckAsync()
    {
        var info = await UpdateService.CheckAsync(Version);
        if (info is null || info.Version == _cfg.SkippedUpdateVersion) return;

        _pendingUpdate      = info;
        UpdateVersionText   = $"TransitLab v{info.Version} is available";
        IsUpdateDone        = false;
        IsUpdateAvailable   = true;

        await CheckSelfUpdateAsync();
    }

    // Only offered when this instance is an Inno-managed install (installer\TransitLab.iss)
    // AND the latest release actually has a Setup .exe asset — both are independently checked
    // since older releases predate the installer and a portable/manual copy can't be safely
    // reinstalled over silently.
    private async Task CheckSelfUpdateAsync()
    {
        _pendingInstallerUpdate = null;
        CanSelfUpdate = false;
        if (!UpdateService.IsSelfUpdateCapable()) return;

        var installerInfo = await UpdateService.CheckInstallerAsync(Version);
        if (installerInfo is null) return;

        _pendingInstallerUpdate = installerInfo;
        CanSelfUpdate = true;
    }

    [RelayCommand]
    private async Task SelfUpdateNowAsync()
    {
        SessionLogService.Write("[Update] User clicked Update Now (self-update)");
        if (_pendingInstallerUpdate is null) return;

        var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TransitLab");
        System.IO.Directory.CreateDirectory(tempDir);
        var destPath = System.IO.Path.Combine(tempDir, _pendingInstallerUpdate.AssetName);

        IsUpdateAvailable   = false;
        IsUpdateDownloading = true;
        UpdateStatusText    = "Downloading update…";

        try
        {
            var progress = new Progress<(long done, long total)>(t =>
            {
                if (t.total > 0)
                {
                    UpdateProgress   = (double)t.done / t.total * 100;
                    UpdateStatusText = $"Downloading update… {t.done / 1_048_576.0:F1} MB / {t.total / 1_048_576.0:F1} MB";
                }
            });
            await ExoticInstallService.DownloadFileAsync(_pendingInstallerUpdate.DownloadUrl, destPath, progress, default);

            UpdateStatusText = "Installing update — TransitLab will close and reopen automatically…";
            SessionLogService.Write($"[Update] Launching silent installer: {destPath}");
            UpdateService.LaunchSilentInstall(destPath);

            // The installer's Restart Manager-based CloseApplications should close this window
            // on its own once it detects the file lock on TransitLab.exe. This is a fallback in
            // case that doesn't happen promptly, so the update never gets stuck with two
            // processes running.
            await Task.Delay(TimeSpan.FromSeconds(5));
            RequestAppExitAction?.Invoke();
        }
        catch (Exception ex)
        {
            SessionLogService.Write($"[Update] Self-update failed: {ex.Message}");
            UpdateStatusText    = $"Update failed: {ex.Message}";
            IsUpdateDownloading = false;
            IsUpdateDone        = true;
        }
    }

    [RelayCommand]
    private void SkipUpdate()
    {
        SessionLogService.Write($"[Update] User clicked Skip Update for v{_pendingUpdate?.Version}");
        if (_pendingUpdate is not null)
        {
            _cfg.SkippedUpdateVersion = _pendingUpdate.Version;
            ConfigService.Save(_cfg);
        }
        IsUpdateAvailable = false;
    }

    [RelayCommand]
    private async Task DownloadUpdate()
    {
        SessionLogService.Write("[Update] User clicked Download Update");
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
        SessionLogService.Write("[Update] User clicked Check for Updates");
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

        await CheckSelfUpdateAsync();
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

    // Quick Look (-ql) only exists in the EXOTIC 4.3.2 pre-release dev build — same detection
    // pattern as PlateSolveSetupViewModel.IsNextAstroSupported. Defaults to false (button
    // greyed out) until RefreshPrereleaseGatingAsync confirms the active environment.
    [ObservableProperty] private bool _isPrereleaseActive = false;
    public bool CanQuickLook => CanSaveAndRun && IsPrereleaseActive;
    public string QuickLookRequirementNote { get; } =
        "⚠  Requires the EXOTIC 4.3.2 pre-release dev build — install via Tools → Python & EXOTIC Setup → Pre-release / Development Build, then select it as the active environment.";
    public string QuickLookTooltip => IsPrereleaseActive
        ? "Runs a fast least-squares fit instead of the full posterior inference — no AAVSO report is produced."
        : QuickLookRequirementNote;

    partial void OnIsExoticRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSaveAndRun));
        OnPropertyChanged(nameof(CanQuickLook));
    }

    partial void OnIsPrereleaseActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(CanQuickLook));
        OnPropertyChanged(nameof(QuickLookTooltip));
    }

    /// <summary>
    /// Detects whether the active EXOTIC environment is the 4.3.2 pre-release dev build, for
    /// gating Quick Look mode and the Advanced settings' CLI switches. Called at startup and
    /// whenever the active environment changes in Setup.
    /// </summary>
    private async Task RefreshPrereleaseGatingAsync()
    {
        try
        {
            var pythonExe = GetActiveEnvironmentPythonExePath();
            var version = !string.IsNullOrWhiteSpace(pythonExe)
                ? await ExoticInstallService.GetExoticVersionAsync(pythonExe)
                : null;
            IsPrereleaseActive = version?.StartsWith("4.3.2.dev", StringComparison.OrdinalIgnoreCase) == true;
        }
        catch
        {
            IsPrereleaseActive = false;
        }

        if (IsPrereleaseActive && !_cfg.HasSeenWbomFeaturesAlert)
        {
            _cfg.HasSeenWbomFeaturesAlert = true;
            ConfigService.Save(_cfg);
            if (ShowInfoFunc is not null)
                await ShowInfoFunc("New in v2.9.1 — EXOTIC 4.3.2 Pre-Release Features",
                    "TransitLab detected the EXOTIC 4.3.2 pre-release dev build is active. Six new options unique to that build are now available:\n\n" +
                    "• Quick Look — a fast preliminary fit button next to Save & Run EXOTIC\n\n" +
                    "In Tools → Settings → Advanced:\n" +
                    "• Non-interactive run\n" +
                    "• NextAstro variability server\n" +
                    "• Multiprocessing (frame alignment / light-curve fits)\n" +
                    "• Use ensemble photometry\n" +
                    "• Use exactly the comps provided\n\n" +
                    "Each has a [?] explaining what it does. This message only appears once.");
        }
    }

    private CancellationTokenSource? _exoticCts;
    private Process?                  _exoticProcess;
    private ExclusionSession?         _activeExclSession;
    private bool                      _ldtkCorruptionAlerted;
    private bool                      _sawLdtkTracebackFrame;
    private bool                      _ldtkNetworkFailureDetected;

    private AppConfig _cfg;
    private string?   _detectedExoticVersion;

    public MainWindowViewModel()
    {
        _cfg = ConfigService.Load();
        Observation.EquipmentTarget    = EquipmentTarget;
        Observation.ConfigSaveCallback = SaveObservationLists;
        ExoticSetup.ConfigSaveCallback = SaveExoticSetupLists;
        ExoticSetup.ActiveEnvironmentChanged = envName =>
        {
            SaveExoticSetupLists();
            _ = RefreshPrereleaseGatingAsync();
        };
        FrameAnalysis.FitsDirFunc      = () => Observation.FitsDir;
        FrameAnalysis.TargetNameFunc   = () =>
        {
            var hn = EquipmentTarget.HostStarName;
            return string.IsNullOrWhiteSpace(hn) ? EquipmentTarget.PlanetName : hn;
        };
        // VSP Star field is populated by FrameAnalysisViewModel.ScanFiles() when
        // images are loaded — no push needed here from the NEA callback.

        // Wire transit visualizer to equipment target
        Transit.Connect(EquipmentTarget);

        // Wire sound notification when light curve results appear
        Results.LightCurveReadySound = () => { if (CompletionSound != "None") PlaySoundAction?.Invoke(CompletionSound, CompletionSoundPath); };

        // Wire operation status sounds (NEA fetch, plate solve, AAVSO comp fetch)
        EquipmentTarget.PlayStatusSoundAction = success => { if (StatusAlertsEnabled) PlayStatusSoundAction?.Invoke(success); };

        // Stone pipeline settings
        EquipmentTarget.MaxCompStars  = _cfg.MaxCompStars;

        // Stellar Variability Only warning popup — restore "don't show again" setting
        EquipmentTarget.SuppressStellarVariabilityWarning = _cfg.SuppressStellarVariabilityWarning;

        // Stream Stone Method verbose log into the Results tab log window
        EquipmentTarget.CompLogAction = msg => AppendLog(msg);
        Observation.AutoScanAndGetFitsFunc = FrameAnalysis.ScanExcludeAndGetFirstAsync;
        FrameAnalysis.EquipmentTarget = EquipmentTarget;
        FrameAnalysis.DarksDirFunc = () => Observation.DarksDir;
        EquipmentTarget.FitsDirFunc   = () => Observation.FitsDir;
        EquipmentTarget.GetFirstNonExcludedFitsFunc = FrameAnalysis.ScanExcludeAndGetFirstAsync;
        EquipmentTarget.GetExcludedPathsFunc        = FrameAnalysis.GetExcludedPaths;
        // Late-bound closures: ShowConfirmFunc may be assigned by the View after this
        // constructor runs, so forward to whatever it is at call time, not wiring time.
        Observation.ShowConfirmFunc = (title, msg) => ShowConfirmFunc?.Invoke(title, msg) ?? Task.FromResult(false);
        Observation.TriggerScanFramesFunc = () => FrameAnalysis.ScanFilesCommand.ExecuteAsync(null);
        MObs.UseDataCallback = (scienceDir, darksDir) =>
        {
            Observation.FitsDir  = scienceDir;
            Observation.DarksDir = darksDir;
            Observation.SaveDir  = scienceDir;  // override parent-default; save plots into the science folder
            SelectTabFunc?.Invoke(0); // stay on Data=0
        };

        // Propagate FitsDirNeedsHeaderRead changes to CanSaveAndRun / CanQuickLook
        Observation.FitsDirNeedsHeaderReadChanged = () =>
        {
            OnPropertyChanged(nameof(CanSaveAndRun));
            OnPropertyChanged(nameof(CanQuickLook));
        };

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

        // Probe EXOTIC version asynchronously so the Results tab shows it immediately.
        if (!string.IsNullOrWhiteSpace(_cfg.PythonExePath))
            _ = ProbeAndSetExoticVersionAsync();
        _ = RefreshPrereleaseGatingAsync();

        // Wire Results callbacks
        Results.ObscodeFunc          = () => Observation.AavsoCode;
        Results.RecordSubmissionFunc = RecordSubmissionFromFinalParams;
        Results.ImmediatelyClearPasswordFunc = () =>
        {
            _cfg.AavsoPassword = "";
            _cfg.SavePassword  = false;
            ConfigService.Save(_cfg);
        };

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

        // Plate Solve must check the same active multi-environment install that Check
        // System and Save & Run use, not the legacy shared base Python — otherwise it can
        // report "EXOTIC not found" after that install is only in an isolated Stable/
        // Pre-release venv the base Python never had EXOTIC installed into.
        Observation.ResolveActivePythonPathFunc = () =>
        {
            var activeEnv = _cfg.ExoticEnvironments.FirstOrDefault(e => e.Name == _cfg.ActiveEnvironmentName);
            return !string.IsNullOrWhiteSpace(activeEnv?.PythonExePath) ? activeEnv.PythonExePath : _cfg.PythonExePath;
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
        bool solveAllFrames = false, string starFixPath = "", bool save = true)
    {
        _cfg.PlateSolver        = solver;
        _cfg.AstapExePath       = astapPath;
        _cfg.AstapCatalogDir    = catalogDir;
        _cfg.AstapSearchRadius  = searchRadius;
        _cfg.AstapDownsample    = downsample;
        _cfg.AstapSolveAllFrames = solveAllFrames;
        _cfg.StarFixExePath     = starFixPath;

        var config = new PlateSolveService.SolverConfig(solver, astapPath, catalogDir, searchRadius, downsample, solveAllFrames, StarFixExePath: starFixPath);
        EquipmentTarget.PlateSolverConfig  = config;
        EquipmentTarget.ActiveSolverLabel  = solver switch
        {
            "ASTAP"     => "Solver: ASTAP",
            "NextAstro" => "Solver: NextAstro",
            "StarFix"   => "Solver: StarFix",
            _           => "Solver: Astrometry.net",
        };

        if (save) ConfigService.Save(_cfg);
    }

    /// <summary>Exposes current solver config for the Plate Solve Setup dialog.</summary>
    public (string Solver, string AstapPath, string CatalogDir, int SearchRadius, int Downsample, bool SolveAllFrames, string StarFixPath) GetPlateSolverConfig()
        => (_cfg.PlateSolver, _cfg.AstapExePath, _cfg.AstapCatalogDir, _cfg.AstapSearchRadius, _cfg.AstapDownsample, _cfg.AstapSolveAllFrames, _cfg.StarFixExePath);

    /// <summary>
    /// The python.exe of whichever environment (Stable or Pre-release) is currently active in
    /// Setup — same resolution used when naming Results folders and launching EXOTIC itself, so
    /// callers like the NextAstro support check in Plate Solve Setup see the same environment the
    /// app would actually use, rather than whatever generic Python happens to be found on the system.
    /// </summary>
    public string GetActiveEnvironmentPythonExePath()
    {
        var activeEnv = _cfg.ExoticEnvironments.FirstOrDefault(e => e.Name == _cfg.ActiveEnvironmentName);
        return !string.IsNullOrWhiteSpace(activeEnv?.PythonExePath)
            ? activeEnv.PythonExePath
            : _cfg.PythonExePath;
    }

    /// <summary>Persists the live Automation settings to config.json. Called from the Settings Save callback.</summary>
    public void SaveAutomationConfig()
    {
        _cfg.AutoMonitorFolder    = Automation.MonitorFolder;
        _cfg.AutoStartTime        = Automation.StartTime;
        ConfigService.Save(_cfg);
    }

    // ── Automation sequence ───────────────────────────────────────────────────

    /// <summary>
    /// Full automation sequence triggered when the monitor folder reaches file stability:
    /// clear darks → disable dark subtract → scan frames → auto-exclude flagged →
    /// read FITS header (→ plate solve → NEA → AAVSO comps) → auto-select target →
    /// fetch comp stars → Save &amp; Run EXOTIC.
    /// </summary>
    public async Task RunAutomationSequenceAsync()
    {
        _isAutomationRunning = true;
        EquipmentTarget.IsAutomationRunning = true;
        try
        {
        await RunAutomationSequenceInternalAsync();
        }
        finally
        {
            _isAutomationRunning = false;
            EquipmentTarget.IsAutomationRunning = false;
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

        // 5. Read FITS header — clear stale RA/Dec first so the wait loop below cannot pass until
        //    NEA fetches fresh coordinates for this target (WCS is re-set synchronously inside
        //    ReadFitsHeader when FITS already carry WCS, so _wcsReady alone is not sufficient).
        EquipmentTarget.TargetRa  = "";
        EquipmentTarget.TargetDec = "";
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

        // 8. Fetch comp stars — no longer auto-triggered; call explicitly and await completion.
        Automation.AutomationStatus = $"Running sequence: fetching comp stars ({EquipmentTarget.SelectedCompMethod})…";
        SessionLogService.Write($"[Automation] Fetching comp stars ({EquipmentTarget.SelectedCompMethod})…");
        await EquipmentTarget.FetchCompsCommand.ExecuteAsync(null);
        SessionLogService.Write($"[Automation] Comp fetch result: {EquipmentTarget.AavsoCompStatus}");

        // 9. Save & Run EXOTIC
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

    private void SaveExoticSetupLists()
    {
        _cfg.BranchUrls           = [.. ExoticSetup.LiveBranchUrls];
        _cfg.ExoticEnvironments   = ExoticSetup.SnapshotEnvironments();
        _cfg.ActiveEnvironmentName = ExoticSetup.ActiveEnvironmentName;
        ConfigService.Save(_cfg);
    }

    private void ApplyConfig(AppConfig cfg)
    {
        // Observer code collections
        Observation.LoadFromConfig(cfg.AavsoCode, cfg.SecondaryCodes, cfg.Observatories);
        ExoticSetup.LoadFromConfig(cfg.BranchUrls);
        ExoticSetup.LoadEnvironments(cfg.ExoticEnvironments, cfg.ActiveEnvironmentName);
        ExoticSetup.SeedPythonExe(cfg.PythonExePath);

        // EXOTIC runtime paths (used by reduction launch and plate solve)
        Observation.PythonExePath = cfg.PythonExePath;
        Observation.ExoticExePath = cfg.ExoticExePath;

        // Plate solver config
        ApplyPlateSolverSettings(cfg.PlateSolver, cfg.AstapExePath, cfg.AstapCatalogDir, cfg.AstapSearchRadius, cfg.AstapDownsample, cfg.AstapSolveAllFrames, cfg.StarFixExePath, save: false);

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
        // CameraType/Binning/Filter/CompMethod are all non-editable ComboBox SelectedItem
        // bindings — if a saved value doesn't match any entry in the current dropdown list
        // (corrupted config, or a value from an older version whose list has since changed),
        // the ComboBox coerces the bound property to null and that null gets persisted on
        // the next save, crashing every future launch during config restore. Coalesce to a
        // known-good default here so a bad saved value self-heals instead of propagating.
        EquipmentTarget.CameraType = cfg.LastUi.CameraType ?? "CCD";
        EquipmentTarget.Binning    = cfg.LastUi.Binning ?? "1x1";
        EquipmentTarget.Filter     = cfg.LastUi.Filter ?? "CV";
        EquipmentTarget.FilterMin  = cfg.LastUi.FilterMin;
        EquipmentTarget.FilterMax  = cfg.LastUi.FilterMax;
        EquipmentTarget.PixelScale        = cfg.LastUi.PixelScale;
        EquipmentTarget.Notes             = cfg.LastUi.Notes;
        // "Stone + VSP" was renamed to "VSP + Stone" (v2.8.0) to reflect that VSP stars
        // always fill first — remap an older saved config so it doesn't silently fail to
        // match any entry in the current CompMethods dropdown.
        EquipmentTarget.SelectedCompMethod = cfg.LastUi.CompMethod == "Stone + VSP"
            ? "VSP + Stone"
            : cfg.LastUi.CompMethod ?? "AAVSO VSP";

        // Session fields (ObsDate, lat/lon/elev, planet params, star coords) are not
        // restored — the user must re-read the FITS header to populate them each session.

        // MObs
        if (!string.IsNullOrEmpty(cfg.MobsDownloadDir))
            MObs.DownloadFolder = cfg.MobsDownloadDir;
        MObs.IsDefaultFolder  = cfg.MobsIsDefaultFolder;
        MObs.LookbackDays     = cfg.MobsLookbackDays;

        // Frame Analysis
        FrameAnalysis.FlagSigma = (decimal)cfg.FlagSigma;

        // Automation
        Automation.MonitorFolder    = cfg.AutoMonitorFolder;
        Automation.StartTime        = cfg.AutoStartTime;

        // Results / AAVSO credentials
        Results.AavsoUsername     = cfg.AavsoUsername;
        Results.SavePassword      = cfg.SavePassword;
        Results.AavsoPassword     = cfg.SavePassword ? cfg.AavsoPassword : "";
        Results.AutoAnswerPrompts = cfg.AutoAnswerPrompts;

        // Restore output file paths from the last session's SaveDir so the
        // Upload button is available without needing to re-run EXOTIC.
        if (!string.IsNullOrEmpty(cfg.LastUi.SaveDir) && Directory.Exists(cfg.LastUi.SaveDir))
            Results.ScanOutputFiles(cfg.LastUi.SaveDir);
    }

    public void SaveOnExit()
    {
        SnapshotToConfig();
        ConfigService.Save(_cfg);
    }

    /// <summary>
    /// Runs once at startup (fire-and-forget) to populate the EXOTIC version
    /// label in the Results tab without blocking the UI.
    /// </summary>
    private async Task ProbeAndSetExoticVersionAsync()
    {
        try
        {
            var ver = await ExoticInstallService.GetExoticVersionAsync(_cfg.PythonExePath!);
            if (ver is not null)
            {
                _detectedExoticVersion = ver;
                Results.ExoticVersion  = $"EXOTIC {ver}";
            }
        }
        catch { /* best-effort — version label stays at default */ }
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
            PixelScale   = EquipmentTarget.PixelScale,
            Notes        = EquipmentTarget.Notes,
            CompMethod   = EquipmentTarget.SelectedCompMethod,
        };

        _cfg.AavsoCode      = [.. Observation.LiveAavsoCodes];
        _cfg.SecondaryCodes = [.. Observation.LiveSecondaryCodes];
        _cfg.Observatories  = [.. Observation.LiveObservatories];
        _cfg.MobsDownloadDir       = MObs.DownloadFolder;
        _cfg.MobsIsDefaultFolder   = MObs.IsDefaultFolder;
        _cfg.MobsLookbackDays      = (int)MObs.LookbackDays;
        _cfg.FlagSigma             = (double)FrameAnalysis.FlagSigma;
        _cfg.AutoMonitorFolder    = Automation.MonitorFolder;
        _cfg.AutoStartTime        = Automation.StartTime;
        _cfg.AavsoUsername         = Results.AavsoUsername;
        _cfg.SavePassword          = Results.SavePassword;
        _cfg.AavsoPassword         = Results.SavePassword ? Results.AavsoPassword : "";
        _cfg.AutoAnswerPrompts     = Results.AutoAnswerPrompts;
    }

    // ── inits.json builder ────────────────────────────────────────────────────

    /// <summary>
    /// Returns true when the EXOTIC version is 4.3.2 or later.
    /// Handles version strings with pre-release suffixes (e.g. "4.3.2.dev0").
    /// Defaults to false (safe for 4.3.1) when version is null or unparseable.
    /// </summary>
    private static bool IsExotic432OrLater(string? versionString)
    {
        if (string.IsNullOrWhiteSpace(versionString)) return false;
        var parts = versionString.Trim().Split('.');
        if (!int.TryParse(parts[0], out var major)) return false;
        int minor = 0, patch = 0;
        if (parts.Length >= 2 && !int.TryParse(parts[1], out minor)) return false;
        if (parts.Length >= 3)
        {
            // patch segment may have a non-numeric suffix (e.g. "2dev0"); extract leading digits
            var patchStr = new string(parts[2].TakeWhile(char.IsDigit).ToArray());
            if (!int.TryParse(patchStr, out patch)) return false;
        }
        return (major > 4) ||
               (major == 4 && minor > 3) ||
               (major == 4 && minor == 3 && patch >= 2);
    }

    /// <summary>Trims a reported EXOTIC version like "4.3.2.dev98+g7dd88b98f.d20260724" down to "4.3.2".</summary>
    private static string ExoticVersionTag(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return "unknown";
        var parts = version.Split('.');
        return parts.Length >= 3 ? string.Join(".", parts[0], parts[1], parts[2]) : version;
    }

    /// <summary>
    /// "{Target Name}_{Obs Date}_Results{n}_{ExoticVersion}" — n is the count of existing subfolders
    /// for this exact target+date already in baseDir, plus one, so repeat runs never overwrite
    /// each other's output. The counter runs across all EXOTIC versions together.
    /// </summary>
    private string BuildRunResultsDirName(string baseDir)
    {
        var planet  = string.IsNullOrWhiteSpace(EquipmentTarget.PlanetName) ? "Target" : EquipmentTarget.PlanetName.Trim();
        var obsDate = string.IsNullOrWhiteSpace(Observation.ObsDate) ? DateTime.Now.ToString("dd-MMM-yyyy").ToUpperInvariant() : Observation.ObsDate.Trim();
        var version = ExoticVersionTag(_detectedExoticVersion);

        static string Sanitize(string s)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }

        var prefix = Sanitize($"{planet}_{obsDate}_Results");
        int n = 1;
        if (Directory.Exists(baseDir))
            n = Directory.GetDirectories(baseDir)
                    .Count(d => Path.GetFileName(d).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) + 1;

        return $"{prefix}{n}_{version}";
    }

    /// <summary>Extra fields needed only for a -pre (--prereduced) EXOTIC run, built from an imported AAVSO report.</summary>
    public record PreReducedExtras(
        string PreredFilePath, string FileTimeFormat, string FileUnits, double? Exposure,
        string? CompStarRa, string? CompStarDec, string? CompStarX, string? CompStarY);

    private JsonObject BuildInits(PreReducedExtras? preReduced = null)
    {
        static JsonNode? N(string s)  => string.IsNullOrWhiteSpace(s) ? null : JsonValue.Create(s);
        // InvariantCulture matters here: without it, double.TryParse uses the OS's current
        // locale, so a normal period-decimal value (e.g. from NEA) can silently fail to parse
        // — or worse, get mis-parsed via the wrong decimal/thousands separator — on a non-US
        // Windows locale, writing JSON null into inits.json with no visible error anywhere.
        static JsonNode? NF(string s) => NumericParseService.TryParse(s, out var d) ? JsonValue.Create(d) : null;

        var et = EquipmentTarget;
        var ob = Observation;

        var pp = new JsonObject
        {
            ["Target Star RA"]  = NF(et.TargetRa),
            ["Target Star Dec"] = NF(et.TargetDec),
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
            ["Add Comparison Stars from AAVSO? (y/n)"] = JsonValue.Create(
                preReduced is not null ? "n" :
                string.IsNullOrWhiteSpace(et.CompXY) ? "y" :
                IsExotic432OrLater(_detectedExoticVersion) ? "n" : "y"),
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
            ["stellar_variability_only"] = JsonValue.Create(et.StellarVariabilityOnly),
        };

        // Ensemble comparison-star options (Settings → Advanced) — unlike the four CLI
        // switches in LaunchExoticAsync, these are inits.json optional_info keys, read via
        // exotic_infoDict.get(...) rather than argparse. Both are dev-branch-only (confirmed
        // absent from the real Stable 4.3.1 wheel), but since an unrecognized inits.json key
        // is simply never read rather than causing an error the way an unrecognized CLI
        // argument does, writing them unconditionally would already be harmless on Stable —
        // still gated here for consistency with the CLI-flag features and clearer intent.
        if (_detectedExoticVersion?.StartsWith("4.3.2.dev", StringComparison.OrdinalIgnoreCase) == true)
        {
            optional["use_ensemble_photometry_rather_than_single_comp"] = JsonValue.Create(_cfg.UseEnsemblePhotometry);
            optional["use_exactly_the_comps_provided"] = JsonValue.Create(_cfg.UseExactlyTheCompsProvided);
        }

        if (preReduced is not null)
        {
            optional["Pre-reduced File:"] = JsonValue.Create(preReduced.PreredFilePath);
            optional["Pre-reduced File Time Format (BJD_TDB, JD_UTC, MJD_UTC)"] = JsonValue.Create(preReduced.FileTimeFormat);
            optional["Pre-reduced File Units of Flux (flux, magnitude, millimagnitude)"] = JsonValue.Create(preReduced.FileUnits);
            if (preReduced.Exposure is double exp) optional["Exposure Time (s)"] = JsonValue.Create(exp);
            optional["Comparison Star used in Photometry (leave blank if none)"] = new JsonObject
            {
                ["ra"]  = N(preReduced.CompStarRa  ?? ""),
                ["dec"] = N(preReduced.CompStarDec ?? ""),
                ["x"]   = N(preReduced.CompStarX   ?? ""),
                ["y"]   = N(preReduced.CompStarY   ?? ""),
            };
        }

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

    private void WriteInits(string path, PreReducedExtras? preReduced = null)
    {
        var opts = new JsonSerializerOptions { WriteIndented = true };
        var json = BuildInits(preReduced).ToJsonString(opts);
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
        static bool B(JsonNode? n)
        {
            if (n is null) return false;
            try { return n.GetValue<bool>(); } catch { return false; }
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
        EquipmentTarget.StellarVariabilityOnly = B(op["stellar_variability_only"]);

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
    private async Task SaveAndRun() => await SaveAndRunAsync("-red");

    /// <summary>
    /// Quick Look (-ql) — EXOTIC 4.3.2 pre-release only. Runs a fast least-squares fit instead
    /// of full ultranest posterior inference; no AAVSO report is produced. Reuses the exact
    /// same validation, inits.json build, and launch path as a normal Save &amp; Run — only the
    /// mode flag passed to EXOTIC differs.
    /// </summary>
    [RelayCommand]
    private async Task QuickLook()
    {
        if (!IsPrereleaseActive)
        {
            if (ShowErrorFunc is not null)
                await ShowErrorFunc("Quick Look Unavailable", QuickLookRequirementNote);
            return;
        }
        await SaveAndRunAsync("-ql");
    }

    private async Task SaveAndRunAsync(string mode)
    {
        if (IsExoticRunning) return;

        if (Observation.FitsDirNeedsHeaderRead)
        {
            if (_isAutomationRunning)
            {
                SessionLogService.Write("[Automation] Aborted — FITS directory changed since last header read; Read FITS Header is required.");
                return;
            }
            if (ShowErrorFunc is not null)
                await ShowErrorFunc("Read FITS Header Required",
                    "The FITS Files Directory has changed since the last header read.\n\n" +
                    "Please click 'Read FITS Header' on the Parameters tab to update observation data and trigger a plate solve before running.");
            return;
        }

        // Switch to Results tab immediately so the user lands there for validation messages and the live log
        SelectTabFunc?.Invoke(3);

        // ── Validate all required fields before writing inits.json ───────────────
        // RequireNumeric catches a field that's non-blank but won't actually parse as a
        // number — e.g. a locale/typo issue — which previously passed this check silently,
        // then BuildInits()'s NF() helper wrote it into inits.json as JSON null, and the
        // failure only surfaced deep inside EXOTIC as a confusing "Warning: X is None."
        var missing = new System.Text.StringBuilder();
        void Require(string label, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                missing.AppendLine($"  • {label}");
        }
        void RequireNumeric(string label, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                missing.AppendLine($"  • {label}");
            else if (!NumericParseService.TryParse(value, out _))
                missing.AppendLine($"  • {label}  (\"{value}\" is not a valid number)");
        }

        var ob = Observation;
        var et = EquipmentTarget;

        // Observation tab
        Require("FITS Files Directory",              ob.FitsDir);
        Require("Save Plots Directory",              ob.SaveDir);
        Require("Observation Date",                  ob.ObsDate);
        RequireNumeric("Observatory Latitude",        ob.Latitude);
        RequireNumeric("Observatory Longitude",       ob.Longitude);

        // Equipment & Target tab
        Require("Camera Type",                       et.CameraType);
        Require("Pixel Binning",                     et.Binning);
        Require("Filter",                            et.Filter);
        RequireNumeric("Filter Min Wavelength (nm)",  et.FilterMin);
        RequireNumeric("Filter Max Wavelength (nm)",  et.FilterMax);
        Require("Pixel Scale (arcsec/px)",           et.PixelScale);
        Require("Target Star X,Y  (select on Image Analysis tab)",        et.TargetXY);
        Require("Comparison Star(s) X,Y  (select on Image Analysis tab)", et.CompXY);

        // Planet / stellar parameters
        Require("Planet Name",                       et.PlanetName);
        RequireNumeric("Target Star RA",              et.TargetRa);
        RequireNumeric("Target Star Dec",             et.TargetDec);
        Require("Host Star Name",                    et.HostStarName);
        RequireNumeric("Orbital Period",              et.OrbitalPeriod);
        RequireNumeric("Mid-Transit Time",            et.MidTransitTime);
        RequireNumeric("Rp/Rs",                       et.RpRs);
        RequireNumeric("a/Rs",                        et.ARs);
        RequireNumeric("Orbital Inclination",         et.Inclination);
        RequireNumeric("Star Effective Temperature (Teff)", et.Teff);
        RequireNumeric("Star Metallicity",            et.Metallicity);
        RequireNumeric("Star Surface Gravity (log g)", et.Logg);

        if (missing.Length > 0)
        {
            var missingFlat = missing.ToString().Trim().Replace(Environment.NewLine, ", ").TrimStart(',', ' ');
            SessionLogService.Write($"[Run] Validation failed — missing fields: {missingFlat}");
            if (_isAutomationRunning)
            {
                SessionLogService.Write($"[Automation] Aborted — required fields are empty: {missingFlat}");
                return;
            }
            if (ShowErrorFunc is not null)
                await ShowErrorFunc("Cannot Run EXOTIC",
                    "The following required fields are empty:\n\n" + missing.ToString().TrimEnd() +
                    "\n\nTip: Read FITS Header (Parameters tab) populates observation data and triggers a plate solve. Fetch from NEA populates planet and stellar parameters.");
            return;
        }

        // Rp/Rs sanity check — outside 0.01–0.35 almost certainly indicates a data error
        if (NumericParseService.TryParse(et.RpRs, out double rprsCheck) &&
            (rprsCheck < 0.01 || rprsCheck > 0.35))
        {
            SessionLogService.Write($"[Run] Validation failed — suspicious Rp/Rs = {rprsCheck:G6} (expected 0.01–0.35)");
            if (_isAutomationRunning)
            {
                SessionLogService.Write($"[Automation] Aborted — suspicious Rp/Rs value: {rprsCheck:G6}");
                return;
            }
            if (ShowErrorFunc is not null)
                await ShowErrorFunc("Suspicious Rp/Rs Value",
                    $"Rp/Rs = {rprsCheck:G6} is outside the expected range (0.01 – 0.35) for a transiting exoplanet.\n\n" +
                    "This value will likely produce an invalid result. Please verify and correct the Rp/Rs field before running.");
            return;
        }

        var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");

        // Resolve the ACTIVE environment's EXOTIC runtime ONCE, here, fresh for every run —
        // and reuse this exact same resolution for the actual EXOTIC launch below. This used
        // to be two independent lookups (a quick direct check here for the version, versus
        // ExoticRuntimeService.ResolveAsync's own multi-tier fallback chain inside
        // LaunchExoticAsync) that could genuinely disagree — the fallback chain can land on a
        // different install than a plain direct check does, so the version used to name the
        // Results folder wasn't guaranteed to be the version that actually ran. Resolving once
        // and passing the result through removes that possibility entirely, and also fixes the
        // older bug where the version was only probed once per session and went stale after
        // switching between Stable and Pre-release environments.
        var activeEnvForVersion  = _cfg.ExoticEnvironments.FirstOrDefault(e => e.Name == _cfg.ActiveEnvironmentName);
        var pythonPathForVersion = !string.IsNullOrWhiteSpace(activeEnvForVersion?.PythonExePath)
            ? activeEnvForVersion.PythonExePath
            : _cfg.PythonExePath;
        var resolvedRuntime = await ExoticRuntimeService.ResolveAsync(
            pythonPathForVersion, _cfg.ExoticExePath, CancellationToken.None);
        if (resolvedRuntime?.Version is not null)
        {
            _detectedExoticVersion = resolvedRuntime.Version;
            Results.ExoticVersion  = $"EXOTIC {resolvedRuntime.Version}";
        }

        // Each run writes into its own "{Target}_{ObsDate}_Results{n}_{ExoticVersion}" subfolder
        // of the configured Save Plots directory (inits.json included, so a run's full record stays
        // together), rather than overwriting the same output files on every run — ob.SaveDir itself
        // is restored to the user's base directory afterward.
        var baseSaveDir     = ob.SaveDir;
        var originalSaveDir = baseSaveDir;
        string? runSaveDir  = null;
        if (!string.IsNullOrWhiteSpace(baseSaveDir))
        {
            runSaveDir = Path.Combine(baseSaveDir, BuildRunResultsDirName(baseSaveDir));
            Directory.CreateDirectory(runSaveDir);
            ob.SaveDir = runSaveDir;
        }

        var path = Path.Combine(runSaveDir ?? InitsDefaultPath(), $"inits_{ts}.json");

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

        Results.IsQuickLookRun = mode == "-ql";

        try
        {
            await LaunchExoticAsync(path, mode: mode, preResolvedRuntime: resolvedRuntime);
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

            // ── Scan the run's own output subfolder after EXOTIC finishes ─────
            var saveDir = runSaveDir;
            ob.SaveDir  = originalSaveDir;
            if (!string.IsNullOrEmpty(saveDir))
            {
                FixAavsoSecondaryObscodes(saveDir);
                CreateWebObsCompatibleAidFile(saveDir);

                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    Results.LoadOutputImages(saveDir, EquipmentTarget.StellarVariabilityOnly);
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
            // Recursive: EXOTIC has moved this file to a different subfolder more than once
            // (confirmed 2026-08-29) — search the whole run output tree rather than assuming
            // it's directly in saveDir.
            foreach (var file in Directory.GetFiles(saveDir, "AAVSO_*.txt", SearchOption.AllDirectories))
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

    /// <summary>
    /// Whether this run's output directory actually contains something a user could use —
    /// a report, a light curve, or fitted parameters. EXOTIC can exit with code 0 (e.g. when
    /// its own internal require_comp_star check aborts the fit gracefully rather than crashing)
    /// while producing none of these, which the plain exit-code check alone can't tell apart
    /// from a genuine successful run.
    /// </summary>
    private static bool HasRealExoticOutput(string dir)
    {
        try
        {
            return Directory.Exists(dir) && (
                Directory.GetFiles(dir, "AAVSO_*.txt", SearchOption.AllDirectories).Length > 0 ||
                Directory.GetFiles(dir, "FinalParams_*.json", SearchOption.AllDirectories).Length > 0 ||
                Directory.GetFiles(dir, "FinalLightCurve_*.png", SearchOption.AllDirectories).Length > 0);
        }
        catch { return false; }
    }

    /// <summary>
    /// Finds this run's own EXOTIC-side log, regardless of which environment ran it: exotic.log
    /// directly in the output directory (the Stable build's convention) or the newest
    /// Diagnostics\EXOTIC_RunLog_*.log (the Pre-release build's separate, distinct convention —
    /// Stable never writes here and Pre-release never writes exotic.log). Returns null if neither
    /// exists, e.g. the process never got far enough to create either.
    /// </summary>
    private static string? FindExoticErrorLogPath(string dir)
    {
        try
        {
            var exoticLog = Path.Combine(dir, "exotic.log");
            if (File.Exists(exoticLog)) return exoticLog;

            var diagDir = Path.Combine(dir, "Diagnostics");
            if (!Directory.Exists(diagDir)) return null;
            return Directory.GetFiles(diagDir, "EXOTIC_RunLog_*.log")
                .OrderByDescending(File.GetLastWriteTime)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>
    /// Reads the given EXOTIC log and returns the last real error line — either an explicit
    /// log_info(..., error=True) message (e.g. "Error: require_comp_star is enabled...", used
    /// when EXOTIC stops itself deliberately) or, for an unhandled Python exception, the
    /// traceback's own final "SomeException: message" line (matched separately since a raw
    /// traceback line has no "Error:"-prefixed log_info wrapper at all — it's Python's own
    /// unformatted output appended straight to the log).
    /// </summary>
    private static string? TryReadLastErrorFromLog(string logPath)
    {
        try
        {
            var lines = File.ReadAllLines(logPath);
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                var trimmed  = lines[i].TrimStart();
                var excMatch = System.Text.RegularExpressions.Regex.Match(trimmed, @"^[A-Za-z_][\w.]*(Error|Exception):\s");
                var idx      = lines[i].IndexOf("Error:", StringComparison.Ordinal);
                if (!excMatch.Success && idx < 0) continue;

                var msg = excMatch.Success ? trimmed : lines[i][idx..].Trim();
                // The message can continue as plain indented text on following lines
                // (no further "timestamp [thread] LEVEL" prefix) — fold those in too.
                for (int j = i + 1; j < lines.Length && !System.Text.RegularExpressions.Regex.IsMatch(lines[j], @"^\d{4}-\d{2}-\d{2}T"); j++)
                    msg += " " + lines[j].Trim();
                return msg;
            }
            return null;
        }
        catch { return null; }
    }

    /// <summary>
    /// EXOTIC's own AID_AAVSO_*.txt writer (output_files.py) rounds MAG/MERR/CMAG to 5 decimal
    /// places. For a typical 2-digit-integer-part magnitude that's exactly 8 characters
    /// ("XX.XXXXX") — right at AAVSO's own field-width limit for those fields with zero margin
    /// for a sign or a 3-digit value — and implies unrealistic 0.00001-mag precision that
    /// WebObs 2.0's validator rejects with a decimal-places error. TransitLab doesn't generate
    /// this file (EXOTIC does), so rather than patch the installed EXOTIC package — not durable,
    /// wiped out on reinstall, doesn't help other users' installs — write a second, WebObs-safe
    /// copy alongside the original with MAG/MERR/CMAG re-rounded to a realistic 3 decimal
    /// places. The original AID_AAVSO_*.txt file is left completely untouched.
    /// </summary>
    private static void CreateWebObsCompatibleAidFile(string saveDir)
    {
        try
        {
            // Recursive for the same reason as FixAavsoSecondaryObscodes above — write the
            // WebObs-safe copy next to wherever the original actually was found (Path.GetDirectoryName
            // of the match), not into the hardcoded run root, so it lands alongside the original
            // regardless of which subfolder EXOTIC happened to put it in this version.
            foreach (var file in Directory.GetFiles(saveDir, "AID_AAVSO_*.txt", SearchOption.AllDirectories))
            {
                var fileName = Path.GetFileName(file);
                if (fileName.StartsWith("AID_AAVSO_WebObs2.0_", StringComparison.OrdinalIgnoreCase))
                    continue;   // already-generated output — don't reprocess on a re-scan

                var lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Length == 0 || lines[i][0] == '#') continue;

                    // #NAME,DATE,MAG,MERR,FILT,TRANS,MTYPE,CNAME,CMAG,KNAME,KMAG,AMASS,GROUP,CHART,NOTES
                    var fields = lines[i].Split(',');
                    if (fields.Length < 9) continue;
                    RoundField(fields, 2);   // MAG
                    RoundField(fields, 3);   // MERR
                    RoundField(fields, 8);   // CMAG
                    lines[i] = string.Join(",", fields);
                }

                var newName = "AID_AAVSO_WebObs2.0_" + fileName["AID_AAVSO_".Length..];
                var fileDir = Path.GetDirectoryName(file) ?? saveDir;
                File.WriteAllLines(Path.Combine(fileDir, newName), lines);
            }
        }
        catch { }

        static void RoundField(string[] fields, int index)
        {
            if (NumericParseService.TryParse(fields[index], out var value))
                fields[index] = value.ToString("0.0##", System.Globalization.CultureInfo.InvariantCulture);
        }
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

    private async Task LaunchExoticAsync(string initsPath, bool isRetry = false, string mode = "-red",
        ExoticRuntime? preResolvedRuntime = null)
    {
        _exoticCts = new CancellationTokenSource();

        // If the caller already resolved the runtime (e.g. SaveAndRunAsync, so the EXOTIC
        // version used for the Results-folder name is guaranteed to be the same one that
        // actually runs — see the comment there), reuse it instead of resolving again.
        // ResolveAsync has its own multi-tier fallback chain (saved path → ExoticFinder →
        // general PATH/conda search), so a second independent call here could in principle
        // land on a different install than the first one did.
        var runtime = preResolvedRuntime;
        if (runtime is null)
        {
            // Prefer the active EXOTIC environment (v2.8.0+ multi-install support) over the
            // legacy single global PythonExePath — falls back to it automatically when no
            // environments are configured yet (e.g. upgrading users who haven't visited Setup).
            var activeEnv = _cfg.ExoticEnvironments.FirstOrDefault(e => e.Name == _cfg.ActiveEnvironmentName);
            var savedPythonPath = !string.IsNullOrWhiteSpace(activeEnv?.PythonExePath)
                ? activeEnv.PythonExePath
                : _cfg.PythonExePath;

            runtime = await ExoticRuntimeService.ResolveAsync(
                savedPythonPath,
                _cfg.ExoticExePath,
                _exoticCts.Token);
        }

        if (runtime is null)
        {
            if (_isAutomationRunning)
            {
                SessionLogService.Write("[Automation] Aborted — EXOTIC runtime could not be resolved automatically.");
                _exoticCts = null;
                return;
            }
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

        // Cache the resolved EXOTIC version for future WriteInits() calls
        // and update the Results tab label.
        if (runtime.Version is not null)
        {
            _detectedExoticVersion = runtime.Version;
            Results.ExoticVersion  = $"EXOTIC {runtime.Version}";
        }

        // Switch to Results tab (index 3: Data=0, ImageAnalysis=1, Parameters=2, Results=3, Visualizer=4, History=5)
        SelectTabFunc?.Invoke(3);

        Results.ClearLog();
        Results.ClearOutputImages();
        IsExoticRunning          = true;
        Results.IsExoticRunning  = true;
        ExoticStatusText         = "Running…";
        _ldtkCorruptionAlerted      = false;
        _sawLdtkTracebackFrame      = false;
        _ldtkNetworkFailureDetected = false;

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

        AppendLog($"▶  {runtime.PythonExePath} -u -m exotic.exotic {mode} \"{initsPath}\"\n\n");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = runtime.PythonExePath,
                ArgumentList           = { "-u", "-m", "exotic.exotic", mode, initsPath },
                WorkingDirectory       = System.IO.Path.GetDirectoryName(initsPath) ?? System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                RedirectStandardInput  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding  = Encoding.UTF8,
            };

            // Advanced switches (Settings → Advanced) — all four only exist in the EXOTIC
            // 4.3.2 pre-release dev build; Stable errors on an unrecognized argument, so these
            // are only appended when the runtime actually resolved to that build, regardless
            // of whether the toggle happens to be on.
            if (runtime.Version?.StartsWith("4.3.2.dev", StringComparison.OrdinalIgnoreCase) == true)
            {
                if (_cfg.NonInteractiveRun)
                    psi.ArgumentList.Add("--non-interactive-run");
                if (_cfg.UseNextAstroVariabilityServer)
                    psi.ArgumentList.Add("--use-nextastro-variability-server");
                if (_cfg.MultiprocessTransformations is int mpt)
                {
                    psi.ArgumentList.Add("--multiprocess-transformations");
                    psi.ArgumentList.Add(mpt.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                if (_cfg.MultiprocessLightcurveFits is int mplf)
                {
                    psi.ArgumentList.Add("--multiprocess-lightcurve-fits");
                    psi.ArgumentList.Add(mplf.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }

            foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;

            // Delete stale exotic.log so EXOTIC's TimedRotatingFileHandler has nothing
            // to rotate on cross-day runs (avoids PermissionError: [WinError 32] spam).
            var exoticLogPath = Path.Combine(psi.WorkingDirectory, "exotic.log");
            if (File.Exists(exoticLogPath))
                try { File.Delete(exoticLogPath); } catch { }

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
                    CheckLdtkCorruption(line);
                    AppendLog(line + "\n");
                }
            });

            // Stdout: interactive — surfaces EXOTIC prompts to the user in the Results tab
            await ReadStdoutInteractiveAsync(proc, _exoticCts.Token);

            await stderrTask;
            await Task.Run(() => proc.WaitForExit());

            var rc              = proc.ExitCode;
            var producedRealOutput = rc != 0 || HasRealExoticOutput(psi.WorkingDirectory);
            var errorLogPath    = FindExoticErrorLogPath(psi.WorkingDirectory);

            if (rc == 0 && !producedRealOutput)
            {
                AppendLog("\n⚠  EXOTIC exited normally, but produced no report, light curve, or fitted parameters.\n");
                var reason = errorLogPath is not null ? TryReadLastErrorFromLog(errorLogPath) : null;
                AppendLog(reason is not null
                    ? $"   Likely cause (from EXOTIC's own run log): {reason}\n"
                    : "   Check that run's Diagnostics folder for EXOTIC's own run log — it may explain why nothing was produced.\n");
            }
            else
            {
                AppendLog(rc == 0
                    ? "\n✓  EXOTIC finished successfully.\n"
                    : $"\n✗  EXOTIC exited with code {rc}.\n");
            }
            if (rc != 0 && errorLogPath is not null)
            {
                AppendLog($"   Detailed error log: {errorLogPath}\n");
                var reason = TryReadLastErrorFromLog(errorLogPath);
                if (reason is not null)
                    AppendLog($"   Likely cause: {reason}\n");
            }
        }
        catch (OperationCanceledException)
        {
            AppendLog("\n⏹  EXOTIC run cancelled.\n");
        }
        finally
        {
            _exoticProcess   = null;
            IsExoticRunning         = false;
            Results.IsExoticRunning = false;
            ExoticStatusText        = "";
        }

        if (_ldtkNetworkFailureDetected && !isRetry)
        {
            var fetched = await TryFetchLdtkFallbackForCurrentTargetAsync();
            if (fetched)
            {
                AppendLog("\n🔁  Retrying EXOTIC now that the required limb-darkening data has been fetched via HTTPS…\n");
                await LaunchExoticAsync(initsPath, isRetry: true, mode: mode);
            }
        }
    }

    /// <summary>
    /// Fetches just the ldtk grid files the current target needs (via the HTTPS PHOENIX
    /// mirrors) when EXOTIC's FTP connection to the limb-darkening server fails outright
    /// — e.g. a firewall blocking passive-mode FTP data connections. Used to automatically
    /// recover from that failure and retry the run once, without a full ~2 GB download.
    /// </summary>
    private async Task<bool> TryFetchLdtkFallbackForCurrentTargetAsync()
    {
        try
        {
            double.TryParse(EquipmentTarget.Teff, out var teff);
            double.TryParse(EquipmentTarget.TeffPlus, out var teffPlus);
            double.TryParse(EquipmentTarget.TeffMinus, out var teffMinus);
            double.TryParse(EquipmentTarget.Logg, out var logg);
            double.TryParse(EquipmentTarget.LoggPlus, out var loggPlus);
            double.TryParse(EquipmentTarget.LoggMinus, out var loggMinus);
            double.TryParse(EquipmentTarget.Metallicity, out var z);
            double.TryParse(EquipmentTarget.MetallicityPlus, out var zPlus);
            double.TryParse(EquipmentTarget.MetallicityMinus, out var zMinus);

            if (teff <= 0)
            {
                AppendLog("⚠  Could not determine the target's Teff/logg/metallicity — skipping automatic ldtk fallback.\n");
                return false;
            }

            var python = await ExoticInstallService.FindPythonAsync();
            if (python is null)
            {
                AppendLog("⚠  Python not found — cannot fetch ldtk fallback data automatically.\n");
                return false;
            }

            AppendLog("⟳  Fetching the required limb-darkening data via an HTTPS mirror…\n");
            var progress = new Progress<string>(msg => AppendLog(msg + "\n"));
            await ExoticInstallService.FetchLdtkFilesForTargetAsync(
                python.ExePath,
                teff, Math.Max(teffPlus, teffMinus),
                logg, Math.Max(loggPlus, loggMinus),
                z, Math.Max(zPlus, zMinus),
                progress);
            return true;
        }
        catch (Exception ex)
        {
            AppendLog($"✗  ldtk HTTPS fallback failed: {ex.Message}\n");
            return false;
        }
    }

    [RelayCommand]
    private void CancelExotic()
    {
        SessionLogService.Write("[Run] User cancelled EXOTIC run.");
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
                CheckLdtkCorruption(line);
                AppendLog(line);
                buf.Clear();
            }
            else if (ch == '\r')
            {
                // Carriage return: EXOTIC spinner frame — update UI in place, no session log
                var frame = buf.ToString().TrimEnd('\r');
                if (!string.IsNullOrWhiteSpace(frame))
                    UpdateSpinnerLine(frame);
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

    /// <summary>
    /// Detects the ldtk "Empty or corrupt FITS file" crash (a corrupted/incomplete
    /// limb-darkening model cache file, unrelated to the user's own FITS data) and
    /// points the user at Tools → Clear ldtk Cache instead of leaving them with a
    /// raw Python traceback.
    /// </summary>
    private void CheckLdtkCorruption(string line)
    {
        // Track whether we're currently inside an ldtk stack frame in a traceback, so a
        // generic network-error line a few lines later can be attributed to ldtk specifically
        // rather than an unrelated timeout (e.g. an NEA or AAVSO query).
        if (line.Contains("ldtk/client.py", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("ldtk\\client.py", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("ldtk/ldtk.py", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("ldtk\\ldtk.py", StringComparison.OrdinalIgnoreCase))
        {
            _sawLdtkTracebackFrame = true;
        }

        if (!_ldtkCorruptionAlerted && line.Contains("Empty or corrupt FITS file"))
        {
            _ldtkCorruptionAlerted = true;
            if (ShowErrorFunc is not null)
                Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
                    await ShowErrorFunc("Limb-Darkening Cache Corrupted",
                        "EXOTIC hit a corrupted or incomplete cache file used for limb-darkening " +
                        "calculations. This is not a problem with your FITS data or with TransitLab " +
                        "— it's a local file left over from an interrupted download.\n\n" +
                        "Recommended fix: go to Tools → Clear ldtk Cache, then re-run this reduction."));
            return;
        }

        if (!_ldtkNetworkFailureDetected && _sawLdtkTracebackFrame &&
            (line.Contains("TimeoutError") ||
             line.Contains("ConnectionRefusedError") ||
             line.Contains("[Errno 60]") ||
             line.Contains("[Errno 61]") ||
             (line.Contains("OSError") && line.Contains("timed out"))))
        {
            _ldtkNetworkFailureDetected = true;
            AppendLog("\n⚠  ldtk could not reach its FTP server (likely a network/firewall issue) — " +
                       "TransitLab will fetch the required data via an HTTPS mirror and retry automatically.\n");
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

    /// <summary>
    /// Called for each \r-terminated EXOTIC spinner frame.
    /// Updates the UI in place — no session log entry (spinner frames are transient).
    /// </summary>
    private void UpdateSpinnerLine(string text) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            Results.UpdateSpinnerLine(text));

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
        SessionLogService.Write("[Session] User clicked Save inits Only");
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
        SessionLogService.Write("[Session] User clicked Load previous inits");
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

    // ── Import AAVSO Report (-pre / --prereduced) ─────────────────────────────

    /// <summary>
    /// Re-fit an existing "AAVSO_&lt;planet&gt;_&lt;date&gt;.txt" Exoplanet Watch report via EXOTIC's
    /// -pre mode — no FITS files, comp star selection, or plate solve needed. The orbital solution
    /// (period, Rp/Rs, a/Rs, inc, ecc, Tc) is recovered exactly from the file's own #PRIORS-XC /
    /// #RESULTS-XC metadata; only stellar parameters (Teff/logg/FeH, needed solely for EXOTIC's own
    /// limb-darkening recompute) come from a fresh NASA Exoplanet Archive query.
    /// </summary>
    [RelayCommand]
    private async Task ImportAavsoReport()
    {
        SessionLogService.Write("[Run] User clicked Import AAVSO Report");
        if (OpenAavsoFilePickerFunc is null) return;

        var path = await OpenAavsoFilePickerFunc(
            string.IsNullOrEmpty(_cfg.LastInitsDir) ? InitsDefaultPath() : _cfg.LastInitsDir);
        if (path is null) return;

        AavsoReportImportService.AavsoReportData data;
        try
        {
            data = AavsoReportImportService.Parse(path);
        }
        catch (Exception ex)
        {
            SessionLogService.Write($"[Run] Import AAVSO Report — ERROR parsing {Path.GetFileName(path)}: {ex.Message}");
            if (ShowErrorFunc is not null)
                await ShowErrorFunc("Can't Import This File", ex.Message);
            return;
        }

        var et = EquipmentTarget;
        var ob = Observation;

        et.PlanetName = data.PlanetName;
        await et.FetchFromNeaAsync();

        if (et.NeaStatus.StartsWith('✗'))
        {
            SessionLogService.Write($"[Run] Import AAVSO Report — aborted, NEA lookup failed for stellar parameters: {et.NeaStatus}");
            if (ShowErrorFunc is not null)
                await ShowErrorFunc("Can't Import This File",
                    $"The orbital solution was recovered from the file, but a NASA Exoplanet Archive lookup for " +
                    $"\"{data.PlanetName}\" (needed only for stellar Teff/logg/FeH, which drive limb-darkening) failed:\n\n{et.NeaStatus}\n\n" +
                    "Correct the planet name in this file or try again once NEA is reachable.");
            return;
        }

        static string S(double v)  => v.ToString(System.Globalization.CultureInfo.InvariantCulture);
        static string? SU(double? v) => v is double d ? S(d) : null;

        // Override the fit-driving parameters with the file's own exact original values —
        // NEA is only trusted above for stellar parameters, never for the orbital solution.
        et.OrbitalPeriod       = S(data.Period);
        et.OrbitalPeriodUnc    = SU(data.PeriodUnc) ?? "";
        et.RpRs                = S(data.RpRs);
        et.RpRsUnc             = SU(data.RpRsUnc) ?? "";
        et.ARs                 = S(data.ARs);
        et.ARsUnc              = SU(data.ARsUnc) ?? "";
        et.Inclination         = S(data.Inc);
        et.InclinationUnc      = SU(data.IncUnc) ?? "";
        et.Eccentricity        = S(data.Ecc);
        et.MidTransitTime      = S(data.Tc);
        et.MidTransitTimeUnc   = SU(data.TcUnc) ?? "";
        et.HostStarName        = string.IsNullOrWhiteSpace(et.HostStarName) ? data.HostStarName : et.HostStarName;

        et.Filter      = data.Filter;
        et.CameraType  = data.Camera;
        et.Binning     = data.Binning;
        et.Notes       = data.Notes;
        et.FilterMin   = SU(data.WlMin) ?? "";
        et.FilterMax   = SU(data.WlMax) ?? "";
        ob.AavsoCode      = data.ObsCode;
        ob.SecondaryCode  = data.SecondaryObsCodes;

        var preReduced = new PreReducedExtras(
            PreredFilePath: path,
            FileTimeFormat: data.FileTimeFormat,
            FileUnits:      data.FileUnits,
            Exposure:       data.Exposure,
            CompStarRa:     data.CompStarRa, CompStarDec: data.CompStarDec,
            CompStarX:      data.CompStarX,  CompStarY:   data.CompStarY);

        // Output into a fresh subfolder next to the source file — never overwrite the
        // AAVSO report being imported (a rerun would reproduce an identically-named file).
        var originalSaveDir = ob.SaveDir;
        var reimportDir = Path.Combine(
            Path.GetDirectoryName(path) ?? InitsDefaultPath(),
            $"pre_import_{DateTime.Now:yyyyMMdd_HHmmss}");
        Directory.CreateDirectory(reimportDir);
        ob.SaveDir = reimportDir;

        var initsPath = Path.Combine(reimportDir, $"inits_{DateTime.Now:yyyyMMdd_HHmmss}.json");
        WriteInits(initsPath, preReduced);
        RecordSession(initsPath);
        SessionLogService.Write($"[Run] Import AAVSO Report — starting EXOTIC -pre — planet: \"{et.PlanetName}\", source: {path}, output: {reimportDir}");

        Results.IsQuickLookRun = false;

        try
        {
            await LaunchExoticAsync(initsPath, mode: "-pre");
        }
        finally
        {
            ob.SaveDir = originalSaveDir;
            FixAavsoSecondaryObscodes(reimportDir);
            CreateWebObsCompatibleAidFile(reimportDir);
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                Results.LoadOutputImages(reimportDir, EquipmentTarget.StellarVariabilityOnly);
                Results.ScanOutputFiles(reimportDir);
            });
        }
    }

    private void RecordSubmissionFromFinalParams()
    {
        // Observation.SaveDir has already been reset to the base Save Plots directory by the
        // time this runs (Submit is a separate, later click) — the actual FinalParams_*.json
        // lives in the run's own per-run output subfolder (v2.8.0+), which only Results'
        // last-scanned report path still knows. Using Observation.SaveDir here silently found
        // nothing and never added a History row. Fall back to it only if the report path is
        // somehow unavailable.
        var reportDir = !string.IsNullOrEmpty(Results.ReportFullPath)
            ? Path.GetDirectoryName(Results.ReportFullPath)
            : Observation.SaveDir;
        if (string.IsNullOrEmpty(reportDir)) return;

        // EXOTIC's dev keeps moving FinalParams_*.json to different subfolders between versions
        // (confirmed twice now: a bare run root, then an "AAVSO_Files"-sibling layout) — rather
        // than chasing each new location by name, search the whole tree under the run's parent
        // folder recursively. "Most recently written" (below) picks the right file even though
        // this can also pick up FinalParams_*.json from older runs sitting in sibling folders.
        var searchRoot = Path.GetDirectoryName(reportDir) ?? reportDir;
        var candidates = Directory.Exists(searchRoot)
            ? Directory.GetFiles(searchRoot, "FinalParams_*.json", SearchOption.AllDirectories)
            : [];
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

            // Try both key names for fields renamed in EXOTIC 4.3.2
            static (string Val, string Unc) ParseVUAny(JsonElement el, params string[] keys)
            {
                foreach (var k in keys) { var r = ParseVU(el, k); if (r.Val != "") return r; }
                return ("", "");
            }

            var (tmidV,  tmidU)  = ParseVU(pp, "Mid-Transit Time (Tmid)");
            var (depthV, depthU) = ParseVUAny(pp,
                "Radius-ratio area depth (Rp/R*)^2",   // EXOTIC 4.3.2+
                "Transit depth (Rp/Rs)^2");              // EXOTIC 4.3.1 and earlier
            var (incV,   _)      = ParseVU(pp, "Orbital Inclination (inc)");
            var (durV,   _)      = ParseVU(pp, "Transit Duration (day)");

            // Scatter key renamed in EXOTIC 4.3.2
            string scatter = "";
            foreach (var scatterKey in new[] {
                "Residual scatter around full model fit",       // EXOTIC 4.3.2+
                "Scatter in the residuals of the lightcurve fit is" }) // EXOTIC 4.3.1
            {
                if (pp.TryGetProperty(scatterKey, out var sv))
                { scatter = sv.ToString().Replace("%", "").Trim(); break; }
            }

            // Rp/Rs: read directly if available (EXOTIC 4.3.2+), otherwise derive from depth
            var (rpRsDirect, rpRsDirectUnc) = ParseVUAny(pp,
                "Ratio of Planet to Stellar Radius (Rp/R*)");  // EXOTIC 4.3.2+
            string rpRs, rpRsUnc;
            if (!string.IsNullOrEmpty(rpRsDirect))
            {
                rpRs    = rpRsDirect;
                rpRsUnc = rpRsDirectUnc;
            }
            else
            {
                // Derive from depth percentage: depth% = (Rp/Rs)^2 * 100
                rpRs = depthV; rpRsUnc = depthU;
                if (double.TryParse(depthV, out var dv) &&
                    double.TryParse(depthU, out var du) && dv > 0.1)
                {
                    rpRs    = (dv / 100.0).ToString("F6");
                    rpRsUnc = (du / 100.0).ToString("F6");
                }
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
        SessionLogService.Write("[Session] User clicked Clear All Fields");
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

    [RelayCommand]
    private async Task ClearLdtkCache()
    {
        SessionLogService.Write("[Tools] User clicked Clear ldtk Cache");

        if (ShowConfirmFunc is not null)
        {
            var proceed = await ShowConfirmFunc("Clear ldtk Cache",
                "This deletes EXOTIC's limb-darkening model cache (~/.ldtk).\n\n" +
                "It's safe to clear — EXOTIC/ldtk automatically re-downloads any needed " +
                "files the next time you run a reduction. This can fix crashes such as " +
                "\"Empty or corrupt FITS file\" caused by a corrupted or incomplete cache file.");
            if (!proceed) return;
        }

        try
        {
            var cleared = await ExoticInstallService.ClearLdtkCacheAsync();
            if (ShowInfoFunc is not null)
                await ShowInfoFunc("Clear ldtk Cache",
                    cleared
                        ? "ldtk cache cleared successfully."
                        : "No ldtk cache was found — nothing to clear.");
        }
        catch (Exception ex)
        {
            SessionLogService.Write($"[Tools] ERROR clearing ldtk cache: {ex.Message}");
            if (ShowErrorFunc is not null)
                await ShowErrorFunc("Clear ldtk Cache Failed", ex.Message);
        }
    }
}
