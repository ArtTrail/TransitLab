using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace TransitLab.Services;

/// <summary>
/// Unpacks a NASA Exoplanet Watch "Data Checkout System" (DCS) download. Those arrive
/// double-compressed: an outer .zip whose entries are each individually gzipped FITS
/// (…​.FITS.gz), typically with a darks/ subfolder and a README.txt. This decompresses every
/// .FITS.gz straight to a plain .FITS (in-memory gunzip — no intermediate .gz written to disk)
/// so EXOTIC can read the frames directly.
///
/// Layout produced under <c>targetDir</c>:
///   science/  — top-level FITS from the archive (the light frames)
///   darks/    — the archive's darks/ folder (and any other calibration subfolders kept as-is)
///   README.txt and any other non-FITS top-level files at the root
/// Science and darks are kept in SEPARATE folders on purpose, so EXOTIC never scans the darks
/// as light frames (the failure mode a stray MasterDark in the lights folder causes).
/// </summary>
public static class DcsImportService
{
    public record Result(bool Ok, string Message, string ScienceDir, string? DarksDir, int ScienceCount, int DarkCount);

    private static bool IsFits(string name) =>
        name.EndsWith(".fits", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".fit",  StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".fts",  StringComparison.OrdinalIgnoreCase);

    public static Task<Result> ImportAsync(
        string zipPath, string targetDir, IProgress<string>? progress = null, CancellationToken ct = default)
        => Task.Run(() =>
    {
        try
        {
            if (!File.Exists(zipPath))
                return new Result(false, $"File not found: {zipPath}", "", null, 0, 0);

            using var zip = ZipFile.OpenRead(zipPath);

            // Real files only (skip directory marker entries).
            var files = new System.Collections.Generic.List<ZipArchiveEntry>();
            foreach (var e in zip.Entries)
                if (!string.IsNullOrEmpty(e.Name)) files.Add(e);
            if (files.Count == 0)
                return new Result(false, "Archive is empty.", "", null, 0, 0);

            var scienceDir = Path.Combine(targetDir, "science");
            int sci = 0, dark = 0, done = 0;

            foreach (var entry in files)
            {
                ct.ThrowIfCancellationRequested();

                var rel   = entry.FullName.Replace('\\', '/');
                bool isGz = rel.EndsWith(".gz", StringComparison.OrdinalIgnoreCase);
                var bare  = isGz ? rel[..^3] : rel;               // strip .gz for the on-disk name
                bool inSub = rel.Contains('/');                    // e.g. "darks/Dark-….FITS.gz"

                // Route: calibration subfolders keep their structure; top-level FITS go to science/;
                // everything else (README.txt, etc.) lands at the target root.
                string outRel;
                if (inSub)                 outRel = bare;                                   // darks/… (or flats/…)
                else if (IsFits(bare))     outRel = "science/" + Path.GetFileName(bare);
                else                       outRel = Path.GetFileName(bare);

                var outPath = Path.Combine(targetDir, outRel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

                using (var es = entry.Open())
                using (var outFs = File.Create(outPath))
                {
                    if (isGz)
                    {
                        using var gz = new GZipStream(es, CompressionMode.Decompress);
                        gz.CopyTo(outFs);
                    }
                    else es.CopyTo(outFs);
                }

                if (IsFits(bare))
                {
                    if (rel.StartsWith("darks/", StringComparison.OrdinalIgnoreCase)) dark++;
                    else if (!inSub) sci++;
                }

                progress?.Report($"Unpacking… {++done}/{files.Count}");
            }

            var darksDir = Path.Combine(targetDir, "darks");
            string? darksOut = (dark > 0 && Directory.Exists(darksDir)) ? darksDir : null;

            if (sci == 0)
                return new Result(false,
                    "No science FITS frames found — is this a NASA Exoplanet Watch (DCS) download?",
                    scienceDir, darksOut, 0, dark);

            var msg = $"Unpacked {sci} science frame{(sci == 1 ? "" : "s")}"
                    + (dark > 0 ? $" + {dark} dark{(dark == 1 ? "" : "s")}" : "") + ".";
            return new Result(true, msg, scienceDir, darksOut, sci, dark);
        }
        catch (OperationCanceledException)
        {
            return new Result(false, "Import cancelled.", "", null, 0, 0);
        }
        catch (Exception ex)
        {
            return new Result(false, $"Import failed: {ex.Message}", "", null, 0, 0);
        }
    }, ct);
}
