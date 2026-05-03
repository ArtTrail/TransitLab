using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TransitLab.Services;

/// <summary>
/// Plate-solves a FITS file using EXOTIC's built-in PlateSolution
/// (exotic.api.plate_solution), which calls astrometry.net without
/// requiring the user to supply an API key.
/// </summary>
public static class PlateSolveService
{
    public record Result(bool Success, string Message);

    /// <summary>Solver selection and ASTAP parameters.</summary>
    public record SolverConfig(
        string Solver,           // "AstrometryNet" | "ASTAP"
        string AstapExePath,
        string CatalogDir     = "",   // blank = same dir as exe
        int    SearchRadius   = 60,   // arcminutes (converted to degrees when calling ASTAP)
        int    Downsample     = 0,
        bool   SolveAllFrames = false);

    // Inline Python script — called by the conda Python that has EXOTIC installed.
    // Prints the wcs.fits path on success, or "FAILED: <reason>" on failure.
    private const string PythonScript = """
import sys
from pathlib import Path

fits_path = sys.argv[1]
save_dir  = sys.argv[2]

# Redirect library stdout to stderr so progress text doesn't pollute our
# result channel — only the final path (or FAILED: line) goes to stdout.
_real_stdout = sys.stdout
sys.stdout   = sys.stderr

try:
    Path(save_dir, "temp").mkdir(parents=True, exist_ok=True)
    from exotic.api.plate_solution import PlateSolution
    ps       = PlateSolution(file=fits_path, directory=save_dir)
    wcs_file = ps.plate_solution()
except ImportError as e:
    sys.stdout = _real_stdout
    print(f"FAILED: {e}  (Python: {sys.executable})")
    sys.exit(1)
except Exception as e:
    sys.stdout = _real_stdout
    print(f"FAILED: {e}")
    sys.exit(1)

sys.stdout = _real_stdout
if wcs_file and Path(str(wcs_file)).exists():
    print(str(wcs_file))
else:
    print("FAILED: plate solver returned no file")
""";

