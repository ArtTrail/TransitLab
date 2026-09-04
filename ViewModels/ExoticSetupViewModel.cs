using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TransitLab.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TransitLab.ViewModels;

public partial class ExoticSetupViewModel : ViewModelBase
{
    // ── Status display ─────────────────────────────────────────────────────────
    // "Python" here is the shared base interpreter used only to create the isolated
    // environments below — it is not necessarily what actually runs a reduction.
    [ObservableProperty] private string  _pythonStatus     = "—";
    [ObservableProperty] private string  _pythonStatusIcon = "·";
    [ObservableProperty] private string  _headlineText     = "Click  Check System  to detect your Python and EXOTIC installation.";
    [ObservableProperty] private string  _logText          = "";

    // ── Pre-release's own base interpreter (Python 3.12+) ───────────────────────
    // Some EXOTIC branches now require Python >=3.12, which the shared Stable-oriented
    // interpreter above (searched/installed as 3.10) can't satisfy — so Pre-release gets
    // its own, entirely independent base-interpreter search/install below.
    [ObservableProperty] private string  _prereleasePythonStatus     = "—";
    [ObservableProperty] private string  _prereleasePythonStatusIcon = "·";
    [ObservableProperty] private bool    _prereleaseCanGetPython     = false;
    [ObservableProperty] private string  _prereleaseGetPythonLabel   = "Download & Install Python 3.12";

    // ── EXOTIC environments (v2.8.0+): Stable and Pre-release install side by side ──
    // Each is an isolated Python venv (see ExoticInstallService.CreateVenvAsync), so both
    // can stay installed simultaneously — switching is just picking which one runs next,
    // no uninstall/reinstall required.
    [ObservableProperty] private string _stableStatusIcon      = "·";
    [ObservableProperty] private string _stableStatusText      = "Not installed";
    [ObservableProperty] private bool   _stableCanInstall      = false;
    [ObservableProperty] private bool   _stableCanUninstall    = false;
    [ObservableProperty] private bool   _stableCanRemove       = false;
    [ObservableProperty] private bool   _stableIsActive        = true;

    [ObservableProperty] private string _prereleaseStatusIcon   = "·";
    [ObservableProperty] private string _prereleaseStatusText   = "Not installed";
    [ObservableProperty] private bool   _prereleaseCanInstall   = false;
    [ObservableProperty] private bool   _prereleaseCanUninstall = false;
    [ObservableProperty] private bool   _prereleaseCanRemove    = false;
    [ObservableProperty] private bool   _prereleaseIsActive     = false;

    private const string StableName     = "Stable";
    private const string PrereleaseName = "Pre-release";

    // Cached per-slot state (mirrors ExoticEnvironment; kept as plain fields since there
    // are always exactly these two known slots — see ConfigService.ExoticEnvironment for
    // the persisted, generic-list form this gets flattened to/from).
    private string _stableVenvPath = "";
    private string _stablePythonExe = "";
    private string _prereleaseVenvPath = "";
    private string _prereleasePythonExe = "";
    private string? _prereleaseBranchUrlInstalled;

    // True only once the exotic package itself has actually been confirmed present in the
    // slot's venv — NOT merely once the venv's own Python interpreter exists. EnsureSlotVenvAsync
    // sets *PythonExe as soon as the (still-empty) venv is created, before the exotic package
    // install even runs; without this separate flag, a network failure partway through
    // installing EXOTIC (e.g. pip/git unable to reach the index) still left the slot showing
    // a green checkmark and "Installed" even though EXOTIC was never actually there.
    private bool _stableConfirmedInstalled;
    private bool _prereleaseConfirmedInstalled;

    partial void OnStableIsActiveChanged(bool value)
    {
        if (!value) return;
        PrereleaseIsActive = false;
        ActiveEnvironmentChanged?.Invoke(StableName);
        UpdateCanCheckPrereleaseVersion();
    }

    partial void OnPrereleaseIsActiveChanged(bool value)
    {
        if (!value) return;
        StableIsActive = false;
        ActiveEnvironmentChanged?.Invoke(PrereleaseName);
        UpdateCanCheckPrereleaseVersion();
    }

    /// <summary>Fired whenever the user picks a different active environment; argument is "Stable" or "Pre-release".</summary>
    public Action<string>? ActiveEnvironmentChanged { get; set; }

    // ── Progress bar ───────────────────────────────────────────────────────────
    [ObservableProperty] private bool    _isProgressVisible = false;
    [ObservableProperty] private double  _progressValue     = 0;
    [ObservableProperty] private double  _progressMax       = 1;
    [ObservableProperty] private bool    _isProgressIndeterminate = false;
    [ObservableProperty] private string  _progressText      = "";

    // ── Button enable flags ────────────────────────────────────────────────────
    [ObservableProperty] private bool   _canCheck             = true;
    [ObservableProperty] private bool   _canGetPython         = false;
    [ObservableProperty] private string _getPythonLabel       = "Download & Install Python";
    [ObservableProperty] private bool   _canInstallBranch     = false;
    [ObservableProperty] private bool   _canCancel            = false;

    // ── Pre-release branch URL ─────────────────────────────────────────────────
    [ObservableProperty] private string _branchUrl = "";
    [ObservableProperty] private bool   _canCheckPrereleaseVersion = false;
    [ObservableProperty] private string _prereleaseVersionCheckStatus = "";

    public ObservableCollection<string> BranchUrls { get; } = new();

