using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TransitLab.Models;
using TransitLab.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace TransitLab.ViewModels;

public partial class ObservationViewModel : ViewModelBase
{
    // Set by MainWindowViewModel so ReadFitsHeader can populate equipment fields too
    public EquipmentTargetViewModel? EquipmentTarget { get; set; }

    // Set by MainWindowViewModel from config
    public string PythonExePath { get; set; } = "";
    public string ExoticExePath { get; set; } = "";

    // Set by MainWindowViewModel to resolve the currently-ACTIVE multi-environment
    // Python path (Setup tab's Stable/Pre-release slot), so Plate Solve checks the same
    // install that Check System and Save & Run use — falling back to PythonExePath above
    // (the legacy shared base Python) only if no active environment is configured.
    public Func<string>? ResolveActivePythonPathFunc { get; set; }

    // ── Directories ───────────────────────────────────────────────────────────
    [ObservableProperty] private string _fitsDir          = "";
    [ObservableProperty] private string _saveDir          = "";
    // True once the user has manually browsed for a Save Plots directory.
    // Persisted via config so a deliberate choice is honoured across sessions.
    public bool SaveDirUserSet { get; set; } = false;
    [ObservableProperty] private string _darksDir         = "";
    [ObservableProperty] private string _flatsDir         = "";
    [ObservableProperty] private string _biasDir          = "";
    [ObservableProperty] private string _fitsHeaderStatus = "";
    private bool _darksAuto = true;
    private bool _flatsAuto = true;
    private bool _biasAuto  = true;

    // ── OSC (one-shot-color) detection + debayer ──────────────────────────────
    public Func<string, string, Task<bool>>? ShowConfirmFunc     { get; set; }
    public Func<Task>?                       TriggerScanFramesFunc { get; set; }
    [ObservableProperty] private bool   _isDebayering       = false;
    [ObservableProperty] private string _debayerStatus      = "";
    [ObservableProperty] private int    _debayerProgress    = 0;
    [ObservableProperty] private int    _debayerProgressMax = 1;
    // Exact directory paths already prompted (debayered or explicitly skipped) —
    // never re-prompt for the same directory twice in a session.
    private readonly HashSet<string> _oscHandledDirs = new(StringComparer.OrdinalIgnoreCase);
    // Suppresses the per-directory OSC check while OnFitsDirChanged's own auto-cascade
    // (Darks/Flats/Bias auto-derivation) is in progress, so the cascade produces exactly
    // one combined check/prompt instead of one per cascaded directory.
    private bool _suppressOscCheck = false;

    // ── Observer Information ──────────────────────────────────────────────────
    [ObservableProperty] private string _aavsoCode      = "";
    [ObservableProperty] private string _secondaryCode  = "";
    [ObservableProperty] private string _obsDate        = "";
    public ObservableCollection<string> AavsoCodes     { get; } = new();
    public ObservableCollection<string> SecondaryCodes { get; } = new();

    // ── Observatory / Location ────────────────────────────────────────────────
    [ObservableProperty] private string _selectedObservatory = "— custom —";
    [ObservableProperty] private string _latitude   = "";
    [ObservableProperty] private string _longitude  = "";
    [ObservableProperty] private string _elevation  = "";
    private readonly List<Observatory> _observatories = new();
    public ObservableCollection<string> ObservatoryNames { get; } = new() { "— custom —" };

    [ObservableProperty] private string _observatoryStatus = "";

    // Injected by the View. Args: (dialog title, suggested starting directory).
    public Func<string, string, Task<string?>>? FolderPickerFunc  { get; set; }
    public Func<string, Task<string?>>? NameDialogFunc    { get; set; }  // shows input dialog, returns typed name or null

    // ── FITS dir / header-read tracking (item 9) ──────────────────────────────
    // Set to true only when user browses to a new directory; cleared when header is read.
    private bool _fitsDirNeedsHeaderRead = false;
    public  bool FitsDirNeedsHeaderRead  => _fitsDirNeedsHeaderRead;
    // Wired by MainWindowViewModel to propagate to CanSaveAndRun
    public Action? FitsDirNeedsHeaderReadChanged   { get; set; }
    // Wired by MainWindowViewModel to clear all session-specific fields on new FITS dir
    public Action? ClearSessionFieldsCallback      { get; set; }

    // Injected by MainWindowViewModel — called whenever a list or observatory changes
    public Action? ConfigSaveCallback { get; set; }

