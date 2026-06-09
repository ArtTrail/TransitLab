using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace TransitLab.Services;

public record UpdateInfo(string Version, string AssetName, string DownloadUrl);
public record ReleaseInfo(string Version, string Released, string AssetName, string DownloadUrl, bool HasAsset);

public static class UpdateService
{
    private const string ApiUrl     = "https://api.github.com/repos/ArtTrail/TransitLab/releases/latest";
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
            var json = await Http.GetStringAsync(ApiUrl, ct);
            var root = JsonNode.Parse(json);

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