    partial void OnBranchUrlChanged(string value)
    {
        UpdateCanInstallBranch();
        UpdateCanCheckPrereleaseVersion();
    }

    // Installing the pre-release needs its OWN base Python (3.12+) to be ready (to create its venv) —
    // independent of Stable's shared _pythonExe.
    private void UpdateCanInstallBranch() =>
        CanInstallBranch = !string.IsNullOrWhiteSpace(BranchUrl) && _prereleasePythonBaseExe is not null;

    // Checking the branch's latest commit only needs a URL and Pre-release actually selected —
    // per the user's own spec, deliberately narrower than CanInstallBranch (which doesn't care
    // which environment is active) so the button only lights up once Pre-release is the one
    // being considered, not just because a URL happens to be typed in.
    private void UpdateCanCheckPrereleaseVersion() =>
        CanCheckPrereleaseVersion = PrereleaseIsActive && !string.IsNullOrWhiteSpace(BranchUrl);

    // ── Population from config ────────────────────────────────────────────────
    public void LoadFromConfig(IEnumerable<string> branchUrlsFromCfg)
    {
        BranchUrls.Clear();
        foreach (var u in branchUrlsFromCfg) BranchUrls.Add(u);
    }

    // ── Expose live list for config snapshot ──────────────────────────────────
    public IReadOnlyList<string> LiveBranchUrls => BranchUrls;

    // ── Environment persistence (v2.8.0+) ──────────────────────────────────────
    private string? _stableCachedVersion;
    private string? _prereleaseCachedVersion;

    public void LoadEnvironments(IEnumerable<ExoticEnvironment> envs, string activeName)
    {
        var stable = envs.FirstOrDefault(e => e.Name == StableName);
        var pre    = envs.FirstOrDefault(e => e.Name == PrereleaseName);

        if (stable is not null)
        {
            _stableVenvPath          = stable.VenvPath;
            _stablePythonExe         = stable.PythonExePath;
            _stableCachedVersion     = stable.CachedVersion;
            _stableConfirmedInstalled = true;   // persisted from a prior session's confirmed state
            RefreshStableStatus();
        }
        if (pre is not null)
        {
            _prereleaseVenvPath           = pre.VenvPath;
            _prereleasePythonExe          = pre.PythonExePath;
            _prereleaseCachedVersion      = pre.CachedVersion;
            _prereleaseBranchUrlInstalled = pre.SourceBranchUrl;
            _prereleaseConfirmedInstalled = true;   // persisted from a prior session's confirmed state
            SeedPrereleasePythonBaseExe(pre.BasePythonExePath);
            RefreshPrereleaseStatus();
        }

        PrereleaseIsActive = activeName == PrereleaseName;
        StableIsActive     = !PrereleaseIsActive;
    }

    /// <summary>
    /// Seeds the base Python path from a previous session's config, so Install/Reinstall is
    /// usable immediately on load rather than staying disabled until Check System is run.
    /// _pythonExe (the base interpreter used to create a new venv) is a session-only field —
    /// unlike each environment's own PythonExePath, it was never persisted or restored here,
    /// so StableCanInstall/PrereleaseCanInstall/CanInstallBranch (all gated on _pythonExe being
    /// non-null) stayed at their false default after every restart, even when both environments
    /// already showed as fully installed. Only re-enables the buttons if the saved path still
    /// exists on disk; otherwise leaves them disabled until Check System re-detects Python.
    /// </summary>
    public void SeedPythonExe(string? pythonExePath)
    {
        if (string.IsNullOrWhiteSpace(pythonExePath) || !File.Exists(pythonExePath))
            return;

        _pythonExe           = pythonExePath;
        StableCanInstall     = true;
        // NOTE: PrereleaseCanInstall is seeded independently by SeedPrereleasePythonBaseExe —
        // Pre-release no longer depends on this shared interpreter.
        UpdateCanInstallBranch();
    }

    /// <summary>Same purpose as <see cref="SeedPythonExe"/>, for Pre-release's own separately-persisted
    /// base interpreter (see <see cref="ExoticEnvironment.BasePythonExePath"/>).</summary>
    public void SeedPrereleasePythonBaseExe(string? pythonExePath)
    {
        if (string.IsNullOrWhiteSpace(pythonExePath) || !File.Exists(pythonExePath))
            return;

        _prereleasePythonBaseExe = pythonExePath;
        PrereleaseCanInstall     = true;
        UpdateCanInstallBranch();
    }

    /// <summary>Snapshot of both environment slots for saving into AppConfig.ExoticEnvironments.</summary>
    public List<ExoticEnvironment> SnapshotEnvironments()
    {
        var list = new List<ExoticEnvironment>();
        if (!string.IsNullOrEmpty(_stablePythonExe))
            list.Add(new ExoticEnvironment
            {
                Name = StableName, VenvPath = _stableVenvPath,
                PythonExePath = _stablePythonExe, CachedVersion = _stableCachedVersion,
            });
        if (!string.IsNullOrEmpty(_prereleasePythonExe))
            list.Add(new ExoticEnvironment
            {
                Name = PrereleaseName, VenvPath = _prereleaseVenvPath,
                PythonExePath = _prereleasePythonExe, CachedVersion = _prereleaseCachedVersion,
                SourceBranchUrl = _prereleaseBranchUrlInstalled,
                BasePythonExePath = _prereleasePythonBaseExe,
            });
        return list;
    }

    public string ActiveEnvironmentName => PrereleaseIsActive ? PrereleaseName : StableName;

