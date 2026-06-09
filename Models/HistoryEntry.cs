using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace TransitLab.Models;

public class HistoryEntry : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (System.Collections.Generic.EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private string _planet   = ""; public string Planet   { get => _planet;   set => Set(ref _planet,   value ?? ""); }
    private string _obs      = ""; public string Obs      { get => _obs;      set => Set(ref _obs,      value ?? ""); }
    private string _obsDate  = ""; public string ObsDate  { get => _obsDate;  set => Set(ref _obsDate,  value ?? ""); }
    private string _tmid     = ""; public string Tmid     { get => _tmid;     set => Set(ref _tmid,     value ?? ""); }
    private string _tmidUnc  = ""; public string TmidUnc  { get => _tmidUnc;  set => Set(ref _tmidUnc,  value ?? ""); }
    private string _rpRs     = ""; public string RpRs     { get => _rpRs;     set => Set(ref _rpRs,     value ?? ""); }
    private string _rpRsUnc  = ""; public string RpRsUnc  { get => _rpRsUnc;  set => Set(ref _rpRsUnc,  value ?? ""); }
    private string _snr      = ""; public string Snr      { get => _snr;      set => Set(ref _snr,      value ?? ""); }
    private string _depth    = ""; public string Depth    { get => _depth;    set => Set(ref _depth,    value ?? ""); }
    private string _inc      = ""; public string Inc      { get => _inc;      set => Set(ref _inc,      value ?? ""); }
    private string _duration = ""; public string Duration { get => _duration; set => Set(ref _duration, value ?? ""); }
    private string _scatter  = ""; public string Scatter  { get => _scatter;  set => Set(ref _scatter,  value ?? ""); }

    private DateTime _submitted = DateTime.Today;
    public DateTime Submitted
    {
        get => _submitted;
        set
        {
            if (_submitted == value) return;
            _submitted = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Submitted)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SubmittedStr)));
        }
    }

    // Editable display proxy — the DataGrid column binds to this.
    // Setter accepts "dd-MMM-yyyy" or any parseable date string.
    [JsonIgnore]
    public string SubmittedStr
    {
        get => Submitted == default ? "" : Submitted.ToString("dd-MMM-yyyy").ToUpper();
        set
        {
            var s = value?.Trim() ?? "";
            if (DateTime.TryParseExact(s, "dd-MMM-yyyy",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
                || DateTime.TryParse(s, out dt))
                Submitted = dt;
        }
    }

    // ── Sort keys (typed, for correct DataGrid column sorting) ────────────────
    [JsonIgnore]
    public DateTime ObsDateSort
    {
        get
        {
            if (DateTime.TryParseExact(ObsDate, "dd-MMM-yyyy",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var dt))
                return dt;
            return DateTime.MinValue;
        }
    }

    [JsonIgnore] public double TmidSort     => double.TryParse(Tmid,     out var v) ? v : 0;
    [JsonIgnore] public double TmidUncSort  => double.TryParse(TmidUnc,  out var v) ? v : 0;
    [JsonIgnore] public double RpRsSort     => double.TryParse(RpRs,     out var v) ? v : 0;
    [JsonIgnore] public double RpRsUncSort  => double.TryParse(RpRsUnc,  out var v) ? v : 0;
    [JsonIgnore] public double SnrSort      => double.TryParse(Snr,      out var v) ? v : 0;
    [JsonIgnore] public double DepthSort    => double.TryParse(Depth,    out var v) ? v : 0;
    [JsonIgnore] public double IncSort      => double.TryParse(Inc,      out var v) ? v : 0;
    [JsonIgnore] public double DurationSort => double.TryParse(Duration, out var v) ? v : 0;
    [JsonIgnore] public double ScatterSort  => double.TryParse(Scatter,  out var v) ? v : 0;
}
