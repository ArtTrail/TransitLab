using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TransitLab.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TransitLab.ViewModels;

public partial class EquipmentTargetViewModel : ViewModelBase
{
    // ── Equipment ─────────────────────────────────────────────────────────────
    [ObservableProperty] private string _cameraType  = "CCD";
    [ObservableProperty] private string _binning     = "1x1";
    [ObservableProperty] private string _filter      = "";
    [ObservableProperty] private string _filterMin   = "";
    [ObservableProperty] private string _filterMax   = "";
    [ObservableProperty] private string _pixelScale  = "";
    [ObservableProperty] private string _notes       = "";

    public string[]                     CameraTypes    { get; } = ["CCD", "DSLR"];
    public string[]                     BinningOptions { get; } = ["1x1", "2x2", "3x3", "4x4"];
    public string[]                     FilterCodes    { get; } = [
        "SU","SG","SR","SI","SZ",
        "U","B","V","R","I",
        "Y","J","H","K",
        "CBB","CV","IJ","L","MA","MB","MI","N/A",
        "RJ","STB","STHBN","STHBW","STU","STV","STY",
    ];
    public ObservableCollection<string> PixelScales   { get; } = new();
    public ObservableCollection<string> NotesList     { get; } = new();

    private static readonly Dictionary<string, (double Min, double Max)> FilterFwhm = new()
    {
        ["U"]    = (333.8,  398.8),  ["B"]    = (391.6,  480.6),  ["V"]    = (502.8,  586.8),
        ["RJ"]   = (590.0,  810.0),  ["IJ"]   = (780.0, 1020.0),
        ["R"]    = (561.7,  719.7),  ["I"]    = (721.0,  875.0),
        ["J"]    = (1040.0,1360.0),  ["H"]    = (1420.0,1780.0),  ["K"]    = (2015.0,2385.0),
        ["SU"]   = (321.8,  386.8),  ["SG"]   = (402.5,  551.5),  ["SR"]   = (553.1,  693.1),
        ["SI"]   = (697.5,  827.5),  ["SZ"]   = (841.2,  978.2),
        ["L"]    = (430.0,  700.0),  ["CV"]   = (350.0,  850.0),  ["CBB"]  = (500.0, 1000.0),
        ["N/A"]  = (404.2,  845.8),  ["Y"]    = (946.4, 1054.4),
        ["STB"]  = (459.55,478.05),  ["STY"]  = (536.7,  559.3),  ["STU"]  = (336.3,  367.7),
        ["STV"]  = (401.5,  418.5),  ["STHBW"]= (481.5,  496.5),  ["STHBN"]= (487.5,  484.5),
        ["MA"]   = (706.5,  717.5),  ["MB"]   = (748.5,  759.5),  ["MI"]   = (1003.0,1045.0),
    };

    partial void OnFilterChanged(string value)
    {
        if (FilterFwhm.TryGetValue(value, out var fwhm))
        { FilterMin = fwhm.Min.ToString("G"); FilterMax = fwhm.Max.ToString("G"); }
    }

    // ── Star Selection ────────────────────────────────────────────────────────
    [ObservableProperty] private string _csvFileName        = "No file selected";
    [ObservableProperty] private string _starListPreview    = "";
    [ObservableProperty] private string _plateSolveStatus   = "";
    [ObservableProperty] private string _activeSolverLabel  = "Solver: Astrometry.net";
    [ObservableProperty] private string _aavsoCompStatus    = "";

    /// <summary>Set by MainWindowViewModel when plate solver config changes.</summary>
    public PlateSolveService.SolverConfig? PlateSolverConfig { get; set; }

    /// <summary>Called with true=success/false=failure after NEA fetch, plate solve, or AAVSO comp fetch completes.</summary>
    public Action<bool>? PlayStatusSoundAction { get; set; }
    [ObservableProperty] private string _targetXY           = "";
    [ObservableProperty] private string _compXY             = "";
    [ObservableProperty] private string _autoTargetStatus   = "⚠  Fetch planet parameters first";
    [ObservableProperty] private bool   _isAutoTargetEnabled;
    [ObservableProperty] private bool   _isAutoCompsEnabled;
    [ObservableProperty] private bool   _isPsCancelVisible;
    [ObservableProperty] private bool   _isPsRetryEnabled;
    [ObservableProperty] private bool   _isCompsRetryEnabled;

    // ── Planet Parameters ─────────────────────────────────────────────────────
    [ObservableProperty] private string _planetName     = "";
    [ObservableProperty] private string _neaStatus      = "";
    public ObservableCollection<string> RecentPlanets  { get; } = new();

