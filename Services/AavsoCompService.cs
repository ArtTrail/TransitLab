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
    /// Query the AAVSO VSP API for comparison stars near (ra, dec) that have published
    /// photometry in the requested filter band, convert their sky coordinates to pixel
    /// positions via the WCS in fitsPath, and return up to 10 in-frame pairs. A star with
    /// no chart entry for this band is skipped — the VSP chart is not band-agnostic (a
    /// field's chart is often only populated for a subset of bands, e.g. V but not SR), so
    /// silently accepting any band would return positions the target filter can't actually
    /// calibrate against.
    /// </summary>
    public static async Task<CompResult> FetchAsync(
        string fitsPath, double ra, double dec, string filterCode,
        int targetPx = 0, int targetPy = 0,
        CancellationToken ct = default)
    {
        if (!GaiaCompService.VspFilterMap.TryGetValue(filterCode, out var vspBand))
            return new CompResult([], $"✗  Filter '{filterCode}' has no known AAVSO VSP band mapping");
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

        // FormattableString.Invariant — without it, {ra:F6} etc. format using the OS's current
        // culture, so on a locale with a comma decimal separator (e.g. Bulgarian, Russian,
        // German) the URL would embed "283,306316" instead of "283.306316", producing a
        // malformed request the API rejects with HTTP 400 on every single query.
        // apps.aavso.org, not www.aavso.org — the www host sits behind a Cloudflare bot
        // challenge (a JS-based "Just a moment..." interstitial) that blocks any non-browser
        // HTTP client outright, confirmed directly: even the plain www.aavso.org homepage
        // returns HTTP 403 to curl/.NET's HttpClient alike, regardless of headers. apps.aavso.org
        // is a different host with no such challenge — confirmed serving real VSP JSON data.
        var url = FormattableString.Invariant(
            $"https://apps.aavso.org/vsp/api/chart/?ra={ra:F6}&dec={dec:F6}&fov={fovArcmin:F1}&maglimit=14.5&format=json");

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

        // ── Parse photometry list — only keep stars with a chart entry in the
        //    requested band (matches GaiaCompService.QueryVspAsync's band matching) ──
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

                bool hasBandMag = false;
                if (star.TryGetProperty("bands", out var bands))
                {
                    foreach (var b in bands.EnumerateArray())
                    {
                        if (!b.TryGetProperty("band", out var bandEl)) continue;
                        if (bandEl.GetString() != vspBand) continue;
                        if (!b.TryGetProperty("mag", out var magEl)) continue;
                        var ms = magEl.ValueKind == JsonValueKind.String
                            ? magEl.GetString() ?? "" : magEl.GetRawText();
                        if (NumericParseService.TryParse(ms, out _))
                            hasBandMag = true;
                        break;
                    }
                }
                if (!hasBandMag) continue;

                skyPairs.Add((sRa, sDec));
            }
        }
        catch (Exception ex)
        {
            return new CompResult([], $"✗  Parse error: {ex.Message}");
        }

        if (skyPairs.Count == 0)
            return new CompResult([], $"✗  No AAVSO comparison stars with {vspBand}-band photometry found for this target");

        // ── Convert RA/Dec → pixel, filter in-frame ───────────────────────
        var inFrame = new List<(int X, int Y)>();
        foreach (var (sRa, sDec) in skyPairs)
        {
            var px = WcsService.SkyToPixel(wcs, sRa, sDec);
            if (px is null) continue;
            var (x, y) = px.Value;

            // Must be inside the frame
            if (naxis1 > 0 && naxis2 > 0 &&
                (x < 1 || x > naxis1 || y < 1 || y > naxis2)) continue;

            inFrame.Add((x, y));
        }

        if (inFrame.Count == 0)
            return new CompResult([], "✗  No AAVSO comparison stars fall within the image frame");

        // ── Sanity-check each candidate against the actual pixel data before
        //    accepting it. The VSP chart only guarantees a catalog entry exists at
        //    this RA/Dec — it says nothing about whether that star is actually
        //    detectable in THIS image (faint target, shallow exposure, crowded
        //    field). Unlike the Stone pipeline, this method previously had no such
        //    check at all, so a chart position landing on blank sky was silently
        //    accepted as a valid comparison star, sometimes for every candidate at
        //    once — producing photometry with no real reference signal and no
        //    indication anything was wrong.
        const double MinSnr = 5.0;   // "is there a real point source here at all", not a precision bar
        var imageData = await Task.Run(() => PsfService.ReadFitsPixels(fitsPath), ct);
        var pairs      = new List<(int X, int Y)>();
        int droppedNoSignal = 0;

        if (imageData is null)
        {
            // Can't validate — fall back to the old behaviour rather than blocking entirely.
            pairs = inFrame.Take(10).ToList();
        }
        else
        {
            double saturationAdu = PsfService.EstimateSaturation(hdr);
            double gainEPerAdu   = PsfService.GetGain(hdr);
            foreach (var (x, y) in inFrame)
            {
                var psf = PsfService.Measure(imageData, x, y, saturationAdu, gainEPerAdu);
                if (psf.Success && !psf.Saturated && psf.Snr >= MinSnr)
                    pairs.Add((x, y));
                else
                    droppedNoSignal++;
                if (pairs.Count == 10) break;
            }
        }

        if (pairs.Count == 0)
            return new CompResult([],
                $"⚠  {inFrame.Count} AAVSO candidate position{(inFrame.Count == 1 ? "" : "s")} found, but none showed a detectable star in this image — this field may be too faint for reliable photometry with this exposure. Try Stone or VSP + Stone instead.");

        string note = droppedNoSignal > 0
            ? $"  ⚠  {droppedNoSignal} candidate{(droppedNoSignal == 1 ? "" : "s")} dropped (no detectable signal)"
            : "";
        return new CompResult(pairs, $"✓  {pairs.Count} AAVSO comparison star{(pairs.Count == 1 ? "" : "s")} loaded ({vspBand} band){note}");
    }

    private static bool TryParseCoord(string raw, out double value, bool isDec)
    {
        value = 0;
        raw = raw.Trim();
        if (string.IsNullOrEmpty(raw)) return false;

        // Try decimal first
        if (NumericParseService.TryParse(raw, out value))
            return true;

        // Try sexagesimal (HH MM SS.ss or DD MM SS.s, colon or space separated)
        var parts = raw.Replace(':', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3) return false;
        if (!NumericParseService.TryParse(parts[0], out var d0)) return false;
        if (!NumericParseService.TryParse(parts[1], out var d1)) return false;
        if (!NumericParseService.TryParse(parts[2], out var d2)) return false;

        double abs = Math.Abs(d0) + d1 / 60.0 + d2 / 3600.0;
        double sign = (raw.TrimStart()[0] == '-') ? -1 : 1;
        value = isDec ? sign * abs : abs * 15.0;   // RA: hours→degrees
        return true;
    }
}
