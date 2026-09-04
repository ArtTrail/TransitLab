using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TransitLab.Services;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TransitLab.ViewModels;

public partial class PlateSolveSetupViewModel : ViewModelBase
{
    // ── Solver choice ─────────────────────────────────────────────────────────
    [ObservableProperty] private bool _useAstrometryNet = true;
    [ObservableProperty] private bool _useAstap         = false;
    [ObservableProperty] private bool _useNextAstro      = false;

    // NextAstroPlateSolution only exists in the EXOTIC pre-release dev build built off the
    // 4.3.2 tag — reported by pip/setuptools_scm as "4.3.2.dev<N>+g<hash>.d<date>", not a
    // clean "4.3.2". Defaults to false (grayed out) until CheckNextAstroSupportAsync confirms it.
    [ObservableProperty] private bool _isNextAstroSupported = false;

    public string NextAstroRequirementNote { get; } =
        "⚠  Requires the EXOTIC 4.3.2 pre-release dev build — install via Tools → Python & EXOTIC Setup → Pre-release / Development Build.";

    // ── ASTAP settings ────────────────────────────────────────────────────────
    [ObservableProperty] private string _astapExePath    = "";
    [ObservableProperty] private string _astapCatalogDir = "";  // blank = same dir as exe
    [ObservableProperty] private int    _searchRadius    = 60;   // arcminutes
    [ObservableProperty] private int    _downsample      = 0;
    [ObservableProperty] private bool   _solveAllFrames  = false;

    // ── Status ────────────────────────────────────────────────────────────────
    [ObservableProperty] private string _testStatus     = "";
    [ObservableProperty] private string _catalogStatus  = "";
    [ObservableProperty] private bool   _isTestRunning  = false;

    // ── Callbacks (wired by MainWindow.axaml.cs) ──────────────────────────────
    public Func<Task<string?>>?  BrowseAstapFunc       { get; set; }
    public Func<Task<string?>>?  BrowseCatalogDirFunc  { get; set; }
    public Action<string, string, string, int, int, bool>? SaveCallback { get; set; }
    public Action? CloseCallback { get; set; }
    public Func<string, string, Task>? ShowInfoFunc { get; set; }

    // ── Radio button change handlers ──────────────────────────────────────────
    partial void OnUseAstapChanged(bool value)
    {
        if (value) { UseAstrometryNet = false; UseNextAstro = false; }
    }

    partial void OnUseAstrometryNetChanged(bool value)
    {
        if (value)
        {
            UseAstap = false;
            UseNextAstro = false;
            CheckOnlineSolveAllWarning();
        }
    }

    partial void OnUseNextAstroChanged(bool value)
    {
        if (value)
        {
            UseAstap = false;
            UseAstrometryNet = false;
            CheckOnlineSolveAllWarning();
        }
    }

    partial void OnSolveAllFramesChanged(bool value) => CheckOnlineSolveAllWarning();

    // Astrometry.net/NextAstro solve one frame per network round-trip to a hosted service —
    // solving every frame in a directory can take a while, unlike ASTAP's instant local solve.
    // Warn whenever the combination (online solver + Solve All Frames) becomes active, whichever
    // of the two settings changed to cause it.
    private void CheckOnlineSolveAllWarning()
    {
        if (!SolveAllFrames || UseAstap || ShowInfoFunc is null) return;
        var solverName = UseNextAstro ? "NextAstro" : "Astrometry.net";
        _ = ShowInfoFunc("Solving All Frames May Take a While",
            $"{solverName} solves each frame with its own network round-trip to a hosted service, " +
            "unlike ASTAP's instant local solve. Solving every frame in a large directory can take " +
            "significantly longer — the solve will still run, just expect it to take a while for " +
            "a full night's worth of frames.");
    }

    partial void OnAstapExePathChanged(string value)
    {
        RefreshCatalogStatus();
        TestAstapCommand.NotifyCanExecuteChanged();
    }

    partial void OnAstapCatalogDirChanged(string value) => RefreshCatalogStatus();

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task BrowseAstapAsync()
    {
        if (BrowseAstapFunc is null) return;
        var path = await BrowseAstapFunc();
        if (path is not null)
        {
            AstapExePath = path;
            TestStatus   = "";
            RefreshCatalogStatus();
        }
    }