    private void RefreshStableStatus()
    {
        var installed = !string.IsNullOrEmpty(_stablePythonExe) && _stableConfirmedInstalled;
        StableStatusIcon   = installed ? "✓" : "·";
        StableStatusText   = installed
            ? (_stableCachedVersion is not null ? $"EXOTIC {_stableCachedVersion}" : "Installed")
            : "Not installed";
        StableCanUninstall = installed;
        StableCanRemove    = installed && !string.IsNullOrEmpty(_stableVenvPath);
    }

    private void RefreshPrereleaseStatus()
    {
        var installed = !string.IsNullOrEmpty(_prereleasePythonExe) && _prereleaseConfirmedInstalled;
        PrereleaseStatusIcon   = installed ? "✓" : "·";
        PrereleaseStatusText   = installed
            ? (_prereleaseCachedVersion is not null ? $"EXOTIC {_prereleaseCachedVersion}  [pre-release]" : "Installed  [pre-release]")
            : "Not installed";
        PrereleaseCanUninstall = installed;
        PrereleaseCanRemove    = installed && !string.IsNullOrEmpty(_prereleaseVenvPath);
    }

    /// <summary>
    /// Heads-up shown after installing one environment: if the OTHER one is installed but
    /// still using the shared, non-isolated system Python (only possible for a "Stable" entry
    /// migrated from a pre-2.8.0 config), let the user know they can separate it too.
    /// </summary>
    private string OtherSlotNotIsolatedNote(bool justInstalledStable)
    {
        var otherPythonExe = justInstalledStable ? _prereleasePythonExe : _stablePythonExe;
        var otherVenvPath  = justInstalledStable ? _prereleaseVenvPath  : _stableVenvPath;
        var otherName      = justInstalledStable ? PrereleaseName : StableName;

        return !string.IsNullOrEmpty(otherPythonExe) && string.IsNullOrEmpty(otherVenvPath)
            ? $"  Note: {otherName} is still using the shared system install — click Install on that card too if you want it fully separated."
            : "";
    }

    /// <summary>Re-queries a slot's own cached python exe (if any) for its EXOTIC version and refreshes its status.</summary>
    private async Task RefreshEnvironmentVersionAsync(bool isStable, CancellationToken ct)
    {
        var pythonExe = isStable ? _stablePythonExe : _prereleasePythonExe;
        if (string.IsNullOrEmpty(pythonExe) || !File.Exists(pythonExe))
        {
            if (isStable) RefreshStableStatus(); else RefreshPrereleaseStatus();
            return;
        }

        var confirmed = await ExoticInstallService.IsExoticInstalledAsync(pythonExe, ct);
        var ver       = confirmed ? await ExoticInstallService.GetExoticVersionAsync(pythonExe, ct) : null;
        Log($"  {(isStable ? StableName : PrereleaseName)}: " + confirmed switch
        {
            true  when ver is not null => $"EXOTIC {ver}",
            true                        => "installed, but version could not be read",
            false                       => "not installed",
        });

        if (isStable) { _stableCachedVersion = ver; _stableConfirmedInstalled = confirmed; RefreshStableStatus(); }
        else          { _prereleaseCachedVersion = ver; _prereleaseConfirmedInstalled = confirmed; RefreshPrereleaseStatus(); }
    }

    /// <summary>
    /// ConfigService.MigrateToExoticEnvironments (v2.8.0+) wraps whatever was in the old
    /// single-Python config into a "Stable" entry without knowing what's actually installed
    /// there. If that turns out to be a dev/pre-release branch build (version string contains
    /// ".dev"), it was never really "Stable" — move it into the Pre-release slot instead so
    /// the label matches reality. Only applies to the still-unmanaged migrated entry (no venv
    /// of its own) with the Pre-release slot not already occupied by something else.
    /// </summary>
    private void ReclassifyMigratedDevBuildIfNeeded()
    {
        if (string.IsNullOrEmpty(_stablePythonExe)) return;
        if (!string.IsNullOrEmpty(_stableVenvPath)) return;
        if (!string.IsNullOrEmpty(_prereleasePythonExe)) return;
        if (_stableCachedVersion is null || !_stableCachedVersion.Contains(".dev", StringComparison.OrdinalIgnoreCase)) return;

        Log($"  \"{StableName}\" was actually a pre-release build ({_stableCachedVersion}) carried over from before v2.8.0 — moving it to \"{PrereleaseName}\".");
        _prereleasePythonExe          = _stablePythonExe;
        _prereleaseVenvPath           = _stableVenvPath;
        _prereleaseCachedVersion      = _stableCachedVersion;
        _prereleaseConfirmedInstalled = _stableConfirmedInstalled;

        _stablePythonExe          = "";
        _stableVenvPath           = "";
        _stableCachedVersion      = null;
        _stableConfirmedInstalled = false;

        RefreshStableStatus();
        RefreshPrereleaseStatus();
        PrereleaseIsActive = true;
        ConfigSaveCallback?.Invoke();
    }

    private void RememberBranchUrl()
    {
        var url = BranchUrl.Trim();
        if (string.IsNullOrWhiteSpace(url) || BranchUrls.Contains(url)) return;
        BranchUrls.Add(url);
        ConfigSaveCallback?.Invoke();
    }

    [RelayCommand]
    private void RemoveBranchUrl()
    {
        if (string.IsNullOrWhiteSpace(BranchUrl)) return;
        BranchUrls.Remove(BranchUrl);
        ConfigSaveCallback?.Invoke();
    }