    [ObservableProperty] private string _targetRa          = "";
    [ObservableProperty] private string _targetDec         = "";
    [ObservableProperty] private string _hostStarName      = "";
    [ObservableProperty] private string _orbitalPeriod     = "";
    [ObservableProperty] private string _orbitalPeriodUnc  = "";
    [ObservableProperty] private string _midTransitTime    = "";
    [ObservableProperty] private string _midTransitTimeUnc = "";
    [ObservableProperty] private string _rpRs              = "";
    [ObservableProperty] private string _rpRsUnc           = "";
    [ObservableProperty] private string _aRs               = "";
    [ObservableProperty] private string _aRsUnc            = "";
    [ObservableProperty] private string _inclination       = "";
    [ObservableProperty] private string _inclinationUnc    = "";
    [ObservableProperty] private string _eccentricity      = "";
    [ObservableProperty] private string _argPeriastron     = "";
    [ObservableProperty] private string _teff              = "";
    [ObservableProperty] private string _teffPlus          = "";
    [ObservableProperty] private string _teffMinus         = "";
    [ObservableProperty] private string _metallicity       = "";
    [ObservableProperty] private string _metallicityPlus   = "";
    [ObservableProperty] private string _metallicityMinus  = "";
    [ObservableProperty] private string _logg              = "";
    [ObservableProperty] private string _loggPlus          = "";
    [ObservableProperty] private string _loggMinus         = "";
    [ObservableProperty] private string _starDistance      = "";
    [ObservableProperty] private string _pmRa              = "";
    [ObservableProperty] private string _pmDec             = "";

    // ── Injected ──────────────────────────────────────────────────────────────
    /// <summary>Called whenever a new target host star name becomes available (e.g. from NEA).</summary>
    public Action<string>? TargetNameChanged { get; set; }

    public Func<string, Task<string?>>?  FolderPickerFunc            { get; set; }
    public Func<string, Task<string?>>?  FilePickerFunc              { get; set; }
    public Func<string, string, Task>?   ShowErrorFunc               { get; set; }
    public Func<string, string, Task>?   ShowInfoFunc                { get; set; }
    /// <summary>Returns the FITS directory path.</summary>
    public Func<string>?                 FitsDirFunc                 { get; set; }
    /// <summary>Returns the first non-excluded FITS file path (may scan first).</summary>
    public Func<Task<string?>>?          GetFirstNonExcludedFitsFunc { get; set; }

    // ── WCS state ─────────────────────────────────────────────────────────────
    private bool   _wcsReady    = false;
    private string _wcsFitsPath = "";
    private CancellationTokenSource? _aavsoCompCts;
    private bool   _isNeaRunning = false;  // true while FetchFromNeaAsync is in progress

    /// <summary>Called by ObservationViewModel when a plate solve succeeds or WCS is already present.</summary>
    public void NotifyWcsReady(string fitsPath, string saveDir = "", string exoticExePath = "")
    {
        _wcsReady            = true;
        _wcsFitsPath         = fitsPath;
        IsCompsRetryEnabled  = true;
        if (!string.IsNullOrEmpty(fitsPath))    _psFitsPath  = fitsPath;
        if (!string.IsNullOrEmpty(saveDir))     _psSaveDir   = saveDir;
        if (!string.IsNullOrEmpty(exoticExePath)) _psExoticExe = exoticExePath;
        UpdateAutoTargetEnabled();
        _ = StartAavsoCompFetchAsync(fitsPath);
    }

    [RelayCommand]
    private Task RetryComps() => StartAavsoCompFetchAsync(_wcsFitsPath);