    /// <summary>
    /// Full plate-solve pipeline.  Routes to ASTAP or Astrometry.net
    /// based on <paramref name="solverConfig"/>.
    /// </summary>
    public static async Task<Result> SolveAsync(
        string fitsPath,
        string saveDir,
        string exoticExePath,
        SolverConfig? solverConfig = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        string pythonExePath = "")
    {
        // Route to ASTAP if selected
        if (solverConfig?.Solver == "ASTAP")
        {
            if (solverConfig.SolveAllFrames)
                return await SolveAllWithAstapAsync(fitsPath, solverConfig, progress, ct);
            return await SolveWithAstapAsync(fitsPath, solverConfig, progress, ct);
        }

        // 1. Derive python.exe from exotic.exe location.
        //    Works for conda: <env>\Scripts\exotic.exe → <env>\python.exe
        //    Falls back to FindPythonAsync for --user pip installs where
        //    exotic.exe and python.exe are in different directory trees.
        var pythonExe = NormalizePythonExe(pythonExePath)
                     ?? DerivePythonExe(exoticExePath)
                     ?? (await ExoticInstallService.FindPythonAsync(ct))?.ExePath;
        if (pythonExe is null)
            return new Result(false, "Python not found — install Python and EXOTIC using the EXOTIC Setup tab first.");

        progress?.Report($"Python: {pythonExe}");
        SessionLogService.Write($"[PlateSolve] Starting Astrometry.net solve: {Path.GetFileName(fitsPath)}");
        SessionLogService.Write($"[PlateSolve] Python: {pythonExe}");

        if (!File.Exists(fitsPath))
            return new Result(false, $"FITS file not found: {fitsPath}");

        if (string.IsNullOrWhiteSpace(saveDir))
            saveDir = Path.GetDirectoryName(fitsPath) ?? ".";

        // Strip trailing slash — a path ending in \ breaks argument quoting on Windows
        // (the shell treats \" as an escaped quote rather than a closing quote).
        saveDir = saveDir.TrimEnd('\\', '/');

        // 2. Write the script to a temp file (avoids quoting issues with -c)
        var scriptPath = Path.Combine(Path.GetTempPath(), $"ps_helper_{Guid.NewGuid():N}.py");
        try
        {
            await File.WriteAllTextAsync(scriptPath, PythonScript, ct);

            progress?.Report($"Starting plate solve for {Path.GetFileName(fitsPath)}…");

            var psi = new ProcessStartInfo
            {
                FileName               = pythonExe,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            // ArgumentList handles quoting properly — no manual escaping needed,
            // so paths with spaces or trailing backslashes are passed verbatim.
            psi.ArgumentList.Add(scriptPath);
            psi.ArgumentList.Add(fitsPath);
            psi.ArgumentList.Add(saveDir);

            using var proc = new Process { StartInfo = psi };

            proc.Start();

            // Stream stderr as progress updates; capture stdout for the result
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = ConsumeStderrAsync(proc.StandardError, progress, ct);

            await Task.WhenAll(stdoutTask, stderrTask);

            // Give the process a moment to exit cleanly
            await proc.WaitForExitAsync(ct);

            var output = stdoutTask.Result.Trim();

            if (output.StartsWith("FAILED", StringComparison.OrdinalIgnoreCase))
            {
                var reason = output.Length > 7 ? output[8..].Trim() : "unknown error";
                SessionLogService.Write($"[PlateSolve] ✗  {Path.GetFileName(fitsPath)} — {reason}");
                return new Result(false, $"Plate solve failed — {reason}");
            }

            // output is the wcs.fits path
            var wcsPath = output;
            if (!File.Exists(wcsPath))
                return new Result(false, "Plate solve returned a path that does not exist.");

            // 3. Write WCS back into the original FITS
            progress?.Report("Writing WCS to FITS file…");
            var wcsBytes = await File.ReadAllBytesAsync(wcsPath, ct);
            WriteWcsToFits(fitsPath, wcsBytes);

            SessionLogService.Write($"[PlateSolve] ✓  {Path.GetFileName(fitsPath)} — WCS written.");
            return new Result(true, "Plate solve complete — WCS written to FITS.");
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { /* best-effort */ }
        }
    }

    // ── ASTAP ─────────────────────────────────────────────────────────────────

    private static async Task<Result> SolveWithAstapAsync(
        string fitsPath,
        SolverConfig config,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        if (!File.Exists(config.AstapExePath))
        {
            SessionLogService.Write($"[PlateSolve/ASTAP] ERROR — executable not found: {config.AstapExePath}");
            return new Result(false, $"ASTAP executable not found: {config.AstapExePath}");
        }

        if (!File.Exists(fitsPath))
        {
            SessionLogService.Write($"[PlateSolve/ASTAP] ERROR — FITS file not found: {fitsPath}");
            return new Result(false, $"FITS file not found: {fitsPath}");
        }

        progress?.Report($"Starting ASTAP plate solve for {Path.GetFileName(fitsPath)}…");
        SessionLogService.Write($"[PlateSolve/ASTAP] Starting solve: {Path.GetFileName(fitsPath)}");

        var psi = new ProcessStartInfo
        {
            FileName               = config.AstapExePath,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        psi.ArgumentList.Add("-f");   psi.ArgumentList.Add(fitsPath);
        // SearchRadius is stored in arcminutes; ASTAP -r expects degrees
        var radiusDeg = (config.SearchRadius / 60.0).ToString("F4", System.Globalization.CultureInfo.InvariantCulture);
        psi.ArgumentList.Add("-r");   psi.ArgumentList.Add(radiusDeg);
        psi.ArgumentList.Add("-z");   psi.ArgumentList.Add(config.Downsample.ToString());
        psi.ArgumentList.Add("-fov"); psi.ArgumentList.Add("0");
        psi.ArgumentList.Add("-update");

        // If a separate catalog directory is specified, tell ASTAP where to find it
        if (!string.IsNullOrWhiteSpace(config.CatalogDir) && Directory.Exists(config.CatalogDir))
        {
            psi.ArgumentList.Add("-wcs");
            psi.ArgumentList.Add(config.CatalogDir);
        }

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await Task.WhenAll(stdoutTask, stderrTask);
        await proc.WaitForExitAsync(ct);

        var allOutput = (stdoutTask.Result + "\n" + stderrTask.Result).Trim();
        var outputLines = allOutput.Split('\n')
            .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

        // Log every output line ASTAP produced — includes star count, RA/Dec/scale on
        // success, or the full error trace on failure.
        foreach (var line in outputLines)
            SessionLogService.Write($"[PlateSolve/ASTAP] {line}");

        if (proc.ExitCode != 0)
        {
            var detail = outputLines.LastOrDefault() ?? "no output";
            SessionLogService.Write($"[PlateSolve/ASTAP] ✗  {Path.GetFileName(fitsPath)} — exit code {proc.ExitCode}");
            return new Result(false, $"ASTAP solve failed — {detail}");
        }

        SessionLogService.Write($"[PlateSolve/ASTAP] ✓  {Path.GetFileName(fitsPath)} — WCS written.");
        return new Result(true, "ASTAP plate solve complete — WCS written to FITS.");
    }

    private static async Task<Result> SolveAllWithAstapAsync(
        string firstFitsPath, SolverConfig config,
        IProgress<string>? progress, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(firstFitsPath);
        if (dir is null || !Directory.Exists(dir))
        {
            SessionLogService.Write($"[PlateSolve/ASTAP] ERROR — cannot determine FITS directory from: {firstFitsPath}");
            return new Result(false, "Cannot determine FITS directory.");
        }

        var files = Directory.GetFiles(dir, "*.fits", SearchOption.TopDirectoryOnly)
            .Concat(Directory.GetFiles(dir, "*.fit", SearchOption.TopDirectoryOnly))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0) return new Result(false, "No FITS files found in directory.");

        SessionLogService.Write($"[PlateSolve/ASTAP] Starting all-frames solve in: {dir}  ({files.Length} files)");
        int solved = 0, failed = 0;
        for (int i = 0; i < files.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Solving {i + 1}/{files.Length}: {Path.GetFileName(files[i])}…");
            var r = await SolveWithAstapAsync(files[i], config, null, ct);
            if (r.Success) solved++;
            else { failed++; progress?.Report($"  ✗ {Path.GetFileName(files[i])}: {r.Message}"); }
        }
        var summary = $"All-frames solve complete — {solved} solved, {failed} failed.";
        SessionLogService.Write($"[PlateSolve/ASTAP] {summary}");
        // Succeed if at least one frame solved; individual frame failures (bad/excluded frames) are expected.
        return new Result(solved > 0, summary);
    }

    /// <summary>
    /// Tests whether the ASTAP executable at <paramref name="exePath"/> responds.
    /// Returns (found, version string or error message).
    /// </summary>
    public static async Task<(bool Ok, string Message)> TestAstapAsync(
        string exePath, CancellationToken ct = default)
    {
        if (!File.Exists(exePath))
            return (false, $"File not found: {exePath}");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = exePath,
                Arguments              = "-help",
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            using var proc = new Process { StartInfo = psi };
            proc.Start();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(4));

            string output;
            try
            {
                var outTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
                var errTask = proc.StandardError.ReadToEndAsync(cts.Token);
                await Task.WhenAll(outTask, errTask);
                await proc.WaitForExitAsync(cts.Token);
                output = (outTask.Result + errTask.Result).Trim();
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(); } catch { /* best-effort */ }
                // Still alive after timeout — probably opened a window; exe is present though
                return (true, "ASTAP found (run in CLI mode only)");
            }

