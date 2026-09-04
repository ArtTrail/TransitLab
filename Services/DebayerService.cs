using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TransitLab.Services;

/// <summary>
/// Detects raw one-shot-color (OSC) FITS frames (e.g. Unistellar eVscope data — confirmed
/// via a real BAYERPAT='GBRG' header) and debayers them to a sibling folder before Image
/// Analysis, Plate Solve, or Stone comp-star scoring ever touch the mosaic pixels. Uses the
/// same colour_demosaicing library EXOTIC itself depends on (confirmed in exotic.py's own
/// demosaic_img()), so results are consistent with EXOTIC's own bilinear algorithm.
/// </summary>
public static class DebayerService
{
    public record DetectResult(bool IsOsc, string BayerPattern, string CameraName);
    public record DebayerResult(bool Success, string Message, string OutputDir, int FilesWritten);

    private static readonly string[] FitsExtensions = [".fits", ".fit", ".fts", ".fz"];

    // ── Fast, Python-free detection — checks one representative file ──────────
    public static DetectResult Detect(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            return new DetectResult(false, "", "");

        var first = Directory.GetFiles(dir, "*.*", SearchOption.TopDirectoryOnly)
            .Where(f => FitsExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (first is null) return new DetectResult(false, "", "");

        try
        {
            var hdr = FitsHeaderService.Read(first);
            var bayerPat = hdr.Get("BAYERPAT");
            if (string.IsNullOrWhiteSpace(bayerPat)) return new DetectResult(false, "", "");
            var camera = hdr.Get("TELESCOP");
            if (string.IsNullOrWhiteSpace(camera)) camera = hdr.Get("INSTRUME");
            return new DetectResult(true, bayerPat, camera);
        }
        catch { return new DetectResult(false, "", ""); }
    }

    /// <summary>Progress callback: how many files done so far, the total, and the last file's outcome.</summary>
    public record Progress(int Done, int Total, string LastMessage);

    // Embedded Python script — called by the conda Python that has EXOTIC (and therefore
    // colour_demosaicing + astropy) installed. Processes every FITS file in a directory,
    // writing green-channel-only, BAYERPAT-stripped copies to a sibling Debayered\ folder.
    // Original raw files are never modified or deleted. Prints TOTAL:<n> up front so the
    // caller can drive a determinate progress bar rather than an indeterminate spinner.
    private const string PythonScript = """
import sys, glob, os
from pathlib import Path
from astropy.io import fits
from colour_demosaicing import demosaicing_CFA_Bayer_bilinear

src_dir = sys.argv[1]
out_dir = sys.argv[2]

Path(out_dir).mkdir(parents=True, exist_ok=True)

patterns = ("*.fits", "*.fit", "*.fts", "*.fz")
files = sorted({f for p in patterns for f in glob.glob(os.path.join(src_dir, p))})

print(f"TOTAL:{len(files)}", file=sys.stderr)

written = 0
for f in files:
    name = os.path.basename(f)
    try:
        with fits.open(f) as hdul:
            hdr = hdul[0].header
            data = hdul[0].data
            bayerpat = str(hdr.get("BAYERPAT", "")).strip()
            if not bayerpat:
                print(f"SKIP: {name} (no BAYERPAT)", file=sys.stderr)
                continue

            debayered = demosaicing_CFA_Bayer_bilinear(data, bayerpat.upper())
            green = debayered[:, :, 1].astype(data.dtype)

            new_hdr = hdr.copy()
            if "BAYERPAT" in new_hdr:
                del new_hdr["BAYERPAT"]
            new_hdr["HISTORY"] = "Debayered by TransitLab (green channel, bilinear)"

            out_path = os.path.join(out_dir, name)
            fits.PrimaryHDU(data=green, header=new_hdr).writeto(out_path, overwrite=True)
            written += 1
            print(f"OK: {name}", file=sys.stderr)
    except Exception as e:
        print(f"FAILFILE: {name} -- {e}", file=sys.stderr)

print(f"DONE:{written}")
""";

    /// <summary>
    /// Debayers every FITS file in <paramref name="sourceDir"/> into a sibling "Debayered"
    /// subfolder. Skips files with no BAYERPAT keyword (logged, not treated as an error).
    /// </summary>
    public static async Task<DebayerResult> DebayerDirectoryAsync(
        string sourceDir,
        string exoticExePath,
        string pythonExePath,
        IProgress<Progress>? progress = null,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(sourceDir))
            return new DebayerResult(false, $"Directory not found: {sourceDir}", "", 0);

        var pythonExe = NormalizePythonExe(pythonExePath) ?? DerivePythonExe(exoticExePath);
        if (pythonExe is null)
            return new DebayerResult(false,
                "Python not found — install Python and EXOTIC using the EXOTIC Setup tab first.", "", 0);

        var outDir = Path.Combine(sourceDir.TrimEnd('\\', '/'), "Debayered");
        var scriptPath = Path.Combine(Path.GetTempPath(), $"debayer_helper_{Guid.NewGuid():N}.py");

        try
        {
            await File.WriteAllTextAsync(scriptPath, PythonScript, ct);

            progress?.Report(new Progress(0, 0, $"Starting {Path.GetFileName(sourceDir)}…"));
            SessionLogService.Write($"[Debayer] Starting: {sourceDir} -> {outDir}");

            var psi = new ProcessStartInfo
            {
                FileName               = pythonExe,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            psi.ArgumentList.Add(scriptPath);
            psi.ArgumentList.Add(sourceDir);
            psi.ArgumentList.Add(outDir);

            using var proc = new Process { StartInfo = psi };
            proc.Start();

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = ConsumeStderrAsync(proc.StandardError, progress, ct);
            await Task.WhenAll(stdoutTask, stderrTask);
            await proc.WaitForExitAsync(ct);

            var output   = stdoutTask.Result.Trim();
            var lastLine = output.Split('\n').LastOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim() ?? "";

            if (lastLine.StartsWith("DONE:", StringComparison.OrdinalIgnoreCase))
            {
                int.TryParse(lastLine[5..].Trim(), out var count);
                SessionLogService.Write($"[Debayer] ✓  {count} file(s) written to {outDir}");
                return new DebayerResult(true, $"Debayered {count} file(s).", outDir, count);
            }

            SessionLogService.Write($"[Debayer] ✗  unexpected output: {output}");
            return new DebayerResult(false, $"Debayering failed — unexpected output: {output}", outDir, 0);
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { /* best-effort */ }
        }
    }

    private static async Task ConsumeStderrAsync(StreamReader stderr, IProgress<Progress>? progress, CancellationToken ct)
    {
        int total = 0, done = 0;
        string? line;
        while ((line = await stderr.ReadLineAsync(ct)) is not null)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("TOTAL:", StringComparison.OrdinalIgnoreCase))
            {
                int.TryParse(trimmed[6..].Trim(), out total);
                progress?.Report(new Progress(0, total, total == 0 ? "No FITS files found." : $"0 of {total}…"));
            }
            else if (trimmed.StartsWith("OK:") || trimmed.StartsWith("SKIP:") || trimmed.StartsWith("FAILFILE:"))
            {
                done++;
                progress?.Report(new Progress(done, total, trimmed));
                SessionLogService.Write($"[Debayer] {trimmed}");
            }
        }
    }

    // ── Python resolution — mirrors PlateSolveService's identical private helpers ──
    private static string? DerivePythonExe(string exoticExePath)
    {
        if (string.IsNullOrEmpty(exoticExePath)) return null;
        var directPython = NormalizePythonExe(exoticExePath);
        if (directPython is not null) return directPython;
        var scriptsDir = Path.GetDirectoryName(exoticExePath);
        if (scriptsDir is null) return null;
        var envDir = Path.GetDirectoryName(scriptsDir);
        if (envDir is null) return null;
        var python = Path.Combine(envDir, "python.exe");
        return File.Exists(python) ? python : null;
    }

    private static string? NormalizePythonExe(string pythonExePath)
    {
        if (string.IsNullOrWhiteSpace(pythonExePath)) return null;
        return File.Exists(pythonExePath) && ExoticRuntimeService.LooksLikePython(pythonExePath)
            ? pythonExePath
            : null;
    }
}