    public async Task StartAavsoCompFetchAsync(string fitsPath)
    {
        _aavsoCompCts?.Cancel();
        _aavsoCompCts = new CancellationTokenSource();
        var ct = _aavsoCompCts.Token;

        // ── Determine RA/Dec ──────────────────────────────────────────────────
        double ra = 0, dec = 0;
        bool hasCoords = false;

        // 1. Planet parameters
        if (double.TryParse(TargetRa,  System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out var pRa) &&
            double.TryParse(TargetDec, System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out var pDec))
        {
            ra = pRa; dec = pDec; hasCoords = true;
        }

        // 2. Derive from WCS (image centre), but only if NEA isn't still running.
        //    If NEA is in progress, defer this fetch — NEA will re-trigger it with accurate coords.
        if (!hasCoords)
        {
            if (_isNeaRunning && !ct.IsCancellationRequested)
            {
                AavsoCompStatus = "⟳  Waiting for NEA query to complete…";
                return;
            }

            try
            {
                var hdr = await Task.Run(() => FitsHeaderService.Read(fitsPath));
                var wcs = WcsService.ReadWcs(hdr);
                if (wcs is not null)
                {
                    ra  = wcs.Crval1;
                    dec = wcs.Crval2;
                    hasCoords = true;
                }
            }
            catch (Exception ex)
            {
                Services.SessionLogService.Write($"[AavsoComp] WARNING — could not read WCS from FITS: {ex.Message}");
            }
        }

        if (!hasCoords)
        {
            // Only surface this error if we haven't already been superseded by a newer fetch.
            if (!ct.IsCancellationRequested)
            {
                AavsoCompStatus = "⚠  AAVSO comp fetch skipped — fetch planet parameters or plate solve first";
                Services.SessionLogService.Write("[AavsoComp] Skipped — no RA/Dec available (fetch planet parameters or plate solve first)");
            }
            return;
        }

        // If a newer fetch has already taken over, exit silently — it will set the status.
        if (ct.IsCancellationRequested) return;

        // Clear stale comps from any previous target before the network call,
        // so we never show old data while the new fetch is in progress.
        CompXY = "";
        AavsoCompStatus = "⟳  Fetching AAVSO comparison stars…";
        try
        {
            TryParseXY(TargetXY, out var targetPx, out var targetPy);
            var result = await AavsoCompService.FetchAsync(fitsPath, ra, dec, targetPx, targetPy, ct);
            if (ct.IsCancellationRequested) return;

            if (result.Pairs.Count == 0)
            {
                // First attempt returned nothing — wait 10 s and retry once.
                Services.SessionLogService.Write($"[AavsoComp] Attempt 1 failed ({result.StatusMessage}) — retrying in 10 s");
                AavsoCompStatus = $"⟳  Retrying AAVSO fetch in 10 s… (attempt 1: {result.StatusMessage})";
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                if (ct.IsCancellationRequested) return;

                AavsoCompStatus = "⟳  Retrying AAVSO comparison star fetch…";
                result = await AavsoCompService.FetchAsync(fitsPath, ra, dec, targetPx, targetPy, ct);
                if (ct.IsCancellationRequested) return;
                Services.SessionLogService.Write($"[AavsoComp] Retry result: {result.StatusMessage}");
            }
            else
            {
                Services.SessionLogService.Write($"[AavsoComp] {result.StatusMessage}");
            }

            AavsoCompStatus = result.StatusMessage;
            if (result.Pairs.Count > 0)
            {
                SetCompStars(result.Pairs);   // replace, not merge
                PlayStatusSoundAction?.Invoke(true);
            }
            else
            {
                PlayStatusSoundAction?.Invoke(false);
            }
        }
        catch (OperationCanceledException) { /* superseded */ }
        catch (Exception ex)
        {
            AavsoCompStatus = $"✗  AAVSO fetch failed: {ex.Message}";
            Services.SessionLogService.Write($"[AavsoComp] ERROR — {ex.GetType().Name}: {ex.Message}");
            PlayStatusSoundAction?.Invoke(false);
        }
    }

    // ── Reactive enable-flag helpers ──────────────────────────────────────────
    partial void OnTargetRaChanged(string value)
    {
        UpdateAutoTargetEnabled();
        // New planet loaded — pixel coords and CSV star list from a previous target are stale.
        // CompXY is NOT cleared here to avoid racing with the concurrent AAVSO auto-fetch;
        // it is cleared at the start of StartAavsoCompFetchAsync instead.
        if (!string.IsNullOrWhiteSpace(value))
        {
            TargetXY        = "";
            CsvFileName     = "No file selected";
            StarListPreview = "";
            // NOTE: comp fetch re-trigger is handled at the end of FetchFromNeaAsync, after
            // both TargetRa AND TargetDec are fully set.  Triggering here would race against
            // TargetDec being set on the very next line of FetchFromNeaAsync.
        }
    }

    partial void OnTargetDecChanged(string value) => UpdateAutoTargetEnabled();
    partial void OnTargetXYChanged(string value)  => IsAutoCompsEnabled = !string.IsNullOrWhiteSpace(value);

