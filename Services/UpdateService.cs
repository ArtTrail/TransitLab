using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TransitLab.Services;

public record UpdateInfo(string Version, string AssetName, string DownloadUrl);
public record ReleaseInfo(string Version, string Released, string AssetName, string DownloadUrl, bool HasAsset);

public static class UpdateService
{
    private const string AllApiUrl  = "https://api.github.com/repos/ArtTrail/TransitLab/releases";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
        DefaultRequestHeaders = { { "User-Agent", "TransitLab-UpdateChecker" } },
    };

    public static async Task<UpdateInfo?> CheckAsync(string currentVersion, CancellationToken ct = default)
    {
        try
        {
            var root = await FindLatestAppReleaseAsync(ct);
            var tag = root?["tag_name"]?.GetValue<string>();
            if (tag is null) return null;

            var latestVersion = tag.TrimStart('v');
            if (!IsNewer(latestVersion, currentVersion)) return null;

            var platform = GetPlatformId();
            var assets   = root?["assets"]?.AsArray();
            if (assets is null) return null;

            foreach (var asset in assets)
            {
                var name = asset?["name"]?.GetValue<string>() ?? "";
                var url  = asset?["browser_download_url"]?.GetValue<string>() ?? "";
                if (name.Contains(platform, StringComparison.OrdinalIgnoreCase) &&
                    (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".dmg", StringComparison.OrdinalIgnoreCase)))
                    return new UpdateInfo(latestVersion, name, url);
            }
        }
        catch { }
        return null;
    }

    public static async Task<List<ReleaseInfo>> GetAllReleasesAsync(CancellationToken ct = default)
    {
        var result   = new List<ReleaseInfo>();
        var platform = GetPlatformId();
        try
        {
            var json  = await Http.GetStringAsync(AllApiUrl, ct);
            var array = JsonNode.Parse(json)?.AsArray();
            if (array is null) return result;

            foreach (var release in array)
            {
                var tag = release?["tag_name"]?.GetValue<string>();
                if (tag is null) continue;

                // This repo also hosts non-app releases as a side effect of sharing GitHub
                // infrastructure — e.g. "gaia-catalog-v1" (StarFix's Gaia DR3 catalog data,
                // hosted here rather than under StarFix's own repo). Only a real "vX.Y.Z"
                // TransitLab version tag belongs in this list.
                if (!AppVersionTagPattern.IsMatch(tag)) continue;

                var version = tag.TrimStart('v');

                var rawDate  = release?["published_at"]?.GetValue<string>() ?? "";
                var released = DateTime.TryParse(rawDate, out var dt)
                    ? dt.ToString("yyyy-MM-dd")
                    : rawDate;

                var assets   = release?["assets"]?.AsArray();
                string assetName = "", downloadUrl = "";
                bool hasAsset = false;

                if (assets is not null)
                {
                    foreach (var asset in assets)
                    {
                        var name = asset?["name"]?.GetValue<string>() ?? "";
                        var url  = asset?["browser_download_url"]?.GetValue<string>() ?? "";
                        if (name.Contains(platform, StringComparison.OrdinalIgnoreCase) &&
                            (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".dmg", StringComparison.OrdinalIgnoreCase)))
                        {
                            assetName = name;
                            downloadUrl = url;
                            hasAsset = true;
                            break;
                        }
                    }
                }

                result.Add(new ReleaseInfo(version, released, assetName, downloadUrl, hasAsset));
            }
        }
        catch { }
        return result;
    }

    // Fixed AppId GUID from installer\TransitLab.iss — identifies the Inno-managed install's
    // uninstall registry entry regardless of what folder the user chose during setup.
    private const string InstallerAppId = "{A02ECB23-31B0-44D4-9DAF-5F4DED3CE8E0}_is1";

    /// <summary>
    /// Looks for a Setup .exe asset (built by installer\TransitLab.iss, named
    /// TransitLab-Setup-vX.Y.Z.exe) on the latest release — the self-update path, distinct from
    /// CheckAsync's plain zip/dmg. Windows only, since Inno installers don't exist on other platforms.
    /// </summary>
    public static async Task<UpdateInfo?> CheckInstallerAsync(string currentVersion, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var root = await FindLatestAppReleaseAsync(ct);
            var tag = root?["tag_name"]?.GetValue<string>();
            if (tag is null) return null;

            var latestVersion = tag.TrimStart('v');
            if (!IsNewer(latestVersion, currentVersion)) return null;

            var assets = root?["assets"]?.AsArray();
            if (assets is null) return null;

            foreach (var asset in assets)
            {
                var name = asset?["name"]?.GetValue<string>() ?? "";
                var url  = asset?["browser_download_url"]?.GetValue<string>() ?? "";
                if (name.StartsWith("TransitLab-Setup-", StringComparison.OrdinalIgnoreCase) &&
                    name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    return new UpdateInfo(latestVersion, name, url);
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// True when this running instance is an Inno-managed install (installer\TransitLab.iss) —
    /// found via its fixed-AppId uninstall registry entry, with the entry's InstallLocation
    /// matching where this process is actually running from. A portable/manually-placed copy of
    /// TransitLab (the existing win-x64 zip) always returns false, since silently reinstalling
    /// over it would create a second, separate install rather than upgrading the running one.
    /// </summary>
    public static bool IsSelfUpdateCapable()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var installLocation = GetInstallerInstallLocation();
            if (string.IsNullOrWhiteSpace(installLocation)) return false;

            var runningDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
            return string.Equals(runningDir, installLocation.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    [SupportedOSPlatform("windows")]
    private static string? GetInstallerInstallLocation()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{InstallerAppId}");
        return key?.GetValue("InstallLocation") as string;
    }

    /// <summary>
    /// Launches the downloaded Setup .exe silently. Inno's Restart Manager-based
    /// CloseApplications detects the file lock this running instance holds on TransitLab.exe
    /// (one of the files being overwritten) and closes it — /CLOSEAPPLICATIONS answers that
    /// silently; verified locally to work reliably. The installer's own [Run] section (not
    /// RestartApplications — confirmed unreliable by testing) then reopens TransitLab once the
    /// install finishes.
    /// </summary>
    public static void LaunchSilentInstall(string installerPath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName        = installerPath,
            Arguments       = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS",
            UseShellExecute = true,
        });
    }

    // GitHub Releases regex — matches a real TransitLab version tag ("v2.10.0" or "2.10.0")
    // but not other releases this same repo hosts for unrelated purposes, e.g. "gaia-catalog-v1"
    // (StarFix's Gaia DR3 catalog data). /releases/latest resolves to whichever release was
    // published most recently regardless of what it is, so a non-app release published after
    // the real latest version would otherwise be picked up as "the latest TransitLab version."
    private static readonly Regex AppVersionTagPattern = new(@"^v?\d+\.\d+\.\d+$", RegexOptions.Compiled);

    /// <summary>Fetches the releases list and returns the first (most recent) one that is an actual TransitLab version release.</summary>
    private static async Task<JsonNode?> FindLatestAppReleaseAsync(CancellationToken ct)
    {
        var json  = await Http.GetStringAsync(AllApiUrl, ct);
        var array = JsonNode.Parse(json)?.AsArray();
        if (array is null) return null;

        foreach (var release in array)
        {
            var tag = release?["tag_name"]?.GetValue<string>();
            if (tag is not null && AppVersionTagPattern.IsMatch(tag))
                return release;
        }
        return null;
    }

    public static string GetPlatformId()
    {
        if (OperatingSystem.IsWindows()) return "win-x64";
        if (OperatingSystem.IsMacOS())   return "osx-arm64";
        return "linux-x64";
    }

    private static bool IsNewer(string latest, string current)
    {
        if (!Version.TryParse(latest,  out var l)) return false;
        if (!Version.TryParse(current, out var c)) return false;
        return l > c;
    }
}
