using System;
using System.Text.Json.Serialization;

namespace TransitLab.Models;

public class HistoryEntry
{
    public string   Planet    { get; set; } = "";
    public string   Obs       { get; set; } = "";
    public string   ObsDate   { get; set; } = "";
    public DateTime Submitted { get; set; } = DateTime.Today;
    public string   Tmid      { get; set; } = "";
    public string   TmidUnc   { get; set; } = "";
    public string   RpRs      { get; set; } = "";
    public string   RpRsUnc   { get; set; } = "";
    public string   Snr       { get; set; } = "";
    public string   Depth     { get; set; } = "";
    public string   Inc       { get; set; } = "";
    public string   Duration  { get; set; } = "";
    public string   Scatter   { get; set; } = "";

    [JsonIgnore]
    public string SubmittedStr =>
        Submitted == default ? "" : Submitted.ToString("dd-MMM-yyyy").ToUpper();

    // ── Sort keys (typed, for correct DataGrid column sorting) ────────────────
    [JsonIgnore]
    public DateTime ObsDateSort
    {
        get
        {
            if (DateTime.TryParseExact(ObsDate, "dd-MMM-yyyy",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var dt))
                return dt;
            return DateTime.MinValue;
        }
    }

    [JsonIgnore] public double TmidSort      => double.TryParse(Tmid,    out var v) ? v : 0;
    [JsonIgnore] public double TmidUncSort   => double.TryParse(TmidUnc, out var v) ? v : 0;
    [JsonIgnore] public double RpRsSort      => double.TryParse(RpRs,    out var v) ? v : 0;
    [JsonIgnore] public double RpRsUncSort   => double.TryParse(RpRsUnc, out var v) ? v : 0;
    [JsonIgnore] public double SnrSort       => double.TryParse(Snr,     out var v) ? v : 0;
    [JsonIgnore] public double DepthSort     => double.TryParse(Depth,   out var v) ? v : 0;
    [JsonIgnore] public double IncSort       => double.TryParse(Inc,     out var v) ? v : 0;
    [JsonIgnore] public double DurationSort  => double.TryParse(Duration,out var v) ? v : 0;
    [JsonIgnore] public double ScatterSort   => double.TryParse(Scatter, out var v) ? v : 0;
}