    private void UpdateAutoTargetEnabled()
    {
        bool hasRa  = double.TryParse(TargetRa,  NumberStyles.Any, CultureInfo.InvariantCulture, out _);
        bool hasDec = double.TryParse(TargetDec, NumberStyles.Any, CultureInfo.InvariantCulture, out _);

        if (hasRa && hasDec && _wcsReady)
        {
            IsAutoTargetEnabled = true;
            AutoTargetStatus    = "✓  Planet parameters and plate solution ready";
        }
        else if (!hasRa || !hasDec)
        {
            IsAutoTargetEnabled = false;
            AutoTargetStatus    = "⚠  Fetch planet parameters first";
        }
        else
        {
            IsAutoTargetEnabled = false;
            AutoTargetStatus    = "⚠  Awaiting plate solve…";
        }
    }

    partial void OnIsAutoTargetEnabledChanged(bool value) => AutoSelectTargetCommand.NotifyCanExecuteChanged();
    partial void OnIsAutoCompsEnabledChanged(bool value)  => AutoSelectCompsCommand.NotifyCanExecuteChanged();

    // ── Plate solve state ─────────────────────────────────────────────────────
    private CancellationTokenSource? _psCts;
    private string _psFitsPath    = "";
    private string _psSaveDir     = "";
    private string _psExoticExe   = "";

    public async Task StartPlateSolveAsync(string fitsPath, string saveDir, string exoticExePath)
    {
        _psFitsPath  = fitsPath;
        _psSaveDir   = saveDir;
        _psExoticExe = exoticExePath;

        _psCts?.Cancel();
        _psCts = new CancellationTokenSource();
        var ct = _psCts.Token;

        IsPsCancelVisible = true;
        IsPsRetryEnabled  = false;

        var progress = new Progress<string>(msg => PlateSolveStatus = msg);
        PlateSolveService.Result result;
        try
        {
            result = await PlateSolveService.SolveAsync(fitsPath, saveDir, exoticExePath, PlateSolverConfig, progress, ct);
        }
        catch (OperationCanceledException)
        {
            PlateSolveStatus  = "Plate solve cancelled.";
            IsPsCancelVisible = false;
            IsPsRetryEnabled  = true;
            return;
        }
        catch (Exception ex)
        {
            PlateSolveStatus  = $"✗  {ex.Message}";
            IsPsCancelVisible = false;
            IsPsRetryEnabled  = true;
            PlayStatusSoundAction?.Invoke(false);
            return;
        }

        PlateSolveStatus  = result.Success ? $"✓  {result.Message}" : $"✗  {result.Message}";
        IsPsCancelVisible = false;
        IsPsRetryEnabled  = !result.Success;
        PlayStatusSoundAction?.Invoke(result.Success);

        if (result.Success)
            NotifyWcsReady(fitsPath);
    }

    // ── Commands ──────────────────────────────────────────────────────────────
    [RelayCommand] private async Task BrowseCsv()
    {
        if (FilePickerFunc is null) return;
        var path = await FilePickerFunc("Select Star List CSV");
        if (path is null) return;

        CsvFileName = Path.GetFileName(path);
        try   { ParseStarListCsv(path); }
        catch (Exception ex) { StarListPreview = $"⚠  Error reading CSV: {ex.Message}"; }
    }

    private void ParseStarListCsv(string path)
    {
        var lines = File.ReadAllLines(path);
        if (lines.Length < 2) { StarListPreview = "⚠  CSV appears empty."; return; }

        var headers = SplitCsvLine(lines[0]);
        var rows    = new List<Dictionary<string, string>>();
        for (int i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            var vals = SplitCsvLine(lines[i]);
            var row  = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int j = 0; j < headers.Count && j < vals.Count; j++)
                row[headers[j].Trim()] = vals[j].Trim();
            rows.Add(row);
        }

        // Target row
        var targetRow = rows.FirstOrDefault(r =>
            r.TryGetValue("Type", out var t) && t.Trim().Equals("Target", StringComparison.OrdinalIgnoreCase));
        if (targetRow is null) { StarListPreview = "⚠  No row with Type = \"Target\" found."; return; }

        int tx = (int)double.Parse(targetRow["xPos"], CultureInfo.InvariantCulture);
        int ty = (int)double.Parse(targetRow["yPos"], CultureInfo.InvariantCulture);
        TargetXY = $"[{tx}, {ty}]";

        // Comp rows — filter saturated, dedup, sort by MaxBright desc, top 10
        var compRows = rows.Where(r =>
            r.TryGetValue("Type", out var t) && t.Trim().StartsWith("Comp", StringComparison.OrdinalIgnoreCase));