            // Try to find version string in output (ASTAP prints e.g. "ASTAP version 0.9.855")
            var versionLine = output
                .Split('\n')
                .Select(l => l.Trim())
                .FirstOrDefault(l => l.Contains("ASTAP", StringComparison.OrdinalIgnoreCase)
                                  && l.Contains("version", StringComparison.OrdinalIgnoreCase));

            return versionLine is not null
                ? (true, $"✓  {versionLine}")
                : (true, "✓  ASTAP responded successfully");
        }
        catch (Exception ex)
        {
            return (false, $"Error running ASTAP: {ex.Message}");
        }
    }

    /// <summary>
    /// Checks for catalog (.1) files in the same directory as the ASTAP executable.
    /// Returns a human-readable status string.
    /// </summary>
    /// <param name="isCatalogDir">
    /// When true, <paramref name="astapExePath"/> is treated as a directory path directly.
    /// When false (default), the directory is derived from the exe path.
    /// </param>
    public static string CheckAstapCatalog(string astapExePath, bool isCatalogDir = false)
    {
        if (string.IsNullOrEmpty(astapExePath)) return "";
        string? dir = isCatalogDir
            ? (Directory.Exists(astapExePath) ? astapExePath : null)
            : Path.GetDirectoryName(astapExePath);
        if (dir is null || !Directory.Exists(dir))
            return "⚠  Cannot locate ASTAP directory";

        try
        {
            // ASTAP catalogs are named with a known prefix (h17, h18, d80, w08, d05, g17…)
            // followed by a zone number. Extensions vary by catalog type (.1, .290, .360, etc.)
            // so we match by prefix rather than extension.
            string[] knownPrefixes = ["h17", "h18", "h19", "w08", "d05", "d80", "g17", "m17", "v17"];

            var catalogFiles = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                .Where(f =>
                {
                    var name = Path.GetFileName(f).ToLowerInvariant();
                    return knownPrefixes.Any(p => name.StartsWith(p));
                })
                .ToArray();

            if (catalogFiles.Length > 0)
            {
                // Identify which catalogs are present by prefix
                var found = knownPrefixes
                    .Where(p => catalogFiles.Any(f => Path.GetFileName(f).StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                    .Select(p => p.ToUpperInvariant())
                    .ToArray();
                var catalogNames = string.Join(", ", found);
                return $"✓  Catalog found: {catalogNames}  ({catalogFiles.Length} file{(catalogFiles.Length == 1 ? "" : "s")})";
            }
            return "⚠  No catalog found — download a catalog (H17, D80, etc.) from hnsky.org";
        }
        catch
        {
            return "⚠  Could not read ASTAP directory";
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string? DerivePythonExe(string exoticExePath)
    {
        if (string.IsNullOrEmpty(exoticExePath)) return null;

        var directPython = NormalizePythonExe(exoticExePath);
        if (directPython is not null) return directPython;

        // exotic.exe is in <env>\Scripts\  →  python.exe is in <env>\
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

    private static async Task ConsumeStderrAsync(
        StreamReader stderr,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        string? line;
        while ((line = await stderr.ReadLineAsync(ct)) is not null)
        {
            // Forward meaningful astrometry.net status lines to both progress and the session log
            if (line.Contains("nova.astrometry") || line.Contains("submission") ||
                line.Contains("Waiting") || line.Contains("success") ||
                line.Contains("Uploading") || line.Contains("job"))
            {
                var trimmed = line.Trim();
                progress?.Report(trimmed);
                SessionLogService.Write($"[PlateSolve] {trimmed}");
            }
        }
    }

    // ── WCS writeback (unchanged) ─────────────────────────────────────────────

    private static readonly string[] WcsPrefixes =
    [
        "WCSAXES","CTYPE","CRPIX","CRVAL","CDELT",
        "CD1_","CD2_","PC1_","PC2_",
        "LONPOLE","LATPOLE","RADESYS","EQUINOX",
        "A_ORDER","B_ORDER","AP_ORDER","BP_ORDER",
        "A_","B_","AP_","BP_",
    ];

    private static void WriteWcsToFits(string fitsPath, byte[] wcsBytes)
    {
        var wcsCards = ParseFitsHeaderCards(wcsBytes)
            .Where(c => WcsPrefixes.Any(p => c.Keyword.StartsWith(p, StringComparison.Ordinal)))
            .ToList();

        if (wcsCards.Count == 0) return;

        var bytes    = File.ReadAllBytes(fitsPath);
        var modified = InsertHeaderCards(bytes, wcsCards);
        File.WriteAllBytes(fitsPath, modified);
    }

    private record FitsCard(string Keyword, string Raw80);

    private static List<FitsCard> ParseFitsHeaderCards(byte[] fitsBytes)
    {
        var cards = new List<FitsCard>();
        int pos = 0;
        while (pos + 79 < fitsBytes.Length)
        {
            var record = Encoding.ASCII.GetString(fitsBytes, pos, 80);
            var kw = record[..8].TrimEnd();
            if (kw == "END") break;
            if (kw.Length > 0) cards.Add(new FitsCard(kw, record));
            pos += 80;
            if (pos % 2880 == 0 && pos > 0)
            {
                bool allSpace = true;
                for (int i = pos; i < Math.Min(pos + 2880, fitsBytes.Length); i++)
                    if (fitsBytes[i] != 0x20) { allSpace = false; break; }
                if (allSpace) break;
            }
        }
        return cards;
    }

    private static byte[] InsertHeaderCards(byte[] original, List<FitsCard> newCards)
    {
        int endPos = -1;
        for (int i = 0; i + 79 < original.Length; i += 80)
        {
            var kw = Encoding.ASCII.GetString(original, i, 8).TrimEnd();
            if (kw == "END") { endPos = i; break; }
        }
        if (endPos < 0) return original;

        var existingRecords = new List<byte[]>();
        for (int i = 0; i < endPos; i += 80)
        {
            var kw = Encoding.ASCII.GetString(original, i, 8).TrimEnd();
            if (!WcsPrefixes.Any(p => kw.StartsWith(p, StringComparison.Ordinal)))
                existingRecords.Add(original[i..(i + 80)]);
        }

        var newCardBytes = newCards.Select(c =>
        {
            var raw = c.Raw80.PadRight(80)[..80];
            return Encoding.ASCII.GetBytes(raw);
        }).ToList();

        using var ms = new MemoryStream();
        foreach (var rec in existingRecords) ms.Write(rec);
        foreach (var rec in newCardBytes)    ms.Write(rec);

        var endRec = Encoding.ASCII.GetBytes("END".PadRight(80));
        ms.Write(endRec);

        var headerLen = (int)ms.Length;
        var padLen    = (2880 - headerLen % 2880) % 2880;
        ms.Write(new byte[padLen]);
        if (padLen > 0)
        {
            ms.Seek(-padLen, SeekOrigin.Current);
            for (int i = 0; i < padLen; i++) ms.WriteByte(0x20);
        }

        int origHeaderBlocks = (endPos / 2880) + 1;
        int origDataStart    = origHeaderBlocks * 2880;
        if (origDataStart < original.Length)
            ms.Write(original, origDataStart, original.Length - origDataStart);

        return ms.ToArray();
    }
}
