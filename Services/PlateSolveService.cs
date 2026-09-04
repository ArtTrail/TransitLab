using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace TransitLab.Services;

/// <summary>
/// Plate-solves a FITS file using EXOTIC's built-in exotic.api.plate_solution module,
/// which offers three solvers: PlateSolution (nova.astrometry.net, no API key required),
/// NextAstroPlateSolution (NextAstronomy's hosted service — requires the EXOTIC 4.3.2
/// pre-release dev build, not yet in a stable release), or local ASTAP.
/// </summary>
public static class PlateSolveService
{
    public record Result(bool Success, string Message, double WcsPixelScaleArcsec = 0, bool SmallImageWarning = false, string FirstSolvedPath = "", bool IsPartialSuccess = false);

    /// <summary>Solver selection and ASTAP parameters.</summary>
    public record SolverConfig(
        string Solver,           // "AstrometryNet" | "ASTAP" | "NextAstro" | "StarFix"
        string AstapExePath,
        string CatalogDir       = "",    // blank = same dir as exe
        int    SearchRadius     = 60,    // arcminutes (converted to degrees when calling ASTAP)
        int    Downsample       = 0,
        bool   SolveAllFrames   = false,
        double PixelScaleArcsec = 0.0,   // arcsec/px from user input; 0 = unknown → ASTAP auto-FOV
        double? Ra              = null,  // decimal degrees — hint for AstrometryNet/NextAstro
        double? Dec             = null,  // decimal degrees — hint for AstrometryNet/NextAstro
        string StarFixExePath   = "");   // StarFix install root