        var valid = new List<(string Name, int X, int Y, double MaxBright)>();
        foreach (var r in compRows)
        {
            if (!r.TryGetValue("MaxBright", out var mbStr)) continue;
            if (!double.TryParse(mbStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var mb)) continue;
            if (mb > 60_000) continue;
            if (!r.TryGetValue("xPos", out var xs) || !r.TryGetValue("yPos", out var ys)) continue;
            int cx = (int)double.Parse(xs, CultureInfo.InvariantCulture);
            int cy = (int)double.Parse(ys, CultureInfo.InvariantCulture);
            var name = r.TryGetValue("Name", out var n) ? n.Trim() : "?";
            valid.Add((name, cx, cy, mb));
        }

        var seen   = new HashSet<(int, int)>();
        var unique = new List<(string Name, int X, int Y, double MaxBright)>();
        foreach (var item in valid)
        {
            if (seen.Add((item.X, item.Y)))
                unique.Add(item);
        }

        var top10 = unique.OrderByDescending(x => x.MaxBright).Take(10).ToList();
        CompXY = "[" + string.Join(", ", top10.Select(c => $"[{c.X}, {c.Y}]")) + "]";

        // Preview
        var sb = new StringBuilder();
        var tname = targetRow.TryGetValue("Name", out var tn) ? tn.Trim() : "?";
        sb.AppendLine($"Target:  {tname}  →  [{tx}, {ty}]");
        sb.AppendLine();
        sb.AppendLine($"Comp stars from CSV  ({top10.Count} of {unique.Count} candidates):");
        for (int i = 0; i < top10.Count; i++)
        {
            var c = top10[i];
            sb.AppendLine($"  {i + 1,2}.  {c.Name,-32}  MaxBright={c.MaxBright,6:0}   [{c.X}, {c.Y}]");
        }
        StarListPreview = sb.ToString().TrimEnd();
    }

    private static List<string> SplitCsvLine(string line)
    {
        var result  = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;
        foreach (char c in line)
        {
            if      (c == '"')              inQuotes = !inQuotes;
            else if (c == ',' && !inQuotes) { result.Add(current.ToString()); current.Clear(); }
            else                            current.Append(c);
        }
        result.Add(current.ToString());
        return result;
    }

    [RelayCommand(CanExecute = nameof(IsAutoTargetEnabled))]
    private async Task AutoSelectTarget()
    {
        AutoTargetStatus = "Searching…";

        if (!double.TryParse(TargetRa,  NumberStyles.Any, CultureInfo.InvariantCulture, out var ra) ||
            !double.TryParse(TargetDec, NumberStyles.Any, CultureInfo.InvariantCulture, out var dec))
        {
            AutoTargetStatus = "⚠  RA/Dec must be decimal degrees";
            return;
        }

        // Find the plate-solved FITS file
        var fitsPath = _wcsFitsPath;
        if (string.IsNullOrEmpty(fitsPath) || !File.Exists(fitsPath))
        {
            if (GetFirstNonExcludedFitsFunc is not null)
                fitsPath = await GetFirstNonExcludedFitsFunc() ?? "";
            if (string.IsNullOrEmpty(fitsPath))
            {
                AutoTargetStatus = "⚠  No FITS file found — set FITS directory";
                return;
            }
        }

        try
        {
            var hdr = await Task.Run(() => FitsHeaderService.Read(fitsPath));
            var wcs = WcsService.ReadWcs(hdr);
            if (wcs is null)
            {
                AutoTargetStatus = "⚠  No plate solution — plate-solve FITS first";
                return;
            }

            var naxis1 = hdr.GetInt("NAXIS1") ?? 0;
            var naxis2 = hdr.GetInt("NAXIS2") ?? 0;

            var pixel = WcsService.SkyToPixel(wcs, ra, dec);
            if (pixel is null)
            {
                AutoTargetStatus = "⚠  WCS coordinate conversion failed";
                return;
            }

            var (px, py) = pixel.Value;
            if (naxis1 > 0 && naxis2 > 0 && (px < 1 || px > naxis1 || py < 1 || py > naxis2))
            {
                AutoTargetStatus = $"⚠  [{px}, {py}] is outside image bounds ({naxis1}×{naxis2})";
                return;
            }

            TargetXY         = $"[{px}, {py}]";
            AutoTargetStatus = $"✓  [{px}, {py}]";
            PlayStatusSoundAction?.Invoke(true);
        }
        catch (Exception ex)
        {
            AutoTargetStatus = $"✗  {ex.Message}";
            PlayStatusSoundAction?.Invoke(false);
        }
    }

    [RelayCommand(CanExecute = nameof(IsAutoCompsEnabled))]
    private async Task AutoSelectComps()
    {
        // Parse target position
        if (!TryParseXY(TargetXY, out var tx, out var ty))
        {
            AavsoCompStatus = "⚠  Set Target Star X,Y first";
            return;
        }

        var existing = ParseCompStars();
        int slots    = 10 - existing.Count;
        if (slots <= 0)
        {
            if (ShowErrorFunc is not null)
                await ShowErrorFunc("Maximum Comp Stars Reached",
                    "You already have 10 comparison stars selected.\n\nRemove one or more before running Auto Select Comps.");
            else
                AavsoCompStatus = "⚠  Already have 10 comp stars — remove some first";
            return;
        }

        // Find FITS file
        string fitsPath = "";
        if (GetFirstNonExcludedFitsFunc is not null)
            fitsPath = await GetFirstNonExcludedFitsFunc() ?? "";
        if (string.IsNullOrEmpty(fitsPath))
        {
            var dir = FitsDirFunc?.Invoke() ?? "";
            fitsPath = FitsHeaderService.FindFirstFits(dir) ?? "";
        }
        if (string.IsNullOrEmpty(fitsPath)) { AavsoCompStatus = "⚠  No FITS file found"; return; }

        AavsoCompStatus = "⟳  Detecting stars…";

        try
        {
            var (newComps, targetAdu, totalCandidates) = await Task.Run(() =>
                DetectCompStars(fitsPath, tx, ty, existing, slots));

            if (newComps.Count == 0)
            {
                AavsoCompStatus = "⚠  No suitable candidates found";
                return;
            }

            SetCompStars(existing.Concat(newComps).ToList());
            AavsoCompStatus = $"✓  {newComps.Count} comp(s) added  (target ADU ≈ {targetAdu:N0})";
        }
        catch (Exception ex)
        {
            AavsoCompStatus = $"✗  {ex.Message}";
        }
    }

    private static (List<(int X, int Y)> NewComps, double TargetAdu, int TotalCandidates)
        DetectCompStars(string fitsPath, int tx, int ty,
                        List<(int X, int Y)> existing, int slots)
    {
        var img    = FitsImageService.Load(fitsPath);
        int w      = img.Width;
        int h      = img.Height;
        var pixels = img.Pixels;

        // Background: median + 3×MAD detection threshold
        var sample = pixels.ToArray();
        Array.Sort(sample);
        double bgMed = sample[sample.Length / 2];
        var mads = sample.Select(p => Math.Abs(p - bgMed)).ToArray();
        Array.Sort(mads);
        double bgMad = mads[mads.Length / 2] * 1.4826;
        if (bgMad < 1.0) bgMad = 1.0;
        double threshold = bgMed + 3.0 * bgMad;

        // Target peak ADU in 8-pixel aperture
        const int R = 8;
        int txc = Math.Clamp(tx - 1, R, w - R - 1);   // convert 1-based to 0-based
        int tyc = Math.Clamp(ty - 1, R, h - R - 1);
        double targetAdu = double.MinValue;
        for (int dy = -R; dy <= R; dy++)
            for (int dx = -R; dx <= R; dx++)
                targetAdu = Math.Max(targetAdu, pixels[(tyc + dy) * w + (txc + dx)]);

        // 10% edge exclusion zone
        int edgeX = Math.Max(1, (int)(w * 0.10));
        int edgeY = Math.Max(1, (int)(h * 0.10));

        // Local-maximum star detection (3×3 window) inside the edge exclusion zone
        var stars = new List<(int X, int Y, double Peak)>();
        for (int y = edgeY; y < h - edgeY; y++)
        {
            for (int x = edgeX; x < w - edgeX; x++)
            {
                float v = pixels[y * w + x];
                if (v <= threshold) continue;
                bool isMax = true;
                for (int dy = -1; dy <= 1 && isMax; dy++)
                    for (int dx = -1; dx <= 1 && isMax; dx++)
                        if (!(dx == 0 && dy == 0) && pixels[(y + dy) * w + (x + dx)] >= v)
                            isMax = false;
                if (isMax) stars.Add((x + 1, y + 1, v)); // back to 1-based
            }
        }

        // Filter: reject stars outside the [10%, 100%] ADU range of the target and stars
        // inside the target exclusion zone; rank by brightness similarity to target (ADU delta).
        const int    targetExclusion = 20;
        double       minAdu          = targetAdu * 0.10;
        var candidates = new List<(int X, int Y, double Peak, double Dist)>();
        foreach (var (sx, sy, speak) in stars)
        {
            if (speak > targetAdu) continue;   // brighter than target — skip
            if (speak < minAdu)    continue;   // too faint (< 10% of target ADU) — skip
            double dist = Math.Sqrt((double)(sx - tx) * (sx - tx) + (double)(sy - ty) * (sy - ty));
            if (dist <= targetExclusion) continue;  // inside target exclusion zone
            if (existing.Any(e => Math.Abs(e.X - sx) <= 10 && Math.Abs(e.Y - sy) <= 10)) continue;
            candidates.Add((sx, sy, speak, dist));
        }

        // Rank by brightness similarity to target (closest ADU match first)
        candidates.Sort((a, b) => Math.Abs(a.Peak - targetAdu).CompareTo(Math.Abs(b.Peak - targetAdu)));
        var newComps = candidates.Take(slots).Select(c => (c.X, c.Y)).ToList();
        return (newComps, targetAdu, candidates.Count);
    }

    private List<(int X, int Y)> ParseCompStars()
    {
        var result = new List<(int, int)>();
        var raw = CompXY.Trim();
        if (string.IsNullOrEmpty(raw)) return result;
        try
        {
            var arr = JsonSerializer.Deserialize<int[][]>(raw);
            if (arr is not null)
                foreach (var p in arr)
                    if (p.Length >= 2) result.Add((p[0], p[1]));
        }
        catch { /* ignore parse errors */ }
        return result;
    }

    private void SetCompStars(List<(int X, int Y)> pairs)
    {
        var deduped = new List<(int X, int Y)>();
        foreach (var (x, y) in pairs)
        {
            if (deduped.Any(e => Math.Abs(e.X - x) <= 5 && Math.Abs(e.Y - y) <= 5)) continue;
            deduped.Add((x, y));
            if (deduped.Count == 10) break;
        }
        CompXY = "[" + string.Join(", ", deduped.Select(p => $"[{p.X}, {p.Y}]")) + "]";
    }

    private static bool TryParseXY(string raw, out int x, out int y)
    {
        x = y = 0;
        raw = raw.Trim();
        if (string.IsNullOrEmpty(raw)) return false;
        try
        {
            var arr = JsonSerializer.Deserialize<int[]>(raw);
            if (arr is not null && arr.Length >= 2) { x = arr[0]; y = arr[1]; return true; }
        }
        catch { /* fall through */ }
        return false;
    }

    [RelayCommand] private void CancelPlateSolve() => _psCts?.Cancel();

    [RelayCommand] private async Task RetryPlateSolve()
    {
        if (string.IsNullOrEmpty(_psFitsPath)) return;
        if (string.IsNullOrEmpty(_psExoticExe))
            _psExoticExe = ExoticFinder.Find("") ?? "";
        if (string.IsNullOrEmpty(_psExoticExe))
        {
            PlateSolveStatus = "⚠  EXOTIC not found — install EXOTIC and retry";
            return;
        }
        await StartPlateSolveAsync(_psFitsPath, _psSaveDir, _psExoticExe);
    }

    [RelayCommand] private async Task FetchFromNea() => await FetchFromNeaAsync();

    private CancellationTokenSource? _neaCts;

    public async Task FetchFromNeaAsync()
    {
        var planet = PlanetName.Trim();
        if (string.IsNullOrEmpty(planet))
        {
            NeaStatus = "⚠  Enter a planet name first.";
            return;
        }

        _neaCts?.Cancel();
        _neaCts = new CancellationTokenSource();
        var ct = _neaCts.Token;

        _isNeaRunning = true;
        NeaStatus = "⟳  Querying NASA Exoplanet Archive…";
        Services.SessionLogService.Write($"[NEA] Querying: \"{planet}\"");

        NeaService.PlanetData? data;
        try
        {
            data = await NeaService.FetchAsync(planet, ct);
        }
        catch (OperationCanceledException) { _isNeaRunning = false; return; }
        catch (Exception ex)
        {
            _isNeaRunning = false;
            NeaStatus = $"✗  {ex.Message}";
            Services.SessionLogService.Write($"[NEA] ERROR — {ex.GetType().Name}: {ex.Message}");
            PlayStatusSoundAction?.Invoke(false);
            if (_wcsReady && !string.IsNullOrEmpty(_wcsFitsPath))
                _ = StartAavsoCompFetchAsync(_wcsFitsPath);
            return;
        }

        if (data is null)
        {
            _isNeaRunning = false;
            NeaStatus = $"✗  \"{planet}\" not found in NASA Exoplanet Archive.";
            Services.SessionLogService.Write($"[NEA] Not found: \"{planet}\"");
            PlayStatusSoundAction?.Invoke(false);
            if (_wcsReady && !string.IsNullOrEmpty(_wcsFitsPath))
                _ = StartAavsoCompFetchAsync(_wcsFitsPath);
            return;
        }

        PlanetName        = data.PlanetName;
        HostStarName      = data.HostStarName;
        TargetRa          = data.Ra;
        TargetDec         = data.Dec;
        OrbitalPeriod     = data.OrbitalPeriod;
        OrbitalPeriodUnc  = data.OrbitalPeriodUnc;
        MidTransitTime    = data.MidTransitTime;
        MidTransitTimeUnc = data.MidTransitTimeUnc;
        RpRs              = data.RpRs;
        RpRsUnc           = data.RpRsUnc;
        ARs               = data.ARs;
        ARsUnc            = data.ARsUnc;
        Inclination       = data.Inclination;
        InclinationUnc    = data.InclinationUnc;
        Eccentricity      = data.Eccentricity;
        ArgPeriastron     = data.ArgPeriastron;
        Teff              = data.Teff;
        TeffPlus          = data.TeffPlus;
        TeffMinus         = data.TeffMinus;
        Metallicity       = data.Metallicity;
        MetallicityPlus   = data.MetallicityPlus;
        MetallicityMinus  = data.MetallicityMinus;
        Logg              = data.Logg;
        LoggPlus          = data.LoggPlus;
        LoggMinus         = data.LoggMinus;
        StarDistance      = data.StarDistance;
        PmRa              = data.PmRa;
        PmDec             = data.PmDec;

        _isNeaRunning = false;
        NeaStatus = string.IsNullOrEmpty(data.RpRsWarning)
            ? $"✓  Loaded from NEA — {data.PlanetName}  ({DateTime.Now:HH:mm:ss})"
            : data.RpRsWarning;
        PlayStatusSoundAction?.Invoke(true);
        Services.SessionLogService.Write($"[NEA] ✓  Loaded: {data.PlanetName}");
        if (!string.IsNullOrEmpty(data.RpRsWarning))
            Services.SessionLogService.Write($"[NEA] {data.RpRsWarning}");
        if (!string.IsNullOrEmpty(data.DefaultsApplied))
            Services.SessionLogService.Write($"[NEA] Defaults applied: {data.DefaultsApplied.Replace(Environment.NewLine, " | ")}");

        // Notify so the VSP star field can auto-fill (uses host star name, fallback to planet name)
        var targetName = string.IsNullOrWhiteSpace(data.HostStarName) ? data.PlanetName : data.HostStarName;
        if (!string.IsNullOrWhiteSpace(targetName))
            TargetNameChanged?.Invoke(targetName);

        // Alert user to any defaulted parameters so they can review before running
        if (!string.IsNullOrEmpty(data.DefaultsApplied) && ShowInfoFunc is not null)
            _ = ShowInfoFunc("NEA Parameters — Defaults Applied",
                "One or more parameters were missing from the NASA Exoplanet Archive and have been estimated. " +
                "Please review the values below and edit them on the Equipment & Target tab if you have better data before running EXOTIC.\n\n" +
                data.DefaultsApplied);

        // Re-trigger comp fetch now that both TargetRa and TargetDec are populated.
        // This supersedes the earlier WCS-fallback fetch that fired from NotifyWcsReady
        // before NEA completed, giving it accurate planet coords instead of image-centre coords.
        if (_wcsReady && !string.IsNullOrEmpty(_wcsFitsPath))
            _ = StartAavsoCompFetchAsync(_wcsFitsPath);
    }

    [RelayCommand] private void SavePixelScale()
    { if (!string.IsNullOrWhiteSpace(PixelScale) && !PixelScales.Contains(PixelScale)) PixelScales.Add(PixelScale); }

    [RelayCommand] private void RemovePixelScale() => PixelScales.Remove(PixelScale);

    [RelayCommand] private void SaveNote()
    { if (!string.IsNullOrWhiteSpace(Notes) && !NotesList.Contains(Notes)) NotesList.Add(Notes); }

    [RelayCommand] private void RemoveNote() => NotesList.Remove(Notes);
}
