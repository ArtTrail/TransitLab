using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TransitLab.Services;

/// <summary>
/// Parses a TYPE=EXOPLANET AAVSO report — the "AAVSO_&lt;planet&gt;_&lt;date&gt;.txt" file EXOTIC's
/// <c>OutputFiles.aavso()</c> writes for Exoplanet Watch submission (distinct from the plain
/// magnitude-based "AID_AAVSO_*.txt" AAVSO-database file). This file already embeds the exact
/// orbital priors and fitted results used to produce it (#PRIORS-XC / #RESULTS-XC), so a rerun via
/// EXOTIC's -pre (--prereduced) mode can reproduce the original transit solution without a fresh
/// NASA Exoplanet Archive query — only stellar parameters (Teff/logg/FeH, needed solely for
/// EXOTIC's own limb-darkening recompute) still require an NEA lookup.
/// </summary>
public static class AavsoReportImportService
{
    public record AavsoReportData(
        string PlanetName, string HostStarName,
        string ObsCode, string SecondaryObsCodes, string Camera, string Binning, string Notes,
        string Filter, string? FilterDesc, double? WlMin, double? WlMax,
        double? Exposure,
        string? CompStarRa, string? CompStarDec, string? CompStarX, string? CompStarY,
        double Period, double? PeriodUnc,
        double RpRs, double? RpRsUnc,
        double ARs, double? ARsUnc,
        double Inc, double? IncUnc,
        double Ecc,
        double Tc, double? TcUnc,
        string FileTimeFormat, string FileUnits);

    private static readonly Regex HeaderLine = new(@"^#([A-Za-z0-9_/\-]+)=(.*)$", RegexOptions.Compiled);

    public static AavsoReportData Parse(string path)
    {
        var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(path))
        {
            if (!line.StartsWith('#')) continue;
            var m = HeaderLine.Match(line);
            if (m.Success) kv[m.Groups[1].Value] = m.Groups[2].Value;
        }

        if (!kv.TryGetValue("TYPE", out var type) || !type.Equals("EXOPLANET", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "This doesn't look like an EXOTIC Exoplanet Watch AAVSO report (missing #TYPE=EXOPLANET). " +
                "Note: the plain \"AID_AAVSO_*.txt\" file (for AAVSO's own database) is a different, incompatible format — " +
                "use the \"AAVSO_<planet>_<date>.txt\" file instead.");

        if (!kv.TryGetValue("PRIORS-XC", out var priorsJson) || !kv.TryGetValue("RESULTS-XC", out var resultsJson))
            throw new InvalidDataException(
                "This AAVSO report doesn't contain the #PRIORS-XC / #RESULTS-XC metadata needed to recover the " +
                "original transit solution (older EXOTIC versions didn't write it). Re-import isn't possible from this file.");

        var priors  = JsonNode.Parse(priorsJson)  as JsonObject ?? throw new InvalidDataException("Malformed #PRIORS-XC block.");
        var results = JsonNode.Parse(resultsJson) as JsonObject ?? throw new InvalidDataException("Malformed #RESULTS-XC block.");

        JsonObject? filterXc = kv.TryGetValue("FILTER-XC", out var filterJson) ? JsonNode.Parse(filterJson) as JsonObject : null;
        JsonObject? compXc   = kv.TryGetValue("COMP_STAR-XC", out var compJson) ? JsonNode.Parse(compJson) as JsonObject : null;

        double PriorValue(string key) =>
            NumericParseService.TryParse(priors[key]?["value"]?.GetValue<string>(), out var v)
                ? v : throw new InvalidDataException($"#PRIORS-XC is missing a usable \"{key}\" value.");

        double? PriorUnc(string key) =>
            NumericParseService.TryParse(priors[key]?["uncertainty"]?.GetValue<string>(), out var v)
                ? v : null;

        double ResultValue(string key) =>
            NumericParseService.TryParse(results[key]?["value"]?.GetValue<string>(), out var v)
                ? v : throw new InvalidDataException($"#RESULTS-XC is missing a usable \"{key}\" value.");

        double? ResultUnc(string key) =>
            NumericParseService.TryParse(results[key]?["uncertainty"]?.GetValue<string>(), out var v)
                ? v : null;

        double? WlAt(int index) =>
            filterXc?["fwhm"]?[index]?["value"] is JsonNode n &&
            NumericParseService.TryParse(n.GetValue<string>(), out var v)
                ? v : null;

        string? CompField(string key) => compXc?[key]?.GetValue<string>();

        return new AavsoReportData(
            PlanetName:        kv.GetValueOrDefault("EXOPLANET_NAME", ""),
            HostStarName:      kv.GetValueOrDefault("STAR_NAME", ""),
            ObsCode:           kv.GetValueOrDefault("OBSCODE", ""),
            SecondaryObsCodes: kv.GetValueOrDefault("SECONDARY_OBSCODES", ""),
            Camera:            kv.GetValueOrDefault("OBSTYPE", ""),
            Binning:           kv.GetValueOrDefault("BINNING", ""),
            Notes:             kv.GetValueOrDefault("NOTES", ""),
            Filter:            kv.GetValueOrDefault("FILTER", ""),
            FilterDesc:        filterXc?["desc"]?.GetValue<string>(),
            WlMin:             WlAt(0),
            WlMax:             WlAt(1),
            Exposure:          NumericParseService.TryParse(kv.GetValueOrDefault("EXPOSURE_TIME", ""), out var exp) ? exp : null,
            CompStarRa:        CompField("ra"),
            CompStarDec:       CompField("dec"),
            CompStarX:         CompField("x"),
            CompStarY:         CompField("y"),
            Period:            PriorValue("Period"),  PeriodUnc: PriorUnc("Period"),
            RpRs:              PriorValue("Rp/R*"),   RpRsUnc:   PriorUnc("Rp/R*"),
            ARs:               PriorValue("a/R*"),    ARsUnc:    PriorUnc("a/R*"),
            Inc:               PriorValue("inc"),     IncUnc:    PriorUnc("inc"),
            Ecc:               PriorValue("ecc"),
            Tc:                ResultValue("Tc"),      TcUnc:     ResultUnc("Tc"),
            FileTimeFormat:    kv.GetValueOrDefault("DATE_TYPE", "BJD_TDB"),
            FileUnits:         "flux");  // EXOTIC always writes MEASUREMENT_TYPE=Rnflux (normalized flux) for this file
    }
}