    [RelayCommand]
    private void AutoDetect()
    {
        string[] candidates;
        string notFoundMsg;
        if (OperatingSystem.IsWindows())
        {
            // Prefer astap_cli.exe (headless CLI build) over astap.exe
            candidates   = [@"C:\Program Files\astap\astap_cli.exe", @"C:\Program Files\astap\astap.exe"];
            notFoundMsg  = @"⚠  ASTAP not found at C:\Program Files\astap\ — use Browse";
        }
        else if (OperatingSystem.IsMacOS())
        {
            var userApps = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile), "Applications");
            candidates  =
            [
                "/Applications/ASTAP.app/Contents/MacOS/astap",
                Path.Combine(userApps, "ASTAP.app", "Contents", "MacOS", "astap"),
            ];
            notFoundMsg = "⚠  ASTAP not found in /Applications — use Browse to locate ASTAP.app";
        }
        else  // Linux
        {
            candidates  = ["/usr/bin/astap", "/usr/local/bin/astap"];
            notFoundMsg = "⚠  ASTAP not found at /usr/bin/astap — use Browse";
        }

        var candidate = candidates.FirstOrDefault(File.Exists);
        if (candidate is not null)
        {
            AstapExePath = candidate;
            TestStatus   = "✓  ASTAP found at default location";
            RefreshCatalogStatus();
        }
        else
        {
            TestStatus = notFoundMsg;
        }
    }

    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestAstapAsync()
    {
        IsTestRunning = true;
        TestStatus    = "⟳  Testing…";
        try
        {
            var (ok, msg) = await PlateSolveService.TestAstapAsync(AstapExePath, CancellationToken.None);
            TestStatus = ok ? msg : $"✗  {msg}";
            if (ok) RefreshCatalogStatus();
        }
        finally
        {
            IsTestRunning = false;
        }
    }
    private bool CanTest() => !string.IsNullOrWhiteSpace(AstapExePath) && !IsTestRunning;

    partial void OnIsTestRunningChanged(bool value) => TestAstapCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    private async Task BrowseCatalogDirAsync()
    {
        if (BrowseCatalogDirFunc is null) return;
        var path = await BrowseCatalogDirFunc();
        if (path is not null)
        {
            AstapCatalogDir = path;
            RefreshCatalogStatus();
        }
    }

    [RelayCommand]
    private void Save()
    {
        var solver = UseAstap ? "ASTAP" : UseNextAstro ? "NextAstro" : "AstrometryNet";
        SaveCallback?.Invoke(solver, AstapExePath, AstapCatalogDir, SearchRadius, Downsample, SolveAllFrames);
        CloseCallback?.Invoke();
    }

    [RelayCommand]
    private void Close() => CloseCallback?.Invoke();

    [RelayCommand]
    private void OpenAstapWebsite()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = "https://www.hnsky.org/astap.htm",
                UseShellExecute = true,
            });
        }
        catch { /* best-effort */ }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void RefreshCatalogStatus()
    {
        // Use explicit catalog dir if set, otherwise fall back to exe's directory
        var dir = string.IsNullOrWhiteSpace(AstapCatalogDir) ? AstapExePath : AstapCatalogDir;
        CatalogStatus = PlateSolveService.CheckAstapCatalog(dir, isCatalogDir: !string.IsNullOrWhiteSpace(AstapCatalogDir));
    }

    public void LoadFromConfig(string solver, string astapPath, string catalogDir, int searchRadius, int downsample, bool solveAllFrames = false)
    {
        UseAstap         = solver == "ASTAP";
        UseNextAstro     = solver == "NextAstro";
        UseAstrometryNet = !UseAstap && !UseNextAstro;
        AstapExePath     = astapPath;
        AstapCatalogDir  = catalogDir;
        SearchRadius     = searchRadius;
        Downsample       = downsample;
        SolveAllFrames   = solveAllFrames;
        RefreshCatalogStatus();
    }

    /// <summary>
    /// Detects whether the installed EXOTIC is the pre-release dev build that contains
    /// NextAstroPlateSolution, and enables/disables the NextAstro option accordingly. If a
    /// previously-saved config had NextAstro selected but it's no longer supported (e.g. the
    /// user reinstalled stable EXOTIC since last saving), falls back to Astrometry.net rather
    /// than leaving an unusable solver selected.
    /// </summary>
    /// <param name="activeEnvironmentPythonExePath">
    /// The python.exe of whichever environment (Stable or Pre-release) is currently active
    /// in Setup, per <c>ExoticSetupViewModel.ActiveEnvironmentName</c>. NextAstro only exists
    /// in the Pre-release build, so this must reflect the actual selected environment rather
    /// than whatever generic Python <see cref="ExoticInstallService.FindPythonAsync"/> happens
    /// to find on the system (v2.8.0's multi-environment install means that's frequently the
    /// Stable environment even while Pre-release is the one selected/in use). Falls back to
    /// FindPythonAsync only when no active-environment path is available (e.g. pre-2.8.0 config).
    /// </param>
    public async Task CheckNextAstroSupportAsync(string? activeEnvironmentPythonExePath = null)
    {
        try
        {
            var pythonExe = !string.IsNullOrWhiteSpace(activeEnvironmentPythonExePath)
                ? activeEnvironmentPythonExePath
                : (await ExoticInstallService.FindPythonAsync())?.ExePath;
            var version = pythonExe is not null
                ? await ExoticInstallService.GetExoticVersionAsync(pythonExe)
                : null;
            IsNextAstroSupported = version?.StartsWith("4.3.2.dev", StringComparison.OrdinalIgnoreCase) == true;
        }
        catch
        {
            IsNextAstroSupported = false;
        }

        if (!IsNextAstroSupported && UseNextAstro)
        {
            UseNextAstro     = false;
            UseAstrometryNet = true;
        }
    }
}
