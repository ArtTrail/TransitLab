using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TransitLab.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace TransitLab.ViewModels;

public partial class ExoticSetupViewModel : ViewModelBase
{
    // ── Status display ─────────────────────────────────────────────────────────
    [ObservableProperty] private string  _pythonStatus     = "—";
    [ObservableProperty] private string  _exoticStatus     = "—";
    [ObservableProperty] private string  _pythonStatusIcon = "·";
    [ObservableProperty] private string  _exoticStatusIcon = "·";
    [ObservableProperty] private string  _headlineText     = "Click  Check System  to detect your Python and EXOTIC installation.";
    [ObservableProperty] private string  _logText          = "";

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
    [ObservableProperty] private bool   _canInstallExotic     = false;
    [ObservableProperty] private bool   _canUninstallExotic   = false;
    [ObservableProperty] private bool   _canInstallBranch     = false;
    [ObservableProperty] private bool   _canCancel            = false;

    // ── Pre-release branch URL ─────────────────────────────────────────────────
    [ObservableProperty] private string _branchUrl = "";

    partial void OnBranchUrlChanged(string value) => UpdateCanInstallBranch();

    private void UpdateCanInstallBranch() =>
        CanInstallBranch = !string.IsNullOrWhiteSpace(BranchUrl);

    // ── Internal state ─────────────────────────────────────────────────────────
    private string? _pythonExe;
    private CancellationTokenSource? _cts;

    // ── Platform ───────────────────────────────────────────────────────────────
    public bool IsLinux    { get; } = OperatingSystem.IsLinux();
    public bool IsNotLinux { get; } = !OperatingSystem.IsLinux();

    // ── Callbacks ──────────────────────────────────────────────────────────────
    public Action<string>?        PythonFoundCallback    { get; set; }
    public Action<string>?        ExoticExeFoundCallback { get; set; }
    public Func<string, Task>?    ShowWarningAsync       { get; set; }

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
            Log("Searching for Python (≥ 3.8)…");
            var py = await ExoticInstallService.FindPythonAsync(cts.Token);
            if (py is null)
            {
                PythonStatusIcon = "✗";
                PythonStatus     = "Not found";
                Log("  Python not found.");
                ExoticStatusIcon = "✗";
                ExoticStatus     = "Cannot check — Python required";
                HeadlineText     = OperatingSystem.IsWindows()
                    ? "Python is not installed.  Download and install it below, then install EXOTIC."
                    : "Python 3.10 is not installed.  Install it manually, then click Check System.";
                GetPythonLabel   = OperatingSystem.IsWindows() ? "Download & Install Python" : "Python Setup Help";
                CanGetPython     = true;
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
                    ExoticStatusIcon = "✗";
                    ExoticStatus     = "Cannot check — Python is broken";
                    HeadlineText     = OperatingSystem.IsWindows()
                        ? "Python installation is corrupt.  Click  Reinstall Python  to reinstall."
                        : "Python installation is corrupt.  Reinstall Python manually, then click Check System.";
                    GetPythonLabel   = OperatingSystem.IsWindows() ? "Reinstall Python" : "Python Setup Help";
                    CanGetPython     = true;
                    return;
                }

                _pythonExe       = py.ExePath;
                PythonFoundCallback?.Invoke(py.ExePath);
                UpdateCanInstallBranch();
                PythonStatusIcon = "✓";
                PythonStatus     = $"Python {py.Version}   {py.ExePath}";

                if (PythonInfo.IsOutOfSupportedRange(py.Version) && ShowWarningAsync is not null)
                    await ShowWarningAsync(
                        "EXOTIC officially supports Python 3.8–3.10. " +
                        "Python 3.11 and above are not officially supported and may not work correctly. " +
                        "Python 3.10.11 is recommended.");

