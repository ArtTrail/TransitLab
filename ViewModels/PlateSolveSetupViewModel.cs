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

    // ── Radio button change handlers ──────────────────────────────────────────
    partial void OnUseAstapChanged(bool value)
    {
        if (value) UseAstrometryNet = false;
    }

    partial void OnUseAstrometryNetChanged(bool value)
    {
        if (value) UseAstap = false;
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
        var solver = UseAstap ? "ASTAP" : "AstrometryNet";
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
        UseAstrometryNet = !UseAstap;
        AstapExePath     = astapPath;
        AstapCatalogDir  = catalogDir;
        SearchRadius     = searchRadius;
        Downsample       = downsample;
        SolveAllFrames   = solveAllFrames;
        RefreshCatalogStatus();
    }
}
