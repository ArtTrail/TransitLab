using System;
using System.Collections.Generic;
using System.Linq;

namespace TransitLab.Services;

/// <summary>
/// Measures PSF quality (FWHM, SNR, saturation) from FITS pixel data using
/// weighted 2nd-moment analysis — no external library required.
/// </summary>
public static class PsfService
{
    // ── Public result type ────────────────────────────────────────────────────

    public record PsfResult(
        bool   Success,
        double FwhmX,
        double FwhmY,
        double FwhmMean,
        double PeakAdu,
        double Snr,
        bool   Saturated,
        string Message = "");

    // ── FITS pixel reader ─────────────────────────────────────────────────────

    /// <summary>
    /// Read the primary image plane of a FITS file (uncompressed or Rice/GZIP .fz).
    /// Returns float[row, col] (NAXIS2 × NAXIS1), FITS origin (row 0 = bottom).
    /// Returns null on failure.
    /// </summary>
    public static float[,]? ReadFitsPixels(string path)
    {
        try
        {
            var img = FitsImageService.Load(path);
            if (img.Width == 0 || img.Height == 0) return null;
            if ((long)img.Width * img.Height * 4 > 600_000_000L) return null;  // 600 MB sanity cap

            // float[,] is stored contiguously in row-major order, matching img.Pixels layout.
            var image = new float[img.Height, img.Width];
            Buffer.BlockCopy(img.Pixels, 0, image, 0, img.Pixels.Length * sizeof(float));

            return image;
        }
        catch (Exception ex)
        {
            SessionLogService.Write($"[PSF] FITS pixel read failed: {ex.Message}");
            return null;
        }
    }

    // ── Header-derived detector parameters ────────────────────────────────────

    /// <summary>Estimate saturation level (ADU) from FITS header.</summary>
    public static double EstimateSaturation(FitsHeaderService.FitsHeader hdr)
    {
        var sat = hdr.GetDouble("SATURATE");
        if (sat.HasValue && sat.Value > 0) return sat.Value;

        int    bitpix = hdr.GetInt("BITPIX") ?? 16;
        double bzero  = hdr.GetDouble("BZERO") ?? 0.0;

        return bitpix switch
        {
            16  => (bzero > 0 ? 65535.0 : 32767.0) * 0.90,
            32  => 2_147_483_647.0 * 0.80,
            8   => 255.0 * 0.90,
            -32 => 1e9,
            -64 => 1e9,
            _   => 60_000.0
        };
    }

    /// <summary>
    /// Return detector gain in e-/ADU from EGAIN or GAIN header keywords.
    /// Sanity-checked to 0.1–10 e-/ADU; falls back to 1.0.
    /// </summary>
    public static double GetGain(FitsHeaderService.FitsHeader hdr)
    {
        var egain = hdr.GetDouble("EGAIN");
        if (egain.HasValue && egain.Value is >= 0.1 and <= 10.0) return egain.Value;

        var gain = hdr.GetDouble("GAIN");
        if (gain.HasValue && gain.Value is >= 0.1 and <= 10.0) return gain.Value;

        return 1.0;
    }

    // ── PSF measurement ───────────────────────────────────────────────────────

