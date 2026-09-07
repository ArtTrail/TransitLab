using Amazon.S3;
using Amazon.S3.Model;
using MObsSync;
using System.Text.Json;
using System.Text.Json.Serialization;

// ── Config ───────────────────────────────────────────────────────────────────

var accountId = RequireEnv("R2_ACCOUNT_ID");
var accessKey = RequireEnv("R2_ACCESS_KEY_ID");
var secretKey = RequireEnv("R2_SECRET_ACCESS_KEY");
const string BucketName    = "transitlab-mobs-mirror";
const int    LookbackDays  = 14;
var telescopes = new[] { "Cecilia" };

static string RequireEnv(string name) =>
    Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Required environment variable '{name}' is not set.");

using var s3 = new AmazonS3Client(accessKey, secretKey, new AmazonS3Config
{
    ServiceURL     = $"https://{accountId}.r2.cloudflarestorage.com",
    ForcePathStyle = true,
});

var manifest = new ManifestDto { GeneratedUtc = DateTime.UtcNow.ToString("O") };

foreach (var telescope in telescopes)
{
    Console.WriteLine($"[{telescope}] Fetching observation list from MicroObservatory…");
    List<MObsService.Observation> observations;
    try
    {
        observations = await MObsService.FetchListAsync(telescope, LookbackDays);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[{telescope}] ERROR fetching list: {ex.Message} — skipping this telescope for this run.");
        continue;
    }
    Console.WriteLine($"[{telescope}] {observations.Count} observation(s) within {LookbackDays} days.");

    var entries = new List<ManifestObservationDto>();

    foreach (var obs in observations)
    {
        var dateToken = obs.Date.ToString("yyyy-MM-dd");
        var objectKey = SafeKeySegment(obs.ObjectName);

        var sciFiles = new List<ManifestFileDto>();
        foreach (var f in obs.ScienceFiles)
        {
            var entry = await MirrorFileAsync(s3, BucketName, telescope, dateToken, objectKey, "science", f);
            if (entry is not null) sciFiles.Add(entry);   // only list files actually present in R2
        }

        var calFiles = new List<ManifestFileDto>();
        foreach (var f in obs.CalibrationFiles)
        {
            var entry = await MirrorFileAsync(s3, BucketName, telescope, dateToken, objectKey, "cal", f);
            if (entry is not null) calFiles.Add(entry);   // only list files actually present in R2
        }

        // Skip an observation entirely if none of its science frames made it to R2 — a manifest
        // entry with zero science files would show a phantom target in TransitLab's Fetch List
        // that fails on every download. Missing files get retried by the next run (ObjectExists
        // returns false for them), so a transient upstream failure self-heals on the next sync.
        if (sciFiles.Count == 0)
        {
            Console.WriteLine($"  ⚠ {obs.ObjectName} ({dateToken}): 0 of {obs.ScienceFiles.Count} science files reached R2 — omitting from manifest this run.");
            continue;
        }

        entries.Add(new ManifestObservationDto
        {
            ObjectName       = obs.ObjectName,
            Date             = dateToken,
            DateDisplay      = obs.DateDisplay,
            Weather          = obs.Weather,
            CalFallbackDate  = obs.CalFallbackDate,
            ScienceFiles     = sciFiles,
            CalibrationFiles = calFiles,
        });
    }

    manifest.Telescopes[telescope] = entries;
}

// ── Upload the aggregate manifest, overwriting the previous run's ──────────────

var manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = false });
await s3.PutObjectAsync(new PutObjectRequest
{
    BucketName           = BucketName,
    Key                  = "mobs/manifest.json",
    ContentBody          = manifestJson,
    ContentType          = "application/json",
    DisablePayloadSigning = true, // R2 doesn't implement the AWS SDK's default chunked/trailer-signed streaming upload
});
Console.WriteLine($"Uploaded manifest.json ({manifestJson.Length} bytes) covering {manifest.Telescopes.Sum(t => t.Value.Count)} observation(s) across {manifest.Telescopes.Count} telescope(s).");

// ── Helpers ─────────────────────────────────────────────────────────────────

static string SafeKeySegment(string s) =>
    string.Join("_", s.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();

/// <summary>Uploads one FITS file to R2 if not already present there. Returns its manifest entry
/// on success (or if it was already mirrored), or <c>null</c> if the file could not be placed in
/// R2 — so the caller never lists a key that isn't actually there for download.</summary>
static async Task<ManifestFileDto?> MirrorFileAsync(
    AmazonS3Client s3, string bucketName, string telescope, string dateToken, string objectKey, string kind, MObsService.FitsFile file)
{
    var key = $"mobs/{telescope}/{dateToken}/{objectKey}/{kind}/{file.Filename}";

    if (await ObjectExistsAsync(s3, bucketName, key))
        return new ManifestFileDto { Filename = file.Filename, Key = key };

    try
    {
        var bytes = await MObsService.DownloadBytesAsync(file.DownloadUrl);
        using var stream = new MemoryStream(bytes);
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName            = bucketName,
            Key                   = key,
            InputStream           = stream,
            ContentType           = "application/octet-stream",
            DisablePayloadSigning = true, // R2 doesn't implement the AWS SDK's default chunked/trailer-signed streaming upload
        });
        Console.WriteLine($"  + {key} ({bytes.Length:N0} bytes)");
        return new ManifestFileDto { Filename = file.Filename, Key = key };
    }
    catch (Exception ex)
    {
        // Don't list it — a common cause is an upstream fetch failure (e.g. Harvard's
        // mo-www.cfa.harvard.edu FITS host, whose TLS cert has lapsed before). The file
        // stays absent from R2, so the next run retries it (ObjectExists is false).
        Console.WriteLine($"  ! FAILED {file.Filename}: {ex.Message} — omitting from manifest; will retry next run.");
        return null;
    }
}

static async Task<bool> ObjectExistsAsync(AmazonS3Client s3, string bucketName, string key)
{
    try
    {
        await s3.GetObjectMetadataAsync(bucketName, key);
        return true;
    }
    catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
    {
        return false;
    }
}

// ── manifest.json DTOs (kept in sync with TransitLab's own Services\MObsService.cs reader) ──

internal sealed class ManifestDto
{
    [JsonPropertyName("generated_utc")]
    public string GeneratedUtc { get; set; } = "";
    [JsonPropertyName("telescopes")]
    public Dictionary<string, List<ManifestObservationDto>> Telescopes { get; set; } = [];
}

internal sealed class ManifestObservationDto
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

internal sealed class ManifestFileDto
{
    [JsonPropertyName("filename")]
    public string Filename { get; set; } = "";
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";
}
