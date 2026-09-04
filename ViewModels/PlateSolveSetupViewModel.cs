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
    [ObservableProperty] private bool _useStarFix        = false;

    // NextAstroPlateSolution only exists in the EXOTIC pre-release dev build built off the
    // 4.3.2 tag — reported by pip/setuptools_scm as "4.3.2.dev<N>+g<hash>.d<date>", not a
    // clean "4.3.2". Defaults to false (grayed out) until CheckNextAstroSupportAsync confirms it.
    [ObservableProperty] private bool _isNextAstroSupported = false;

    public string NextAstroRequirementNote { get; } =
        "⚠  Requires the EXOTIC 4.3.2 pre-release dev build — install via Tools → Python & EXOTIC Setup → Pre-release / Development Build.";

    // ── ASTAP settings ────────────────────────────────────────────────────────
    // Windows-specific wording would wrongly suggest a .exe is required on Linux/macOS,
    // where the binary has no fixed extension (issue #50).
    public string AstapPathWatermark { get; } = OperatingSystem.IsWindows()
        ? "Path to astap_cli.exe or astap.exe…"
        : OperatingSystem.IsMacOS()
            ? "Path to the astap binary inside ASTAP.app, or the .app bundle itself…"
            : "Path to the astap or astap_cli binary…";

    [ObservableProperty] private string _astapExePath    = "";
    [ObservableProperty] private string _astapCatalogDir = "";  // blank = same dir as exe
    [ObservableProperty] private int    _searchRadius    = 60;   // arcminutes
    [ObservableProperty] private int    _downsample      = 0;
    [ObservableProperty] private bool   _solveAllFrames  = false;

    // ── StarFix settings ──────────────────────────────────────────────────────
    [ObservableProperty] private string _starFixExePath        = "";  // install root, contains StarFix.exe
    [ObservableProperty] private bool   _isStarFixInstalled     = false;
    [ObservableProperty] private string _starFixStatus          = "";
    [ObservableProperty] private bool   _isStarFixDownloading   = false;
    [ObservableProperty] private double _starFixDownloadProgress = 0;

    // ── Status ────────────────────────────────────────────────────────────────
    [ObservableProperty] private string _testStatus     = "";
    [ObservableProperty] private string _catalogStatus  = "";
    [ObservableProperty] private bool   _isTestRunning  = false;

    // ── Callbacks (wired by MainWindow.axaml.cs) ──────────────────────────────
    public Func<Task<string?>>?  BrowseAstapFunc       { get; set; }
    public Func<Task<string?>>?  BrowseCatalogDirFunc  { get; set; }
    public Func<Task<string?>>?  BrowseStarFixFunc     { get; set; }
    public Func<string>?         GetDownloadTempDirFunc { get; set; }
    public Action<string, string, string, int, int, bool, string>? SaveCallback { get; set; }
    public Action? CloseCallback { get; set; }
    public Func<string, string, Task>? ShowInfoFunc { get; set; }

    // ── Radio button change handlers ──────────────────────────────────────────
    partial void OnUseAstapChanged(bool value)
    {
        if (value) { UseAstrometryNet = false; UseNextAstro = false; UseStarFix = false; }
    }

    partial void OnUseAstrometryNetChanged(bool value)
    {
        if (value)
        {
            UseAstap = false;
            UseNextAstro = false;
            UseStarFix = false;
            CheckOnlineSolveAllWarning();
        }
    }

    partial void OnUseNextAstroChanged(bool value)
    {
        if (value)
        {
            UseAstap = false;
            UseAstrometryNet = false;
            UseStarFix = false;
            CheckOnlineSolveAllWarning();
        }
    }

    partial void OnUseStarFixChanged(bool value)
    {
        if (value)
        {
            UseAstap = false;
            UseAstrometryNet = false;
            UseNextAstro = false;
            RefreshStarFixDetection();
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
        var solver = UseAstap ? "ASTAP" : UseNextAstro ? "NextAstro" : UseStarFix ? "StarFix" : "AstrometryNet";
        SaveCallback?.Invoke(solver, AstapExePath, AstapCatalogDir, SearchRadius, Downsample, SolveAllFrames, StarFixExePath);
        CloseCallback?.Invoke();
    }

    // ── StarFix ───────────────────────────────────────────────────────────────

    partial void OnStarFixExePathChanged(string value) => IsStarFixInstalled = StarFixService.IsValidInstall(value);

    /// <summary>Re-checks StarFix's install state via its Windows uninstall registry entry.</summary>
    private void RefreshStarFixDetection()
    {
        if (IsStarFixInstalled) return;  // already known-good (e.g. from a saved path) — don't clobber it
        var detected = StarFixService.DetectInstalledPath();
        if (detected is not null)
        {
            StarFixExePath  = detected;
            StarFixStatus   = "✓  StarFix found";
        }
        else
        {
            StarFixStatus = "StarFix is not installed.";
        }
    }

    [RelayCommand]
    private async Task BrowseStarFixAsync()
    {
        if (BrowseStarFixFunc is null) return;
        var path = await BrowseStarFixFunc();
        if (path is not null)
        {
            StarFixExePath = path;
            StarFixStatus  = IsStarFixInstalled ? "✓  StarFix found" : "⚠  StarFix.exe not found at that location";
        }
    }

    [RelayCommand]
    private async Task DownloadAndInstallStarFixAsync()
    {
        IsStarFixDownloading    = true;
        StarFixDownloadProgress = 0;
        StarFixStatus           = "⟳  Checking latest StarFix release…";
        try
        {
            var release = await StarFixService.CheckLatestAsync();
            if (release is null)
            {
                StarFixStatus = "✗  Could not reach the StarFix release page — check your internet connection.";
                return;
            }

            var tempDir  = GetDownloadTempDirFunc?.Invoke() ?? Path.GetTempPath();
            var destPath = Path.Combine(tempDir, release.AssetName);

            StarFixStatus = $"⟳  Downloading StarFix v{release.Version}…";
            var progress = new Progress<(long done, long total)>(t =>
            {
                if (t.total > 0)
                {
                    StarFixDownloadProgress = (double)t.done / t.total * 100;
                    StarFixStatus = $"⟳  Downloading StarFix v{release.Version}… {t.done / 1_048_576.0:F1} MB / {t.total / 1_048_576.0:F1} MB";
                }
            });
            await StarFixService.DownloadAndLaunchInstallerAsync(release.DownloadUrl, destPath, progress, default);

            StarFixStatus = "✓  Installer launched — finish the setup wizard, then click Auto-detect below.";
        }
        catch (Exception ex)
        {
            StarFixStatus = $"✗  {ex.Message}";
        }
        finally
        {
            IsStarFixDownloading = false;
        }
    }

    [RelayCommand]
    private void AutoDetectStarFix()
    {
        var detected = StarFixService.DetectInstalledPath();
        if (detected is not null)
        {
            StarFixExePath = detected;
            StarFixStatus  = "✓  StarFix found";
        }
        else
        {
            StarFixStatus = "⚠  StarFix not found — install it above, or use Browse if it's in a non-default location.";
        }
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

    [RelayCommand]
    private void OpenStarFixWebsite()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = "https://github.com/ArtTrail/StarFix",
                UseShellExecute = true,
            });
        }
        catch { /* best-effort */ }
    }

    [RelayCommand]
    private void OpenNextAstroWebsite()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = "https://nextastro.org",
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

    public void LoadFromConfig(string solver, string astapPath, string catalogDir, int searchRadius, int downsample, bool solveAllFrames = false, string starFixPath = "")
    {
        StarFixExePath   = starFixPath;   // set before UseStarFix so detection reflects the saved path, not a fresh registry lookup
        UseAstap         = solver == "ASTAP";
        UseNextAstro     = solver == "NextAstro";
        UseStarFix       = solver == "StarFix";
        UseAstrometryNet = !UseAstap && !UseNextAstro && !UseStarFix;
        AstapExePath     = astapPath;
        AstapCatalogDir  = catalogDir;
        SearchRadius     = searchRadius;
        Downsample       = downsample;
        SolveAllFrames   = solveAllFrames;
        RefreshCatalogStatus();
        if (UseStarFix && IsStarFixInstalled) StarFixStatus = "✓  StarFix found";
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