                Log("Checking for EXOTIC…");
                var ver = await ExoticInstallService.GetExoticVersionAsync(py.ExePath, cts.Token);
                var exe = await ExoticInstallService.FindExoticExeAsync(py.ExePath, cts.Token);

                if (ver is not null)
                {
                    ExoticStatusIcon = "✓";
                    ExoticStatus     = exe is not null
                        ? $"EXOTIC {ver}   {exe}"
                        : $"EXOTIC {ver}   via {py.ExePath}";
                    Log($"  Found EXOTIC {ver}" + (exe is not null ? $" at {exe}" : $" via {py.ExePath}"));
                    if (exe is null)
                        Log("  exotic.exe was not found; TransitLab will run EXOTIC with python -m exotic.exotic.");
                    HeadlineText = "Everything is up to date.  You can reinstall or uninstall EXOTIC below.";
                    CanInstallExotic   = true;
                    CanUninstallExotic = true;
                    if (exe is not null) ExoticExeFoundCallback?.Invoke(exe);
                }
                else
                {
                    ExoticStatusIcon = "✗";
                    ExoticStatus     = "Not installed";
                    Log("  EXOTIC not found.");
                    HeadlineText     = "Python is ready.  Click  Install EXOTIC  to continue.";
                    CanInstallExotic = true;
                }
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
            Log("Python 3.10 is required. Open a terminal and run:");
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
            HeadlineText = "Download and install Python 3.10 for macOS.";
            Log("Python 3.10 is required. Download the latest 3.10.x installer from:");
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
                UpdateCanInstallBranch();
                PythonStatusIcon = "✓";
                PythonStatus     = $"Python {ExoticInstallService.PyVersion}   {pythonExe}";
                HeadlineText     = "Python installed.  Click  Install EXOTIC  to continue.";
                CanInstallExotic = true;
                Log($"Python exe: {pythonExe}");
                // Reset stale EXOTIC status from any previous broken-Python check
                ExoticStatusIcon = "·";
                ExoticStatus     = "Not yet checked";
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

    [RelayCommand]
    private async Task InstallExotic()
    {
        if (_pythonExe is null)
        {
            HeadlineText = "Python is not detected.  Run  Check System  first.";
            return;
        }

        SetBusy();
        HeadlineText            = "Installing EXOTIC via pip…";
        IsProgressVisible       = true;
        IsProgressIndeterminate = true;
        ProgressText            = "";

        using var cts = new CancellationTokenSource();
        _cts = cts;
        try
        {
            var log = new Progress<string>(msg => Log(msg));
            await ExoticInstallService.InstallExoticAsync(_pythonExe, log, cts.Token);
            PythonFoundCallback?.Invoke(_pythonExe);

            Log("Locating optional exotic.exe script…");
            var exe = await ExoticInstallService.FindExoticExeAsync(_pythonExe, cts.Token);

            var ver = await ExoticInstallService.GetExoticVersionAsync(_pythonExe, cts.Token);

            ExoticStatusIcon = "✓";
            ExoticStatus     = ver is not null
                ? (exe is not null ? $"EXOTIC {ver}   {exe}" : $"EXOTIC {ver}   via {_pythonExe}")
                : (exe is not null ? $"Installed   {exe}" : $"Installed via {_pythonExe}");

            HeadlineText = "EXOTIC installation complete!";

            if (exe is not null)
            {
                Log($"exotic.exe : {exe}");
                ExoticExeFoundCallback?.Invoke(exe);
            }
            else
            {
                Log($"exotic.exe not found; TransitLab will run: {_pythonExe} -m exotic.exotic");
            }
        }
        catch (OperationCanceledException) { HeadlineText = "Cancelled."; CanCheck = true; }
        catch (Exception ex)              { HeadlineText = $"Error: {ex.Message}"; Log($"ERROR: {ex.Message}"); CanCheck = true; }
        finally { ClearBusy(); _cts = null; }
    }

