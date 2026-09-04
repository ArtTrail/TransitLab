using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace TransitLab.Services;

/// <summary>
/// Checks for, downloads, and launches the StarFix installer (ArtTrail/StarFix — a separate,
/// standalone plate-solving app with its own Gaia DR3 catalog). TransitLab never bundles or
/// manages StarFix's catalog itself — that stays entirely inside StarFix's own UI.
/// </summary>
public static class StarFixService
{
    private const string ApiUrl = "https://api.github.com/repos/ArtTrail/StarFix/releases/latest";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
        DefaultRequestHeaders = { { "User-Agent", "TransitLab-StarFixInstaller" } },
    };

    public record ReleaseInfo(string Version, string AssetName, string DownloadUrl);

    /// <summary>Finds the Inno Setup installer asset (a .exe, not the portable .zip) in the latest StarFix release.</summary>
    public static async Task<ReleaseInfo?> CheckLatestAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await Http.GetStringAsync(ApiUrl, ct);
            var root = JsonNode.Parse(json);

            var tag = root?["tag_name"]?.GetValue<string>();
            if (tag is null) return null;
            var version = tag.TrimStart('v');

            var assets = root?["assets"]?.AsArray();
            if (assets is null) return null;

            foreach (var asset in assets)
            {
                var name = asset?["name"]?.GetValue<string>() ?? "";
                var url  = asset?["browser_download_url"]?.GetValue<string>() ?? "";
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    return new ReleaseInfo(version, name, url);
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Finds an existing StarFix install via its Windows uninstall registry entry (Inno Setup
    /// writes one automatically) — not a fixed path, since the installer lets the user pick any
    /// install location. Returns the install root folder (contains StarFix.exe), or null if not found.
    /// </summary>
    public static string? DetectInstalledPath()
    {
        if (!OperatingSystem.IsWindows()) return null;

        string[] roots =
        [
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        ];

        foreach (var root in roots)
        {
            using var baseKey = OpenBaseKey(root);
            if (baseKey is null) continue;

            foreach (var subKeyName in baseKey.GetSubKeyNames())
            {
                using var subKey = baseKey.OpenSubKey(subKeyName);
                var displayName = subKey?.GetValue("DisplayName") as string;
                if (displayName is null || !displayName.StartsWith("StarFix", StringComparison.OrdinalIgnoreCase))
                    continue;

                var installLocation = subKey?.GetValue("InstallLocation") as string;
                if (!string.IsNullOrWhiteSpace(installLocation) && Directory.Exists(installLocation))
                    return installLocation.TrimEnd('\\', '/');
            }
        }
        return null;
    }

    [SupportedOSPlatform("windows")]
    private static Microsoft.Win32.RegistryKey? OpenBaseKey(string root)
    {
        var parts = root.Split('\\', 2);
        var hive = parts[0] switch
        {
            "HKEY_LOCAL_MACHINE" => Microsoft.Win32.Registry.LocalMachine,
            "HKEY_CURRENT_USER"  => Microsoft.Win32.Registry.CurrentUser,
            _ => null,
        };
        return hive?.OpenSubKey(parts[1]);
    }

    /// <summary>Path to StarFix's headless solve CLI, given its install root — verified layout, not guessed.</summary>
    public static string SolveExePath(string installRoot) =>
        Path.Combine(installRoot, "PySolver", "solve", "solve.exe");

    public static bool IsValidInstall(string installRoot) =>
        !string.IsNullOrWhiteSpace(installRoot) &&
        File.Exists(Path.Combine(installRoot, "StarFix.exe")) &&
        File.Exists(SolveExePath(installRoot));

    /// <summary>Downloads the installer with progress, then launches it — the user completes the wizard themselves.</summary>
    public static async Task DownloadAndLaunchInstallerAsync(
        string url, string destPath,
        IProgress<(long done, long total)>? progress,
        CancellationToken ct)
    {
        await ExoticInstallService.DownloadFileAsync(url, destPath, progress, ct);
        Process.Start(new ProcessStartInfo
        {
            FileName        = destPath,
            UseShellExecute = true,   // lets Windows handle any UAC elevation the installer needs
        });
    }
}
