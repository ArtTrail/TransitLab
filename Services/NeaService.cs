using System;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace TransitLab.Services;

/// <summary>
/// Queries the NASA Exoplanet Archive TAP service using the same two-pass strategy
/// as EXOTIC: the most recent publication from the per-publication table (ps) is used
/// as the primary source, with earlier publications scanned for any remaining null fields.
/// Hardcoded and calculated fallbacks (omega=0, inc=90, ecc=0; Rp/Rs from transit depth
/// or radii; a/Rs from Kepler's third law) match EXOTIC's internal logic.
/// </summary>
public static class NeaService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private const string TapBase = "https://exoplanetarchive.ipac.caltech.edu/TAP/sync";

    // Physical constants for derived calculations
    private const double RjupM  = 71_492_000.0;   // Jupiter radius in metres
    private const double RsunM  = 695_700_000.0;  // Solar radius in metres
    private const double MsunKg = 1.989e30;        // Solar mass in kg
    private const double G      = 6.674e-11;       // Gravitational constant (SI)

    public class PlanetData
    {
        public string PlanetName        { get; init; } = "";
        public string HostStarName      { get; init; } = "";
        public string Ra                { get; init; } = "";
        public string Dec               { get; init; } = "";
        public string OrbitalPeriod     { get; init; } = "";
        public string OrbitalPeriodUnc  { get; init; } = "";
        public string MidTransitTime    { get; init; } = "";
        public string MidTransitTimeUnc { get; init; } = "";
        public string RpRs              { get; init; } = "";
        public string RpRsUnc           { get; init; } = "";
        public string RpRsWarning       { get; init; } = "";
        public string DefaultsApplied   { get; init; } = "";
        public string ARs               { get; init; } = "";
        public string ARsUnc            { get; init; } = "";
        public string Inclination       { get; init; } = "";
        public string InclinationUnc    { get; init; } = "";
        public string Eccentricity      { get; init; } = "";
        public string ArgPeriastron     { get; init; } = "";
        public string Teff              { get; init; } = "";
        public string TeffPlus          { get; init; } = "";
        public string TeffMinus         { get; init; } = "";
        public string Metallicity       { get; init; } = "";
        public string MetallicityPlus   { get; init; } = "";
        public string MetallicityMinus  { get; init; } = "";
        public string Logg              { get; init; } = "";
        public string LoggPlus          { get; init; } = "";
        public string LoggMinus         { get; init; } = "";
        public string StarDistance      { get; init; } = "";
        public string PmRa              { get; init; } = "";
        public string PmDec             { get; init; } = "";
        public bool   FromNextAstroCache { get; init; }
    }

    /// <summary>
    /// Resolve the canonical casing of a planet name, then fetch all parameters from the
    /// live NASA Exoplanet Archive TAP service. If that's unreachable (outage, timeout,
    /// DNS/connection failure), falls back to NextAstro's cached mirror of NEA parameters —
    /// the same fallback EXOTIC's own pre-release code (NASAExoplanetArchive._load_params_from_nextastro_cache
    /// in nea.py) already uses internally. Returns null only if the planet genuinely isn't
    /// found in either source.
    /// </summary>
    public static async Task<PlanetData?> FetchAsync(string planetName, CancellationToken ct = default)
    {
        try
        {
            var canonical = await ResolveCanonicalNameAsync(planetName, ct);
            if (canonical is null) return null;
            return await FetchParametersAsync(canonical, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && IsConnectivityFailure(ex))
        {
            var fallback = await FetchFromNextAstroCacheAsync(planetName, ct);
            if (fallback is not null) return fallback;
            throw;
        }
    }

    private static bool IsConnectivityFailure(Exception ex) =>
        ex is HttpRequestException ||
        ex is System.Net.Sockets.SocketException ||
        (ex is TaskCanceledException tce && tce.InnerException is TimeoutException);

    /// <summary>
    /// NextAstro's cached mirror of NASA Exoplanet Archive parameters — same endpoint and
    /// field mapping as EXOTIC's own nea.py fallback. Used only when the live NASA TAP
    /// service is unreachable; NextAstro's cache doesn't get the elaborate Rp/Rs
    /// cross-validation the primary path applies, matching EXOTIC's own fallback (which
    /// also skips it).
    /// </summary>
    private const string NextAstroNeaUrl = "https://archive.nextastro.org/api/exoplanet_params";

    private static async Task<PlanetData?> FetchFromNextAstroCacheAsync(string planetName, CancellationToken ct)
    {
        string json;
        try
        {
            json = await Http.GetStringAsync(
                $"{NextAstroNeaUrl}?name={Uri.EscapeDataString(planetName)}", ct);
        }
        catch { return null; }

        var root = JsonNode.Parse(json);
        if (root?["params"] is not JsonObject p) return null;

        (string val, string errPlus) ValueAndPlus(string key) =>
            p[key] is JsonObject obj ? (F(obj["value"]), F(obj["errPlus"])) : (F(p[key]), "");

        string NegErr(JsonNode? holder, string errKey)
        {
            var raw = holder is JsonObject obj ? F(obj[errKey]) : "";
            return !string.IsNullOrEmpty(raw) && NumericParseService.TryParse(raw, out double d)
                ? Fmt(-Math.Abs(d)) : "";
        }

        var (period, periodUnc) = ValueAndPlus("orbitalPeriodDays");
        var (midt, midtUnc)     = ValueAndPlus("midTransitTimeDays");
        var (rprs, rprsUnc)     = ValueAndPlus("rpOverRs");
        var (ars, arsUnc)       = ValueAndPlus("aOverRs");
        var (incl, inclUnc)     = ValueAndPlus("inclinationDeg");
        var (teff, teffPlus)    = ValueAndPlus("starTeffK");
        var (feh, fehPlus)      = ValueAndPlus("starFeh");
        var (logg, loggPlus)    = ValueAndPlus("starLogg");

        var name = S(p["name"]);

        return new PlanetData
        {
            PlanetName        = string.IsNullOrEmpty(name) ? planetName : name,
            HostStarName      = S(p["hostStarName"]),
            Ra                = F(p["raDeg"]),
            Dec               = F(p["decDeg"]),
            OrbitalPeriod     = period,
            OrbitalPeriodUnc  = periodUnc,
            MidTransitTime    = midt,
            MidTransitTimeUnc = midtUnc,
            RpRs              = rprs,
            RpRsUnc           = rprsUnc,
            ARs               = ars,
            ARsUnc            = string.IsNullOrEmpty(arsUnc) ? "0.1" : arsUnc,
            Inclination       = string.IsNullOrEmpty(incl) ? "90" : incl,
            InclinationUnc    = inclUnc,
            Eccentricity      = string.IsNullOrEmpty(F(p["eccentricity"])) ? "0" : F(p["eccentricity"]),
            ArgPeriastron     = string.IsNullOrEmpty(F(p["argPeriastronDeg"])) ? "0" : F(p["argPeriastronDeg"]),
            Teff              = teff,
            TeffPlus          = teffPlus,
            TeffMinus         = NegErr(p["starTeffK"], "errMinus"),
            Metallicity       = string.IsNullOrEmpty(feh) ? "0" : feh,
            MetallicityPlus   = string.IsNullOrEmpty(feh) ? "0.1" : fehPlus,
            MetallicityMinus  = string.IsNullOrEmpty(feh) ? "-0.1" : NegErr(p["starFeh"], "errMinus"),
            Logg              = logg,
            LoggPlus          = loggPlus,
            LoggMinus         = NegErr(p["starLogg"], "errMinus"),
            StarDistance      = "",
            PmRa              = "",
            PmDec             = "",
            FromNextAstroCache = true,
        };
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static async Task<string?> ResolveCanonicalNameAsync(string name, CancellationToken ct)
    {
        var safe  = name.Replace("'", "''");
        var query = $"SELECT pl_name FROM pscomppars WHERE UPPER(pl_name)=UPPER('{safe}')";
        var url   = $"{TapBase}?query={Uri.EscapeDataString(query)}&format=json";

        var json = await Http.GetStringAsync(url, ct);
        var arr  = JsonNode.Parse(json) as JsonArray;
        if (arr is null || arr.Count == 0) return null;
        return arr[0]?["pl_name"]?.GetValue<string>();
    }

    private static async Task<PlanetData?> FetchParametersAsync(string canonical, CancellationToken ct)
    {
        // Columns matching EXOTIC's NASAExoplanetArchive._new_scrape() selection,
        // plus additional columns needed for derived fallback calculations.
        const string cols =
            "pl_name,hostname,ra,dec," +
            "pl_orbper,pl_orbpererr1,pl_orbpererr2," +
            "pl_tranmid,pl_tranmiderr1,pl_tranmiderr2," +
            "pl_ratror,pl_ratrorerr1,pl_ratrorerr2," +
            "pl_trandep,pl_trandeperr1," +             // transit depth (ppm) — Rp/Rs fallback
            "pl_radj,pl_radjerr1,pl_radjerr2," +       // planet radius (Rj) — Rp/Rs calculation
            "pl_ratdor,pl_ratdorerr1,pl_ratdorerr2," +
            "pl_orbincl,pl_orbinclerr1,pl_orbinclerr2," +
            "pl_orbeccen,pl_orblper," +
            "st_teff,st_tefferr1,st_tefferr2," +
            "st_met,st_meterr1,st_meterr2," +
            "st_logg,st_loggerr1,st_loggerr2," +
            "st_mass,st_masserr1," +                   // stellar mass (Msun) — a/Rs Kepler fallback
            "st_rad,st_raderr1,st_raderr2," +          // stellar radius (Rsun) — Rp/Rs + a/Rs
            "sy_dist,sy_pmra,sy_pmdec," +
            "pl_pubdate";

        var safe = canonical.Replace("'", "''");

        // ── Pass 1: default_flag=1 — curator's reference row ─────────────────────
        // EXOTIC also fetches this row. We keep it as a last-resort fallback if
        // the tran_flag query returns nothing.
        var q1    = $"SELECT {cols} FROM ps WHERE pl_name='{safe}' AND default_flag=1";
        var url1  = $"{TapBase}?query={Uri.EscapeDataString(q1)}&format=json";
        var json1 = await Http.GetStringAsync(url1, ct);
        var defArr = JsonNode.Parse(json1) as JsonArray;
        var def   = defArr is { Count: > 0 } ? defArr[0] : null;

        // ── Pass 2: all transiting publications, newest first ─────────────────────
        // Matches EXOTIC's extra query: ps with tran_flag=1, ordered by pl_pubdate DESC.
        // EXOTIC uses extra.iloc[0] (most recent row) as its working base, then fills
        // NaN fields by scanning the remaining rows — we replicate that logic here.
        var q2    = $"SELECT {cols} FROM ps WHERE pl_name='{safe}' AND tran_flag=1 ORDER BY pl_pubdate DESC";
        var url2  = $"{TapBase}?query={Uri.EscapeDataString(q2)}&format=json";
        var json2 = await Http.GetStringAsync(url2, ct);
        var extra = JsonNode.Parse(json2) as JsonArray;

        // Base = most recent transiting publication; fall back to default_flag row.
        JsonNode? baseRow = (extra is { Count: > 0 }) ? extra[0] : def;
        if (baseRow is null) return null;

        // Pick: try base row first, then scan extra rows in order, then default row.
        string Pick(string col)
        {
            var v = F(baseRow[col]);
            if (!string.IsNullOrEmpty(v)) return v;
            if (extra is not null)
                foreach (var row in extra) { v = F(row?[col]); if (!string.IsNullOrEmpty(v)) return v; }
            return F(def?[col]);
        }
        string PickS(string col)
        {
            var v = S(baseRow[col]);
            if (!string.IsNullOrEmpty(v)) return v;
            if (extra is not null)
                foreach (var row in extra) { v = S(row?[col]); if (!string.IsNullOrEmpty(v)) return v; }
            return S(def?[col]);
        }

        // ── Rp/Rs ─────────────────────────────────────────────────────────────────
        // Preference order (matches EXOTIC):
        //   1. pl_ratror  (direct Rp/Rs ratio)
        //   2. sqrt(pl_trandep [ppm] / 1e6)
        //   3. pl_radj * R_Jup / (st_rad * R_Sun)
        var rprs    = Pick("pl_ratror");
        var rprsUnc = Pick("pl_ratrorerr1");
        if (string.IsNullOrEmpty(rprs))
        {
            var dep = Pick("pl_trandep");
            if (!string.IsNullOrEmpty(dep) &&
                NumericParseService.TryParse(dep, out double depVal) && depVal > 0)
            {
                rprs = Fmt(Math.Sqrt(depVal / 1e6));
                var depUnc = Pick("pl_trandeperr1");
                if (!string.IsNullOrEmpty(depUnc) &&
                    NumericParseService.TryParse(depUnc, out double depUncVal) && depUncVal > 0)
                    rprsUnc = Fmt(0.5 * (depUncVal / 1e6) / (depVal / 1e6) * double.Parse(rprs,
                                  System.Globalization.CultureInfo.InvariantCulture));
            }
            if (string.IsNullOrEmpty(rprs))
            {
                var radj  = Pick("pl_radj");
                var strad = Pick("st_rad");
                if (!string.IsNullOrEmpty(radj) && !string.IsNullOrEmpty(strad) &&
                    NumericParseService.TryParse(radj, out double radjVal) &&
                    NumericParseService.TryParse(strad, out double stradVal) &&
                    stradVal > 0)
                    rprs = Fmt(radjVal * RjupM / (stradVal * RsunM));
            }
        }

        // ── Rp/Rs cross-validation ────────────────────────────────────────────────
        // If pl_ratror was present but the parsed value is outside the physically
        // plausible range for a transiting exoplanet (0.01–0.35), attempt to derive
        // a better value from transit depth or planet/star radii and auto-substitute.
        string rprsWarning    = "";
        bool   rprsUncEst     = false;   // true when unc was defaulted to 10%
        if (!string.IsNullOrEmpty(rprs) &&
            NumericParseService.TryParse(rprs, out double rprsVal) &&
            (rprsVal < 0.01 || rprsVal > 0.35))
        {
            string? alt        = null;
            string  altSource  = "";
            double  altDepVal  = 0, altRadjVal = 0, altStradVal = 0;

            // Alt 1: sqrt(pl_trandep [ppm] / 1e6)
            var dep = Pick("pl_trandep");
            if (!string.IsNullOrEmpty(dep) &&
                NumericParseService.TryParse(dep, out altDepVal) && altDepVal > 0)
            {
                double candidate = Math.Sqrt(altDepVal / 1e6);
                if (candidate >= 0.01 && candidate <= 0.35)
                { alt = Fmt(candidate); altSource = "transit depth"; }
            }

            // Alt 2: pl_radj * R_Jup / (st_rad * R_Sun)
            if (alt is null)
            {
                var radj  = Pick("pl_radj");
                var strad = Pick("st_rad");
                if (!string.IsNullOrEmpty(radj) && !string.IsNullOrEmpty(strad) &&
                    NumericParseService.TryParse(radj, out altRadjVal) &&
                    NumericParseService.TryParse(strad, out altStradVal) &&
                    altStradVal > 0)
                {
                    double candidate = altRadjVal * RjupM / (altStradVal * RsunM);
                    if (candidate >= 0.01 && candidate <= 0.35)
                    { alt = Fmt(candidate); altSource = "planet/star radii"; }
                }
            }

            if (alt is not null)
            {
                rprsWarning = $"⚠  Rp/Rs = {rprsVal:G6} from NEA (suspicious) — replaced with {alt} (from {altSource})";
                rprs    = alt;
                rprsUnc = "";

                // Compute uncertainty for the substituted value
                if (altSource == "transit depth" && altDepVal > 0)
                {
                    var depUncStr = Pick("pl_trandeperr1");
                    if (!string.IsNullOrEmpty(depUncStr) &&
                        NumericParseService.TryParse(depUncStr, out double depUnc) && depUnc > 0 &&
                        NumericParseService.TryParse(alt, out double altRpRsD))
                        rprsUnc = Fmt(0.5 * (depUnc / 1e6) / (altDepVal / 1e6) * altRpRsD);
                }
                else if (altSource == "planet/star radii" && altRadjVal > 0 && altStradVal > 0)
                {
                    var radjErrStr  = Pick("pl_radjerr1");
                    var stradErrStr = Pick("st_raderr1");
                    if (!string.IsNullOrEmpty(radjErrStr) && !string.IsNullOrEmpty(stradErrStr) &&
                        NumericParseService.TryParse(radjErrStr, out double radjErr) &&
                        NumericParseService.TryParse(stradErrStr, out double stradErr) &&
                        NumericParseService.TryParse(alt, out double altRpRsR))
                        rprsUnc = Fmt(altRpRsR * Math.Sqrt(
                            Math.Pow(Math.Abs(radjErr) / altRadjVal, 2) +
                            Math.Pow(Math.Abs(stradErr) / altStradVal, 2)));
                }
                // Last resort: estimate at 10% of substituted value
                if (string.IsNullOrEmpty(rprsUnc) &&
                    NumericParseService.TryParse(alt, out double altRpRsF))
                { rprsUnc = Fmt(altRpRsF * 0.10); rprsUncEst = true; }
            }
            else
            {
                rprsWarning = $"⚠  Rp/Rs = {rprsVal:G6} from NEA is outside 0.01–0.35 — please verify before running";
            }
        }

        // ── a/Rs ──────────────────────────────────────────────────────────────────
        // Preference order (matches EXOTIC):
        //   1. pl_ratdor  (direct a/Rs)
        //   2. Kepler's 3rd law: a = (G * M_star * P^2 / 4pi^2)^(1/3)  →  a/Rs = a / (st_rad * R_Sun)
        var ars    = Pick("pl_ratdor");
        var arsUnc = Pick("pl_ratdorerr1");
        if (string.IsNullOrEmpty(ars))
        {
            var per   = Pick("pl_orbper");
            var stmas = Pick("st_mass");
            var strad = Pick("st_rad");
            if (!string.IsNullOrEmpty(per) && !string.IsNullOrEmpty(stmas) && !string.IsNullOrEmpty(strad) &&
                NumericParseService.TryParse(per, out double perDays) &&
                NumericParseService.TryParse(stmas, out double massSun) &&
                NumericParseService.TryParse(strad, out double radSun) &&
                massSun > 0 && radSun > 0)
            {
                double perSec  = perDays * 86400.0;
                double aMetres = Math.Pow(G * massSun * MsunKg * perSec * perSec
                                          / (4.0 * Math.PI * Math.PI), 1.0 / 3.0);
                ars = Fmt(aMetres / (radSun * RsunM));
            }
        }

        // ── Parameter defaults — fill blanks and build user notification ─────────
        var defaultsMsg = new System.Text.StringBuilder();

        // Rp/Rs substitution note
        if (!string.IsNullOrEmpty(rprsWarning) && rprsWarning.Contains("replaced"))
        {
            defaultsMsg.AppendLine("• Rp/Rs: value from NEA was outside the expected range (0.01–0.35) and has been recalculated from available data. See the NEA status line for the original and replacement values.");
            if (rprsUncEst)
                defaultsMsg.AppendLine($"• Rp/Rs Uncertainty: no source data available — estimated at 10% of the recalculated Rp/Rs ({rprsUnc}). Replace with a published value if possible.");
            else if (!string.IsNullOrEmpty(rprsUnc))
                defaultsMsg.AppendLine($"• Rp/Rs Uncertainty: propagated from source data ({rprsUnc}).");
        }

        // a/Rs uncertainty
        if (string.IsNullOrEmpty(arsUnc))
        {
            arsUnc = "0.1";
            defaultsMsg.AppendLine("• a/Rs Uncertainty: not reported in NEA — set to 0.1 as a conservative placeholder.");
        }

        // Metallicity
        var met      = Pick("st_met");
        var metPlus  = Pick("st_meterr1");
        var metMinus = Pick("st_meterr2");
        if (string.IsNullOrEmpty(met))
        {
            met      = "0";
            metPlus  = "0.1";
            metMinus = "-0.1";
            defaultsMsg.AppendLine("• Star Metallicity [Fe/H]: not reported in NEA — set to 0.0 ± 0.1 (solar). Edit if a published value is known.");
        }

        var defaultsApplied = defaultsMsg.Length > 0 ? defaultsMsg.ToString().TrimEnd() : "";

        // ── Hardcoded fallbacks matching EXOTIC's defaults ────────────────────────
        var inc   = Pick("pl_orbincl");
        if (string.IsNullOrEmpty(inc))   inc   = "90";

        var ecc   = Pick("pl_orbeccen");
        if (string.IsNullOrEmpty(ecc))   ecc   = "0";

        var omega = Pick("pl_orblper");
        if (string.IsNullOrEmpty(omega)) omega = "0";

        return new PlanetData
        {
            PlanetName        = canonical,
            HostStarName      = PickS("hostname"),
            Ra                = Pick("ra"),
            Dec               = Pick("dec"),
            OrbitalPeriod     = Pick("pl_orbper"),
            OrbitalPeriodUnc  = Pick("pl_orbpererr1"),
            MidTransitTime    = Pick("pl_tranmid"),
            MidTransitTimeUnc = Pick("pl_tranmiderr1"),
            RpRs              = rprs,
            RpRsUnc           = rprsUnc,
            RpRsWarning       = rprsWarning,
            DefaultsApplied   = defaultsApplied,
            ARs               = ars,
            ARsUnc            = arsUnc,
            Inclination       = inc,
            InclinationUnc    = Pick("pl_orbinclerr1"),
            Eccentricity      = ecc,
            ArgPeriastron     = omega,
            Teff              = Pick("st_teff"),
            TeffPlus          = Pick("st_tefferr1"),
            TeffMinus         = Pick("st_tefferr2"),
            Metallicity       = met,
            MetallicityPlus   = metPlus,
            MetallicityMinus  = metMinus,
            Logg              = Pick("st_logg"),
            LoggPlus          = Pick("st_loggerr1"),
            LoggMinus         = Pick("st_loggerr2"),
            StarDistance      = Pick("sy_dist"),
            PmRa              = Pick("sy_pmra"),
            PmDec             = Pick("sy_pmdec"),
        };
    }

    // ── Formatting helpers ────────────────────────────────────────────────────

    /// <summary>String field — return "" for null/whitespace.</summary>
    private static string S(JsonNode? n)
    {
        if (n is null) return "";
        var s = n.GetValue<string?>() ?? "";
        return s.Trim();
    }

    /// <summary>Numeric field — format to reasonable precision, return "" for null/NaN.</summary>
    private static string F(JsonNode? n)
    {
        if (n is null) return "";
        try
        {
            var d = n.GetValue<double>();
            if (double.IsNaN(d) || double.IsInfinity(d)) return "";
            return d.ToString("G10", System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            var s = n.ToString().Trim();
            return NumericParseService.TryParse(s, out _)
                   ? s : "";
        }
    }

    /// <summary>Format a computed double value.</summary>
    private static string Fmt(double d) =>
        double.IsNaN(d) || double.IsInfinity(d)
            ? ""
            : d.ToString("G10", System.Globalization.CultureInfo.InvariantCulture);
}
