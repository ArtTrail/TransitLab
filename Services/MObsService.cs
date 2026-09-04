using TransitLab.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace TransitLab.Services;

/// <summary>
/// Reads MicroObservatory (MObs) observation data from a Cloudflare R2 mirror instead of
/// MicroObservatory's own servers directly. A separate scheduled job (MObsSync, run via
/// GitHub Actions) fetches MicroObservatory's listing once daily and uploads recent FITS
/// files plus a manifest.json to R2 — every TransitLab install reads from that shared,
/// publicly-cached copy instead of each install hitting MicroObservatory's site itself.
/// This was changed after MicroObservatory (Harvard-Smithsonian) reported that TransitLab's
/// growing user base was putting real load on their infrastructure.
///
/// Trade-off: data refreshes once daily (whenever the sync job last ran), not live — a
/// frame from tonight's session won't appear here until the next day's sync.
/// </summary>
public static class MObsService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
        DefaultRequestHeaders = { { "User-Agent", "Mozilla/5.0" } },
    };

    // Public read-only endpoint for the R2 bucket the daily sync job populates.
    // Not a secret — this bucket only ever grants public GET access; the sync job's
    // write credentials live solely in that job's own GitHub Actions Secrets, never here.
    private const string R2PublicBaseUrl = "https://pub-2e8ad4f6b18748098ec4ecfc9f95ca0c.r2.dev";
    private const string ManifestUrl     = R2PublicBaseUrl + "/mobs/manifest.json";

    // ── Public API (unchanged shape — MObsViewModel/DownloadAsync depend on exactly this) ──

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

    /// <summary>Fetch the R2-mirrored manifest and return this telescope's observations within lookbackDays.</summary>
    public static async Task<List<Observation>> FetchListAsync(
        string telescope, int lookbackDays, CancellationToken ct = default)
    {
        var json = await Http.GetStringAsync(ManifestUrl, ct);
        var manifest = JsonSerializer.Deserialize<ManifestDto>(json, JsonOpts)
            ?? throw new InvalidDataException("Empty or unparsable manifest.json");

        if (!manifest.Telescopes.TryGetValue(telescope, out var entries))
            return [];

        // Compare against midnight N days ago, not "now minus N days" — observation dates
        // are date-only (always midnight), but DateTime.UtcNow carries today's time-of-day.
        // Subtracting days from UtcNow directly meant the cutoff could land later than
        // yesterday's midnight (e.g. 17:38 UTC today - 1 day = 17:38 UTC yesterday), silently
        // excluding a session from "yesterday" unless Lookback Days was bumped up by one to
        // compensate — worse the later in the UTC day the fetch happened.
        var cutoff = DateTime.UtcNow.Date.AddDays(-lookbackDays);

        var observations = entries
            .Where(e => DateTime.TryParse(e.Date, out var d) && d >= cutoff)
            .Select(e => new Observation
            {
                ObjectName      = e.ObjectName,
                Date            = DateTime.Parse(e.Date),
                DateDisplay     = e.DateDisplay,
                Weather         = e.Weather,
                Telescope       = telescope,
                CalFallbackDate = e.CalFallbackDate,
                ScienceFiles    = e.ScienceFiles.Select(ToFitsFile).ToList(),
                CalibrationFiles = e.CalibrationFiles.Select(ToFitsFile).ToList(),
            })
            .ToList();

        observations.Sort((a, b) => b.Date.CompareTo(a.Date));
        return observations;
    }

    private static FitsFile ToFitsFile(ManifestFileDto f) => new()
    {
        Filename    = f.Filename,
        DownloadUrl = $"{R2PublicBaseUrl}/{f.Key}",
    };

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

        var sw = new Stopwatch();

        async Task Dl(FitsFile file, string destDir, bool isCalibration)
        {
            var outPath = Path.Combine(destDir, file.Filename);
            sw.Restart();
            try
            {
                using var resp = await Http.GetAsync(file.DownloadUrl,
                    HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                var ct2 = resp.Content.Headers.ContentType?.MediaType ?? "";
                if (ct2.Contains("text/html"))
                    throw new InvalidDataException("Server returned HTML instead of FITS");

                long bytesWritten;
                await using (var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write))
                {
                    await resp.Content.CopyToAsync(fs, ct);
                    bytesWritten = fs.Length;
                }
                sw.Stop();

                if (isCalibration) calOk++;    else scienceOk++;

                var cfRay = resp.Headers.TryGetValues("CF-RAY", out var rayValues)
                    ? rayValues.FirstOrDefault() : null;
                var secs  = Math.Max(sw.Elapsed.TotalSeconds, 0.001);
                var kbps  = bytesWritten / 1024.0 / secs;
                SessionLogService.Write(
                    $"[MObs] ✓ {file.Filename} — {bytesWritten:N0} bytes in {secs:F2}s ({kbps:F0} KB/s)" +
                    (cfRay is not null ? $"  [CF-RAY: {cfRay}]" : ""));
            }
            catch (Exception ex)
            {
                sw.Stop();
                if (isCalibration) calFail++;  else scienceFail++;
                SessionLogService.Write(
                    $"[MObs] ✗ {file.Filename} — FAILED after {sw.Elapsed.TotalSeconds:F2}s: {ex.GetType().Name}: {ex.Message}");
            }
            done++;
            progress?.Report((done, total, file.Filename));
        }

        var batchSw = Stopwatch.StartNew();
        foreach (var f in obs.ScienceFiles)     await Dl(f, scienceDir, false);
        foreach (var f in obs.CalibrationFiles) await Dl(f, darksDir,   true);
        batchSw.Stop();

        SessionLogService.Write(
            $"[MObs] Download batch finished in {batchSw.Elapsed.TotalSeconds:F1}s — " +
            $"{scienceOk + calOk} succeeded, {scienceFail + calFail} failed, {total} total.");

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

    // ── manifest.json DTOs (written by MObsSync; keep in sync with it) ────────

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed class ManifestDto
    {
        [JsonPropertyName("generated_utc")]
        public string GeneratedUtc { get; set; } = "";
        [JsonPropertyName("telescopes")]
        public Dictionary<string, List<ManifestObservationDto>> Telescopes { get; set; } = [];
    }

    private sealed class ManifestObservationDto
    {
        [JsonPropertyName("object")]
        public string ObjectName { get; set; } = "";
        [JsonPropertyName("date")]
        public string Date { get; set; } = "";
        [JsonPropertyName("date_display")]
        public string DateDisplay { get; set; } = "";
        [JsonPropertyName("weather")]
        public string Weather { get; set; } = "";
        [JsonPropertyName("cal_fallback_date")]
        public string? CalFallbackDate { get; set; }
        [JsonPropertyName("science_files")]
        public List<ManifestFileDto> ScienceFiles { get; set; } = [];
        [JsonPropertyName("cal_files")]
        public List<ManifestFileDto> CalibrationFiles { get; set; } = [];
    }

    private sealed class ManifestFileDto
    {
        [JsonPropertyName("filename")]
        public string Filename { get; set; } = "";
        [JsonPropertyName("key")]
        public string Key { get; set; } = "";
    }
}