    // ── Internal state ─────────────────────────────────────────────────────────
    private string? _pythonExe;
    private string? _prereleasePythonBaseExe;
    private CancellationTokenSource? _cts;

    // ── Platform ───────────────────────────────────────────────────────────────
    public bool IsLinux    { get; } = OperatingSystem.IsLinux();
    public bool IsNotLinux { get; } = !OperatingSystem.IsLinux();

    // ── Callbacks ──────────────────────────────────────────────────────────────
    public Action<string>?        PythonFoundCallback    { get; set; }
    public Action<string>?        ExoticExeFoundCallback { get; set; }
    public Func<string, Task>?    ShowWarningAsync       { get; set; }
    public Action?                ConfigSaveCallback     { get; set; }

    // ── Commands ───────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task CheckSystem()
    {
        SetBusy();
        ClearLog();
        HeadlineText        = "Scanning for Python and EXOTIC…";
        IsProgressVisible   = true;
        IsProgressIndeterminate = true;

        using var cts = new CancellationTokenSource();
        _cts = cts;
        try
        {
            Log("Searching for Python (≥ 3.10)…");
            var py = await ExoticInstallService.FindPythonAsync(cts.Token);
            if (py is null)
            {
                PythonStatusIcon = "✗";
                PythonStatus     = "Not found";
                Log("  Python not found.");
                HeadlineText     = OperatingSystem.IsWindows()
                    ? "Python is not installed.  Download and install it below, then install EXOTIC."
                    : "Python 3.10 is not installed.  Install it manually, then click Check System.";
                GetPythonLabel   = OperatingSystem.IsWindows() ? "Download & Install Python" : "Python Setup Help";
                CanGetPython     = true;
                // Existing installed environments (if any) don't need the base Python to
                // still exist — only creating a NEW one does. Just disable new installs.
                StableCanInstall     = false;
                PrereleaseCanInstall = false;
                UpdateCanInstallBranch();
            }
            else
            {
                Log($"  Found Python {py.Version} at {py.ExePath}");
                Log("  Verifying Python installation…");
                var isValid = await ExoticInstallService.ValidatePythonAsync(py.ExePath, cts.Token);
                if (!isValid)
                {
                    PythonStatusIcon = "✗";
                    PythonStatus     = $"Broken   {py.ExePath}";
                    Log("  Python installation is corrupt (cannot import core modules).");
                    Log("  Click  Get Python  to download and install a fresh copy.");
                    HeadlineText     = OperatingSystem.IsWindows()
                        ? "Python installation is corrupt.  Click  Reinstall Python  to reinstall."
                        : "Python installation is corrupt.  Reinstall Python manually, then click Check System.";
                    GetPythonLabel   = OperatingSystem.IsWindows() ? "Reinstall Python" : "Python Setup Help";
                    CanGetPython     = true;
                    StableCanInstall     = false;
                    PrereleaseCanInstall = false;
                    UpdateCanInstallBranch();
                    return;
                }

                _pythonExe       = py.ExePath;
                PythonFoundCallback?.Invoke(py.ExePath);
                PythonStatusIcon = "✓";
                PythonStatus     = $"Python {py.Version}   {py.ExePath}";
                StableCanInstall     = true;
                PrereleaseCanInstall = true;
                UpdateCanInstallBranch();

                if (PythonInfo.IsOutOfSupportedRange(py.Version) && ShowWarningAsync is not null)
                    await ShowWarningAsync(
                        "EXOTIC requires Python 3.10–3.12. " +
                        "The detected version is outside this range and may not work correctly. " +
                        "Python 3.10.11 is recommended.");

                Log("Checking installed environments…");
                await RefreshEnvironmentVersionAsync(isStable: true,  cts.Token);
                ReclassifyMigratedDevBuildIfNeeded();
                await RefreshEnvironmentVersionAsync(isStable: false, cts.Token);

                HeadlineText   = "Ready.  Install, reinstall, or remove either environment below, and pick which one runs next.";
                GetPythonLabel = OperatingSystem.IsWindows() ? "Reinstall Python" : "Python Setup Help";
                CanGetPython   = true;
            }
        }
        catch (OperationCanceledException) { HeadlineText = "Cancelled."; }
        catch (Exception ex)              { HeadlineText = $"Error: {ex.Message}"; Log($"ERROR: {ex.Message}"); }
        finally { ClearBusy(); _cts = null; }
    }

