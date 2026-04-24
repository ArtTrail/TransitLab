using HtmlAgilityPack;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TransitLab.Services;

public record AavsoSiteEquip(string Id, string Name);

/// <summary>
/// Handles Auth0 login and multipart upload to apps.aavso.org/exosite.
/// Create one instance per login session; dispose when done.
/// </summary>
public sealed class AavsoExositeService : IDisposable
{
    private readonly HttpClient _http;

    public AavsoExositeService()
    {
        var handler = new HttpClientHandler
        {
            CookieContainer          = new CookieContainer(),
            UseCookies               = true,
            AllowAutoRedirect        = true,
            MaxAutomaticRedirections = 20,
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
    }

    // ── HTTP helpers ──────────────────────────────────────────────────────────

    private async Task<(string Html, string Url)> GetAsync(string url, CancellationToken ct)
    {
        var r = await _http.GetAsync(url, ct);
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadAsStringAsync(ct),
                r.RequestMessage?.RequestUri?.ToString() ?? url);
    }

    private async Task<(string Html, string Url)> PostFormAsync(
        string url, IEnumerable<KeyValuePair<string, string>> data,
        string referer, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Referrer     = new Uri(referer);
        req.Content              = new FormUrlEncodedContent(data);
        var r = await _http.SendAsync(req, ct);
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadAsStringAsync(ct),
                r.RequestMessage?.RequestUri?.ToString() ?? url);
    }

    // ── HTML helpers ──────────────────────────────────────────────────────────

    private static Dictionary<string, string> ParseFormInputs(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in doc.DocumentNode.SelectNodes("//input[@name]") ?? Enumerable.Empty<HtmlNode>())
        {
            var name = node.GetAttributeValue("name", "");
            if (!string.IsNullOrEmpty(name))
                result[name] = node.GetAttributeValue("value", "");
        }
        return result;
    }

    private static string ResolveAction(string action, string baseUrl)
    {
        if (string.IsNullOrEmpty(action)) return baseUrl;
        if (action.StartsWith('/'))
        {
            var uri = new Uri(baseUrl);
            return $"{uri.Scheme}://{uri.Host}{action}";
        }
        return action.StartsWith("http") ? action : baseUrl;
    }

    private static string? ParseFormAction(string html, string baseUrl)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var form = doc.DocumentNode.SelectSingleNode("//form");
        if (form is null) return null;
        return ResolveAction(form.GetAttributeValue("action", "").Trim(), baseUrl);
    }

    private static bool HasRelayForm(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        return doc.DocumentNode.SelectSingleNode("//form[@id='oauth2Form']") is not null ||
               doc.DocumentNode.SelectSingleNode(
                   "//form[translate(@method,'POST','post')='post']") is not null;
    }

    private static List<AavsoSiteEquip> ParseSelect(string html, string selectName)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var sel = doc.DocumentNode.SelectSingleNode($"//select[@name='{selectName}']");
        if (sel is null)
        {
            SessionLogService.Write($"[AAVSO Login] WARNING — <select name=\"{selectName}\"> not found in page HTML");
            return [];
        }
        var result = new List<AavsoSiteEquip>();
        foreach (var opt in sel.SelectNodes(".//option") ?? Enumerable.Empty<HtmlNode>())
        {
            var val  = opt.GetAttributeValue("value", "").Trim();
            var text = opt.InnerText.Trim();
            if (!string.IsNullOrEmpty(val))
            {
                result.Add(new AavsoSiteEquip(val, text));
            }
            else
            {
                // Log any option that is being skipped so we can diagnose missing entries
                SessionLogService.Write($"[AAVSO Login] Skipped {selectName} option with empty value — text: \"{text}\"");
            }
        }
        return result;
    }

    // ── Login ─────────────────────────────────────────────────────────────────

    public async Task<(bool Success, string Message, List<AavsoSiteEquip> Sites, List<AavsoSiteEquip> Equips)>
        LoginAsync(string username, string password, Action<string> status, CancellationToken ct)
    {
        try
        {
            const string loginUrl  = "https://apps.aavso.org/accounts/login/?next=/exosite/submit";
            const string submitUrl = "https://apps.aavso.org/exosite/submit";

            status("⟳  Step 1/2: Connecting to AAVSO…");
            var (html, url) = await GetAsync(loginUrl, ct);

            // Follow relay form if not yet on Auth0
            if (!url.Contains("auth.aavso.org") && HasRelayForm(html))
            {
                var action = ParseFormAction(html, url) ?? url;
                var data   = ParseFormInputs(html);
                (html, url) = await PostFormAsync(action, data, url, ct);
            }

            // Auth0 login page
            if (url.Contains("auth.aavso.org"))
            {
                var action = ParseFormAction(html, url) ?? url;
                var data   = ParseFormInputs(html);
                data["username"] = username;
                data["password"] = password;
                (html, url) = await PostFormAsync(action, data, url, ct);

                if (url.Contains("auth.aavso.org"))
                {
                    var lower = html.ToLowerInvariant();
                    if (url.Contains("error") || lower.Contains("wrong") ||
                        lower.Contains("invalid") || lower.Contains("incorrect"))
                        return (false, "✗  Login failed — incorrect username or password", [], []);

                    if (HasRelayForm(html))
                    {
                        var ra = ParseFormAction(html, url) ?? url;
                        var rd = ParseFormInputs(html);
                        (html, url) = await PostFormAsync(ra, rd, url, ct);
                    }
                }
            }

            status("⟳  Step 2/2: Fetching site and equipment lists…");
            var (submitHtml, submitUrl2) = await GetAsync(submitUrl, ct);

            if (submitUrl2.Contains("auth.aavso.org"))
                return (false, "✗  Authentication failed — could not access submit page", [], []);

            var sites  = ParseSelect(submitHtml, "site");
            var equips = ParseSelect(submitHtml, "equipment");

            SessionLogService.Write($"[AAVSO Login] User: {username}");
            SessionLogService.Write($"[AAVSO Login] Sites found ({sites.Count}): {string.Join(", ", sites.Select(s => $"\"{s.Name}\" (id={s.Id})"))}");
            SessionLogService.Write($"[AAVSO Login] Equipment found ({equips.Count}): {string.Join(", ", equips.Select(e => $"\"{e.Name}\" (id={e.Id})"))}");

            var note   = (sites.Count == 0 || equips.Count == 0)
                ? "  (enter site/equipment manually)" : "";

            var result = $"✓  Logged in{note}";
            SessionLogService.Write($"[AAVSO Login] {result}");
            return (true, result, sites, equips);
        }
        catch (Exception ex)
        {
            SessionLogService.Write($"[AAVSO Login] FAILED — {ex.GetType().Name}: {ex.Message}");
            return (false, $"✗  {ex.GetType().Name}: {ex.Message}", [], []);
        }
    }

    // ── Upload ────────────────────────────────────────────────────────────────

    public async Task<(bool Success, string Message)> UploadAsync(
        string reportPath, string? lcPath,
        string siteId, string equipId,
        string obscode, CancellationToken ct)
    {
        try
        {
            const string submitUrl = "https://apps.aavso.org/exosite/submit";

            var (html, _) = await GetAsync(submitUrl, ct);
            var inputs    = ParseFormInputs(html);
            var csrf      = inputs.TryGetValue("csrfmiddlewaretoken", out var c) ? c : "";

            var reportBytes = PatchObscode(reportPath, obscode);
            var reportName  = Path.GetFileName(reportPath);

            var form = new MultipartFormDataContent();
            form.Add(new StringContent(csrf),    "csrfmiddlewaretoken");
            form.Add(new StringContent(siteId),  "site");
            form.Add(new StringContent(equipId), "equipment");
            form.Add(new StringContent("on"),    "terms");
            form.Add(new StringContent("true"),  "submit");

            var reportContent = new ByteArrayContent(reportBytes);
            reportContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            form.Add(reportContent, "report", reportName);

            if (!string.IsNullOrEmpty(lcPath) && File.Exists(lcPath))
            {
                var imgContent = new ByteArrayContent(File.ReadAllBytes(lcPath));
                imgContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                form.Add(imgContent, "image", Path.GetFileName(lcPath));
            }

            using var req = new HttpRequestMessage(HttpMethod.Post, submitUrl);
            req.Headers.Referrer = new Uri(submitUrl);
            req.Content = form;

            var resp = await _http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();

            var finalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? submitUrl;
            var body     = await resp.Content.ReadAsStringAsync(ct);
            var lower    = body.ToLowerInvariant();

            bool success = finalUrl.TrimEnd('/') != submitUrl.TrimEnd('/') ||
                           lower.Contains("upload successful") ||
                           lower.Contains("successfully submitted");

            if (!success)
            {
                var doc2 = new HtmlDocument();
                doc2.LoadHtml(body);
                var alerts = doc2.DocumentNode.SelectNodes(
                    "//*[contains(@class,'alert') or contains(@class,'error')]");
                var texts = (alerts ?? Enumerable.Empty<HtmlNode>())
                    .Select(n => n.InnerText.Trim()).Where(t => t.Length > 0).ToList();

                if (texts.Any(t => t.ToLowerInvariant().Contains("successful")))
                    success = true;
                else if (texts.Count > 0)
                    return (false, $"⚠  Upload rejected: {string.Join("; ", texts.Take(2))}");
            }

            return success
                ? (true,  "✓  Uploaded successfully.")
                : (false, "⚠  Upload rejected (stayed on submit page)");
        }
        catch (Exception ex)
        {
            return (false, $"✗  Upload failed: {ex.Message}");
        }
    }

    private static byte[] PatchObscode(string filePath, string obscode)
    {
        var sb = new StringBuilder();
        foreach (var line in File.ReadAllLines(filePath, Encoding.UTF8))
        {
            sb.AppendLine(line.TrimEnd() == "#OBSCODE=" && !string.IsNullOrEmpty(obscode)
                ? $"#OBSCODE={obscode}" : line);
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    public void Dispose() => _http.Dispose();
}
