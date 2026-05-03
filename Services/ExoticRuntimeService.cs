using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TransitLab.Services;

public sealed record ExoticRuntime(string PythonExePath, string? ExoticExePath, string? Version);

public static class ExoticRuntimeService
{
    public static async Task<ExoticRuntime?> ResolveAsync(
        string savedPythonPath,
        string savedExoticPath,
        CancellationToken ct = default)
    {
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        async Task<ExoticRuntime?> TryPythonAsync(string? pythonExe, string? exoticExe)
        {
            if (string.IsNullOrWhiteSpace(pythonExe) || !tried.Add(pythonExe))
                return null;

            if (!await CanRunExoticModuleAsync(pythonExe, ct))
                return null;

            var version = await ExoticInstallService.GetExoticVersionAsync(pythonExe, ct);
            var script = exoticExe;
            if (string.IsNullOrWhiteSpace(script) || !File.Exists(script))
                script = await ExoticInstallService.FindExoticExeAsync(pythonExe, ct);
            return new ExoticRuntime(pythonExe, script, version);
        }

        var runtime = await TryPythonAsync(NormalizeExistingPython(savedPythonPath), savedExoticPath);
        if (runtime is not null) return runtime;

        var exoticPath = ExoticFinder.Find(savedExoticPath);
        runtime = await TryPythonAsync(DerivePythonExe(exoticPath), exoticPath);
        if (runtime is not null) return runtime;

        var py = await ExoticInstallService.FindPythonAsync(ct);
        runtime = await TryPythonAsync(py?.ExePath, exoticPath);
        return runtime;
    }

    public static bool LooksLikePython(string path)
    {
        var name = Path.GetFileName(path);
        return name.Equals("python", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("python.exe", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("python3", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("python3.exe", StringComparison.OrdinalIgnoreCase) ||
               (name.StartsWith("python3.", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
    }

    public static string? DerivePythonExe(string? exoticPath)
    {
        if (string.IsNullOrWhiteSpace(exoticPath) || !File.Exists(exoticPath))
            return null;

        if (LooksLikePython(exoticPath))
            return exoticPath;

        var commandDir = Path.GetDirectoryName(exoticPath);
        if (commandDir is null) return null;

        var candidates = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            var envDir = Path.GetDirectoryName(commandDir);
            if (envDir is not null)
            {
                candidates.Add(Path.Combine(envDir, "python.exe"));
                candidates.Add(Path.Combine(envDir, "python3.exe"));
            }
        }
        else
        {
            candidates.Add(Path.Combine(commandDir, "python3"));
            candidates.Add(Path.Combine(commandDir, "python"));
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? NormalizeExistingPython(string pythonPath)
    {
        if (string.IsNullOrWhiteSpace(pythonPath))
            return null;
        return File.Exists(pythonPath) || !Path.IsPathRooted(pythonPath) ? pythonPath : null;
    }

    private static async Task<bool> CanRunExoticModuleAsync(string pythonExe, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = pythonExe,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("import importlib.util,sys; sys.exit(0 if importlib.util.find_spec('exotic.exotic') else 1)");

            using var proc = Process.Start(psi);
            if (proc is null) return false;
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            await Task.WhenAll(stdoutTask, stderrTask);
            await proc.WaitForExitAsync(ct);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