    /// <summary>
    /// Measure PSF quality for a star centered at (cx, cy) using:
    ///   • Weighted 2nd-moment analysis for FWHM_x and FWHM_y
    ///   • Aperture photometry (2×FWHM radius) for SNR
    ///   • Peak ADU search in a 21×21 box for saturation detection
    ///
    /// Returns a PsfResult; never throws.
    /// </summary>
    public static PsfResult Measure(
        float[,] image, int cx, int cy,
        double saturationAdu, double gainEPerAdu)
    {
        int rows = image.GetLength(0);
        int cols = image.GetLength(1);

        // Need at least 20px clearance from edge for the background annulus
        if (cx < 20 || cy < 20 || cx > cols - 21 || cy > rows - 21)
            return Fail("Near image edge");

        // ── 1. Peak ADU (21×21 search box) + saturation ──────────────────────
        double peakAdu = 0;
        for (int dy = -10; dy <= 10; dy++)
            for (int dx = -10; dx <= 10; dx++)
                peakAdu = Math.Max(peakAdu, image[cy + dy, cx + dx]);

        bool saturated = peakAdu >= saturationAdu;

        // ── 2. Background — median of annulus at 13–18 px ────────────────────
        var skyPixels = new List<float>(250);
        for (int dy = -20; dy <= 20; dy++)
        {
            int ry = cy + dy;
            if (ry < 0 || ry >= rows) continue;
            for (int dx = -20; dx <= 20; dx++)
            {
                double r = Math.Sqrt(dx * dx + dy * dy);
                if (r < 13 || r > 18) continue;
                int rx = cx + dx;
                if (rx < 0 || rx >= cols) continue;
                skyPixels.Add(image[ry, rx]);
            }
        }

        if (skyPixels.Count < 12) return Fail("Insufficient sky pixels");
        skyPixels.Sort();
        double background = skyPixels[skyPixels.Count / 2];

        // ── 3. Weighted 2nd-moment FWHM (10px half-aperture) ─────────────────
        const int half = 10;
        double sumW = 0, sumWdx = 0, sumWdy = 0;

        for (int dy = -half; dy <= half; dy++)
            for (int dx = -half; dx <= half; dx++)
            {
                double w = Math.Max(0.0, image[cy + dy, cx + dx] - background);
                sumW   += w;
                sumWdx += w * dx;
                sumWdy += w * dy;
            }

        if (sumW <= 0) return Fail("No flux above background");

        double xOff = sumWdx / sumW;  // centroid offset from initial (cx,cy)
        double yOff = sumWdy / sumW;

        double sumWxx = 0, sumWyy = 0;
        for (int dy = -half; dy <= half; dy++)
            for (int dx = -half; dx <= half; dx++)
            {
                double w  = Math.Max(0.0, image[cy + dy, cx + dx] - background);
                double ex = dx - xOff;
                double ey = dy - yOff;
                sumWxx += w * ex * ex;
                sumWyy += w * ey * ey;
            }

        double sigmaX   = Math.Sqrt(sumWxx / sumW);
        double sigmaY   = Math.Sqrt(sumWyy / sumW);
        double fwhmX    = 2.355 * sigmaX;
        double fwhmY    = 2.355 * sigmaY;
        double fwhmMean = (fwhmX + fwhmY) / 2.0;

        if (fwhmMean < 0.3 || fwhmMean > 60)
            return Fail($"Unphysical FWHM ({fwhmMean:F1}px)");

        // ── 4. Aperture SNR (2×FWHM aperture radius) ─────────────────────────
        double aperR  = Math.Max(3.0, 2.0 * fwhmMean);
        double aperR2 = aperR * aperR;
        double flux   = 0;
        int    nPix   = 0;
        int    extent = (int)(aperR + 1.5);

        for (int dy = -extent; dy <= extent; dy++)
        {
            int ry = cy + dy;
            if (ry < 0 || ry >= rows) continue;
            for (int dx = -extent; dx <= extent; dx++)
            {
                if (dx * dx + dy * dy > aperR2) continue;
                int rx = cx + dx;
                if (rx < 0 || rx >= cols) continue;
                flux += Math.Max(0.0, image[ry, rx] - background);
                nPix++;
            }
        }

        if (flux <= 0) return Fail("Non-positive aperture flux");

        double gain      = Math.Max(0.1, gainEPerAdu);
        double noiseVar  = flux / gain + nPix * Math.Max(0, background) / gain;
        double snr       = noiseVar > 0 ? flux / Math.Sqrt(noiseVar) : 0;

        return new PsfResult(true, fwhmX, fwhmY, fwhmMean, peakAdu, snr, saturated);
    }

    // ── Field FWHM median (for outlier threshold) ─────────────────────────────

    /// <summary>
    /// Compute the median FWHM from a list of per-star PSF results.
    /// Excludes failures and saturated stars.
    /// </summary>
    public static double MedianFwhm(IEnumerable<PsfResult> results)
    {
        var fwhms = results
            .Where(r => r.Success && !r.Saturated && r.FwhmMean > 0)
            .Select(r => r.FwhmMean)
            .OrderBy(x => x)
            .ToList();

        if (fwhms.Count == 0) return 0;
        int mid = fwhms.Count / 2;
        return fwhms.Count % 2 == 0 ? (fwhms[mid - 1] + fwhms[mid]) / 2.0 : fwhms[mid];
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static PsfResult Fail(string msg) =>
        new(false, 0, 0, 0, 0, 0, false, msg);
}
