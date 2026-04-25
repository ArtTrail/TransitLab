using System;
using System.Collections.Generic;
using System.IO;

namespace TransitLab.Services;

/// <summary>
/// Locates the EXOTIC executable from common conda environments.
/// Returns a command list ready to pass to Process.Start, or null if not found.
/// </summary>
public static class ExoticFinder
{
    /// <summary>
    /// Try to find exotic.exe.  Returns the full path or null.
    /// Search order:
    ///   1. Saved path in config
    ///   2. Conda envs in common locations (miniconda3/anaconda3 under home + LOCALAPPDATA)
    ///   3. exotic.exe on PATH
    /// </summary>
    public static string? Find(string savedPath)
    {
        // 1. Saved path from a previous session
        if (!string.IsNullOrEmpty(savedPath) && File.Exists(savedPath))
            return savedPath;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (OperatingSystem.IsWindows())
        {
            // 2. Conda environments
            foreach (var candidate in CondaCandidatesWindows())
                if (File.Exists(candidate)) return candidate;

            // 3. pip --user Scripts dirs  (AppData\Roaming\Python\Python3xx\Scripts\)
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var userPythonRoot = Path.Combine(appData, "Python");
            if (Directory.Exists(userPythonRoot))
            {
                foreach (var pyDir in Directory.GetDirectories(userPythonRoot, "Python3*"))
                {
                    var exe = Path.Combine(pyDir, "Scripts", "exotic.exe");
                    if (File.Exists(exe)) return exe;
                }
            }

            return Which("exotic.exe") ?? Which("exotic");
        }
        else
        {
            // 2. pip --user install (~/.local/bin/exotic)
            var localBin = Path.Combine(home, ".local", "bin", "exotic");
            if (File.Exists(localBin)) return localBin;

            // 3. System install
            foreach (var p in new[] { "/usr/local/bin/exotic", "/usr/bin/exotic" })
                if (File.Exists(p)) return p;

            // 4. Conda environments
            foreach (var candidate in CondaCandidatesUnix())
                if (File.Exists(candidate)) return candidate;

            return Which("exotic");
        }
    }

    private static IEnumerable<string> CondaCandidatesWindows()
    {
        var home     = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localApp = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "";
        var roots = new[]
        {
            Path.Combine(home,     "miniconda3"),
            Path.Combine(home,     "miniconda"),
            Path.Combine(home,     "anaconda3"),
            Path.Combine(home,     "anaconda"),
            Path.Combine(localApp, "miniconda3"),
            Path.Combine(localApp, "anaconda3"),
        };
        foreach (var root in roots)
        {
            var envsDir = Path.Combine(root, "envs");
            if (!Directory.Exists(envsDir)) continue;
            foreach (var envDir in Directory.GetDirectories(envsDir))
            {
                var exoticPkg = Path.Combine(envDir, "Lib", "site-packages", "exotic");
                if (!Directory.Exists(exoticPkg)) continue;
                var exe = Path.Combine(envDir, "Scripts", "exotic.exe");
                if (File.Exists(exe)) yield return exe;
            }
        }
    }

    private static IEnumerable<string> CondaCandidatesUnix()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new[]
        {
            Path.Combine(home, "miniconda3"),
            Path.Combine(home, "miniconda"),
            Path.Combine(home, "anaconda3"),
            Path.Combine(home, "anaconda"),
            "/opt/conda",
        };
        foreach (var root in roots)
        {
            var envsDir = Path.Combine(root, "envs");
            if (!Directory.Exists(envsDir)) continue;
            foreach (var envDir in Directory.GetDirectories(envsDir))
            {
                var exoticPkg = Path.Combine(envDir, "lib");
                if (!Directory.Exists(exoticPkg)) continue;
                var exe = Path.Combine(envDir, "bin", "exotic");
                if (File.Exists(exe)) yield return exe;
            }
        }
    }

    private static string? Which(string name)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            var full = Path.Combine(dir.Trim(), name);
            if (File.Exists(full)) return full;
        }
        return null;
    }
}
