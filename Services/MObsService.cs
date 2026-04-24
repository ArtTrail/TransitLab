using TransitLab.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TransitLab.Services;

public static class MObsService
{
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
    })
    {
        Timeout    = TimeSpan.FromSeconds(30),
        DefaultRequestHeaders = { { "User-Agent", "Mozilla/5.0" } },
    };

    private const string BaseUrl  = "https://waps.cfa.harvard.edu/microobservatory/MOImageDirectory/ImageDirectory.php";
    private const string SiteRoot = "https://waps.cfa.harvard.edu";

    private static readonly string[] IgnoreWords =
        ["spiral", "whirl", "irreg", "edge", "andro", "calib"];

    // ── Public API ────────────────────────────────────────────────────────────

    public class Observation
    {
        public string   ObjectName       { get; init; } = "";
        public DateTime Date             { get; init; }
        public string   DateDisplay      { get; init; } = "";
        public string   Weather          { get; init; } = "";
        public string   Telescope        { get; init; } = "";
        public string?  CalFallbackDate  { get; init; }   // set if cals from a prior date
        public List<FitsFile> ScienceFiles     { get; init; } = [];
        public List<FitsFile> CalibrationFiles { get; init; } = [];
    }

    public class FitsFile
    {
        public string Filename    { get; init; } = "";
        public string DownloadUrl { get; init; } = "";
    }

    public class DownloadResult
    {
        public string ScienceDir   { get; init; } = "";
        public string DarksDir     { get; init; } = "";
        public int    ScienceOk    { get; init; }
        public int    ScienceFail  { get; init; }
        public int    CalOk        { get; init; }
        public int    CalFail      { get; init; }
    }

    /// <summary>Fetch and parse the MObs observation list for a given telescope.</summary>
    public static async Task<List<Observation>> FetchListAsync(
        string telescope, int lookbackDays, CancellationToken ct = default)
    {
        var html = await Http.GetStringAsync(BaseUrl, ct);
        return ParseHtml(html, telescope, lookbackDays);
    }

    /// <summary>Download all FITS files for one observation to disk, reporting progress.</summary>
    public static async Task<DownloadResult> DownloadAsync(
        Observation obs, string downloadDir,
        IProgress<(int done, int total, string filename)>? progress = null,
        CancellationToken ct = default)
    {
        var dateToken   = obs.Date.ToString("yyyy-MM-dd");
        var folderName  = $"{obs.ObjectName}_{dateToken}".Replace(" ", "_");
        var scienceDir  = Path.Combine(downloadDir, folderName);
        var darksDir    = Path.Combine(scienceDir, "Darks");
        Directory.CreateDirectory(scienceDir);
        Directory.CreateDirectory(darksDir);

        int total      = obs.ScienceFiles.Count + obs.CalibrationFiles.Count;
        int done       = 0;
        int scienceOk  = 0, scienceFail = 0, calOk = 0, calFail = 0;

        async Task Dl(FitsFile file, string destDir, bool isCalibration)
        {
            var outPath = Path.Combine(destDir, file.Filename);
            try
            {
                using var resp = await Http.GetAsync(file.DownloadUrl,
                    HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                var ct2 = resp.Content.Headers.ContentType?.MediaType ?? "";
                if (ct2.Contains("text/html"))
                    throw new InvalidDataException("Server returned HTML instead of FITS");

                await using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write);
                await resp.Content.CopyToAsync(fs, ct);

                if (isCalibration) calOk++;    else scienceOk++;
            }
            catch
            {
                if (isCalibration) calFail++;  else scienceFail++;
            }
            done++;
            progress?.Report((done, total, file.Filename));
        }

        foreach (var f in obs.ScienceFiles)     await Dl(f, scienceDir, false);
        foreach (var f in obs.CalibrationFiles) await Dl(f, darksDir,   true);

        return new DownloadResult
        {
            ScienceDir  = scienceDir,
            DarksDir    = darksDir,
            ScienceOk   = scienceOk,
            ScienceFail = scienceFail,
            CalOk       = calOk,
            CalFail     = calFail,
        };
    }

    // ── HTML parsing ──────────────────────────────────────────────────────────

    private static readonly Regex DateRe     = new(@"\b(\d{2}-[A-Za-z]{3}-\d{4})\s+(\d{2}:\d{2}:\d{2})\b");
    private static readonly Regex FileNameRe = new(@"^(?<obj>.+?)(?<stamp>\d{12})$", RegexOptions.IgnoreCase);
    private static readonly Regex WeatherRe  = new(@"\b\d+%\s+Clear\b", RegexOptions.IgnoreCase);
    private static readonly Regex FileNameQs = new(@"fileName=([^&]+)", RegexOptions.IgnoreCase);

    private static List<Observation> ParseHtml(string html, string telescope, int lookbackDays)
    {
        // Minimal table parser — find <tr> blocks, then <td> and <a href> elements
        var rows = new List<Dictionary<string, string>>();

        var trMatches = Regex.Matches(html, @"<tr[^>]*>(.*?)</tr>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        var cutoff = DateTime.UtcNow.AddDays(-lookbackDays);

        var rawRows = new List<(string obj, DateTime dt, string weather, string dlUrl, string filename)>();

        foreach (Match trm in trMatches)
        {
            var trHtml = trm.Groups[1].Value;

            // Extract all <td> text values
            var tdTexts = Regex.Matches(trHtml, @"<td[^>]*>(.*?)</td>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline)
                .Select(m => StripTags(m.Groups[1].Value).Trim())
                .ToList();

            // Must contain this telescope name in one of the cells
            if (!tdTexts.Any(t => t.Equals(telescope, StringComparison.OrdinalIgnoreCase)))
                continue;

            var rowText = StripTags(trHtml);
            var dm = DateRe.Match(rowText);
            if (!dm.Success) continue;

            if (!DateTime.TryParseExact($"{dm.Groups[1].Value} {dm.Groups[2].Value}",
                "dd-MMM-yyyy HH:mm:ss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal,
                out var dt))
                continue;

            if (dt < cutoff) continue;

            // Find download URL — prefer a direct FITS file link (path ends with .fits/.fit)
            // over a JS9 viewer link that merely has fileName= in the query string.
            var hrefs = Regex.Matches(trHtml, @"href=""([^""]+)""", RegexOptions.IgnoreCase)
                .Select(m => m.Groups[1].Value).ToList();

            string? dlUrl    = null;
            string? filename = null;

            // 1st priority: href whose path (before any ?) ends with .fits / .fit
            foreach (var href in hrefs)
            {
                var pathPart = href.Split('?')[0];
                if (!pathPart.EndsWith(".fits", StringComparison.OrdinalIgnoreCase) &&
                    !pathPart.EndsWith(".fit",  StringComparison.OrdinalIgnoreCase))
                    continue;
                dlUrl    = href.StartsWith("http") ? href : SiteRoot + href;
                filename = Path.GetFileName(pathPart);
                break;
            }

            // 2nd priority: extract filename from fileName= query param (e.g. JS9 viewer link)
            if (dlUrl is null)
            {
                foreach (var href in hrefs)
                {
                    var fnm = FileNameQs.Match(href);
                    if (!fnm.Success) continue;
                    var fn = Uri.UnescapeDataString(fnm.Groups[1].Value.Trim());
                    dlUrl    = href.StartsWith("http") ? href : SiteRoot + href;
                    filename = Path.GetFileName(fn);
                    break;
                }
            }

            if (dlUrl is null || filename is null) continue;

            // Derive object name from filename
            var fnNoExt = Path.GetFileNameWithoutExtension(filename);
            var objm    = FileNameRe.Match(fnNoExt);
            var derived = objm.Success
                ? Normalize(objm.Groups["obj"].Value)
                : Normalize(fnNoExt);

            // Cell before telescope index = object column
            int telIdx = tdTexts.FindIndex(t =>
                t.Equals(telescope, StringComparison.OrdinalIgnoreCase));
            var cellObj = telIdx > 0 ? tdTexts[telIdx - 1] : "";
            var objName = !string.IsNullOrEmpty(cellObj) ? cellObj : derived;

            var wm      = WeatherRe.Match(rowText);
            var weather = wm.Success ? wm.Value : "Unknown";

            rawRows.Add((objName, dt, weather, dlUrl, filename));
        }

        // Classify calibration vs science
        bool IsCal(string name)
        {
            var low = name.ToLowerInvariant();
            return low.Contains("calibration") || low.Contains("dark");
        }

        bool IsIgnored(string name)
        {
            var low = name.ToLowerInvariant();
            return IgnoreWords.Any(w => low.Contains(w));
        }

        // Group by (object, date)
        var calByDate  = new Dictionary<string, List<(string url, string fn)>>();
        var sciGroups  = new Dictionary<(string obj, string date),
                             (string weather, DateTime latest, List<(string url, string fn, DateTime dt)> files)>();

        foreach (var (obj, dt, weather, url, fn) in rawRows)
        {
            // Use a 12-hour offset so that files timestamped after midnight UTC (00:00–12:00)
            // are grouped with the previous evening's session, not a new calendar day.
            // This keeps overnight acquisitions that span midnight UTC as a single observation.
            var dateKey = dt.AddHours(-12).Date.ToString("yyyy-MM-dd");
            if (IsCal(obj) || IsCal(fn))
            {
                calByDate.TryAdd(dateKey, []);
                calByDate[dateKey].Add((url, fn));
                continue;
            }
            if (IsIgnored(obj)) continue;

            var key = (obj, dateKey);
            if (!sciGroups.ContainsKey(key))
                sciGroups[key] = (weather, dt, []);
            var g = sciGroups[key];
            g.files.Add((url, fn, dt));
            if (dt > g.latest) sciGroups[key] = (weather, dt, g.files);
        }

        var observations = new List<Observation>();
        foreach (var (key, g) in sciGroups)
        {
            var (obj, dateKey) = key;
            var cals = calByDate.GetValueOrDefault(dateKey);

            string? calFallback = null;
            if (cals is null || cals.Count == 0)
            {
                // Find most-recent prior date with calibration files
                var prior = calByDate.Keys
                    .Where(d => string.Compare(d, dateKey, StringComparison.Ordinal) < 0)
                    .OrderDescending()
                    .FirstOrDefault();
                if (prior is not null) { cals = calByDate[prior]; calFallback = prior; }
            }

            observations.Add(new Observation
            {
                ObjectName      = obj,
                Date            = DateTime.Parse(dateKey),
                DateDisplay     = DateTime.Parse(dateKey).ToString("dd MMM yyyy"),
                Weather         = g.weather,
                Telescope       = telescope,
                CalFallbackDate = calFallback,
                ScienceFiles    = g.files
                    .OrderBy(f => f.dt)
                    .Select(f => new FitsFile { Filename = f.fn, DownloadUrl = f.url })
                    .ToList(),
                CalibrationFiles = (cals ?? [])
                    .Select(c => new FitsFile { Filename = c.fn, DownloadUrl = c.url })
                    .ToList(),
            });
        }

        observations.Sort((a, b) => b.Date.CompareTo(a.Date));
        return observations;
    }

    private static string StripTags(string html)
        => Regex.Replace(html, "<[^>]+>", " ");

    private static string Normalize(string s)
        => Regex.Replace(s.Replace('\u00a0', ' '), @"\s+", " ").Trim();
}
