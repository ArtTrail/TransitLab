using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TransitLab.Services;

public static class AavsoCompService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public record CompResult(List<(int X, int Y)> Pairs, string StatusMessage);

    /// <summary>
    /// Query the AAVSO VSP API for comparison stars near (ra, dec), convert
    /// their sky coordinates to pixel positions via the WCS in fitsPath, and
    /// return up to 10 in-frame pairs.
    /// </summary>
    public static async Task<CompResult> FetchAsync(
        string fitsPath, double ra, double dec,
        int targetPx = 0, int targetPy = 0,
        CancellationToken ct = default)
    {
        // ── Read FITS header for WCS + image size ─────────────────────────
        var hdr = await Task.Run(() => FitsHeaderService.Read(fitsPath), ct);
        if (ct.IsCancellationRequested) return new CompResult([], "");
        var wcs    = WcsService.ReadWcs(hdr);
        if (wcs is null)
            return new CompResult([], "⚠  No plate solution — plate-solve first");

        int naxis1 = hdr.GetInt("NAXIS1") ?? 0;
        int naxis2 = hdr.GetInt("NAXIS2") ?? 0;

        // ── Compute FOV from pixel scale matrix (cap 10–90 arcmin) ────────
        double fovArcmin = 60.0;
        if (naxis1 > 0 && naxis2 > 0)
        {
            double scaleRaDeg  = Math.Sqrt(wcs.Cd11 * wcs.Cd11 + wcs.Cd21 * wcs.Cd21);
            double scaleDecDeg = Math.Sqrt(wcs.Cd12 * wcs.Cd12 + wcs.Cd22 * wcs.Cd22);
            double scaleDeg    = (scaleRaDeg + scaleDecDeg) / 2.0;
            if (scaleDeg > 0)
                fovArcmin = Math.Clamp(Math.Max(naxis1, naxis2) * scaleDeg * 60.0 * 1.2, 10.0, 90.0);
        }

        // ── Query VSP API ─────────────────────────────────────────────────
        if (ct.IsCancellationRequested) return new CompResult([], "");

        var url = $"https://www.aavso.org/apps/vsp/api/chart/" +
                  $"?ra={ra:F6}&dec={dec:F6}&fov={fovArcmin:F1}&maglimit=14.5&format=json";

        string json;
        try
        {
            using var resp = await Http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();
            json = await resp.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // HttpClient.Timeout fired — return a failed result so the retry logic can handle it
            return new CompResult([], "✗  AAVSO request timed out");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new CompResult([], $"✗  AAVSO API error: {ex.Message}");
        }

        // ── Parse photometry list ─────────────────────────────────────────
        List<(double Ra, double Dec)> skyPairs = [];
        try
        {
            using var doc  = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("photometry", out var phot))
                return new CompResult([], "✗  No comparison stars found");

            foreach (var star in phot.EnumerateArray())
            {
                if (!star.TryGetProperty("ra",  out var raEl)  ||
                    !star.TryGetProperty("dec", out var decEl)) continue;
                if (!TryParseCoord(raEl.GetString()  ?? "", out var sRa,  isDec: false)) continue;
                if (!TryParseCoord(decEl.GetString() ?? "", out var sDec, isDec: true))  continue;
                skyPairs.Add((sRa, sDec));
            }
        }
        catch (Exception ex)
        {
            return new CompResult([], $"✗  Parse error: {ex.Message}");
        }

        if (skyPairs.Count == 0)
            return new CompResult([], "✗  No AAVSO comparison stars found for this target");

        // ── Convert RA/Dec → pixel, filter in-frame ───────────────────────
        var pairs = new List<(int X, int Y)>();
        foreach (var (sRa, sDec) in skyPairs)
        {
            var px = WcsService.SkyToPixel(wcs, sRa, sDec);
            if (px is null) continue;
            var (x, y) = px.Value;

            // Must be inside the frame
            if (naxis1 > 0 && naxis2 > 0 &&
                (x < 1 || x > naxis1 || y < 1 || y > naxis2)) continue;

            pairs.Add((x, y));
            if (pairs.Count == 10) break;
        }

        if (pairs.Count == 0)
            return new CompResult([], "✗  No AAVSO comparison stars fall within the image frame");

        return new CompResult(pairs, $"✓  {pairs.Count} AAVSO comparison star{(pairs.Count == 1 ? "" : "s")} loaded");
    }

    private static bool TryParseCoord(string raw, out double value, bool isDec)
    {
        value = 0;
        raw = raw.Trim();
        if (string.IsNullOrEmpty(raw)) return false;

        // Try decimal first
        if (double.TryParse(raw, System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out value))
            return true;

        // Try sexagesimal (HH MM SS.ss or DD MM SS.s, colon or space separated)
        var parts = raw.Replace(':', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3) return false;
        if (!double.TryParse(parts[0], System.Globalization.NumberStyles.Any,
                             System.Globalization.CultureInfo.InvariantCulture, out var d0)) return false;
        if (!double.TryParse(parts[1], System.Globalization.NumberStyles.Any,
                             System.Globalization.CultureInfo.InvariantCulture, out var d1)) return false;
        if (!double.TryParse(parts[2], System.Globalization.NumberStyles.Any,
                             System.Globalization.CultureInfo.InvariantCulture, out var d2)) return false;

        double abs = Math.Abs(d0) + d1 / 60.0 + d2 / 3600.0;
        double sign = (raw.TrimStart()[0] == '-') ? -1 : 1;
        value = isDec ? sign * abs : abs * 15.0;   // RA: hours→degrees
        return true;
    }
}
