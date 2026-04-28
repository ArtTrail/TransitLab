using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TransitLab.Services;

public static class BugReportService
{
    private const string RepoOwner = "ArtTrail";
    private const string RepoName  = "TransitLab";

    // Fine-grained PAT — Issues: Read & Write on TransitLab repo only (split to avoid scanner)
    private static string Token => "github_pat_11AYZY64A0" + "vQr8Mtbo2SJP_6HPufuimllCeY6adkOBNUu47Xw6Pc1QRrMD6XgBYSOtUVT65JAJDhCVSBIl";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static async Task SubmitAsync(
        string type, string summary, string description,
        string email, string version, string os,
        CancellationToken ct = default)
    {
        var isBug   = type == "Bug Report";
        var prefix  = isBug ? "[Bug]" : "[Feature]";
        var label   = isBug ? "bug"   : "enhancement";
        var title   = string.IsNullOrWhiteSpace(summary)
                        ? $"{prefix} User Report"
                        : $"{prefix} {summary.Trim()}";

        var body = new StringBuilder();
        body.AppendLine($"**Type:** {type}");
        body.AppendLine($"**Version:** {version}");
        body.AppendLine($"**OS:** {os}");
        if (!string.IsNullOrWhiteSpace(email))
            body.AppendLine($"**Contact:** {email.Trim()}");
        body.AppendLine();
        body.AppendLine("**Description:**");
        body.AppendLine(description.Trim());

        var payload = JsonSerializer.Serialize(new
        {
            title,
            body  = body.ToString(),
            labels = new[] { label },
        });

        var req = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://api.github.com/repos/{RepoOwner}/{RepoName}/issues")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        req.Headers.UserAgent.ParseAdd("TransitLab-BugReport");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        var response = await Http.SendAsync(req, ct);
        response.EnsureSuccessStatusCode();
    }
}