    [RelayCommand]
    private async Task GetPython()
    {
        if (OperatingSystem.IsLinux())
        {
            HeadlineText = "Python must be installed manually on Linux.";
            Log("Python 3.10 or higher is required. Open a terminal and run:");
            Log("");
            Log("  sudo add-apt-repository ppa:deadsnakes/ppa");
            Log("  sudo apt-get update");
            Log("  sudo apt-get install python3.10 python3.10-venv python3.10-distutils");
            Log("");
            Log("After installing, click Check System to verify.");
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            HeadlineText = "Download and install Python 3.10 or higher for macOS.";
            Log("Python 3.10 or higher is required. Download the latest 3.10.x installer from:");
            Log("");
            Log("  https://www.python.org/downloads/macos/");
            Log("");
            Log("Look for the latest Python 3.10.x release and download the");
            Log("'macOS 64-bit universal2 installer'. Run it, then click Check System.");
            return;
        }

        SetBusy();
        HeadlineText      = "Downloading Python installer…";
        IsProgressVisible = true;
        IsProgressIndeterminate = false;
        ProgressValue     = 0;
        ProgressMax       = 1;
        ProgressText      = "";

        using var cts = new CancellationTokenSource();
        _cts = cts;
        try
        {
            var dest = ExoticInstallService.PyInstallerPath;
            Log($"Downloading Python {ExoticInstallService.PyVersion} installer…");
            Log($"  URL  : {ExoticInstallService.PyUrl}");
            Log($"  Dest : {dest}");

            var progress = new Progress<(long done, long total)>(t =>
            {
                if (t.total > 0)
                {
                    ProgressMax   = t.total;
                    ProgressValue = t.done;
                    ProgressText  = $"{t.done / 1_048_576.0:F1} MB / {t.total / 1_048_576.0:F1} MB";
                }
            });
            await ExoticInstallService.DownloadFileAsync(ExoticInstallService.PyUrl, dest, progress, cts.Token);
            Log("Download complete.");

            HeadlineText            = "Installing Python (silent)…";
            IsProgressIndeterminate = true;
            ProgressText            = "";

            var logProgress = new Progress<string>(msg => { Log(msg); });
            var pythonExe = await ExoticInstallService.InstallPythonAsync(dest, logProgress, cts.Token);

            if (pythonExe is null)
            {
                HeadlineText     = "Installation finished — could not locate python.exe.  Try clicking Check System.";
                PythonStatusIcon = "?";
                PythonStatus     = "Installed, but exe not found";
                CanCheck         = true;
            }
            else
            {
                _pythonExe       = pythonExe;
                PythonFoundCallback?.Invoke(pythonExe);
                PythonStatusIcon = "✓";
                PythonStatus     = $"Python {ExoticInstallService.PyVersion}   {pythonExe}";
                HeadlineText     = "Python installed.  Install an environment below to continue.";
                StableCanInstall     = true;
                PrereleaseCanInstall = true;
                UpdateCanInstallBranch();
                Log($"Python exe: {pythonExe}");
            }
        }
        catch (OperationCanceledException) { HeadlineText = "Cancelled."; CanCheck = true; }
        catch (Exception ex)              { HeadlineText = $"Error: {ex.Message}"; Log($"ERROR: {ex.Message}"); CanCheck = true; }
        finally { ClearBusy(); _cts = null; }
    }

    /// <summary>Pre-release's own "Check System", scoped to finding a Python >=3.12 independently of Stable's search.</summary>
    [RelayCommand]
    private async Task CheckPrereleasePython()
    {
        SetBusy();
        ClearLog();
        HeadlineText        = "Scanning for Python 3.12+…";
        IsProgressVisible   = true;
        IsProgressIndeterminate = true;

        using var cts = new CancellationTokenSource();
        _cts = cts;
        try
        {
            Log("Searching for Python (≥ 3.12) for Pre-release…");
            var py = await ExoticInstallService.FindPythonAsync(cts.Token, minMinor: 12);
            if (py is null)
            {
                PrereleasePythonStatusIcon = "✗";
                PrereleasePythonStatus     = "Not found";
                Log("  Python 3.12+ not found.");
                HeadlineText     = OperatingSystem.IsWindows()
                    ? "Python 3.12+ is not installed.  Download and install it below, then install the pre-release branch."
                    : "Python 3.12+ is not installed.  Install it manually, then click Check System again.";
                PrereleaseGetPythonLabel = OperatingSystem.IsWindows() ? "Download & Install Python 3.12" : "Python 3.12 Setup Help";
                PrereleaseCanGetPython   = true;
                PrereleaseCanInstall     = false;
                UpdateCanInstallBranch();
            }
            else
            {
                Log($"  Found Python {py.Version} at {py.ExePath}");
                Log("  Verifying Python installation…");
                var isValid = await ExoticInstallService.ValidatePythonAsync(py.ExePath, cts.Token);
                if (!isValid)
                {
                    PrereleasePythonStatusIcon = "✗";
                    PrereleasePythonStatus     = $"Broken   {py.ExePath}";
                    Log("  Python installation is corrupt (cannot import core modules).");
                    HeadlineText     = OperatingSystem.IsWindows()
                        ? "Python 3.12 installation is corrupt.  Click  Reinstall Python 3.12  to reinstall."
                        : "Python 3.12 installation is corrupt.  Reinstall it manually, then click Check System.";
                    PrereleaseGetPythonLabel = OperatingSystem.IsWindows() ? "Reinstall Python 3.12" : "Python 3.12 Setup Help";
                    PrereleaseCanGetPython   = true;
                    PrereleaseCanInstall     = false;
                    UpdateCanInstallBranch();
                    return;
                }

                _prereleasePythonBaseExe   = py.ExePath;
                PrereleasePythonStatusIcon = "✓";
                PrereleasePythonStatus     = $"Python {py.Version}   {py.ExePath}";
                PrereleaseCanInstall       = true;
                UpdateCanInstallBranch();

                HeadlineText              = "Ready.  Install / Reinstall the pre-release branch below.";
                PrereleaseGetPythonLabel  = OperatingSystem.IsWindows() ? "Reinstall Python 3.12" : "Python 3.12 Setup Help";
                PrereleaseCanGetPython    = true;
                ConfigSaveCallback?.Invoke();
            }
        }
        catch (OperationCanceledException) { HeadlineText = "Cancelled."; }
        catch (Exception ex)              { HeadlineText = $"Error: {ex.Message}"; Log($"ERROR: {ex.Message}"); }
        finally { ClearBusy(); _cts = null; }
    }