    // Injected by MainWindowViewModel — auto-scan + exclude flagged, return first non-excluded path
    public Func<Task<string?>>? AutoScanAndGetFitsFunc { get; set; }

    private System.Threading.Timer? _fitsDirScanTimer;

    // ── Population from config ────────────────────────────────────────────────
    public void LoadFromConfig(
        IEnumerable<string> aavsoCodesFromCfg,
        IEnumerable<string> secondaryCodesFromCfg,
        IEnumerable<Models.Observatory> observatoriesFromCfg)
    {
        AavsoCodes.Clear();
        foreach (var c in aavsoCodesFromCfg) AavsoCodes.Add(c);

        SecondaryCodes.Clear();
        foreach (var c in secondaryCodesFromCfg) SecondaryCodes.Add(c);

        _observatories.Clear();
        ObservatoryNames.Clear();
        ObservatoryNames.Add("— custom —");
        foreach (var obs in observatoriesFromCfg)
        {
            _observatories.Add(obs);
            ObservatoryNames.Add(obs.Name);
        }
    }

    // ── Expose live lists for config snapshot ─────────────────────────────────
    public IReadOnlyList<string>             LiveAavsoCodes     => AavsoCodes;
    public IReadOnlyList<string>             LiveSecondaryCodes => SecondaryCodes;
    public IReadOnlyList<Models.Observatory> LiveObservatories  => _observatories;

    // ── Reactive helpers ──────────────────────────────────────────────────────
    partial void OnFitsDirChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var fitsDir = value.TrimEnd('/', '\\');
        var parent  = Path.GetDirectoryName(fitsDir) ?? "";

        // Auto-fill SaveDir whenever FitsDir changes, unless the user has
        // deliberately browsed to a different Save Plots location.
        _suppressOscCheck = true;
        if (!SaveDirUserSet)
            SaveDir = string.IsNullOrEmpty(parent) ? fitsDir : parent;
        if (_darksAuto) DarksDir = FindCalibDir(fitsDir, parent, "dark", "darks") ?? "";
        if (_flatsAuto) FlatsDir = FindCalibDir(fitsDir, parent, "flat", "flats") ?? "";
        if (_biasAuto)  BiasDir  = FindCalibDir(fitsDir, parent, "bias", "biases", "biasd", "bias frames") ?? "";
        _suppressOscCheck = false;

