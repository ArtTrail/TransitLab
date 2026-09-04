using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TransitLab.Services;

public record PythonInfo(string Version, string ExePath)
{
    public static bool IsCompatible(string version, int minMinor = 10)
    {
        var parts = version.Split('.');
        if (parts.Length < 2) return false;
        return int.TryParse(parts[0], out var maj) &&
               int.TryParse(parts[1], out var min) &&
               maj == 3 && min >= minMinor;
    }

    public static bool IsOutOfSupportedRange(string version)
    {
        var parts = version.Split('.');
        if (parts.Length < 2) return false;
        if (!int.TryParse(parts[0], out var maj) || !int.TryParse(parts[1], out var min)) return false;
        return maj != 3 || min < 10 || min > 12;
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

    // Pre-release environment base interpreter — some EXOTIC branches now declare
    // Requires-Python >=3.12, which the Stable-oriented 3.10.11 interpreter above can't
    // satisfy. Kept fully separate from PyVersion/PyUrl/PyInstallerName so Stable's
    // install is completely unaffected.
    public const  string PyVersion312       = "3.12.10";
    public const  string PyUrl312           = "https://www.python.org/ftp/python/3.12.10/python-3.12.10-amd64.exe";
    public const  string PyInstallerName312 = "python-3.12.10-amd64.exe";
    public static string PyInstallerPath312 =>
        Path.Combine(Path.GetTempPath(), PyInstallerName312);

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(15),
        DefaultRequestHeaders = { { "User-Agent", "Mozilla/5.0" } },
    };

    // ── Python detection ──────────────────────────────────────────────────────

    public static async Task<PythonInfo?> FindPythonAsync(CancellationToken ct = default, int minMinor = 10)
    {
        // 1. py launcher lists all installed versions
        var fromLauncher = await FindViaPyLauncherAsync(ct, minMinor);
        if (fromLauncher != null) return fromLauncher;

        // 2. macOS absolute paths first — app bundles don't inherit shell PATH,
        //    so Xcode's python3 (3.9) would otherwise win over a newer Python.org install.
        //    Listed newest-first so a minMinor:12 search prefers a real 3.12 install.
        if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var macosPaths = new[]
            {
                "/Library/Frameworks/Python.framework/Versions/3.12/bin/python3.12",
                "/usr/local/bin/python3.12",
                "/opt/homebrew/bin/python3.12",
                Path.Combine(home, ".pyenv", "shims", "python3.12"),
                "/Library/Frameworks/Python.framework/Versions/3.10/bin/python3.10",
                "/usr/local/bin/python3.10",
                "/opt/homebrew/bin/python3.10",
                "/Library/Frameworks/Python.framework/Versions/3.9/bin/python3.9",
                "/usr/local/bin/python3.9",
                "/opt/homebrew/bin/python3.9",
                "/Library/Frameworks/Python.framework/Versions/3.8/bin/python3.8",
                "/usr/local/bin/python3.8",
                Path.Combine(home, ".pyenv", "shims", "python3.10"),
                Path.Combine(home, ".pyenv", "shims", "python3"),
            };
            foreach (var absPath in macosPaths)
            {
                if (!File.Exists(absPath)) continue;
                var p = await ProbeExeAsync(absPath, ct, minMinor);
                if (p != null) return p;
            }
        }

        // 3. python3.12 / python3.10 / python3 / python on PATH (newest-first)
        foreach (var cmd in new[] { "python3.12", "python3.11", "python3.10", "python3.9", "python3", "python" })
        {
            var p = await ProbeExeAsync(cmd, ct, minMinor);
            if (p != null) return p;
        }