    // Inline Python script — called by the conda Python that has EXOTIC installed.
    // Shared by both online solvers (AstrometryNet's PlateSolution and NextAstro's
    // NextAstroPlateSolution expose the same plate_solution() contract), selected via
    // the "astrometrynet"/"nextastro" argv[1] flag. Prints the wcs.fits path on success,
    // or "FAILED: <reason>" on failure.
    private const string PythonScript = """
import sys
from pathlib import Path

solver_name = sys.argv[1]
fits_path   = sys.argv[2]
save_dir    = sys.argv[3]
ra          = sys.argv[4] if len(sys.argv) > 4 and sys.argv[4] else None
dec         = sys.argv[5] if len(sys.argv) > 5 and sys.argv[5] else None
pixel_scale = sys.argv[6] if len(sys.argv) > 6 and sys.argv[6] else None

# Redirect library stdout to stderr so progress text doesn't pollute our
# result channel — only the final path (or FAILED: line) goes to stdout.
_real_stdout = sys.stdout
sys.stdout   = sys.stderr

try:
    Path(save_dir, "temp").mkdir(parents=True, exist_ok=True)
    # Some EXOTIC builds (e.g. the 4.3.2 pre-release) write the solved WCS to
    # <save_dir>/working_artifacts/wcs.fits instead of <save_dir>/temp/wcs.fits,
    # but never create that folder themselves -- pre-create it here so the
    # write doesn't fail with a bare "No such file or directory".
    Path(save_dir, "working_artifacts").mkdir(parents=True, exist_ok=True)
    if solver_name == "nextastro":
        from exotic.api.plate_solution import NextAstroPlateSolution
        ps = NextAstroPlateSolution(
            file=fits_path, directory=save_dir,
            ra=float(ra) if ra else None,
            dec=float(dec) if dec else None,
            pixel_scale=float(pixel_scale) if pixel_scale else None,
        )
    else:
        from exotic.api.plate_solution import PlateSolution
        ps = PlateSolution(file=fits_path, directory=save_dir)
    wcs_file = ps.plate_solution()
except ImportError as e:
    sys.stdout = _real_stdout
    extra = ("  (NextAstro plate solving requires the EXOTIC 4.3.2 pre-release dev build -- "
             "see Tools -> Python & EXOTIC Setup -> Pre-release / Development Build)"
             if solver_name == "nextastro" else "")
    print(f"FAILED: {e}{extra}  (Python: {sys.executable})")
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
    /// Full plate-solve pipeline.  Routes to ASTAP, Astrometry.net, or NextAstro
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

        // Route to StarFix if selected — guarded explicitly rather than falling through to the
        // online-solver branch below, which would otherwise silently run Astrometry.net instead.
        if (solverConfig?.Solver == "StarFix")
        {
            if (solverConfig.SolveAllFrames)
                return await SolveAllWithStarFixAsync(fitsPath, solverConfig, progress, ct);
            return await SolveWithStarFixAsync(fitsPath, solverConfig, progress, ct);
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

        if (solverConfig?.SolveAllFrames == true)
            return await SolveAllWithOnlineSolverAsync(fitsPath, saveDir, solverConfig, pythonExe, progress, ct);

        return await SolveOneWithOnlineSolverAsync(fitsPath, saveDir, solverConfig, pythonExe, progress, ct);
    }

    // ── Astrometry.net / NextAstro (shared online-solver code path) ───────────

    private static async Task<Result> SolveOneWithOnlineSolverAsync(
        string fitsPath,
        string saveDir,
        SolverConfig? solverConfig,
        string pythonExe,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var isNextAstro = solverConfig?.Solver == "NextAstro";
        var solverLabel = isNextAstro ? "NextAstro" : "Astrometry.net";

        progress?.Report($"Python: {pythonExe}");
        SessionLogService.Write($"[PlateSolve] Starting {solverLabel} solve: {Path.GetFileName(fitsPath)}");
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
            psi.ArgumentList.Add(isNextAstro ? "nextastro" : "astrometrynet");
            psi.ArgumentList.Add(fitsPath);
            psi.ArgumentList.Add(saveDir);
            // RA/Dec/pixel-scale hints — optional for Astrometry.net (blind solve works fine
            // without them) but meaningfully speed up and improve reliability for NextAstro,
            // which matches detected sources against the hinted field rather than a blind index search.
            psi.ArgumentList.Add(solverConfig?.Ra?.ToString("F6", System.Globalization.CultureInfo.InvariantCulture) ?? "");
            psi.ArgumentList.Add(solverConfig?.Dec?.ToString("F6", System.Globalization.CultureInfo.InvariantCulture) ?? "");
            psi.ArgumentList.Add(solverConfig?.PixelScaleArcsec > 0
                ? solverConfig.PixelScaleArcsec.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)
                : "");

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

            // 4. Report the actual pixel scale derived from the WCS, for diagnostics —
            //    useful context if the user later tries ASTAP, which uses the configured
            //    scale as a FOV hint and needs it to be accurate to converge.
            double wcsScale = ReadActualPixelScale(wcsBytes);
            if (wcsScale > 0)
            {
                SessionLogService.Write($"[PlateSolve] WCS pixel scale: {wcsScale:F3}\"/px");
                if (solverConfig?.PixelScaleArcsec > 0)
                {
                    double configScale = solverConfig.PixelScaleArcsec;
                    double diffPct = Math.Abs(wcsScale - configScale) / configScale * 100.0;
                    if (diffPct > 5.0)
                        SessionLogService.Write(
                            $"[PlateSolve] ⚠  Pixel scale mismatch: WCS={wcsScale:F3}\"/px vs " +
                            $"configured={configScale:F3}\"/px ({diffPct:F1}% difference). " +
                            $"If you later switch to ASTAP, update the pixel scale in Tools → Plate Solve Setup first — " +
                            $"ASTAP uses it as a FOV hint and a mismatch this large will cause quad matching to fail.");
                    else
                        SessionLogService.Write(
                            $"[PlateSolve] Pixel scale check: WCS={wcsScale:F3}\"/px vs " +
                            $"configured={configScale:F3}\"/px ({diffPct:F1}% — within tolerance).");
                }
            }

            SessionLogService.Write($"[PlateSolve] ✓  {Path.GetFileName(fitsPath)} — WCS written.");
            return new Result(true, "Plate solve complete — WCS written to FITS.", wcsScale);
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { /* best-effort */ }
        }
    }

    private static async Task<Result> SolveAllWithOnlineSolverAsync(
        string firstFitsPath,
        string saveDir,
        SolverConfig? solverConfig,
        string pythonExe,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var solverLabel = solverConfig?.Solver == "NextAstro" ? "NextAstro" : "Astrometry.net";
        var dir = Path.GetDirectoryName(firstFitsPath);
        if (dir is null || !Directory.Exists(dir))
        {
            SessionLogService.Write($"[PlateSolve/{solverLabel}] ERROR — cannot determine FITS directory from: {firstFitsPath}");
            return new Result(false, "Cannot determine FITS directory.");
        }

        var files = Directory.GetFiles(dir, "*.fits", SearchOption.TopDirectoryOnly)
            .Concat(Directory.GetFiles(dir, "*.fit", SearchOption.TopDirectoryOnly))
            .Concat(Directory.GetFiles(dir, "*.fts", SearchOption.TopDirectoryOnly))
            .Concat(Directory.GetFiles(dir, "*.fz",  SearchOption.TopDirectoryOnly))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0) return new Result(false, "No FITS files found in directory.");

        SessionLogService.Write($"[PlateSolve/{solverLabel}] Starting all-frames solve in: {dir}  ({files.Length} files)");
        int solved = 0, failed = 0;
        string firstSolvedPath = "";
        for (int i = 0; i < files.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            // Each frame is a separate network round-trip to a hosted solver — this can take
            // a while for a full directory, unlike ASTAP's instant local solve. A live progress
            // line stays ⟳ (neutral) regardless of how many have failed; only the final verdict
            // below gets a ✓/⚠/✗ prefix.
            progress?.Report($"⟳  Solving {i + 1}/{files.Length} via {solverLabel}…  ({solved} solved, {failed} failed so far)");
            var r = await SolveOneWithOnlineSolverAsync(files[i], saveDir, solverConfig, pythonExe, null, ct);
            if (r.Success)
            {
                solved++;
                if (firstSolvedPath.Length == 0) firstSolvedPath = files[i];
            }
            else
            {
                failed++;
                SessionLogService.Write($"[PlateSolve/{solverLabel}] ✗  {Path.GetFileName(files[i])} — {r.Message}");
            }
        }

        bool partial = solved > 0 && failed > 0;
        string summary = (solved, failed) switch
        {
            (_, 0) => $"All {solved} frame{(solved == 1 ? "" : "s")} solved — no errors.",
            (0, _) => $"Plate solve failed — 0/{files.Length} solved. EXOTIC cannot run without at least one solved frame.",
            _      => $"{solved} solved, {failed} failed — usable (EXOTIC only needs one solved reference frame), but check the excluded/failed frames.",
        };
        SessionLogService.Write($"[PlateSolve/{solverLabel}] All-frames solve complete — {solved} solved, {failed} failed.");

        return new Result(solved > 0, summary, FirstSolvedPath: firstSolvedPath, IsPartialSuccess: partial);
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

        // Compute FOV from pixel scale × shorter image dimension.
        // ASTAP -fov expects the SHORTER image dimension in degrees (per ASTAP docs).
        // FITS convention: NAXIS1 = width (columns), NAXIS2 = height (rows).
        // For landscape images (most CCDs) NAXIS1 > NAXIS2, so NAXIS2 is already the shorter
        // side and the result is identical to before.  Reading both ensures portrait-orientation
        // or square images are also handled correctly, and lets us log NAXIS1 for diagnostics.
        // (Hardcoded -fov 0 caused ASTAP to try all FOV values from 9.5° down, producing
        // spurious 3-quad solutions with completely wrong CD matrices on small images.)
        string fovArg = "0";
        if (config.PixelScaleArcsec > 0)
        {
            int? naxis1  = ReadFitsHeaderInt(fitsPath, "NAXIS1");
            int? naxis2  = ReadFitsHeaderInt(fitsPath, "NAXIS2");
            int? bitpix  = ReadFitsHeaderInt(fitsPath, "BITPIX");

            int? shortSide = (naxis1.HasValue && naxis2.HasValue)
                ? Math.Min(naxis1.Value, naxis2.Value)
                : (naxis2 ?? naxis1);

            if (shortSide is > 0)
            {
                double fovDeg = shortSide.Value * config.PixelScaleArcsec / 3600.0;
                fovArg = fovDeg.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);
                SessionLogService.Write(
                    $"[PlateSolve/ASTAP] FOV = {fovArg}°  " +
                    $"(NAXIS1={naxis1?.ToString() ?? "?"}, NAXIS2={naxis2?.ToString() ?? "?"}, " +
                    $"shorter={shortSide}, scale={config.PixelScaleArcsec}\"/px, " +
                    $"BITPIX={bitpix?.ToString() ?? "?"})");
            }
        }
        psi.ArgumentList.Add("-fov"); psi.ArgumentList.Add(fovArg);
        psi.ArgumentList.Add("-update");

        // If a separate catalog directory is specified, tell ASTAP where to find it.
        // -d <path>  sets the catalog search directory.
        // -wcs (no argument) writes a .wcs output file — not the same flag.
        if (!string.IsNullOrWhiteSpace(config.CatalogDir) && Directory.Exists(config.CatalogDir))
        {
            psi.ArgumentList.Add("-d");
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

        // Detect ASTAP's "Warning, small image dimensions!!" — emitted when pixel count is
        // too low for reliable quad centroiding.  Captured here so it can be checked in both
        // the failure path and the return value regardless of exit code.
        bool smallImageWarning = outputLines.Any(l =>
            l.Contains("small image", StringComparison.OrdinalIgnoreCase));

        // Log every output line ASTAP produced — includes star count, RA/Dec/scale on
        // success, or the full error trace on failure.
        foreach (var line in outputLines)
            SessionLogService.Write($"[PlateSolve/ASTAP] {line}");

        if (proc.ExitCode != 0)
        {
            // When ASTAP writes nothing to stdout/stderr but still fails, it almost always means
            // it could not find its star catalog zone files for the target's sky region.
            // (ASTAP prints star counts and a "Solution NOT found" line when it actually searches;
            //  silent exit = catalog lookup failed before any search was attempted.)
            string iniDetail = "";
            try
            {
                var iniPath = Path.ChangeExtension(fitsPath, ".ini");
                if (File.Exists(iniPath))
                {
                    var iniLines = File.ReadAllLines(iniPath);
                    var relevant = iniLines
                        .Where(l => l.StartsWith("PLTSOLVF", StringComparison.OrdinalIgnoreCase)
                                 || l.Contains("WARNING", StringComparison.OrdinalIgnoreCase)
                                 || l.Contains("ERROR",   StringComparison.OrdinalIgnoreCase)
                                 || l.Contains("stars",   StringComparison.OrdinalIgnoreCase))
                        .Take(4).ToList();
                    if (relevant.Any())
                        iniDetail = " | ASTAP ini: " + string.Join("; ", relevant);
                }
            }
            catch { /* best-effort */ }

            string detail;
            if (outputLines.Count > 0)
            {
                detail = outputLines.Last() + iniDetail;
            }
            else
            {
                detail = "ASTAP produced no output" + iniDetail;
                SessionLogService.Write(
                    $"[PlateSolve/ASTAP] ⚠  No stdout/stderr from ASTAP — BITPIX was logged above; " +
                    $"possible causes: (1) FITS format not supported by this ASTAP version — " +
                    $"BITPIX=-32 (float) is not handled by all builds; " +
                    $"(2) missing H18 zone file for this declination (does not apply to D80/W08 which are all-sky); " +
                    $"(3) image dimensions or FOV hint too far from actual field; " +
                    $"(4) very dense star field near galactic plane confusing ASTAP's quad matcher.");
            }

            // When ASTAP flagged the image as too small, override the generic "No solution found"
            // detail with an actionable message.  At coarse pixel scales (e.g. 5"/px), star
            // centroids span less than one pixel — quad geometry deviates past ASTAP's 0.007
            // tolerance even though stars and quads are found in abundance.
            if (smallImageWarning)
            {
                detail = $"image too small for ASTAP quad-matching at {config.PixelScaleArcsec}\"/px " +
                         $"— switch to Astrometry.net";
                SessionLogService.Write(
                    $"[PlateSolve/ASTAP] ⚠  'Small image dimensions' — at {config.PixelScaleArcsec}\"/px " +
                    $"star centroids are sub-pixel precise; quad geometry deviates past ASTAP's " +
                    $"0.007 tolerance. Astrometry.net is the correct solver for this dataset.");
            }

            SessionLogService.Write($"[PlateSolve/ASTAP] ✗  {Path.GetFileName(fitsPath)} — exit code {proc.ExitCode}");
            return new Result(false, $"ASTAP solve failed — {detail}", SmallImageWarning: smallImageWarning);
        }

        // Quad-count guard: ASTAP's minimum is 3 matched quads, but a 3-quad solution can
        // produce a completely wrong CD matrix (wrong scale, wrong orientation).  Require ≥4.
        int quadCount = ParseAstapQuadCount(allOutput);
        if (quadCount >= 0 && quadCount < 4)
        {
            var quadMsg = $"solution rejected — only {quadCount} quad{(quadCount == 1 ? "" : "s")} matched " +
                          "(minimum 4 required for a reliable plate solution)";
            SessionLogService.Write($"[PlateSolve/ASTAP] ✗  {Path.GetFileName(fitsPath)} — {quadMsg}");
            return new Result(false, $"ASTAP plate solve failed — {quadMsg}");
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
            .Concat(Directory.GetFiles(dir, "*.fts", SearchOption.TopDirectoryOnly))
            .Concat(Directory.GetFiles(dir, "*.fz",  SearchOption.TopDirectoryOnly))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0) return new Result(false, "No FITS files found in directory.");

        SessionLogService.Write($"[PlateSolve/ASTAP] Starting all-frames solve in: {dir}  ({files.Length} files)");
        int solved = 0, failed = 0;
        bool anySmallImageWarning = false;
        string firstSolvedPath = "";
        for (int i = 0; i < files.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            // A running counter, not the failing filename — individual frame failures are
            // routine (especially on MObs data, where ASTAP fails most frames outright due to
            // its small-image-dimensions limitation) and previously looked identical to a
            // final "solve failed" result, since both used the same ✗-prefixed status text.
            // A live progress line stays ⟳ (neutral/in-progress) no matter how many frames
            // have failed so far — only the true final result below gets a ✓/⚠/✗ verdict.
            progress?.Report($"⟳  Solving {i + 1}/{files.Length}…  ({solved} solved, {failed} failed so far)");
            var r = await SolveWithAstapAsync(files[i], config, null, ct);
            if (r.SmallImageWarning) anySmallImageWarning = true;
            if (r.Success)
            {
                solved++;
                // Track the first successfully-solved file so callers can use a file that
                // actually has WCS (the first file in the directory may be a bad/dark frame
                // that is never solvable and therefore never receives a plate solution).
                if (firstSolvedPath.Length == 0) firstSolvedPath = files[i];
            }
            else
            {
                failed++;
                SessionLogService.Write($"[PlateSolve/ASTAP] ✗  {Path.GetFileName(files[i])} — {r.Message}");
            }
        }

        bool partial = solved > 0 && failed > 0;
        string summary = (solved, failed) switch
        {
            (_, 0)      => $"All {solved} frame{(solved == 1 ? "" : "s")} solved — no errors.",
            (0, _)      => $"Plate solve failed — 0/{files.Length} solved. EXOTIC cannot run without at least one solved frame.",
            _           => $"{solved} solved, {failed} failed — usable (EXOTIC only needs one solved reference frame), but check the excluded/failed frames.",
        };
        SessionLogService.Write($"[PlateSolve/ASTAP] All-frames solve complete — {solved} solved, {failed} failed.");

        if (solved == 0 && anySmallImageWarning)
        {
            // All frames failed because of image size — give a specific, actionable summary
            // rather than the generic diagnostic (which covers BITPIX/catalog/galactic-plane issues).
            summary = $"ASTAP: image dimensions too small for quad-matching — " +
                      $"0/{files.Length} solved. Switch to Astrometry.net.";
            SessionLogService.Write(
                $"[PlateSolve/ASTAP] ⚠  All frames failed with 'small image dimensions'. " +
                $"At this pixel scale, star centroids are sub-pixel precise and ASTAP's " +
                $"quad geometry cannot converge. " +
                $"Switch to Astrometry.net (Tools → Plate Solve Setup).");
        }
        else if (solved == 0)
        {
            SessionLogService.Write(
                $"[PlateSolve/ASTAP] ⚠  Zero frames solved.  " +
                $"Check the per-frame lines above for BITPIX and NAXIS values.  " +
                $"If ASTAP produced no output for any frame, likely causes: " +
                $"BITPIX=-32 (float FITS — not supported by all ASTAP builds); " +
                $"missing H18 zone for this Dec (H18 only — D80/W08 are all-sky); " +
                $"or extreme star density near the galactic plane.  " +
                $"Fallback: switch to Astrometry.net in Tools → Plate Solve Setup.");
        }

        // Succeed if at least one frame solved; individual frame failures (bad/excluded frames) are expected.
        return new Result(solved > 0, summary, SmallImageWarning: anySmallImageWarning,
            FirstSolvedPath: firstSolvedPath, IsPartialSuccess: partial);
    }

    // ── StarFix (ArtTrail/StarFix, separate app, invoked headlessly) ──────────

    /// <summary>
    /// The Gaia catalog StarFix's GUI downloads into via Tools → Download Gaia Catalog — solve.exe
    /// needs STARFIX_GAIA_CATALOG_DIR pointed at it explicitly, since its own default is relative
    /// to the frozen exe's own folder, not where the catalog actually landed.
    /// </summary>
    private static string StarFixCatalogDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StarFix", "gaia_catalog");

    private static async Task<Result> SolveWithStarFixAsync(
        string fitsPath,
        SolverConfig config,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var solveExe = StarFixService.SolveExePath(config.StarFixExePath);
        if (!File.Exists(solveExe))
        {
            SessionLogService.Write($"[PlateSolve/StarFix] ERROR — solve.exe not found: {solveExe}");
            return new Result(false, $"StarFix not found — install it in Tools → Plate Solve Setup.");
        }

        if (!File.Exists(fitsPath))
        {
            SessionLogService.Write($"[PlateSolve/StarFix] ERROR — FITS file not found: {fitsPath}");
            return new Result(false, $"FITS file not found: {fitsPath}");
        }

        progress?.Report($"Starting StarFix plate solve for {Path.GetFileName(fitsPath)}…");
        SessionLogService.Write($"[PlateSolve/StarFix] Starting solve: {Path.GetFileName(fitsPath)}");

        var psi = new ProcessStartInfo
        {
            FileName               = solveExe,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        psi.ArgumentList.Add(fitsPath);
        // RA/Dec hints are optional — solve.exe falls back to the FITS header's own RA/DEC when
        // omitted, same default AstrometryNet/NextAstro already rely on via this same config.
        if (config.Ra is double ra)   { psi.ArgumentList.Add("--ra");  psi.ArgumentList.Add(ra.ToString("F6", CultureInfo.InvariantCulture)); }
        if (config.Dec is double dec) { psi.ArgumentList.Add("--dec"); psi.ArgumentList.Add(dec.ToString("F6", CultureInfo.InvariantCulture)); }
        // SearchRadius is stored in arcminutes (shared with ASTAP's own field); solve.exe's -r
        // expects degrees, added as a margin on top of the image's own computed FOV.
        var radiusDeg = (config.SearchRadius / 60.0).ToString("F4", CultureInfo.InvariantCulture);
        psi.ArgumentList.Add("-r"); psi.ArgumentList.Add(radiusDeg);
        psi.ArgumentList.Add("--json");
        // No --dry-run: solve.exe writes the WCS back into the file in place by default, same as ASTAP.

        if (Directory.Exists(StarFixCatalogDir))
            psi.Environment["STARFIX_GAIA_CATALOG_DIR"] = StarFixCatalogDir;

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await Task.WhenAll(stdoutTask, stderrTask);
        await proc.WaitForExitAsync(ct);

        var stdout = stdoutTask.Result.Trim();
        var stderr = stderrTask.Result.Trim();

        foreach (var line in stderr.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0))
            SessionLogService.Write($"[PlateSolve/StarFix] {line}");

        if (proc.ExitCode != 0 || stdout.Length == 0)
        {
            var detail = ExtractPythonErrorLine(stderr) ?? "no output from StarFix";
            SessionLogService.Write($"[PlateSolve/StarFix] ✗  {Path.GetFileName(fitsPath)} — exit code {proc.ExitCode}: {detail}");
            return new Result(false, $"StarFix solve failed — {detail}");
        }

        try
        {
            var json        = JsonNode.Parse(stdout);
            var summary     = json?["summary"];
            var numDetected = json?["num_detected"]?.GetValue<int>() ?? 0;
            var numMatched  = json?["num_matched"]?.GetValue<int>() ?? 0;
            var rmsPixels   = json?["rms_pixels"]?.GetValue<double>() ?? 0;
            var pixelScale  = summary?["pixel_scale_arcsec"]?.GetValue<double>() ?? 0;

            SessionLogService.Write(
                $"[PlateSolve/StarFix] ✓  {Path.GetFileName(fitsPath)} — {numMatched}/{numDetected} matched, " +
                $"RMS {rmsPixels:F2}px, WCS written.");
            return new Result(true,
                $"StarFix plate solve complete — {numMatched}/{numDetected} matched, RMS {rmsPixels:F2}px.",
                WcsPixelScaleArcsec: pixelScale);
        }
        catch (Exception ex)
        {
            SessionLogService.Write($"[PlateSolve/StarFix] ✗  {Path.GetFileName(fitsPath)} — could not parse output: {ex.Message}");
            return new Result(false, $"StarFix returned unparseable output: {ex.Message}");
        }
    }

    private static async Task<Result> SolveAllWithStarFixAsync(
        string firstFitsPath, SolverConfig config,
        IProgress<string>? progress, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(firstFitsPath);
        if (dir is null || !Directory.Exists(dir))
        {
            SessionLogService.Write($"[PlateSolve/StarFix] ERROR — cannot determine FITS directory from: {firstFitsPath}");
            return new Result(false, "Cannot determine FITS directory.");
        }

        var files = Directory.GetFiles(dir, "*.fits", SearchOption.TopDirectoryOnly)
            .Concat(Directory.GetFiles(dir, "*.fit", SearchOption.TopDirectoryOnly))
            .Concat(Directory.GetFiles(dir, "*.fts", SearchOption.TopDirectoryOnly))
            .Concat(Directory.GetFiles(dir, "*.fz",  SearchOption.TopDirectoryOnly))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0) return new Result(false, "No FITS files found in directory.");

        SessionLogService.Write($"[PlateSolve/StarFix] Starting all-frames solve in: {dir}  ({files.Length} files)");
        int solved = 0, failed = 0;
        string firstSolvedPath = "";
        for (int i = 0; i < files.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"⟳  Solving {i + 1}/{files.Length}…  ({solved} solved, {failed} failed so far)");
            var r = await SolveWithStarFixAsync(files[i], config, null, ct);
            if (r.Success)
            {
                solved++;
                if (firstSolvedPath.Length == 0) firstSolvedPath = files[i];
            }
            else
            {
                failed++;
                SessionLogService.Write($"[PlateSolve/StarFix] ✗  {Path.GetFileName(files[i])} — {r.Message}");
            }
        }

        bool partial = solved > 0 && failed > 0;
        string summary = (solved, failed) switch
        {
            (_, 0) => $"All {solved} frame{(solved == 1 ? "" : "s")} solved — no errors.",
            (0, _) => $"Plate solve failed — 0/{files.Length} solved. EXOTIC cannot run without at least one solved frame.",
            _      => $"{solved} solved, {failed} failed — usable (EXOTIC only needs one solved reference frame), but check the excluded/failed frames.",
        };
        SessionLogService.Write($"[PlateSolve/StarFix] All-frames solve complete — {solved} solved, {failed} failed.");

        return new Result(solved > 0, summary, FirstSolvedPath: firstSolvedPath, IsPartialSuccess: partial);
    }

    /// <summary>
    /// Pulls the last "SomeException: message"-shaped line out of a Python traceback (the real
    /// error, as opposed to PyInstaller's own generic wrapper line that follows it) — falls back
    /// to the last non-empty line if nothing matches that shape.
    /// </summary>
    private static string? ExtractPythonErrorLine(string stderr)
    {
        var lines = stderr.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0) return null;

        for (int i = lines.Count - 1; i >= 0; i--)
        {
            var line = lines[i];
            var colonIdx = line.IndexOf(':');
            if (colonIdx <= 0) continue;
            var head = line[..colonIdx];
            if (!head.Contains(' ') &&
                (head.EndsWith("Error", StringComparison.Ordinal) || head.EndsWith("Exception", StringComparison.Ordinal)))
                return line;
        }
        return lines[^1];
    }

    /// <summary>
    /// Reads a single integer FITS header keyword from the first 5 blocks of a file
    /// without loading the entire file into memory.
    /// </summary>
    private static int? ReadFitsHeaderInt(string fitsPath, string keyword)
    {
        try
        {
            using var fs = File.OpenRead(fitsPath);
            int maxBytes = (int)Math.Min(fs.Length, 2880L * 5);
            var buf  = new byte[maxBytes];
            int read = fs.Read(buf, 0, maxBytes);
            var cards = ParseFitsHeaderCards(buf[..read]);
            var card  = cards.FirstOrDefault(c =>
                c.Keyword.Equals(keyword, StringComparison.OrdinalIgnoreCase));
            if (card is null) return null;
            // FITS value field: characters 10-29 (0-indexed), followed by optional '/' comment
            var raw        = card.Raw80;
            var valueField = (raw.Length >= 30 ? raw[10..30] : raw[Math.Min(10, raw.Length)..])
                             .Split('/')[0].Trim();
            return int.TryParse(valueField, out var n) ? n : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Parses the number of matched quads from ASTAP's console output.
    /// ASTAP prints: "N of N quads selected matching within 0.007 tolerance"
    /// Returns -1 if the line is absent (not all ASTAP versions print it).
    /// </summary>
    private static int ParseAstapQuadCount(string output)
    {
        foreach (var line in output.Split('\n'))
        {
            var t   = line.Trim();
            var idx = t.IndexOf("quads selected matching", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            // The match count is the first token before "of N quads …"
            var tokens = t[..idx].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length >= 1 && int.TryParse(tokens[0], out var n))
                return n;
        }
        return -1;   // line not found
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

    /// <summary>
    /// Derives the pixel scale (arcsec/px) from a WCS FITS byte array.
    /// Tries CDELT1 first (simple WCS), then the CD matrix.
    /// Returns 0 if the scale cannot be determined.
    /// </summary>
    private static double ReadActualPixelScale(byte[] wcsBytes)
    {
        try
        {
            var cards = ParseFitsHeaderCards(wcsBytes);

            // Simple WCS: CDELT1 is degrees/pixel (negative for RA axis)
            var cdelt1Card = cards.FirstOrDefault(c =>
                c.Keyword.Equals("CDELT1", StringComparison.OrdinalIgnoreCase));
            if (cdelt1Card is not null)
            {
                var raw = cdelt1Card.Raw80;
                var valueField = (raw.Length >= 30 ? raw[10..30] : raw[Math.Min(10, raw.Length)..])
                                 .Split('/')[0].Trim();
                if (double.TryParse(valueField,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double d) && d != 0)
                    return Math.Abs(d) * 3600.0; // deg/px → arcsec/px
            }

            // CD matrix WCS: scale = sqrt(CD1_1² + CD1_2²) in deg/px
            var cd11Card = cards.FirstOrDefault(c =>
                c.Keyword.Equals("CD1_1", StringComparison.OrdinalIgnoreCase));
            var cd12Card = cards.FirstOrDefault(c =>
                c.Keyword.Equals("CD1_2", StringComparison.OrdinalIgnoreCase));
            if (cd11Card is not null && cd12Card is not null)
            {
                static double Parse(FitsCard card)
                {
                    var raw = card.Raw80;
                    var vf = (raw.Length >= 30 ? raw[10..30] : raw[Math.Min(10, raw.Length)..])
                             .Split('/')[0].Trim();
                    return double.TryParse(vf,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out double v) ? v : 0;
                }
                double a = Parse(cd11Card), b = Parse(cd12Card);
                if (a != 0 || b != 0)
                    return Math.Sqrt(a * a + b * b) * 3600.0;
            }
        }
        catch { /* best-effort */ }
        return 0;
    }

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