        // Debounced auto-scan: cancel any pending scan and schedule a new one 600ms out
        _fitsDirScanTimer?.Dispose();
        _fitsDirScanTimer = new System.Threading.Timer(_ =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                AutoScanAndGetFitsFunc?.Invoke());
        }, null, 600, System.Threading.Timeout.Infinite);

        _ = CheckForOscAndPromptAsync();
    }

    partial void OnDarksDirChanged(string value) { if (!_suppressOscCheck) _ = CheckForOscAndPromptAsync(); }
    partial void OnFlatsDirChanged(string value) { if (!_suppressOscCheck) _ = CheckForOscAndPromptAsync(); }
    partial void OnBiasDirChanged(string value)  { if (!_suppressOscCheck) _ = CheckForOscAndPromptAsync(); }

    /// <summary>
    /// Checks Lights/Darks/Flats/Biases for raw one-shot-color (Bayer) data and, if found,
    /// offers to debayer them in-app before Image Analysis, Plate Solve, or the Stone comp
    /// method ever touch the mosaic pixels. Shows one combined prompt covering whichever
    /// directories are affected, rather than one prompt per directory.
    /// </summary>
    private async Task CheckForOscAndPromptAsync()
    {
        var candidates = new (string Label, string Dir)[]
        {
            ("Lights", FitsDir), ("Darks", DarksDir), ("Flats", FlatsDir), ("Biases", BiasDir),
        };

        var toPrompt = new List<(string Label, string Dir, DebayerService.DetectResult Detect)>();
        foreach (var (label, dir) in candidates)
        {
            if (string.IsNullOrWhiteSpace(dir) || _oscHandledDirs.Contains(dir)) continue;
            var det = DebayerService.Detect(dir);
            if (det.IsOsc) toPrompt.Add((label, dir, det));
        }
        if (toPrompt.Count == 0) return;

        if (ShowConfirmFunc is null) return;

        var camera  = toPrompt[0].Detect.CameraName;
        var pattern = toPrompt[0].Detect.BayerPattern;
        var dirList = string.Join(", ", toPrompt.Select(t => t.Label));
        var cameraNote = string.IsNullOrEmpty(camera) ? $"Bayer pattern {pattern}" : $"{camera}, Bayer pattern {pattern}";

        var proceed = await ShowConfirmFunc("One-Shot-Color Data Detected",
            $"The {dirList} director{(toPrompt.Count == 1 ? "y appears" : "ies appear")} to contain " +
            $"raw color camera data ({cameraNote}).\n\n" +
            "Debayering should happen before Image Analysis, Plate Solve, or comp star selection — " +
            "running these on raw mosaic data distorts star detection and photometry.\n\n" +
            "Debayer these frames now with TransitLab?");

        // Declining marks nothing as handled — the same directory (or the same folder
        // re-selected later) is checked fresh again next time. "Skip" shouldn't
        // permanently suppress the prompt for the rest of the session.
        if (!proceed) return;

        // Disable calibration-dir auto-follow before redirecting FitsDir below — otherwise
        // OnFitsDirChanged's own auto-cascade would re-derive Darks/Flats/Bias from the new
        // (debayered) Lights path and clobber the redirects this method makes for them.
        _darksAuto = false;
        _flatsAuto = false;
        _biasAuto  = false;

        IsDebayering = true;
        foreach (var (label, dir, _) in toPrompt)
        {
            DebayerProgress    = 0;
            DebayerProgressMax = 1;
            DebayerStatus      = $"⟳  Debayering {label}…";
            var result = await DebayerService.DebayerDirectoryAsync(dir, ExoticExePath, PythonExePath,
                new Progress<DebayerService.Progress>(p =>
                {
                    DebayerProgress    = p.Done;
                    DebayerProgressMax = Math.Max(p.Total, 1);
                    DebayerStatus      = $"⟳  Debayering {label}: {p.LastMessage}";
                }));
            if (!result.Success)
            {
                DebayerStatus = $"✗  {label}: {result.Message}";
                continue; // not marked handled — a failed directory can be retried later
            }

            // Only mark handled once debayering has actually succeeded for this directory.
            _oscHandledDirs.Add(dir);

            _suppressOscCheck = true;
            switch (label)
            {
                case "Lights": FitsDir  = result.OutputDir; break;
                case "Darks":  DarksDir = result.OutputDir; break;
                case "Flats":  FlatsDir = result.OutputDir; break;
                case "Biases": BiasDir  = result.OutputDir; break;
            }
            _suppressOscCheck = false;
        }
        IsDebayering  = false;
        DebayerStatus = "✓  Debayering complete.";

        if (TriggerScanFramesFunc is not null)
            await TriggerScanFramesFunc();
    }

    partial void OnSelectedObservatoryChanged(string value)
    {
        var obs = _observatories.FirstOrDefault(o => o.Name == value);
        if (obs is null) return;
        Latitude  = obs.Lat;
        Longitude = obs.Lon;
        Elevation = obs.Elev;
    }

    partial void OnObsDateChanged(string value)       => DebounceLog("Obs date",       value);
    partial void OnLatitudeChanged(string value)      => DebounceLog("Latitude",        value);
    partial void OnLongitudeChanged(string value)     => DebounceLog("Longitude",       value);
    partial void OnElevationChanged(string value)     => DebounceLog("Elevation",       value);
    partial void OnAavsoCodeChanged(string value)     => DebounceLog("AAVSO code",      value);
    partial void OnSecondaryCodeChanged(string value) => DebounceLog("Secondary code",  value);

    // ── Commands — Directories ────────────────────────────────────────────────
    // Darks/Flats/Bias/Save default their picker to the Lights directory when they don't
    // already have a directory of their own — the Data tab's fields no longer share a
    // "last folder" default with unrelated pickers elsewhere in the app (e.g. log export).
    private string StartDirFor(string current) => string.IsNullOrEmpty(current) ? FitsDir : current;

    [RelayCommand] private async Task BrowseFitsDir()
        => await BrowseFolder("Select FITS Files Directory", FitsDir, v =>
        {
            FitsDir = v;
            _fitsDirNeedsHeaderRead = true;
            FitsDirNeedsHeaderReadChanged?.Invoke();
            ClearSessionFieldsCallback?.Invoke();
            Services.SessionLogService.Write($"[FITS] FITS dir: {v}");
        });

    [RelayCommand] private async Task BrowseSaveDir()
        => await BrowseFolder("Select Save Plots Directory", StartDirFor(SaveDir), v =>
        {
            SaveDir = v;
            SaveDirUserSet = true;
            Services.SessionLogService.Write($"[Field] Save dir: {v}");
        });

    [RelayCommand] private async Task BrowseDarksDir()
    {
        _darksAuto = false;
        await BrowseFolder("Select Darks Directory", StartDirFor(DarksDir), v =>
        {
            DarksDir = v;
            Services.SessionLogService.Write($"[Field] Darks dir: {v}");
        });
    }

    [RelayCommand] private void ClearDarksDir()  { DarksDir  = ""; _darksAuto = true; }

    [RelayCommand] private async Task BrowseFlatsDir()
    {
        _flatsAuto = false;
        await BrowseFolder("Select Flats Directory", StartDirFor(FlatsDir), v =>
        {
            FlatsDir = v;
            Services.SessionLogService.Write($"[Field] Flats dir: {v}");
        });
    }

    [RelayCommand] private void ClearFlatsDir()  { FlatsDir = ""; _flatsAuto = true; }

    [RelayCommand] private async Task BrowseBiasDir()
    {
        _biasAuto = false;
        await BrowseFolder("Select Biases Directory", StartDirFor(BiasDir), v =>
        {
            BiasDir = v;
            Services.SessionLogService.Write($"[Field] Bias dir: {v}");
        });
    }

    [RelayCommand] private void ClearBiasDir()   { BiasDir  = ""; _biasAuto = true; }

    // ── Read FITS Header ──────────────────────────────────────────────────────
    [RelayCommand]
    private async Task ReadFitsHeader()
    {
        Services.SessionLogService.Write("[FITS] User clicked Read FITS Header");
        if (string.IsNullOrWhiteSpace(FitsDir))
        {
            FitsHeaderStatus = "⚠  Set the FITS Files Directory first.";
            return;
        }

        // Clear pixel coordinates and stale WCS — both are image-specific and must come from the new plate solve
        if (EquipmentTarget is not null)
        {
            EquipmentTarget.TargetXY = "";
            EquipmentTarget.ResetCompSelection();
            EquipmentTarget.ResetWcs();
        }

        // Auto-scan frames, exclude flagged, get first non-excluded file
        string? fitsPath;
        if (AutoScanAndGetFitsFunc is not null)
        {
            FitsHeaderStatus = "⟳  Scanning frames…";
            fitsPath = await AutoScanAndGetFitsFunc();
        }
        else
        {
            fitsPath = FitsHeaderService.FindFirstFits(FitsDir);
        }

        if (fitsPath is null)
        {
            FitsHeaderStatus = "⚠  No non-excluded FITS files found in the selected directory.";
            Services.SessionLogService.Write($"[FITS] No FITS files found in: {FitsDir}");
            return;
        }

        FitsHeaderStatus = $"Reading {Path.GetFileName(fitsPath)}…";

        FitsHeaderService.FitsHeader hdr;
        try
        {
            hdr = await Task.Run(() => FitsHeaderService.Read(fitsPath));
        }
        catch (Exception ex)
        {
            FitsHeaderStatus = $"✗  FITS read error: {ex.Message}";
            Services.SessionLogService.Write($"[FITS] ERROR reading {Path.GetFileName(fitsPath)}: {ex.Message}");
            return;
        }

        var populated = new System.Text.StringBuilder();

        // DATE-OBS → Observation Date (DD-MON-YYYY)
        var dateObs = hdr.Get("DATE-OBS");
        if (dateObs.Length >= 10)
        {
            var months = new[] { "", "JAN","FEB","MAR","APR","MAY","JUN",
                                      "JUL","AUG","SEP","OCT","NOV","DEC" };
            var year  = dateObs[..4];
            var month = int.TryParse(dateObs[5..7], out var m) ? m : 0;
            var day   = dateObs[8..10];
            var mon   = month is >= 1 and <= 12 ? months[month] : dateObs[5..7];
            ObsDate = $"{day}-{mon}-{year}";
            populated.Append($"Date: {ObsDate}  ");
        }
        else populated.Append("Date: not found  ");

        // Site lat / lon / elev
        var siteLat  = FirstOf(hdr, "SITELAT",  "LATITUDE",  "LAT",     "OBSLAT");
        var siteLon  = FirstOf(hdr, "SITELONG", "SITELON",   "LONGITUD", "LONGITUDE", "LON", "OBSLONG");
        var siteElev = FirstOf(hdr, "SITEELEV", "SITEEL",    "HEIGHT",   "ELEVAT",    "ELEV","ALTITUDE");

        if (!string.IsNullOrEmpty(siteLat))
        {
            var parsedLat = ParseLatLonToDecimal(siteLat);
            Latitude = parsedLat ?? (siteLat.StartsWith('+') || siteLat.StartsWith('-') ? siteLat : "+" + siteLat);
            populated.Append($"Lat: {Latitude}  ");
        }
        if (!string.IsNullOrEmpty(siteLon))
        {
            var parsedLon = ParseLatLonToDecimal(siteLon);
            Longitude = parsedLon ?? (siteLon.StartsWith('+') || siteLon.StartsWith('-') ? siteLon : "+" + siteLon);
            populated.Append($"Lon: {Longitude}  ");
        }
        if (!string.IsNullOrEmpty(siteElev)) { Elevation = siteElev; populated.Append($"Elev: {siteElev}m  "); }

        // Auto-match saved observatory by lat/lon
        if (!string.IsNullOrEmpty(Latitude) && !string.IsNullOrEmpty(Longitude))
        {
            var matched = _observatories.FirstOrDefault(o =>
                CoordApproxEqual(o.Lat, Latitude) && CoordApproxEqual(o.Lon, Longitude));
            if (matched is not null)
            {
                SelectedObservatory = matched.Name;
                populated.Append($"Obs: {matched.Name}  ");
            }
        }

        // Populate equipment fields if EquipmentTarget is wired up
        if (EquipmentTarget is not null)
        {
            // FILTER
            var filt = hdr.Get("FILTER");
            if (!string.IsNullOrEmpty(filt))
            {
                if (filt.Equals("C", StringComparison.OrdinalIgnoreCase) ||
                    filt.Equals("Clear", StringComparison.OrdinalIgnoreCase))
                    filt = "CV";
                EquipmentTarget.Filter = filt;
                populated.Append($"Filter: {filt}  ");

                // Populate filter wavelength range from standard AAVSO filter definitions
                var (fMin, fMax) = filt.ToUpperInvariant() switch
                {
                    "B"                    => ("380", "520"),
                    "V"                    => ("505", "695"),
                    "R"                    => ("560", "730"),
                    "I"                    => ("710", "920"),
                    "CV" or "CG" or "CBB"  => ("350", "850"),
                    "G" or "LP400"         => ("400", "700"),
                    "SG" or "SG'"          => ("400", "550"),
                    "SR" or "SR'"          => ("559", "695"),
                    "SI" or "SI'"          => ("695", "845"),
                    "SZ" or "SZ'"          => ("820", "920"),
                    "TG"                   => ("490", "600"),
                    "HA" or "H-ALPHA"      => ("645", "660"),
                    "OIII"                 => ("493", "503"),
                    _                      => ("", ""),
                };
                if (!string.IsNullOrEmpty(fMin))
                {
                    EquipmentTarget.FilterMin = fMin;
                    EquipmentTarget.FilterMax = fMax;
                }
            }

            // XBINNING + YBINNING
            var xbin = hdr.GetInt("XBINNING");
            var ybin = hdr.GetInt("YBINNING");
            if (xbin.HasValue && ybin.HasValue)
            {
                EquipmentTarget.Binning = $"{xbin}x{ybin}";
                populated.Append($"Bin: {xbin}x{ybin}  ");
            }

            // INSTRUME → Notes
            var instrume = hdr.Get("INSTRUME");
            if (!string.IsNullOrEmpty(instrume))
            {
                EquipmentTarget.Notes = instrume;
                populated.Append($"Instr: {instrume}  ");
            }

            // Pixel scale = (206265 / FOCALLEN) × (XPIXSZ / 1000)
            var focal = hdr.GetDouble("FOCALLEN");
            var xpix  = hdr.GetDouble("XPIXSZ");
            var ypix  = hdr.GetDouble("YPIXSZ");
            if (focal.HasValue && xpix.HasValue && ypix.HasValue && xpix == ypix && focal > 0)
            {
                var scale = (206265.0 / focal.Value) * (xpix.Value / 1000.0);
                EquipmentTarget.PixelScale = scale.ToString("0.##",
                    System.Globalization.CultureInfo.InvariantCulture);
                populated.Append($"Scale: {EquipmentTarget.PixelScale} arcsec/px  ");
            }

            // OBJECT → Planet Name, then trigger NEA fetch
            var obj = hdr.Get("OBJECT");
            if (!string.IsNullOrEmpty(obj))
            {
                obj = NormalizePlanetName(obj);
                EquipmentTarget.PlanetName = obj;
                populated.Append($"Planet: {obj}");
                // Fire-and-forget NEA fetch
                _ = EquipmentTarget.FetchFromNeaAsync();
            }
        }

        FitsHeaderStatus = $"✓  {Path.GetFileName(fitsPath)}";
        _fitsDirNeedsHeaderRead = false;
        FitsDirNeedsHeaderReadChanged?.Invoke();

        // Always update plate solve status based on WCS + config state.
        _lastFitsPath = fitsPath;
        var hasCtype1  = !string.IsNullOrEmpty(hdr.Get("CTYPE1"));
        // The first non-excluded file may be a bad/dark frame that was never solvable
        // and therefore never received a plate solution.  If CTYPE1 is absent in that
        // file, scan up to 5 additional files in the directory — if any already carry
        // a WCS we can skip re-solving and call NotifyWcsReady with a solved file path.
        string wcsReadyPath = fitsPath;
        if (!hasCtype1)
        {
            var fitsDir = Path.GetDirectoryName(fitsPath) ?? "";
            if (!string.IsNullOrEmpty(fitsDir))
            {
                var candidates = Directory.GetFiles(fitsDir, "*.fits", SearchOption.TopDirectoryOnly)
                    .Concat(Directory.GetFiles(fitsDir, "*.fit",  SearchOption.TopDirectoryOnly))
                    .Concat(Directory.GetFiles(fitsDir, "*.fts",  SearchOption.TopDirectoryOnly))
                    .Concat(Directory.GetFiles(fitsDir, "*.fz",   SearchOption.TopDirectoryOnly))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .Where(f => !f.Equals(fitsPath, StringComparison.OrdinalIgnoreCase))
                    .Take(5);
                foreach (var candidate in candidates)
                {
                    try
                    {
                        var cHdr = await Task.Run(() => FitsHeaderService.Read(candidate));
                        if (!string.IsNullOrEmpty(cHdr.Get("CTYPE1")))
                        {
                            hasCtype1    = true;
                            wcsReadyPath = candidate;
                            Services.SessionLogService.Write(
                                $"[FITS] WCS found in {Path.GetFileName(candidate)} — skipping plate solve");
                            break;
                        }
                    }
                    catch { /* unreadable — skip */ }
                }
            }
        }

        if (EquipmentTarget is not null)
        {
            if (hasCtype1)
            {
                EquipmentTarget.PlateSolveStatus = "✓  WCS already present — no plate solve needed";
                EquipmentTarget.IsPsRetryEnabled = false;
                EquipmentTarget.NotifyWcsReady(wcsReadyPath, SaveDir, ExoticExePath);
            }
            else
            {
                var isAstap = EquipmentTarget.PlateSolverConfig?.Solver == "ASTAP";

                // ASTAP doesn't need EXOTIC; astrometry.net does
                if (!isAstap)
                {
                    if (!await ResolveExoticRuntimeForPlateSolveAsync())
                    {
                        EquipmentTarget.PlateSolveStatus = "⚠  EXOTIC not found — install EXOTIC and retry";
                        EquipmentTarget.IsPsRetryEnabled = true;
                        return;
                    }
                }

                FitsHeaderStatus += "  |  Starting plate solve…";
                var solveFile = Path.GetFileName(fitsPath);
                _ = EquipmentTarget.StartPlateSolveAsync(fitsPath, SaveDir, ExoticExePath, PythonExePath)
                    .ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        // Lead with the plate solve's own ✓/⚠/✗ prefix (not a hardcoded ✓) so the
                        // status color reflects the real outcome — a partial or failed solve
                        // previously still rendered green here regardless of what actually happened.
                        FitsHeaderStatus = $"{EquipmentTarget.PlateSolveStatus}  |  {solveFile}"));
            }
        }
    }

    // ── Plate Solve command (manual trigger) ──────────────────────────────────
    private string _lastFitsPath = "";

    [RelayCommand]
    private async Task StartPlateSolve()
    {
        if (EquipmentTarget is null) return;

        // Prefer the live, exclusion-aware lookup over the cached path from the last Read
        // FITS Header — a frame excluded afterward in Image Analysis would otherwise keep
        // getting solved anyway, since _lastFitsPath is only stale-checked for emptiness.
        string path;
        if (AutoScanAndGetFitsFunc is not null)
            path = await AutoScanAndGetFitsFunc() ?? _lastFitsPath;
        else
        {
            path = _lastFitsPath;
            if (string.IsNullOrEmpty(path))
                path = FitsHeaderService.FindFirstFits(FitsDir) ?? "";
        }
        if (string.IsNullOrEmpty(path))
        {
            EquipmentTarget.PlateSolveStatus = "⚠  No FITS file found — set FITS Files Directory first";
            return;
        }
        var isAstap = EquipmentTarget.PlateSolverConfig?.Solver == "ASTAP";
        if (!isAstap)
        {
            if (!await ResolveExoticRuntimeForPlateSolveAsync())
            {
                EquipmentTarget.PlateSolveStatus = "⚠  EXOTIC not found — install EXOTIC and retry";
                return;
            }
        }

        await EquipmentTarget.StartPlateSolveAsync(path, SaveDir, ExoticExePath, PythonExePath);
    }

    private async Task<bool> ResolveExoticRuntimeForPlateSolveAsync()
    {
        var pythonPath = ResolveActivePythonPathFunc?.Invoke();
        if (string.IsNullOrWhiteSpace(pythonPath)) pythonPath = PythonExePath;

        var runtime = await ExoticRuntimeService.ResolveAsync(pythonPath, ExoticExePath);
        if (runtime is null) return false;

        PythonExePath = runtime.PythonExePath;
        if (!string.IsNullOrWhiteSpace(runtime.ExoticExePath))
            ExoticExePath = runtime.ExoticExePath;
        return true;
    }

    // ── Commands — Observer Info ──────────────────────────────────────────────
    [RelayCommand] private void SaveAavso()
    {
        if (string.IsNullOrWhiteSpace(AavsoCode) || AavsoCodes.Contains(AavsoCode)) return;
        var saved = AavsoCode;
        AavsoCodes.Add(saved);
        ConfigSaveCallback?.Invoke();
        // Re-assign so the ComboBox field reflects the newly saved entry
        AavsoCode = "";
        AavsoCode = saved;
    }

    [RelayCommand] private void RemoveAavso()
    {
        AavsoCodes.Remove(AavsoCode);
        ConfigSaveCallback?.Invoke();
    }

    [RelayCommand] private void SaveSecondary()
    {
        if (string.IsNullOrWhiteSpace(SecondaryCode) || SecondaryCodes.Contains(SecondaryCode)) return;
        var saved = SecondaryCode;
        SecondaryCodes.Add(saved);
        ConfigSaveCallback?.Invoke();
        // Re-assign so the ComboBox field reflects the newly saved entry
        SecondaryCode = "";
        SecondaryCode = saved;
    }

    [RelayCommand] private void RemoveSecondary()
    {
        SecondaryCodes.Remove(SecondaryCode);
        ConfigSaveCallback?.Invoke();
    }

    // ── Commands — Observatory ────────────────────────────────────────────────
    [RelayCommand] private async Task SaveObservatory()
    {
        // Determine default name: currently selected (if not custom) or empty
        var defaultName = SelectedObservatory == "— custom —" ? "" : SelectedObservatory.Trim();

        // Prompt for name
        var name = NameDialogFunc is not null
            ? await NameDialogFunc(defaultName)
            : defaultName;

        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();

        var existing = _observatories.FirstOrDefault(o => o.Name == name);
        if (existing is not null)
        {
            existing.Lat  = Latitude;
            existing.Lon  = Longitude;
            existing.Elev = Elevation;
            ObservatoryStatus = $"✓  Updated \"{name}\"";
        }
        else
        {
            var obs = new Models.Observatory { Name = name, Lat = Latitude, Lon = Longitude, Elev = Elevation };
            _observatories.Add(obs);
            ObservatoryNames.Add(name);
            SelectedObservatory = name;
            ObservatoryStatus = $"✓  Saved \"{name}\"";
        }
        ConfigSaveCallback?.Invoke();
    }

    [RelayCommand] private void RemoveObservatory()
    {
        var name = SelectedObservatory.Trim();
        if (string.IsNullOrEmpty(name) || name == "— custom —") return;

        var obs = _observatories.FirstOrDefault(o => o.Name == name);
        if (obs is not null) _observatories.Remove(obs);
        ObservatoryNames.Remove(name);
        SelectedObservatory = "— custom —";
        ConfigSaveCallback?.Invoke();
    }

    // ── Private helpers ───────────────────────────────────────────────────────
    private async Task BrowseFolder(string title, string startDir, Action<string> setter)
    {
        if (FolderPickerFunc is null) return;
        var path = await FolderPickerFunc(title, startDir);
        if (path is not null) setter(path);
    }

    /// <summary>
    /// Converts a FITS-header latitude/longitude value to a plain decimal-degree string.
    /// Handles three conventions seen in the wild: plain decimal ("31.68", "-110.88" — what
    /// MObs and most amateur setups write, already correct as-is), sexagesimal DMS with a
    /// leading sign ("+28 17 58.8", "-16 30 39.7"), and sexagesimal DMS with a trailing
    /// hemisphere letter instead of a sign ("28 17 58.8 S", "16 30 39.7 W" — e.g. Teide
    /// Observatory's convention). The previous code only ever checked for a leading sign, so
    /// a trailing-letter value was never converted from DMS to decimal AND got the wrong sign
    /// (an unsigned "S"/"W" value was blindly treated as positive). A hemisphere letter, when
    /// present, is treated as authoritative over any leading sign on the same value.
    /// Returns null if the value doesn't match any of these shapes.
    /// </summary>
    private static string? ParseLatLonToDecimal(string raw)
    {
        var s = raw.Trim();
        if (s.Length == 0) return null;

        char? hemisphere = null;
        var last = char.ToUpperInvariant(s[^1]);
        if (last is 'N' or 'S' or 'E' or 'W')
        {
            hemisphere = last;
            s = s[..^1].TrimEnd();
        }

        bool negative = false;
        if (s.StartsWith('-'))      { negative = true; s = s[1..].TrimStart(); }
        else if (s.StartsWith('+')) { s = s[1..].TrimStart(); }

        var parts = s.Split([' ', ':'], StringSplitOptions.RemoveEmptyEntries);
        double value;
        switch (parts.Length)
        {
            case 1:
                if (!NumericParseService.TryParse(parts[0], out value)) return null;
                break;
            case 2 or 3:
                if (!NumericParseService.TryParse(parts[0], out var deg)) return null;
                if (!NumericParseService.TryParse(parts[1], out var min)) return null;
                double sec = 0;
                if (parts.Length == 3 && !NumericParseService.TryParse(parts[2], out sec)) return null;
                value = deg + min / 60.0 + sec / 3600.0;
                break;
            default:
                return null;
        }

        if (hemisphere is 'S' or 'W') negative = true;
        else if (hemisphere is 'N' or 'E') negative = false;

        value = negative ? -Math.Abs(value) : Math.Abs(value);
        var formatted = value.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);

        // EXOTIC's own input validation (inputs.py: latitude()/longitude()) requires an
        // explicit leading '+' or '-' on both fields — a bare unsigned positive value like
        // "31.68" is rejected outright ("You forgot the sign for the latitude!  North is '+'
        // and South is '-'.") — so a sign must always be emitted, not only for negatives.
        return formatted.StartsWith('-') ? formatted : "+" + formatted;
    }

    private static bool CoordApproxEqual(string a, string b)
    {
        if (!NumericParseService.TryParse(a.TrimStart('+'), out var da)) return false;
        if (!NumericParseService.TryParse(b.TrimStart('+'), out var db)) return false;
        return Math.Abs(da - db) < 0.01;   // ~1 km tolerance
    }

    private static string FirstOf(FitsHeaderService.FitsHeader hdr, params string[] keys)
    {
        foreach (var k in keys)
        {
            var v = hdr.Get(k);
            if (!string.IsNullOrEmpty(v)) return v;
        }
        return "";
    }

    /// Searches fitsDir then its parent for a subdirectory matching any of the given names
    /// (case-insensitive). Returns the full path if found, null otherwise.
    private static string? FindCalibDir(string fitsDir, string parent, params string[] names)
    {
        foreach (var baseDir in new[] { fitsDir, parent })
        {
            if (!Directory.Exists(baseDir)) continue;
            foreach (var subdir in Directory.GetDirectories(baseDir))
            {
                var dirName = Path.GetFileName(subdir);
                if (names.Any(n => dirName.Equals(n, StringComparison.OrdinalIgnoreCase)))
                    return subdir;
            }
        }
        return null;
    }

    private static string NormalizePlanetName(string name)
    {
        // HATP- → HAT-P-
        name = Regex.Replace(name, @"\bHATP-", "HAT-P-", RegexOptions.IgnoreCase);
        // TOI1234 → TOI-1234
        name = Regex.Replace(name, @"\bTOI[\s-]*(\d)", "TOI-$1", RegexOptions.IgnoreCase);
        // Insert space before trailing capital stuck to previous char: WASP-160B → WASP-160 B
        name = Regex.Replace(name, @"([^\s])([A-Z])$", "$1 $2");
        // Append ' b' if no trailing lowercase planet letter
        if (!Regex.IsMatch(name, @"\s+[a-z]$"))
            name += " b";
        return name;
    }
}
