using System;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace TransitLab.Services;

public record UpdateInfo(string Version, string AssetName, string DownloadUrl);

public static class UpdateService
{
    private const string ApiUrl = "https://api.github.com/repos/ArtTrail/TransitLab/releases/latest";

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
                if (name.Contains(platform, StringComparison.OrdinalIgnoreCase) && name.EndsWith(".zip"))
                    return new UpdateInfo(latestVersion, name, url);
            }
        }
        catch { }
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