    /// <summary>Pre-release's own "Get Python", downloading/installing 3.12.10 independently of Stable's 3.10.11.</summary>
    [RelayCommand]
    private async Task GetPrereleasePython()
    {
        if (OperatingSystem.IsLinux())
        {
            HeadlineText = "Python must be installed manually on Linux.";
            Log("Python 3.12 or higher is required for the pre-release branch. Open a terminal and run:");
            Log("");
            Log("  sudo add-apt-repository ppa:deadsnakes/ppa");
            Log("  sudo apt-get update");
            Log("  sudo apt-get install python3.12 python3.12-venv python3.12-distutils");
            Log("");
            Log("After installing, click Check System to verify.");
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            HeadlineText = "Download and install Python 3.12 or higher for macOS.";
            Log("Python 3.12 or higher is required for the pre-release branch. Download the latest 3.12.x installer from:");
            Log("");
            Log("  https://www.python.org/downloads/macos/");
            Log("");
            Log("Look for the latest Python 3.12.x release and download the");
            Log("'macOS 64-bit universal2 installer'. Run it, then click Check System.");
            return;
        }

        SetBusy();
        HeadlineText      = "Downloading Python 3.12 installer…";
        IsProgressVisible = true;
        IsProgressIndeterminate = false;
        ProgressValue     = 0;
        ProgressMax       = 1;
        ProgressText      = "";

        using var cts = new CancellationTokenSource();
        _cts = cts;
        try
        {
            var dest = ExoticInstallService.PyInstallerPath312;
            Log($"Downloading Python {ExoticInstallService.PyVersion312} installer…");
            Log($"  URL  : {ExoticInstallService.PyUrl312}");
            Log($"  Dest : {dest}");

            var progress = new Progress<(long done, long total)>(t =>
            {
                if (t.total > 0)
                {
                    ProgressMax   = t.total;
                    ProgressValue = t.done;
                    ProgressText  = $"{t.done / 1_048_576.0:F1} MB / {t.total / 1_048_576.0:F1} MB";
                }
            });
            await ExoticInstallService.DownloadFileAsync(ExoticInstallService.PyUrl312, dest, progress, cts.Token);
            Log("Download complete.");

            HeadlineText            = "Installing Python 3.12 (silent)…";
            IsProgressIndeterminate = true;
            ProgressText            = "";

            var logProgress = new Progress<string>(msg => { Log(msg); });
            var pythonExe = await ExoticInstallService.InstallPythonAsync(
                dest, ExoticInstallService.PyVersion312, "Python312", logProgress, cts.Token);

            if (pythonExe is null)
            {
                HeadlineText               = "Installation finished — could not locate python.exe.  Try clicking Check System.";
                PrereleasePythonStatusIcon = "?";
                PrereleasePythonStatus     = "Installed, but exe not found";
                CanCheck                   = true;
            }
            else
            {
                _prereleasePythonBaseExe   = pythonExe;
                PrereleasePythonStatusIcon = "✓";
                PrereleasePythonStatus     = $"Python {ExoticInstallService.PyVersion312}   {pythonExe}";
                HeadlineText               = "Python 3.12 installed.  Install the pre-release branch below to continue.";
                PrereleaseCanInstall       = true;
                UpdateCanInstallBranch();
                Log($"Python exe: {pythonExe}");
                ConfigSaveCallback?.Invoke();
            }
        }
        catch (OperationCanceledException) { HeadlineText = "Cancelled."; CanCheck = true; }
        catch (Exception ex)              { HeadlineText = $"Error: {ex.Message}"; Log($"ERROR: {ex.Message}"); CanCheck = true; }
        finally { ClearBusy(); _cts = null; }
    }