        // 4. Common install paths (Windows)
        var fromScan = ScanCommonPaths(minMinor);
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
                    if (ver is not null && PythonInfo.IsCompatible(ver, minMinor))
                        return new PythonInfo(ver, candidate);
                }
            }
        }

        return null;
    }

    private static async Task<PythonInfo?> FindViaPyLauncherAsync(CancellationToken ct, int minMinor)
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
                if (PythonInfo.IsCompatible(full, minMinor)) return new PythonInfo(full, path);
            }
        }
        catch { }
        return null;
    }

    private static async Task<PythonInfo?> ProbeExeAsync(string exe, CancellationToken ct, int minMinor)
    {
        try
        {
            var ver = await GetVersionAsync(exe, ct);
            if (ver == null || !PythonInfo.IsCompatible(ver, minMinor)) return null;
            var path = await RunCaptureAsync(exe, "-c \"import sys; print(sys.executable)\"", ct);
            path = path.Trim();
            return File.Exists(path) ? new PythonInfo(ver, path) : null;
        }
        catch { return null; }
    }

    /// <summary>Runs "&lt;exe&gt; --version" and parses the result — public so callers can check an
    /// already-existing venv's own interpreter version (e.g. to detect a stale venv built from
    /// an older base Python before rebuilding it).</summary>
    public static async Task<string?> GetVersionAsync(string exe, CancellationToken ct)
    {
        try
        {
            var out1 = await RunCaptureAsync(exe, "--version", ct);
            var m = Regex.Match(out1, @"Python (\d+\.\d+\.\d+)");
            return m.Success ? m.Groups[1].Value : null;
        }
        catch { return null; }
    }

    private static PythonInfo? ScanCommonPaths(int minMinor)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roots = new[]
        {
            Path.Combine(local, "Programs", "Python"),
            @"C:\Python312", @"C:\Python311", @"C:\Python310", @"C:\Python39", @"C:\Python38",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Python312"),
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
                if (PythonInfo.IsCompatible(ver, minMinor)) return new PythonInfo(ver, exe);
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

    public static Task<string?> InstallPythonAsync(
        string installerPath, IProgress<string>? log, CancellationToken ct) =>
        InstallPythonAsync(installerPath, PyVersion, "Python310", log, ct);

    /// <param name="versionLabel">Used only for log text and to decide whether the embeddable
    /// 1603 fallback (3.10-only today) applies — it never affects which installer actually runs.</param>
    /// <param name="dirName">Expected install-dir leaf name (e.g. "Python312") used to locate the
    /// interpreter after a successful MSI install, since Windows installer version-numbers the
    /// default install path and there's no other reliable way to know it from the exit code alone.</param>
    public static async Task<string?> InstallPythonAsync(
        string installerPath, string versionLabel, string dirName, IProgress<string>? log, CancellationToken ct)
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
        // PrependPath=0: every caller below resolves this interpreter via its known fixed
        // install path (see knownPaths below), never via a bare "python"/"pip" PATH lookup —
        // so there's no reason to touch the user's system-wide PATH at all. Prepending it would
        // silently make "python"/"pip" resolve to this 3.10.11 install everywhere else on the
        // machine too, ahead of whatever the user already had (their own venvs, IDE configs,
        // other scripts) — a real, unforced side effect this app has no need to cause.
        // /log: write verbose MSI log so failures can be diagnosed.
        var args = $"/quiet InstallAllUsers=1 PrependPath=0 Include_pip=1 Include_launcher=0 Include_test=0 /log \"{msiLog}\"";
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
                if (versionLabel == PyVersion)
                {
                    Report(log,"Falling back to embeddable package — no Windows Installer required…");
                    return await InstallPythonEmbeddableAsync(log, ct);
                }
                throw new InvalidOperationException(
                    $"Python {versionLabel} installer failed (exit code 1603) and no embeddable fallback is available for this version.");
            }

            throw new InvalidOperationException($"Python installer failed (exit code {proc.ExitCode}). See MSI details above.");
        }

        Report(log,$"Python {versionLabel} installed (MSI).");

        // Check both system-wide (InstallAllUsers=1) and per-user install paths
        var knownPaths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                dirName, "python.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "Python", dirName, "python.exe"),
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

    // ── Isolated environments (venvs, v2.8.0+) ─────────────────────────────────
    // Each ExoticEnvironment gets its own venv so stable and pre-release EXOTIC installs
    // can coexist without reinstalling — see ConfigService.ExoticEnvironment.

    /// <summary>Creates a new Python venv at <paramref name="destPath"/> using <paramref name="basePythonExe"/>.</summary>
    public static async Task CreateVenvAsync(
        string basePythonExe, string destPath, IProgress<string>? log, CancellationToken ct)
    {
        Report(log, $"Creating environment at {destPath}…");
        Directory.CreateDirectory(Path.GetDirectoryName(destPath) ?? ".");
        await RunStreamAsync(basePythonExe, $"-m venv \"{destPath}\"", log, ct);
    }

    /// <summary>Resolves the venv's own python executable path (platform-specific layout).</summary>
    public static string GetVenvPythonExe(string venvPath) =>
        OperatingSystem.IsWindows()
            ? Path.Combine(venvPath, "Scripts", "python.exe")
            : Path.Combine(venvPath, "bin", "python");

    // ── Install EXOTIC ────────────────────────────────────────────────────────

    /// <param name="targetVenv">
    /// True when <paramref name="pythonExe"/> is a managed venv's own interpreter — pip's
    /// --user flag errors inside a venv ("User site-packages are not visible in this
    /// virtualenv"), and --force-reinstall's downgrade workaround is unnecessary since a
    /// venv can never already have a conflicting version installed.
    /// </param>
    public static async Task InstallExoticAsync(
        string pythonExe, IProgress<string>? log, CancellationToken ct, bool targetVenv = false)
    {
        var userFlag = targetVenv ? "" : " --user";

        Report(log,"Upgrading pip…");
        await RunStreamAsync(pythonExe, $"-m pip install --upgrade pip{userFlag} --no-warn-script-location", log, ct);

        Report(log,"\nInstalling prerequisite packages…");
        await RunStreamAsync(pythonExe,
            $"-m pip install{userFlag} --no-warn-script-location " +
            "\"setuptools>=62.6\" \"setuptools_scm[toml]>=6.4.2\" \"wheel>=0.37.1\"",
            log, ct);

        Report(log,"\nInstalling EXOTIC (this may take several minutes)…");
        // --force-reinstall ensures a dev/branch build is replaced by the latest stable PyPI
        // release in the legacy single-install path. Without it, pip sees the dev version as
        // newer and skips the install. Not needed (or usable well) for a fresh venv.
        var forceReinstall = targetVenv ? "" : " --force-reinstall";
        await RunStreamAsync(pythonExe, $"-m pip install --upgrade{forceReinstall} exotic{userFlag} --no-warn-script-location", log, ct);
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

    public sealed record BranchVersionCheckResult(string? RemoteVersion, string? Error);

    /// <summary>
    /// Resolves the exact version a "repoUrl@branch" reference (same format InstallExoticFromBranchAsync
    /// accepts) would install, via `pip install --dry-run --report &lt;file&gt;` — this runs pip's real
    /// dependency resolution and asks the build backend (setuptools_scm) to compute the version, the
    /// same way an actual install would, but writes nothing to site-packages. Slower than a plain
    /// `git ls-remote` (it does shallow-clone the repo to run the build backend, and with the flags
    /// below, re-resolves the full dependency tree too) but gives back the exact same
    /// "4.3.2.devN+g&lt;hash&gt;.d&lt;date&gt;" string the installed version is displayed with, instead of
    /// just a bare commit hash — directly comparable, and directly displayable to the user.
    /// Requires --upgrade --force-reinstall: without them, pip sees "exotic" already satisfied by
    /// whatever's currently installed and reports zero install items — it never even looks at the
    /// branch's current state at all, so the dry-run would silently check nothing.
    /// </summary>
    public static async Task<BranchVersionCheckResult> CheckBranchVersionAsync(string pythonExe, string repoUrlWithRef, CancellationToken ct)
    {
        var tempReport = Path.Combine(Path.GetTempPath(), $"pip_report_{Guid.NewGuid():N}.json");
        try
        {
            if (!await IsGitAvailableAsync(ct))
                return new BranchVersionCheckResult(null, "Git is not installed — install it via Install / Reinstall first.");

            var trimmed = repoUrlWithRef.Trim();
            var pipUrl = trimmed.StartsWith("git+", StringComparison.OrdinalIgnoreCase) ? trimmed : "git+" + trimmed;

            await RunCaptureAsync(pythonExe,
                $"-m pip install --upgrade --force-reinstall --dry-run --quiet --disable-pip-version-check --report \"{tempReport}\" \"{pipUrl}\"", ct);

            if (!File.Exists(tempReport))
                return new BranchVersionCheckResult(null, "pip did not produce a report — the branch or URL may be invalid.");

            var json = await File.ReadAllTextAsync(tempReport, ct);
            using var doc = JsonDocument.Parse(json);
            foreach (var item in doc.RootElement.GetProperty("install").EnumerateArray())
            {
                var name = item.GetProperty("metadata").GetProperty("name").GetString();
                if (!string.Equals(name, "exotic", StringComparison.OrdinalIgnoreCase)) continue;
                var version = item.GetProperty("metadata").GetProperty("version").GetString();
                return new BranchVersionCheckResult(version, null);
            }
            return new BranchVersionCheckResult(null, "exotic package not found in the resolved install report.");
        }
        catch (Exception ex)
        {
            return new BranchVersionCheckResult(null, ex.Message);
        }
        finally
        {
            try { File.Delete(tempReport); } catch { }
        }
    }

    // ── Install EXOTIC from GitHub branch ────────────────────────────────────

    public static async Task InstallExoticFromBranchAsync(
        string pythonExe, string repoUrl, IProgress<string>? log, CancellationToken ct, bool targetVenv = false)
    {
        var userFlag = targetVenv ? "" : " --user";

        // Normalise: pip requires the git+ scheme prefix
        var pipUrl = repoUrl.Trim();
        if (!pipUrl.StartsWith("git+", StringComparison.OrdinalIgnoreCase))
            pipUrl = "git+" + pipUrl;

        // Ensure Git is present — auto-install if missing
        await EnsureGitAsync(log, ct);

        Report(log,"Upgrading pip…");
        await RunStreamAsync(pythonExe, $"-m pip install --upgrade pip{userFlag} --no-warn-script-location", log, ct);

        Report(log,$"\nInstalling EXOTIC pre-release from branch…");
        Report(log,$"  {pipUrl}\n");
        await RunStreamAsync(pythonExe,
            $"-m pip install --upgrade \"{pipUrl}\"{userFlag} --no-warn-script-location",
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

    /// <summary>
    /// Confirms the exotic package is actually importable — distinct from GetExoticVersionAsync
    /// returning null, which is ambiguous (could mean "not installed" or "installed but the
    /// version string couldn't be read"). Uses find_spec + exit code rather than trying to
    /// interpret captured output, so it can't be confused by a truly-missing package.
    /// </summary>
    public static async Task<bool> IsExoticInstalledAsync(string pythonExe, CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo(pythonExe,
                "-c \"import importlib.util,sys; sys.exit(0 if importlib.util.find_spec('exotic.exotic') else 1)\"")
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false,       CreateNoWindow        = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            await Task.WhenAll(stdoutTask, stderrTask);
            await proc.WaitForExitAsync(ct);
            return proc.ExitCode == 0;
        }
        catch { return false; }
    }

    // ── Clear ldtk limb-darkening cache ─────────────────────────────────────

    /// <summary>
    /// Deletes EXOTIC's ldtk limb-darkening model cache (~/.ldtk). ldtk auto-recreates
    /// this directory and re-downloads any needed files on the next EXOTIC run, so this
    /// is always safe. Fixes crashes caused by a truncated/corrupted cache file left
    /// behind by an interrupted download (ldtk itself doesn't reliably detect these).
    /// </summary>
    public static Task<bool> ClearLdtkCacheAsync(IProgress<string>? log = null)
    {
        var ldtkDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ldtk");

        if (!Directory.Exists(ldtkDir))
        {
            Report(log, "No ldtk cache found — nothing to clear.");
            return Task.FromResult(false);
        }

        Report(log, $"Clearing ldtk cache: {ldtkDir}");
        Directory.Delete(ldtkDir, recursive: true);
        Report(log, "ldtk cache cleared. It will be re-downloaded automatically on the next EXOTIC run.");
        return Task.FromResult(true);
    }

    // ── ldtk HTTPS mirrors (fallback when the phoenix.astro.physik.uni-goettingen.de
    //    FTP server is unreachable — same mirrors EXOTIC 4.3.2+'s own ldtk fallback uses) ──

    public static readonly (string Label, string BaseUrl)[] LdtkHttpMirrors =
    {
        ("GWDG (Göttingen)", "https://ftp.gwdg.de/pub/misc/phoenix"),
        ("NextAstronomy",    "https://downloads.nextastro.org/PHOENIX"),
    };

    // ldtk's "vis-lowres" dataset — the one EXOTIC/gael_ld.py uses by default.
    private const string LdtkEdir = "SpecInt50FITS/PHOENIX-ACES-AGSS-COND-SPECINT-2011";

    private static string LdtkCacheDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ldtk", "cache_vis-lowres");

    private static string LdtkServerFileListPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ldtk", "server_file_list_vis-lowres.pkl");

    private sealed record LdtkIndexEntry(string Name, bool IsDirectory, long Size);

    /// <summary>
    /// Crawls an ldtk HTTPS mirror's directory index (Apache/nginx autoindex-style HTML)
    /// and returns every metallicity directory's file list with sizes. Tries each mirror
    /// in <see cref="LdtkHttpMirrors"/> in order.
    /// </summary>
    public static async Task<(Dictionary<string, List<(string Name, long Size)>> Listing, long TotalBytes, string MirrorLabel)>
        CrawlLdtkMirrorAsync(IProgress<string>? log = null, CancellationToken ct = default)
    {
        Exception? lastError = null;
        foreach (var (label, baseUrl) in LdtkHttpMirrors)
        {
            try
            {
                Report(log, $"Indexing {label} mirror…");
                var rootHtml = await Http.GetStringAsync($"{baseUrl}/{LdtkEdir}/", ct);
                var zdirs = ParseLdtkIndex(rootHtml)
                    .Where(e => e.IsDirectory)
                    .Select(e => e.Name)
                    .OrderBy(z => z, StringComparer.Ordinal)
                    .ToList();
                if (zdirs.Count == 0)
                    throw new InvalidOperationException("No metallicity directories found in mirror index.");

                var listing = new Dictionary<string, List<(string, long)>>();
                long total = 0;
                foreach (var zdir in zdirs)
                {
                    ct.ThrowIfCancellationRequested();
                    var zHtml = await Http.GetStringAsync($"{baseUrl}/{LdtkEdir}/{zdir}/", ct);
                    var files = ParseLdtkIndex(zHtml)
                        .Where(e => !e.IsDirectory)
                        .OrderBy(e => e.Name, StringComparer.Ordinal)
                        .Select(e => (e.Name, e.Size))
                        .ToList();
                    listing[zdir] = files;
                    total += files.Sum(f => f.Size);
                }
                Report(log, $"{label} index complete: {listing.Count} directories, " +
                            $"{listing.Sum(kv => kv.Value.Count)} files, {total / 1024.0 / 1024.0:F0} MB.");
                return (listing, total, label);
            }
            catch (Exception ex)
            {
                lastError = ex;
                Report(log, $"{label} mirror indexing failed: {ex.Message}");
            }
        }
        throw new InvalidOperationException("All ldtk HTTPS mirrors failed to index.", lastError);
    }

    /// <summary>
    /// Parses an Apache/nginx-style autoindex HTML page: extracts each anchor's target
    /// name and, for files, the trailing byte-size text that Apache prints after the link
    /// on the same line. Filters out "..", the parent-dir link, and any ".txt" metadata files
    /// — mirroring EXOTIC's own <c>_http_index_names</c> filtering exactly.
    /// </summary>
    private static List<LdtkIndexEntry> ParseLdtkIndex(string html)
    {
        var doc = new HtmlAgilityPack.HtmlDocument();
        doc.LoadHtml(html);
        var result = new List<LdtkIndexEntry>();

        var anchors = doc.DocumentNode.SelectNodes("//a[@href]");
        if (anchors is null) return result;

        foreach (var a in anchors)
        {
            var rawHref = a.GetAttributeValue("href", "");
            var isDirHref = rawHref.EndsWith('/');
            var name = System.Net.WebUtility.UrlDecode(rawHref.Split('?')[0].Split('#')[0]).Trim('/');
            if (string.IsNullOrEmpty(name) || name is "." or ".." || name.Contains('/'))
                continue;
            if (name.ToLowerInvariant().Contains(".txt"))
                continue;

            long size = 0;
            var trailing = a.NextSibling?.InnerText ?? "";
            var match = Regex.Match(trailing, @"(\d+)\s*$");
            if (match.Success) long.TryParse(match.Groups[1].Value, out size);

            result.Add(new LdtkIndexEntry(name, isDirHref, size));
        }
        return result;
    }

    /// <summary>
    /// Downloads every file in <paramref name="listing"/> into ~/.ldtk/cache_vis-lowres/,
    /// skipping files that already exist locally (safe to re-run/resume). Falls back to the
    /// next mirror in <see cref="LdtkHttpMirrors"/> if the current one fails partway through.
    /// </summary>
    public static async Task<int> DownloadLdtkFilesAsync(
        Dictionary<string, List<(string Name, long Size)>> listing,
        string mirrorLabel,
        IProgress<(int done, int total, string currentFile)>? progress = null,
        IProgress<string>? log = null,
        int maxConcurrency = 8,
        CancellationToken ct = default)
    {
        var toDownload = new List<(string ZDir, string Name)>();
        foreach (var (zdir, files) in listing)
        {
            var zdirPath = Path.Combine(LdtkCacheDir, zdir);
            foreach (var (name, _) in files)
            {
                if (!File.Exists(Path.Combine(zdirPath, name)))
                    toDownload.Add((zdir, name));
            }
        }

        Report(log, $"{toDownload.Count} file(s) to download ({listing.Sum(kv => kv.Value.Count) - toDownload.Count} already cached).");
        if (toDownload.Count == 0) return 0;

        var mirrors = LdtkHttpMirrors.SkipWhile(m => m.Label != mirrorLabel).ToArray();
        if (mirrors.Length == 0) mirrors = LdtkHttpMirrors;

        var remaining = toDownload;
        var doneCount = 0;
        Exception? lastError = null;

        foreach (var (label, baseUrl) in mirrors)
        {
            if (remaining.Count == 0) break;
            var stillMissing = new System.Collections.Concurrent.ConcurrentBag<(string ZDir, string Name)>();
            using var gate = new SemaphoreSlim(maxConcurrency);
            var tasks = remaining.Select(async item =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    var url  = $"{baseUrl}/{LdtkEdir}/{item.ZDir}/{Uri.EscapeDataString(item.Name)}";
                    var dest = Path.Combine(LdtkCacheDir, item.ZDir, item.Name);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    await DownloadFileAsync(url, dest, null, ct);
                    var newDone = Interlocked.Increment(ref doneCount);
                    progress?.Report((newDone, toDownload.Count, item.Name));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lastError = ex;
                    stillMissing.Add(item);
                }
                finally { gate.Release(); }
            });
            await Task.WhenAll(tasks);
            remaining = stillMissing.ToList();
            if (remaining.Count > 0)
                Report(log, $"{label} mirror: {remaining.Count} file(s) failed, trying next mirror if available…");
        }

        if (remaining.Count > 0)
            throw new InvalidOperationException(
                $"{remaining.Count} file(s) could not be downloaded from any ldtk mirror.", lastError);

        Report(log, $"Downloaded {doneCount} file(s) successfully.");
        return doneCount;
    }

    /// <summary>
    /// Writes (or merges into an existing) ~/.ldtk/server_file_list_vis-lowres.pkl so ldtk's
    /// Client.__init__ skips its own FTP directory listing entirely. Shells out to the
    /// already-managed Python interpreter for the actual pickle serialization, since the
    /// pickle format must be readable by ldtk's own Python <c>pickle.load()</c> call.
    /// </summary>
    public static async Task WriteLdtkServerFileListAsync(
        string pythonExe,
        Dictionary<string, List<(string Name, long Size)>> listing,
        IProgress<string>? log = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LdtkServerFileListPath)!);

        var tempJson = Path.Combine(Path.GetTempPath(), $"ldtk_listing_{Guid.NewGuid():N}.json");
        var tempScript = Path.Combine(Path.GetTempPath(), $"ldtk_pickle_{Guid.NewGuid():N}.py");
        try
        {
            var jsonObj = listing.ToDictionary(
                kv => kv.Key,
                kv => (object)kv.Value.Select(f => f.Name).OrderBy(n => n, StringComparer.Ordinal).ToList());
            await File.WriteAllTextAsync(tempJson, System.Text.Json.JsonSerializer.Serialize(jsonObj), ct);

            var script =
                "import json, pickle, sys\n" +
                "new_json, pkl_path = sys.argv[1], sys.argv[2]\n" +
                "with open(new_json, 'r') as f:\n" +
                "    new_data = json.load(f)\n" +
                "existing = {}\n" +
                "try:\n" +
                "    with open(pkl_path, 'rb') as f:\n" +
                "        existing = pickle.load(f)\n" +
                "except Exception:\n" +
                "    existing = {}\n" +
                "for zdir, names in new_data.items():\n" +
                "    merged = sorted(set(existing.get(zdir, [])) | set(names))\n" +
                "    existing[zdir] = merged\n" +
                "with open(pkl_path, 'wb') as f:\n" +
                "    pickle.dump(existing, f)\n" +
                "print(f'Wrote {sum(len(v) for v in existing.values())} total filenames across {len(existing)} directories.')\n";
            await File.WriteAllTextAsync(tempScript, script, ct);

            Report(log, "Writing ldtk server file listing…");
            var output = await RunCaptureAsync(
                pythonExe, $"\"{tempScript}\" \"{tempJson}\" \"{LdtkServerFileListPath}\"", ct);
            Report(log, output.Trim());
        }
        finally
        {
            try { File.Delete(tempJson); } catch { }
            try { File.Delete(tempScript); } catch { }
        }
    }

    // ── Per-target ldtk grid selection (C# port of ldtk's own TEFF/LOGG/Z grid math) ──
    //    Verified line-for-line against ldtk.core's a_lims()/is_inside() with real values.

    private static readonly double[] LdtkTeffPoints = BuildLdtkTeffPoints();
    private static readonly double[] LdtkLoggPoints  = Enumerable.Range(0, 13).Select(i => i * 0.5).ToArray();
    private static readonly double[] LdtkZPoints     = { -4.0, -3.0, -2.0, -1.5, -1.0, -0.0, 0.5, 1.0 };

    private static double[] BuildLdtkTeffPoints()
    {
        var pts = new List<double>();
        for (var t = 2300; t <= 7000; t += 100) pts.Add(t);
        for (var t = 7200; t <= 12000; t += 200) pts.Add(t);
        pts.Remove(5000); // the 5000 K models are missing from the PHOENIX grid
        return pts.ToArray();
    }

    /// <summary>numpy-style searchsorted(side='left'): first index i where a[i] &gt;= v.</summary>
    private static int SearchSortedLeft(double[] a, double v)
    {
        int lo = 0, hi = a.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (a[mid] < v) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    /// <summary>Port of ldtk.core.a_lims(a, v, e, s=3): expands ±s·e out to the nearest enclosing grid points.</summary>
    private static (double Lo, double Hi) LdtkALims(double[] a, double v, double e, double s = 3)
    {
        var loIdx = Math.Max(0, SearchSortedLeft(a, v - s * e) - 1);
        var hiIdx = Math.Min(a.Length - 1, SearchSortedLeft(a, v + s * e));
        return (a[loIdx], a[hiIdx]);
    }

    private static List<double> LdtkIsInside(double[] a, double lo, double hi) =>
        a.Where(x => x >= lo && x <= hi).ToList();

    private static string LdtkZDirName(double z) =>
        $"Z{(double.IsNegative(z) ? "-" : "+")}{Math.Abs(z):F1}";

    private static string LdtkFileName(int teff, double logg, double z) =>
        $"lte{teff:D5}-{logg:F2}{(double.IsNegative(z) ? "-" : "+")}{Math.Abs(z):F1}" +
        ".PHOENIX-ACES-AGSS-COND-SPECINT-2011.fits";

    /// <summary>
    /// Computes exactly which (Z-directory, filename) grid points ldtk's own
    /// LDPSetCreator would request for a given target's stellar parameters —
    /// mirrors <c>LDPSetCreator.__init__</c> → <c>Client.set_limits</c> in ldtk 1.8.6.
    /// </summary>
    public static List<(string ZDir, string FileName)> ComputeNeededLdtkFiles(
        double teff, double teffErr, double logg, double loggErr, double z, double zErr)
    {
        var (teffLo, teffHi) = LdtkALims(LdtkTeffPoints, teff, Math.Max(teffErr, 1));
        var (loggLo, loggHi) = LdtkALims(LdtkLoggPoints, logg, Math.Max(loggErr, 0.01));
        var (zLo, zHi)       = LdtkALims(LdtkZPoints, z, Math.Max(zErr, 0.01));

        var teffs = LdtkIsInside(LdtkTeffPoints, teffLo, teffHi);
        var loggs = LdtkIsInside(LdtkLoggPoints, loggLo, loggHi);
        var zs    = LdtkIsInside(LdtkZPoints, zLo, zHi);

        var result = new List<(string, string)>();
        foreach (var t in teffs)
            foreach (var g in loggs)
                foreach (var zz in zs)
                    result.Add((LdtkZDirName(zz), LdtkFileName((int)t, g, zz)));
        return result;
    }

    /// <summary>
    /// Fetches just the grid files a specific target needs (via the HTTPS mirrors) and
    /// merges the corresponding entries into ~/.ldtk/server_file_list_vis-lowres.pkl —
    /// the lightweight counterpart to <see cref="DownloadLdtkFilesAsync"/> for the
    /// reactive mid-run fallback, rather than the full ~2 GB library.
    /// </summary>
    public static async Task<int> FetchLdtkFilesForTargetAsync(
        string pythonExe,
        double teff, double teffErr, double logg, double loggErr, double z, double zErr,
        IProgress<string>? log = null,
        CancellationToken ct = default)
    {
        var needed = ComputeNeededLdtkFiles(teff, teffErr, logg, loggErr, z, zErr);
        Report(log, $"Target needs {needed.Count} limb-darkening grid file(s).");

        var listing = needed
            .GroupBy(f => f.ZDir)
            .ToDictionary(g => g.Key, g => g.Select(f => (Name: f.FileName, Size: 0L)).ToList());

        Exception? lastError = null;
        var downloaded = 0;
        foreach (var (label, baseUrl) in LdtkHttpMirrors)
        {
            var remaining = new List<(string ZDir, string Name)>();
            foreach (var (zdir, files) in listing)
                foreach (var (name, _) in files)
                    if (!File.Exists(Path.Combine(LdtkCacheDir, zdir, name)))
                        remaining.Add((zdir, name));

            if (remaining.Count == 0) break;

            try
            {
                Report(log, $"Fetching {remaining.Count} file(s) from {label}…");
                foreach (var item in remaining)
                {
                    ct.ThrowIfCancellationRequested();
                    var url  = $"{baseUrl}/{LdtkEdir}/{item.ZDir}/{Uri.EscapeDataString(item.Name)}";
                    var dest = Path.Combine(LdtkCacheDir, item.ZDir, item.Name);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    await DownloadFileAsync(url, dest, null, ct);
                    downloaded++;
                }
                break; // this mirror covered everything remaining
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
                Report(log, $"{label} mirror failed partway: {ex.Message}");
            }
        }

        var stillMissing = listing.SelectMany(kv => kv.Value.Select(f => Path.Combine(LdtkCacheDir, kv.Key, f.Name)))
            .Where(p => !File.Exists(p)).ToList();
        if (stillMissing.Count > 0)
            throw new InvalidOperationException(
                $"{stillMissing.Count} of {needed.Count} needed file(s) could not be fetched from any ldtk mirror.", lastError);

        await WriteLdtkServerFileListAsync(pythonExe, listing, log, ct);
        return downloaded;
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
