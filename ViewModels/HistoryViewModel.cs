using ClosedXML.Excel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TransitLab.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace TransitLab.ViewModels;

public partial class HistoryViewModel : ViewModelBase
{
    public ObservableCollection<HistoryEntry> Entries { get; } = new();

    [ObservableProperty] private HistoryEntry? selectedEntry;

    // Injected by MainWindowViewModel
    public Action<IEnumerable<HistoryEntry>>? SaveHistoryFunc   { get; set; }

    // Injected by MainWindow code-behind
    public Func<Task<string?>>?  SaveCsvFunc      { get; set; }
    public Func<Task<string?>>?  SaveXlsxFunc     { get; set; }
    public Func<Task<string?>>?  OpenJsonFunc      { get; set; }
    public Func<Task<string?>>?  OpenCsvXlsxFunc  { get; set; }

    public HistoryViewModel()
    {
        // Subscribe/unsubscribe to each entry's PropertyChanged as items are added or removed,
        // so that any inline edit in the DataGrid triggers an auto-save.
        Entries.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is not null)
                foreach (HistoryEntry item in e.NewItems)
                    item.PropertyChanged += OnEntryChanged;
            if (e.OldItems is not null)
                foreach (HistoryEntry item in e.OldItems)
                    item.PropertyChanged -= OnEntryChanged;
        };
    }

    // Called whenever any property on any entry changes (including inline DataGrid edits).
    private void OnEntryChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // SubmittedStr fires alongside Submitted for the same user action — skip it to avoid
        // saving twice; the Submitted change covers the actual data update.
        if (e.PropertyName == nameof(HistoryEntry.SubmittedStr)) return;
        SaveHistoryFunc?.Invoke(Entries);
    }

    // ── Columns metadata ──────────────────────────────────────────────────────

    private static readonly (string Key, string Header)[] Columns =
    [
        ("Planet",    "Planet"),
        ("Obs",       "Obs"),
        ("ObsDate",   "Obs Date"),
        ("Submitted", "Submitted"),
        ("Tmid",      "Tmid (BJD_TDB)"),
        ("TmidUnc",   "±Tmid"),
        ("RpRs",      "(Rp/R*)²"),
        ("RpRsUnc",   "±(Rp/R*)²"),
        ("Snr",       "SNR"),
        ("Depth",     "Depth %"),
        ("Inc",       "Inc °"),
        ("Duration",  "Dur (d)"),
        ("Scatter",   "Scatter %"),
    ];

    // ── CRUD commands ─────────────────────────────────────────────────────────

    [RelayCommand]
    private void DeleteSelected()
    {
        if (SelectedEntry is not null)
        {
            Entries.Remove(SelectedEntry);
            SaveHistoryFunc?.Invoke(Entries);
        }
    }

    // ── Import FinalParams JSON ───────────────────────────────────────────────

    [RelayCommand]
    private async Task ImportJson()
    {
        if (OpenJsonFunc is null) return;
        var path = await OpenJsonFunc();
        if (path is null) return;
        try
        {
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // EXOTIC 4.3.2+: parameters are nested under "FINAL PLANETARY PARAMETERS".
            // EXOTIC 4.3.1 and earlier: parameters are at the root level.
            var pp = root.TryGetProperty("FINAL PLANETARY PARAMETERS", out var inner) ? inner : root;

            static (string Val, string Unc) ParseVU(JsonElement el, string key)
            {
                if (!el.TryGetProperty(key, out var v)) return ("", "");
                var s   = v.ToString().Trim();
                var idx = s.IndexOf("+/-");
                if (idx < 0) return (s.Split(' ')[0], "");
                var val = s[..idx].Trim();
                var unc = s[(idx + 3)..].Trim().Split(' ')[0];
                return (val, unc);
            }

            // Try multiple key names — some were renamed between EXOTIC 4.3.1 and 4.3.2
            static (string Val, string Unc) ParseVUAny(JsonElement el, params string[] keys)
            {
                foreach (var k in keys) { var r = ParseVU(el, k); if (r.Val != "") return r; }
                return ("", "");
            }

            var (tmidV,  tmidU)  = ParseVU(pp, "Mid-Transit Time (Tmid)");
            var (depthV, depthU) = ParseVUAny(pp,
                "Radius-ratio area depth (Rp/R*)^2",         // EXOTIC 4.3.2+
                "Transit depth (Rp/Rs)^2");                   // EXOTIC 4.3.1 and earlier
            var (incV,   _)      = ParseVU(pp, "Orbital Inclination (inc)");
            var (durV,   _)      = ParseVU(pp, "Transit Duration (day)");

            // Scatter key was renamed in EXOTIC 4.3.2
            string scatter = "";
            foreach (var scatterKey in new[] {
                "Residual scatter around full model fit",              // EXOTIC 4.3.2+
                "Scatter in the residuals of the lightcurve fit is" }) // EXOTIC 4.3.1
            {
                if (pp.TryGetProperty(scatterKey, out var sv))
                { scatter = sv.ToString().Replace("%", "").Trim(); break; }
            }

            // Rp/Rs: available as a direct key in EXOTIC 4.3.2+; derive from depth% otherwise
            var (rpRsDirect, rpRsDirectUnc) = ParseVUAny(pp,
                "Ratio of Planet to Stellar Radius (Rp/R*)");  // EXOTIC 4.3.2+
            string rpRs, rpRsUnc;
            if (!string.IsNullOrEmpty(rpRsDirect))
            {
                rpRs    = rpRsDirect;
                rpRsUnc = rpRsDirectUnc;
            }
            else
            {
                rpRs = depthV; rpRsUnc = depthU;
                if (double.TryParse(depthV, out var dv) &&
                    double.TryParse(depthU, out var du) && dv > 0.1)
                {
                    rpRs    = (dv / 100.0).ToString("F6");
                    rpRsUnc = (du / 100.0).ToString("F6");
                }
            }

            string snr = "";
            if (double.TryParse(rpRs, out var rv) &&
                double.TryParse(rpRsUnc, out var ru) && ru > 0)
                snr = (rv / ru).ToString("F1");

            // Derive ObsDate from Tmid if not otherwise available
            string obsDate = "";
            if (!string.IsNullOrEmpty(tmidV) && double.TryParse(tmidV, out var jd))
            {
                try
                {
                    var dt = new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc)
                                 .AddDays(jd - 2451545.0);
                    obsDate = dt.ToString("dd-MMM-yyyy").ToUpper();
                }
                catch { }
            }

            // Extract planet name from filename: FinalParams_<PlanetName>_<dd-MMM-yyyy>.json
            string planet = "";
            var fname = Path.GetFileNameWithoutExtension(path);
            if (fname.StartsWith("FinalParams_", StringComparison.OrdinalIgnoreCase))
            {
                var body   = fname["FinalParams_".Length..];
                var lastUs = body.LastIndexOf('_');
                if (lastUs > 0)
                {
                    planet = body[..lastUs].Trim();
                    // Normalise compact suffix: "HAT-P-22b" → "HAT-P-22 b"
                    if (planet.Length > 1 && char.IsLetter(planet[^1]) && char.IsDigit(planet[^2]))
                        planet = planet[..^1] + " " + planet[^1];
                }
            }

            var entry = new HistoryEntry
            {
                Planet    = planet,
                ObsDate   = obsDate,
                Submitted = DateTime.Today,
                Tmid      = tmidV,
                TmidUnc   = tmidU,
                RpRs      = rpRs,
                RpRsUnc   = rpRsUnc,
                Snr       = snr,
                Depth     = depthV,
                Inc       = incV,
                Duration  = durV,
                Scatter   = scatter,
            };
            Entries.Insert(0, entry);
            SaveHistoryFunc?.Invoke(Entries);
        }
        catch (Exception ex) { Services.SessionLogService.Write($"[History] ERROR importing JSON \"{Path.GetFileName(path)}\": {ex.Message}"); }
    }

    // ── Import CSV / XLSX ─────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ImportFile()
    {
        if (OpenCsvXlsxFunc is null) return;
        var path = await OpenCsvXlsxFunc();
        if (path is null) return;
        try
        {
            // Build header → property-name map
            var hdrMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, header) in Columns)
                hdrMap[header.ToLowerInvariant()] = key;
            // Extra aliases
            hdrMap["obs date"]      = "ObsDate";
            hdrMap["tmid (bjd_tdb)"]= "Tmid";
            hdrMap["±tmid"]         = "TmidUnc";
            hdrMap["+/-tmid"]       = "TmidUnc";
            hdrMap["(rp/r*)²"]      = "RpRs";
            hdrMap["(rp/r*)^2"]     = "RpRs";
            hdrMap["±(rp/r*)²"]     = "RpRsUnc";
            hdrMap["depth %"]       = "Depth";
            hdrMap["inc °"]         = "Inc";
            hdrMap["dur (d)"]       = "Duration";
            hdrMap["scatter %"]     = "Scatter";

            var rows = new List<Dictionary<string, string>>();

            if (path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            {
                using var wb = new XLWorkbook(path);
                var ws = wb.Worksheets.First();
                var firstRow = ws.FirstRowUsed();
                if (firstRow is null) return;

                var colMap = new Dictionary<int, string>();
                foreach (var cell in firstRow.Cells())
                {
                    var hdr = cell.GetString().Trim().ToLowerInvariant();
                    if (hdrMap.TryGetValue(hdr, out var key))
                        colMap[cell.Address.ColumnNumber] = key;
                }

                foreach (var dataRow in ws.RowsUsed().Skip(1))
                {
                    var entry2 = new Dictionary<string, string>();
                    foreach (var (col, key) in colMap)
                        entry2[key] = dataRow.Cell(col).GetString().Trim();
                    rows.Add(entry2);
                }
            }
            else
            {
                using var reader = new StreamReader(path, Encoding.UTF8);
                var headerLine = reader.ReadLine();
                if (headerLine is null) return;

                var headers = headerLine.Split(',');
                var colKeys = headers
                    .Select(h => hdrMap.TryGetValue(h.Trim().ToLowerInvariant(), out var k)
                        ? k : h.Trim())
                    .ToArray();

                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    var vals  = SplitCsvLine(line);
                    var entry2 = new Dictionary<string, string>();
                    for (int i = 0; i < Math.Min(colKeys.Length, vals.Length); i++)
                        entry2[colKeys[i]] = vals[i].Trim('"').Trim();
                    rows.Add(entry2);
                }
            }

            if (rows.Count == 0) return;

            var existingKeys = Entries
                .Select(e => (e.Planet, e.ObsDate, e.Tmid))
                .ToHashSet();

            foreach (var row in rows)
            {
                row.TryGetValue("Planet",  out var planet);  planet  ??= "";
                row.TryGetValue("ObsDate", out var obsDate); obsDate ??= "";
                row.TryGetValue("Tmid",    out var tmid);    tmid    ??= "";
                if (existingKeys.Contains((planet, obsDate, tmid))) continue;

                var e = new HistoryEntry { Planet = planet, ObsDate = obsDate, Tmid = tmid };
                if (row.TryGetValue("Obs",       out var v)) e.Obs      = v;
                if (row.TryGetValue("Submitted",  out v))
                {
                    e.Submitted = DateTime.TryParse(v, out var dt) ? dt : DateTime.Today;
                }
                if (row.TryGetValue("TmidUnc",   out v)) e.TmidUnc  = v;
                if (row.TryGetValue("RpRs",      out v)) e.RpRs     = v;
                if (row.TryGetValue("RpRsUnc",   out v)) e.RpRsUnc  = v;
                if (row.TryGetValue("Snr",       out v)) e.Snr      = v;
                if (row.TryGetValue("Depth",     out v)) e.Depth    = v;
                if (row.TryGetValue("Inc",       out v)) e.Inc      = v;
                if (row.TryGetValue("Duration",  out v)) e.Duration = v;
                if (row.TryGetValue("Scatter",   out v)) e.Scatter  = v;

                Entries.Add(e);
                existingKeys.Add((planet, obsDate, tmid));
            }
            SaveHistoryFunc?.Invoke(Entries);
        }
        catch (Exception ex) { Services.SessionLogService.Write($"[History] ERROR importing file \"{Path.GetFileName(path)}\": {ex.Message}"); }
    }

    private static string[] SplitCsvLine(string line)
    {
        var result   = new List<string>();
        var current  = new StringBuilder();
        bool inQuotes = false;
        foreach (char c in line)
        {
            if      (c == '"')           { inQuotes = !inQuotes; }
            else if (c == ',' && !inQuotes) { result.Add(current.ToString()); current.Clear(); }
            else                         { current.Append(c); }
        }
        result.Add(current.ToString());
        return [.. result];
    }

    // ── Download CSV ──────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task DownloadCsv()
    {
        if (SaveCsvFunc is null) return;
        var path = await SaveCsvFunc();
        if (path is null) return;
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("Planet,Obs,Obs Date,Submitted,Tmid (BJD_TDB),±Tmid,(Rp/R*)²,±(Rp/R*)²,SNR,Depth %,Inc °,Dur (d),Scatter %");
            foreach (var e in Entries)
                sb.AppendLine(string.Join(",",
                    Csv(e.Planet), Csv(e.Obs), Csv(e.ObsDate), Csv(e.SubmittedStr),
                    Csv(e.Tmid), Csv(e.TmidUnc), Csv(e.RpRs), Csv(e.RpRsUnc),
                    Csv(e.Snr), Csv(e.Depth), Csv(e.Inc), Csv(e.Duration), Csv(e.Scatter)));
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }
        catch (Exception ex) { Services.SessionLogService.Write($"[History] ERROR saving CSV: {ex.Message}"); }
    }

    private static string Csv(string s) => s.Contains(',') ? $"\"{s}\"" : s;

    // ── Download XLSX ─────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task DownloadXlsx()
    {
        if (SaveXlsxFunc is null) return;
        var path = await SaveXlsxFunc();
        if (path is null) return;
        try
        {
            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add("AAVSO Submissions");

            string[] headers =
            [
                "Planet","Obs","Obs Date","Submitted",
                "Tmid (BJD_TDB)","±Tmid","(Rp/R*)²","±(Rp/R*)²",
                "SNR","Depth %","Inc °","Dur (d)","Scatter %"
            ];

            for (int i = 0; i < headers.Length; i++)
            {
                var cell = ws.Cell(1, i + 1);
                cell.Value                      = headers[i];
                cell.Style.Font.Bold            = true;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#1B3F6B");
                cell.Style.Font.FontColor       = XLColor.White;
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            }

            int row = 2;
            foreach (var e in Entries)
            {
                ws.Cell(row, 1).Value  = e.Planet;
                ws.Cell(row, 2).Value  = e.Obs;
                ws.Cell(row, 3).Value  = e.ObsDate;
                ws.Cell(row, 4).Value  = e.SubmittedStr;
                ws.Cell(row, 5).Value  = e.Tmid;
                ws.Cell(row, 6).Value  = e.TmidUnc;
                ws.Cell(row, 7).Value  = e.RpRs;
                ws.Cell(row, 8).Value  = e.RpRsUnc;
                ws.Cell(row, 9).Value  = e.Snr;
                ws.Cell(row, 10).Value = e.Depth;
                ws.Cell(row, 11).Value = e.Inc;
                ws.Cell(row, 12).Value = e.Duration;
                ws.Cell(row, 13).Value = e.Scatter;
                row++;
            }

            ws.Columns().AdjustToContents();
            wb.SaveAs(path);
        }
        catch (Exception ex) { Services.SessionLogService.Write($"[History] ERROR saving XLSX: {ex.Message}"); }
    }
}