    [RelayCommand]
    private void OpenTerminal()
    {
        foreach (var term in new[] { "x-terminal-emulator", "gnome-terminal", "xterm" })
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(term)
                    { UseShellExecute = true });
                return;
            }
            catch { }
        }
        Log("Could not open a terminal automatically. Please open one manually.");
    }

    /// <summary>Creates the slot's venv if it doesn't exist yet, returning its python exe path.</summary>
    private async Task<string> EnsureSlotVenvAsync(bool isStable, CancellationToken ct)
    {
        var venvPath = isStable ? _stableVenvPath : _prereleaseVenvPath;
        if (string.IsNullOrEmpty(venvPath))
            venvPath = Path.Combine(ConfigService.AppDataDir, "python_envs", isStable ? "stable" : "prerelease");

        var venvPython = ExoticInstallService.GetVenvPythonExe(venvPath);

        // Pre-release requires Python >=3.12. A venv created before that requirement existed
        // (or from an older base interpreter) would silently keep running its own stale Python
        // forever otherwise — EXISTS is not the same as COMPATIBLE. Rebuild it fresh in that case.
        if (!isStable && File.Exists(venvPython))
        {
            var existingVer = await ExoticInstallService.GetVersionAsync(venvPython, ct);
            if (existingVer is null || !PythonInfo.IsCompatible(existingVer, minMinor: 12))
            {
                Log($"Existing Pre-release environment uses Python {existingVer ?? "?"}, which no longer meets the ≥3.12 requirement — rebuilding it fresh…");
                try { Directory.Delete(venvPath, recursive: true); } catch (Exception ex) { Log($"  (could not remove old environment: {ex.Message})"); }
            }
        }

        if (!File.Exists(venvPython))
        {
            Log($"Creating isolated environment for {(isStable ? StableName : PrereleaseName)}…");
            var baseExe = isStable ? _pythonExe! : _prereleasePythonBaseExe!;
            await ExoticInstallService.CreateVenvAsync(baseExe, venvPath, new Progress<string>(msg => Log(msg)), ct);
        }

        // Not confirmed yet — the exotic package install itself hasn't run at this point;
        // the caller sets *ConfirmedInstalled = true only once that actually succeeds.
        if (isStable) { _stableVenvPath = venvPath; _stablePythonExe = venvPython; _stableConfirmedInstalled = false; }
        else          { _prereleaseVenvPath = venvPath; _prereleasePythonExe = venvPython; _prereleaseConfirmedInstalled = false; }
        return venvPython;
    }

    [RelayCommand]
    private async Task InstallStable()
    {
        if (_pythonExe is null) { HeadlineText = "Python is not detected.  Run  Check System  first."; return; }

        SetBusy();
        HeadlineText = "Installing Stable EXOTIC…";
        IsProgressVisible = true; IsProgressIndeterminate = true; ProgressText = "";

        using var cts = new CancellationTokenSource();
        _cts = cts;
        try
        {
            var venvPython = await EnsureSlotVenvAsync(isStable: true, cts.Token);
            var log = new Progress<string>(msg => Log(msg));
            await ExoticInstallService.InstallExoticAsync(venvPython, log, cts.Token, targetVenv: true);

            // Reaching here means pip reported success (RunStreamAsync throws on a nonzero
            // exit code), so the install is genuinely confirmed regardless of whether the
            // version string itself can be read afterward.
            _stableCachedVersion      = await ExoticInstallService.GetExoticVersionAsync(venvPython, cts.Token);
            _stableConfirmedInstalled = true;
            RefreshStableStatus();
            HeadlineText = "Stable EXOTIC installation complete!" + OtherSlotNotIsolatedNote(justInstalledStable: true);
            ConfigSaveCallback?.Invoke();
        }
        catch (OperationCanceledException) { HeadlineText = "Cancelled."; }
        catch (Exception ex)              { HeadlineText = $"Error: {ex.Message}"; Log($"ERROR: {ex.Message}"); }
        finally { ClearBusy(); _cts = null; }
    }

    [RelayCommand]
    private async Task UninstallStable()
    {
        if (string.IsNullOrEmpty(_stablePythonExe)) return;

        SetBusy();
        HeadlineText = "Uninstalling Stable EXOTIC…";
        IsProgressVisible = true; IsProgressIndeterminate = true; ProgressText = "";

        using var cts = new CancellationTokenSource();
        _cts = cts;
        try
        {
            var log = new Progress<string>(msg => Log(msg));
            await ExoticInstallService.UninstallExoticAsync(_stablePythonExe, log, cts.Token);
            _stableCachedVersion      = null;
            _stableConfirmedInstalled = false;
            RefreshStableStatus();
            HeadlineText = "Stable EXOTIC uninstalled.  Click  Install / Reinstall  to reinstall.";
            ConfigSaveCallback?.Invoke();
        }
        catch (OperationCanceledException) { HeadlineText = "Cancelled."; }
        catch (Exception ex)              { HeadlineText = $"Error: {ex.Message}"; Log($"ERROR: {ex.Message}"); }
        finally { ClearBusy(); _cts = null; }
    }

    [RelayCommand]
    private void RemoveStable()
    {
        if (string.IsNullOrEmpty(_stableVenvPath)) return;
        try
        {
            if (Directory.Exists(_stableVenvPath)) Directory.Delete(_stableVenvPath, recursive: true);
            Log($"Removed environment: {_stableVenvPath}");
        }
        catch (Exception ex) { Log($"ERROR removing environment: {ex.Message}"); }

        _stableVenvPath = ""; _stablePythonExe = ""; _stableCachedVersion = null;
        RefreshStableStatus();
        if (StableIsActive) PrereleaseIsActive = true;
        HeadlineText = "Stable environment removed.";
        ConfigSaveCallback?.Invoke();
    }

    [RelayCommand]
    private async Task CheckPrereleaseVersion()
    {
        if (string.IsNullOrWhiteSpace(BranchUrl)) return;
        var pythonForCheck = !string.IsNullOrEmpty(_prereleasePythonExe) ? _prereleasePythonExe : _prereleasePythonBaseExe;
        if (pythonForCheck is null) { PrereleaseVersionCheckStatus = "✗  Python 3.12+ is not detected. Run Check System on the Pre-release card first."; return; }

        SetBusy();
        PrereleaseVersionCheckStatus = "⟳  Checking… (resolves the branch's real version — can take up to a minute)";

        using var cts = new CancellationTokenSource();
        _cts = cts;
        try
        {
            var result = await ExoticInstallService.CheckBranchVersionAsync(pythonForCheck, BranchUrl, cts.Token);
            if (result.RemoteVersion is null)
            {
                PrereleaseVersionCheckStatus = $"✗  Could not check: {result.Error}";
                return;
            }

            if (string.IsNullOrEmpty(_prereleaseCachedVersion))
            {
                PrereleaseVersionCheckStatus = $"ℹ  Not installed yet — latest on this branch: {result.RemoteVersion}";
            }
            else if (string.Equals(result.RemoteVersion, _prereleaseCachedVersion, StringComparison.OrdinalIgnoreCase))
            {
                PrereleaseVersionCheckStatus = $"✓  Up to date  ({result.RemoteVersion})";
            }
            else
            {
                PrereleaseVersionCheckStatus =
                    $"⚠  New version available — installed: {_prereleaseCachedVersion}, latest: {result.RemoteVersion}. " +
                    "Click Install / Reinstall to update.";
            }
        }
        catch (OperationCanceledException) { PrereleaseVersionCheckStatus = "Cancelled."; }
        catch (Exception ex)              { PrereleaseVersionCheckStatus = $"✗  {ex.Message}"; }
        finally { ClearBusy(); _cts = null; }
    }

    [RelayCommand]
    private async Task InstallBranch()
    {
        if (_prereleasePythonBaseExe is null) { HeadlineText = "Python 3.12+ is not detected.  Run  Check System  on the Pre-release card first."; return; }
        if (string.IsNullOrWhiteSpace(BranchUrl)) { HeadlineText = "Paste a GitHub repository URL above before installing."; return; }

        RememberBranchUrl();

        SetBusy();
        HeadlineText = "Installing EXOTIC pre-release from branch…";
        IsProgressVisible = true; IsProgressIndeterminate = true; ProgressText = "";

        using var cts = new CancellationTokenSource();
        _cts = cts;
        try
        {
            var venvPython = await EnsureSlotVenvAsync(isStable: false, cts.Token);
            var log = new Progress<string>(msg => Log(msg));
            await ExoticInstallService.InstallExoticFromBranchAsync(venvPython, BranchUrl, log, cts.Token, targetVenv: true);

            // Reaching here means pip reported success (RunStreamAsync throws on a nonzero
            // exit code), so the install is genuinely confirmed regardless of whether the
            // version string itself can be read afterward.
            _prereleaseCachedVersion      = await ExoticInstallService.GetExoticVersionAsync(venvPython, cts.Token);
            _prereleaseConfirmedInstalled = true;
            _prereleaseBranchUrlInstalled = BranchUrl;
            RefreshPrereleaseStatus();
            HeadlineText = "Pre-release installation complete!" + OtherSlotNotIsolatedNote(justInstalledStable: false);
            ConfigSaveCallback?.Invoke();
        }
        catch (OperationCanceledException) { HeadlineText = "Cancelled."; }
        catch (Exception ex)              { HeadlineText = $"Error: {ex.Message}"; Log($"ERROR: {ex.Message}"); }
        finally { ClearBusy(); _cts = null; }
    }

    [RelayCommand]
    private async Task UninstallPrerelease()
    {
        if (string.IsNullOrEmpty(_prereleasePythonExe)) return;

        SetBusy();
        HeadlineText = "Uninstalling pre-release EXOTIC…";
        IsProgressVisible = true; IsProgressIndeterminate = true; ProgressText = "";

        using var cts = new CancellationTokenSource();
        _cts = cts;
        try
        {
            var log = new Progress<string>(msg => Log(msg));
            await ExoticInstallService.UninstallExoticAsync(_prereleasePythonExe, log, cts.Token);
            _prereleaseCachedVersion      = null;
            _prereleaseConfirmedInstalled = false;
            RefreshPrereleaseStatus();
            HeadlineText = "Pre-release EXOTIC uninstalled.";
            ConfigSaveCallback?.Invoke();
        }
        catch (OperationCanceledException) { HeadlineText = "Cancelled."; }
        catch (Exception ex)              { HeadlineText = $"Error: {ex.Message}"; Log($"ERROR: {ex.Message}"); }
        finally { ClearBusy(); _cts = null; }
    }

    [RelayCommand]
    private void RemovePrerelease()
    {
        if (string.IsNullOrEmpty(_prereleaseVenvPath)) return;
        try
        {
            if (Directory.Exists(_prereleaseVenvPath)) Directory.Delete(_prereleaseVenvPath, recursive: true);
            Log($"Removed environment: {_prereleaseVenvPath}");
        }
        catch (Exception ex) { Log($"ERROR removing environment: {ex.Message}"); }

        _prereleaseVenvPath = ""; _prereleasePythonExe = ""; _prereleaseCachedVersion = null;
        _prereleaseBranchUrlInstalled = null;
        RefreshPrereleaseStatus();
        if (PrereleaseIsActive) StableIsActive = true;
        HeadlineText = "Pre-release environment removed.";
        ConfigSaveCallback?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private void SetBusy()
    {
        CanCheck               = false;
        CanGetPython           = false;
        PrereleaseCanGetPython = false;
        StableCanInstall       = false;
        StableCanUninstall     = false;
        StableCanRemove        = false;
        PrereleaseCanInstall   = false;
        PrereleaseCanUninstall = false;
        PrereleaseCanRemove    = false;
        CanInstallBranch       = false;
        CanCheckPrereleaseVersion = false;
        CanCancel              = true;
    }

    private void ClearBusy()
    {
        CanCancel               = false;
        IsProgressVisible       = false;
        IsProgressIndeterminate = false;
        ProgressText            = "";
        CanCheck                = true;
        PrereleaseCanGetPython  = true;
        StableCanInstall        = _pythonExe is not null;
        PrereleaseCanInstall    = _prereleasePythonBaseExe is not null;
        RefreshStableStatus();
        RefreshPrereleaseStatus();
        UpdateCanInstallBranch();
        UpdateCanCheckPrereleaseVersion();
    }

    private void ClearLog() => LogText = "";

    private void Log(string msg)
    {
        LogText += msg + "\n";
        Services.SessionLogService.Write($"[Setup] {msg}");
    }
}
