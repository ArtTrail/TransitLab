using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TransitLab.Services;

public record PythonInfo(string Version, string ExePath)
{
    public static bool IsCompatible(string version)
    {
        var parts = version.Split('.');
        if (parts.Length < 2) return false;
        return int.TryParse(parts[0], out var maj) &&
               int.TryParse(parts[1], out var min) &&
               maj == 3 && min >= 8;
    }

    public static bool IsOutOfSupportedRange(string version)
    {
        var parts = version.Split('.');
        if (parts.Length < 2) return false;
        if (!int.TryParse(parts[0], out var maj) || !int.TryParse(parts[1], out var min)) return false;
        return maj != 3 || min < 8 || min > 10;
    }
}

public static class ExoticInstallService
{
    public const  string PyVersion       = "3.10.11";
    public const  string PyUrl           = "https://www.python.org/ftp/python/3.10.11/python-3.10.11-amd64.exe";
    public const  string PyInstallerName = "python-3.10.11-amd64.exe";

    // Embeddable zip — no Windows Installer, no registry; used as fallback when the MSI fails
    public const  string PyEmbedUrl      = "https://www.python.org/ftp/python/3.10.11/python-3.10.11-embed-amd64.zip";
    public const  string PyEmbedName     = "python-3.10.11-embed-amd64.zip";
    public static string PyEmbedPath     => Path.Combine(Path.GetTempPath(), PyEmbedName);

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(15),
        DefaultRequestHeaders = { { "User-Agent", "Mozilla/5.0" } },
    };

    // ── Python detection ──────────────────────────────────────────────────────

    public static async Task<PythonInfo?> FindPythonAsync(CancellationToken ct = default)
    {
        // 1. py launcher lists all installed versions
        var fromLauncher = await FindViaPyLauncherAsync(ct);
        if (fromLauncher != null) return fromLauncher;

        // 2. python3.10 / python3 / python on PATH (try versioned name first)
        foreach (var cmd in new[] { "python3.10", "python3.9", "python3", "python" })
        {
            var p = await ProbeExeAsync(cmd, ct);
            if (p != null) return p;
        }

        // 3. Common install paths
        var fromScan = ScanCommonPaths();
        if (fromScan != null) return fromScan;

        // 4. Derive python.exe from exotic.exe — covers conda envs and pip --user installs
        //    that the above steps miss (e.g. miniconda3\envs\exotic_env\python.exe)
        var exoticExe = ExoticFinder.Find("");
        if (!string.IsNullOrEmpty(exoticExe))
        {
            var scriptsDir = Path.GetDirectoryName(exoticExe);
            var envDir     = scriptsDir is not null ? Path.GetDirectoryName(scriptsDir) : null;
            if (envDir is not null)
            {
                foreach (var name in new[] { "python.exe", "python3.exe" })
                {
                    var candidate = Path.Combine(envDir, name);
                    if (!File.Exists(candidate)) continue;
                    var ver = await GetVersionAsync(candidate, ct);
                    if (ver is not null && PythonInfo.IsCompatible(ver))
                        return new PythonInfo(ver, candidate);
                }
            }
        }

        return null;
    }

    private static async Task<PythonInfo?> FindViaPyLauncherAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var output = await RunCaptureAsync("py", "-0p", ct);
            // Each line: " -V:3.11 *        C:\...\Python311\python.exe"
            foreach (var line in output.Split('\n'))
            {
                var m = Regex.Match(line.Trim(),
                    @"-V:(\d+\.\d+)\s+\*?\s+(.+python\.exe)", RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                var verShort = m.Groups[1].Value;
                var path     = m.Groups[2].Value.Trim();
                if (!File.Exists(path)) continue;
                var full = await GetVersionAsync(path, ct) ?? verShort;
                if (PythonInfo.IsCompatible(full)) return new PythonInfo(full, path);
            }
        }
        catch { }
        return null;
    }

    private static async Task<PythonInfo?> ProbeExeAsync(string exe, CancellationToken ct)
    {
        try
        {
            var ver = await GetVersionAsync(exe, ct);
            if (ver == null || !PythonInfo.IsCompatible(ver)) return null;
            var path = await RunCaptureAsync(exe, "-c \"import sys; print(sys.executable)\"", ct);
            path = path.Trim();
            return File.Exists(path) ? new PythonInfo(ver, path) : null;
        }
        catch { return null; }
    }

    private static async Task<string?> GetVersionAsync(string exe, CancellationToken ct)
    {
        try
        {
            var out1 = await RunCaptureAsync(exe, "--version", ct);
            var m = Regex.Match(out1, @"Python (\d+\.\d+\.\d+)");
            return m.Success ? m.Groups[1].Value : null;
        }
        catch { return null; }
    }

    private static PythonInfo? ScanCommonPaths()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roots = new[]
        {
            Path.Combine(local, "Programs", "Python"),
            @"C:\Python310", @"C:\Python311", @"C:\Python39", @"C:\Python38",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Python310"),
        };
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            var dirs = new[] { root }.Concat(
                Directory.GetDirectories(root, "Python3*", SearchOption.TopDirectoryOnly));
            foreach (var dir in dirs)
            {
                var exe = Path.Combine(dir, "python.exe");
                if (!File.Exists(exe)) continue;
                var dm = Regex.Match(Path.GetFileName(dir), @"Python(\d)(\d+)");
                if (!dm.Success) continue;
                var ver = $"{dm.Groups[1].Value}.{dm.Groups[2].Value}";
                if (PythonInfo.IsCompatible(ver)) return new PythonInfo(ver, exe);
            }
        }
        return null;
    }

    // ── Validation ───────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true if the Python exe can actually import core modules.
    /// A Python installation whose files were deleted/moved without uninstalling
    /// can pass a --version check but fail immediately when importing anything.
    /// </summary>
    public static async Task<bool> ValidatePythonAsync(string exePath, CancellationToken ct = default)
    {
        try
        {
            var result = await RunCaptureAsync(exePath, "-c \"import encodings, os; print('ok')\"", ct);
            return result.Trim() == "ok";
        }
        catch { return false; }
    }

    // ── Download ──────────────────────────────────────────────────────────────

    public static async Task DownloadFileAsync(
        string url, string dest,
        IProgress<(long done, long total)>? progress,
        CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? -1L;
        await using var src  = await response.Content.ReadAsStreamAsync(ct);
        await using var file = new FileStream(dest, FileMode.Create, FileAccess.Write);
        var buf = new byte[81920];
        long done = 0;
        int  read;
        while ((read = await src.ReadAsync(buf, ct)) > 0)
        {
            await file.WriteAsync(buf.AsMemory(0, read), ct);
            done += read;
            progress?.Report((done, total));
        }
    }

    // ── Install Python ────────────────────────────────────────────────────────

    public static async Task<string?> InstallPythonAsync(
        string installerPath, IProgress<string>? log, CancellationToken ct)
    {
        // Pre-flight: remove any stale Windows registry entry for this Python version.
        // If files were deleted manually without uninstalling, the MSI database still
        // has a product record and the fresh installer will fail with exit code 1603.
        // Running /uninstall first clears that record; it's a no-op if nothing is registered.
        Report(log,"Removing any existing Python registration…");
        using (var uninst = Process.Start(new ProcessStartInfo(installerPath, "/uninstall /quiet")
               { UseShellExecute = true }))
        {
            if (uninst is not null) await uninst.WaitForExitAsync(ct);
        }

        Report(log,"Launching Python installer…");
        Report(log,"  A UAC prompt may appear — please click Yes to allow the installation.");

        var msiLog = Path.Combine(Path.GetTempPath(), "python_install_msi.log");
        if (File.Exists(msiLog)) File.Delete(msiLog);

        // InstallAllUsers=1: system-wide install (Program Files).  Uses HKLM instead of HKCU
        // for MSI registration, bypassing any corrupted per-user MSI entries left behind
        // when a previous install was deleted manually rather than through the uninstaller.
        // Include_launcher=0: skip py.exe launcher to avoid conflicts with existing system launcher.
        // /log: write verbose MSI log so failures can be diagnosed.
        var args = $"/quiet InstallAllUsers=1 PrependPath=1 Include_pip=1 Include_launcher=0 Include_test=0 /log \"{msiLog}\"";
        using var proc = Process.Start(new ProcessStartInfo(installerPath, args)
        {
            UseShellExecute = true,
        }) ?? throw new InvalidOperationException("Cannot start Python installer.");

        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0)
        {
            if (File.Exists(msiLog))
            {
                var lines = await File.ReadAllLinesAsync(msiLog, ct);
                var relevant = lines
                    .Where(l => l.Contains("Error", StringComparison.OrdinalIgnoreCase)
                             || l.Contains("failed", StringComparison.OrdinalIgnoreCase)
                             || l.Contains("return value 3", StringComparison.OrdinalIgnoreCase))
                    .Take(20);
                foreach (var l in relevant) Report(log,"  MSI: " + l);
            }

            if (proc.ExitCode == 1603)
            {
                Report(log,"Windows Installer (MSI) is not usable on this machine (exit 1603).");
                Report(log,"Falling back to embeddable package — no Windows Installer required…");
                return await InstallPythonEmbeddableAsync(log, ct);
            }

            throw new InvalidOperationException($"Python installer failed (exit code {proc.ExitCode}). See MSI details above.");
        }

        Report(log,$"Python {PyVersion} installed (MSI).");

        // Check both system-wide (InstallAllUsers=1) and per-user install paths
        var knownPaths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Python310", "python.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "Python", "Python310", "python.exe"),
        };
        foreach (var p in knownPaths)
            if (File.Exists(p)) return p;
        return (await FindPythonAsync(ct))?.ExePath;
    }

    // ── Install Python (embeddable fallback) ──────────────────────────────────

    /// <summary>
    /// Installs Python via the embeddable zip package — no Windows Installer, no registry.
    /// Used automatically when the MSI installer fails with exit code 1603.
    /// </summary>
    private static async Task<string?> InstallPythonEmbeddableAsync(
        IProgress<string>? log, CancellationToken ct)
    {
        var destDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Python", "Python310");

        // Download embeddable zip
        Report(log,$"Downloading Python {PyVersion} embeddable package…");
        await DownloadFileAsync(PyEmbedUrl, PyEmbedPath, null, ct);

        // Extract (overwrite any prior attempt)
        Report(log,$"Extracting to {destDir}…");
        if (Directory.Exists(destDir)) Directory.Delete(destDir, true);
        Directory.CreateDirectory(destDir);
        await Task.Run(() => ZipFile.ExtractToDirectory(PyEmbedPath, destDir), ct);

        // The embeddable package has import site disabled by default.
        // Uncomment '#import site' in python310._pth so pip and site-packages work.
        var pthFile = Path.Combine(destDir, "python310._pth");
        if (File.Exists(pthFile))
        {
            var content = await File.ReadAllTextAsync(pthFile, ct);
            content = content.Replace("#import site", "import site");
            await File.WriteAllTextAsync(pthFile, content, ct);
            Report(log,"Enabled site-packages.");
        }

        var pythonExe = Path.Combine(destDir, "python.exe");

        // Bootstrap pip via get-pip.py
        var getPipPath = Path.Combine(Path.GetTempPath(), "get-pip.py");
        Report(log,"Downloading pip bootstrap…");
        await DownloadFileAsync("https://bootstrap.pypa.io/get-pip.py", getPipPath, null, ct);
        Report(log,"Installing pip…");
        await RunStreamAsync(pythonExe, $"\"{getPipPath}\" --no-warn-script-location", log, ct);

        Report(log,$"Python {PyVersion} installed (embeddable).");
        return File.Exists(pythonExe) ? pythonExe : null;
    }

    // ── Install EXOTIC ────────────────────────────────────────────────────────

    public static async Task InstallExoticAsync(
        string pythonExe, IProgress<string>? log, CancellationToken ct)
    {
        Report(log,"Upgrading pip…");
        await RunStreamAsync(pythonExe, "-m pip install --upgrade pip --user --no-warn-script-location", log, ct);

        Report(log,"\nInstalling prerequisite packages…");
        await RunStreamAsync(pythonExe,
            "-m pip install --user --no-warn-script-location " +
            "\"setuptools>=62.6\" \"setuptools_scm[toml]>=6.4.2\" \"wheel>=0.37.1\"",
            log, ct);

        Report(log,"\nInstalling EXOTIC (this may take several minutes)…");
        // --force-reinstall ensures a dev/branch build is replaced by the latest stable PyPI
        // release. Without it, pip sees the dev version as newer and skips the install.
        await RunStreamAsync(pythonExe, "-m pip install --upgrade --force-reinstall exotic --user --no-warn-script-location", log, ct);
    }

    // ── Git detection and install ─────────────────────────────────────────────

    public static async Task<bool> IsGitAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await RunCaptureAsync("git", "--version", ct);
            return result.Contains("git version", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// Ensures Git is available. If not found, installs it silently via winget
    /// and patches the current process PATH so pip can use git immediately.
    /// </summary>
    public static async Task EnsureGitAsync(IProgress<string>? log, CancellationToken ct)
    {
        if (await IsGitAvailableAsync(ct))
        {
            Report(log,"Git is available.\n");
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            Report(log,"Git not found. Install it with:  sudo apt install git  then try again.");
            throw new InvalidOperationException("Git is required. Install it with: sudo apt install git");
        }

        Report(log,"Git not found — installing Git for Windows automatically…");
        Report(log,"  (A UAC prompt may appear — click Yes to allow)\n");

        try
        {
            await RunStreamAsync("winget",
                "install --id Git.Git -e --source winget " +
                "--accept-package-agreements --accept-source-agreements --silent",
                log, ct);
        }
        catch (Exception ex)
        {
            Report(log,$"winget install failed: {ex.Message}");
            Report(log,"Please install Git manually from https://git-scm.com and try again.\n");
            throw;
        }

        // Patch this process's PATH so pip can find git without a restart
        foreach (var candidate in new[]
        {
            @"C:\Program Files\Git\cmd\git.exe",
            @"C:\Program Files (x86)\Git\cmd\git.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "Programs", "Git", "cmd", "git.exe"),
        })
        {
            if (!File.Exists(candidate)) continue;
            var dir = Path.GetDirectoryName(candidate)!;
            var current = Environment.GetEnvironmentVariable("PATH") ?? "";
            if (!current.Contains(dir, StringComparison.OrdinalIgnoreCase))
                Environment.SetEnvironmentVariable("PATH", dir + ";" + current);
            Report(log,"Git installed and ready.\n");
            return;
        }

        Report(log,"Git installed. If the next step fails, restart TransitLab and try again.\n");
    }

    // ── Install EXOTIC from GitHub branch ────────────────────────────────────

    public static async Task InstallExoticFromBranchAsync(
        string pythonExe, string repoUrl, IProgress<string>? log, CancellationToken ct)
    {
        // Normalise: pip requires the git+ scheme prefix
        var pipUrl = repoUrl.Trim();
        if (!pipUrl.StartsWith("git+", StringComparison.OrdinalIgnoreCase))
            pipUrl = "git+" + pipUrl;

        // Ensure Git is present — auto-install if missing
        await EnsureGitAsync(log, ct);

        Report(log,"Upgrading pip…");
        await RunStreamAsync(pythonExe, "-m pip install --upgrade pip --user --no-warn-script-location", log, ct);

        Report(log,$"\nInstalling EXOTIC pre-release from branch…");
        Report(log,$"  {pipUrl}\n");
        await RunStreamAsync(pythonExe,
            $"-m pip install --upgrade \"{pipUrl}\" --user --no-warn-script-location",
            log, ct);
    }

    // ── Uninstall EXOTIC ─────────────────────────────────────────────────────

    public static async Task UninstallExoticAsync(string pythonExe, IProgress<string>? log, CancellationToken ct)
    {
        Report(log, "Uninstalling EXOTIC…");
        // pip uninstall -y exits 0 whether or not anything was removed, so we can't
        // use the exit code to break the loop.  Instead, check whether pip actually
        // removed something by looking for "Successfully uninstalled" in the output.
        int passes = 0;
        while (true)
        {
            bool removed = false;
            var trackingLog = new Progress<string>(msg =>
            {
                if (msg.Contains("Successfully uninstalled", StringComparison.OrdinalIgnoreCase))
                    removed = true;
                log?.Report(msg);
            });
            await RunStreamAsync(pythonExe, "-m pip uninstall exotic -y", trackingLog, ct);
            if (!removed) break;
            passes++;
        }
        Report(log, passes > 0 ? "EXOTIC uninstalled." : "EXOTIC was not installed.");
    }

    // ── Repair EXOTIC scripts ─────────────────────────────────────────────────

    /// <summary>
    /// Force-reinstalls only the exotic package (no dependencies) to ensure
    /// exotic.exe is created in the user Scripts folder.  Fast: ~5 seconds.
    /// </summary>
    public static async Task RepairExoticScriptsAsync(
        string pythonExe, IProgress<string>? log, CancellationToken ct)
    {
        Report(log,"\nRepairing EXOTIC script entry (force-reinstall, no deps)…");
        await RunStreamAsync(pythonExe,
            "-m pip install --force-reinstall --no-deps exotic --user --no-warn-script-location",
            log, ct);
    }

    // ── Locate exotic.exe ─────────────────────────────────────────────────────

    public static async Task<string?> FindExoticExeAsync(string pythonExe, CancellationToken ct = default)
    {
        var isWin      = OperatingSystem.IsWindows();
        var exoticName = isWin ? "exotic.exe" : "exotic";
        var scriptsDir = isWin ? "Scripts" : "bin";

        // 1. Derive from exotic package location — works for user AND system installs.
        //    Windows user:  …\AppData\Roaming\Python\Python310\site-packages\exotic\ → …\Scripts\exotic.exe
        //    Linux user:    ~/.local/lib/python3.10/site-packages/exotic\            → ~/.local/bin/exotic
        //    NOTE: take the last non-empty line of stdout to skip any import-time messages.
        try
        {
            var raw = (await RunCaptureAsync(pythonExe,
                "-c \"import exotic; print(exotic.__file__)\"", ct));
            var pkgFile = raw.Split('\n')
                             .Select(l => l.Trim())
                             .LastOrDefault(l => l.Length > 0) ?? "";
            if (!string.IsNullOrEmpty(pkgFile))
            {
                var sitePackages = Path.GetDirectoryName(Path.GetDirectoryName(pkgFile));
                if (sitePackages is not null)
                {
                    foreach (var dir in new[] {
                        Path.GetDirectoryName(sitePackages),
                        Path.GetDirectoryName(Path.GetDirectoryName(sitePackages))
                    })
                    {
                        if (dir is null) continue;
                        var candidate = Path.Combine(dir, scriptsDir, exoticName);
                        if (File.Exists(candidate)) return candidate;
                    }
                }
            }
        }
        catch { }

        // 2. Ask Python via sysconfig where user scripts live
        try
        {
            var script = "import sysconfig,os; print(sysconfig.get_path('scripts',f'{os.name}_user'))";
            var dir = (await RunCaptureAsync(pythonExe, $"-c \"{script}\"", ct)).Trim();
            var candidate = Path.Combine(dir, exoticName);
            if (File.Exists(candidate)) return candidate;
        }
        catch { }

        // 3. Scripts/bin folder alongside python executable (conda / system install)
        var sibling = Path.Combine(Path.GetDirectoryName(pythonExe)!, scriptsDir, exoticName);
        if (File.Exists(sibling)) return sibling;

        // 4. ExoticFinder (conda envs, pip --user dirs, PATH)
        return ExoticFinder.Find("");
    }

    public static async Task<string?> GetExoticVersionAsync(string pythonExe, CancellationToken ct = default)
    {
        try
        {
            var out1 = await RunCaptureAsync(pythonExe,
                "-c \"import exotic; print(getattr(exotic,'__version__','?'))\"", ct);
            var ver = out1.Trim();
            return string.IsNullOrEmpty(ver) ? null : ver;
        }
        catch { return null; }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Sends a message to both the Setup window's progress log and the session diagnostics log.
    /// </summary>
    private static void Report(IProgress<string>? log, string message)
    {
        if (log is not null)
            log.Report(message);
        else
            SessionLogService.Write($"[Setup] {message}");
    }

    private static async Task RunStreamAsync(
        string exe, string args, IProgress<string>? log, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false,       CreateNoWindow        = true,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Cannot start {exe}.");
        var outTask = DrainAsync(proc.StandardOutput, log, ct);
        var errTask = DrainAsync(proc.StandardError,  log, ct);
        await Task.WhenAll(outTask, errTask);
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(exe)} exited with code {proc.ExitCode}.");
    }

    private static async Task DrainAsync(StreamReader reader, IProgress<string>? log, CancellationToken ct)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {
                // Strip trailing \r and skip winget/console spinner frames (-, \, |, /)
                var clean = line.TrimEnd('\r', ' ');
                if (clean.Length == 0 || clean is "-" or "\\" or "|" or "/")
                    continue;
                Report(log, clean);
            }
        }
        catch (OperationCanceledException) { }
    }

    private static async Task<string> RunCaptureAsync(string exe, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false,       CreateNoWindow        = true,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Cannot start {exe}.");
        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        return stdout;
    }

    public static string PyInstallerPath =>
        Path.Combine(Path.GetTempPath(), PyInstallerName);
}