    [RelayCommand]
    private async Task InstallBranch()
    {
        if (_pythonExe is null)
        {
            HeadlineText = "Python is not detected.  Run  Check System  first.";
            return;
        }
        if (string.IsNullOrWhiteSpace(BranchUrl))
        {
            HeadlineText = "Paste a GitHub repository URL above before installing.";
            return;
        }

        SetBusy();
        HeadlineText            = "Installing EXOTIC pre-release from branch…";
        IsProgressVisible       = true;
        IsProgressIndeterminate = true;
        ProgressText            = "";

        using var cts = new CancellationTokenSource();
        _cts = cts;
        try
        {
            var log = new Progress<string>(msg => Log(msg));
            await ExoticInstallService.InstallExoticFromBranchAsync(_pythonExe, BranchUrl, log, cts.Token);
            PythonFoundCallback?.Invoke(_pythonExe);

            Log("Locating optional exotic.exe script…");
            var exe = await ExoticInstallService.FindExoticExeAsync(_pythonExe, cts.Token);

            var ver = await ExoticInstallService.GetExoticVersionAsync(_pythonExe, cts.Token);

            ExoticStatusIcon = "✓";
            ExoticStatus     = ver is not null
                ? (exe is not null ? $"EXOTIC {ver}  [pre-release]   {exe}" : $"EXOTIC {ver}  [pre-release]   via {_pythonExe}")
                : (exe is not null ? $"Installed  [pre-release]   {exe}" : $"Installed [pre-release] via {_pythonExe}");

            HeadlineText = "Pre-release installation complete!";

            if (exe is not null)
            {
                Log($"exotic.exe : {exe}");
                ExoticExeFoundCallback?.Invoke(exe);
            }
            else
            {
                Log($"exotic.exe not found; TransitLab will run: {_pythonExe} -m exotic.exotic");
            }
        }
        catch (OperationCanceledException) { HeadlineText = "Cancelled."; }
        catch (Exception ex)              { HeadlineText = $"Error: {ex.Message}"; Log($"ERROR: {ex.Message}"); }
        finally { ClearBusy(); _cts = null; }
    }

    [RelayCommand]
    private async Task UninstallExotic()
    {
        if (_pythonExe is null)
        {
            HeadlineText = "Python is not detected.  Run  Check System  first.";
            return;
        }

        SetBusy();
        HeadlineText            = "Uninstalling EXOTIC…";
        IsProgressVisible       = true;
        IsProgressIndeterminate = true;
        ProgressText            = "";

        using var cts = new CancellationTokenSource();
        _cts = cts;
        try
        {
            var log = new Progress<string>(msg => Log(msg));
            await ExoticInstallService.UninstallExoticAsync(_pythonExe, log, cts.Token);

            ExoticStatusIcon   = "✗";
            ExoticStatus       = "Not installed";
            HeadlineText       = "EXOTIC uninstalled.  Click  Install EXOTIC  to reinstall.";
            CanInstallExotic   = true;
            CanUninstallExotic = false;
        }
        catch (OperationCanceledException) { HeadlineText = "Cancelled."; CanCheck = true; }
        catch (Exception ex)              { HeadlineText = $"Error: {ex.Message}"; Log($"ERROR: {ex.Message}"); CanCheck = true; }
        finally { ClearBusy(); _cts = null; }
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private void SetBusy()
    {
        CanCheck             = false;
        CanGetPython         = false;
        CanInstallExotic     = false;
        CanUninstallExotic   = false;
        CanInstallBranch     = false;
        CanCancel            = true;
    }

    private void ClearBusy()
    {
        CanCancel               = false;
        IsProgressVisible       = false;
        IsProgressIndeterminate = false;
        ProgressText            = "";
        CanCheck                = true;
        UpdateCanInstallBranch();
    }

    private void ClearLog() => LogText = "";

    private void Log(string msg)
    {
        LogText += msg + "\n";
        Services.SessionLogService.Write($"[Setup] {msg}");
    }
}
