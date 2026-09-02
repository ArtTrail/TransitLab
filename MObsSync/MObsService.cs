using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MObsSync;

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

    private const string BaseUrl  = "https://waps.cfa.harvard.edu/microobservatory/MOImageDirectory/ImageDirectory.php?SortBy=Date&SortPos=DESC&SearchFor=&Type=&SortRange=30";
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

    /// <summary>Fetch and parse the MObs observation list for a given telescope, straight from MicroObservatory.</summary>
    public static async Task<List<Observation>> FetchListAsync(
        string telescope, int lookbackDays, CancellationToken ct = default)
    {
        var html = await Http.GetStringAsync(BaseUrl, ct);
        return ParseHtml(html, telescope, lookbackDays);
    }

    /// <summary>Exposed so Program.cs can download each FITS file's bytes directly (to re-upload to R2)
    /// rather than writing to a local folder structure the way TransitLab's own copy of this class does.</summary>
    public static Task<byte[]> DownloadBytesAsync(string url, CancellationToken ct = default) =>
        Http.GetByteArrayAsync(url, ct);

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
                    !pathPart.EndsWith(".fit",  StringComparison.OrdinalIgnoreCase) &&
                    !pathPart.EndsWith(".fts",  StringComparison.OrdinalIgnoreCase) &&
                    !pathPart.EndsWith(".fz",   StringComparison.OrdinalIgnoreCase))
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

        // Session-cluster threshold: a gap this large between consecutive frames of the
        // same object means a new observing session, not a continuation. MObs frames
        // within one session are typically only a few minutes apart; different nights
        // for the same target are always >>3h apart, so this safely distinguishes the
        // two without risk of merging two genuinely separate nights.
        const double SessionGapHours = 3.0;

        // Splits a time-ordered sequence into sessions, breaking wherever the gap to the
        // next timestamp exceeds SessionGapHours. Returns each cluster as (start index,
        // count) into the given (already time-sorted) list.
        static List<(int start, int count)> ClusterSessions(List<DateTime> sortedTimes)
        {
            var clusters = new List<(int start, int count)>();
            if (sortedTimes.Count == 0) return clusters;
            int start = 0;
            for (int i = 1; i <= sortedTimes.Count; i++)
            {
                if (i == sortedTimes.Count || (sortedTimes[i] - sortedTimes[i - 1]).TotalHours > SessionGapHours)
                {
                    clusters.Add((start, i - start));
                    start = i;
                }
            }
            return clusters;
        }

        // A whole session's date key is derived once, from its earliest frame, rather
        // than per file. Previously each file independently computed dt.AddHours(-12).Date
        // (a 12-hour offset so files after midnight UTC group with the previous evening),
        // which meant a single continuous session whose frames happened to straddle the
        // point where that shift crosses a calendar day boundary (12:00 UTC) was silently
        // split into two separate observations for no real reason — confirmed with a real
        // TOI2570 session (10:24–12:50 UTC) that got split into "2026-08-31" and
        // "2026-09-01" halves, even though every frame was genuinely part of one session.
        static string SessionDateKey(DateTime sessionStart) =>
            sessionStart.AddHours(-12).Date.ToString("yyyy-MM-dd");

        // Group by (object, date) — the date is now a whole-session key, not per-file.
        var calByDate  = new Dictionary<string, List<(string url, string fn)>>();
        var sciGroups  = new Dictionary<(string obj, string date),
                             (string weather, DateTime latest, List<(string url, string fn, DateTime dt)> files)>();

        // Calibration frames aren't per-target, so they're clustered as one pool by time.
        var calRows = rawRows.Where(r => IsCal(r.obj) || IsCal(r.filename)).OrderBy(r => r.dt).ToList();
        var calTimes = calRows.Select(r => r.dt).ToList();
        foreach (var (start, count) in ClusterSessions(calTimes))
        {
            var dateKey = SessionDateKey(calTimes[start]);
            calByDate.TryAdd(dateKey, []);
            for (int i = start; i < start + count; i++)
                calByDate[dateKey].Add((calRows[i].dlUrl, calRows[i].filename));
        }

        // Science frames are clustered per object, so a gap in one target's own timeline
        // starts a new session without being affected by other targets observed nearby.
        var sciRowsByObj = rawRows
            .Where(r => !IsCal(r.obj) && !IsCal(r.filename) && !IsIgnored(r.obj))
            .GroupBy(r => r.obj);

        foreach (var objGroup in sciRowsByObj)
        {
            var objRows = objGroup.OrderBy(r => r.dt).ToList();
            var times   = objRows.Select(r => r.dt).ToList();
            foreach (var (start, count) in ClusterSessions(times))
            {
                var dateKey = SessionDateKey(times[start]);
                var latest  = times[start];
                var files   = new List<(string url, string fn, DateTime dt)>();
                for (int i = start; i < start + count; i++)
                {
                    files.Add((objRows[i].dlUrl, objRows[i].filename, objRows[i].dt));
                    if (objRows[i].dt > latest) latest = objRows[i].dt;
                }
                sciGroups[(objGroup.Key, dateKey)] = (objRows[start].weather, latest, files);
            }
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
