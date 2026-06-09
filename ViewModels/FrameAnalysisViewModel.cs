using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TransitLab.Models;
using TransitLab.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TransitLab.ViewModels;

public partial class FrameAnalysisViewModel : ViewModelBase
{
    // ── Scan controls ─────────────────────────────────────────────────────────
    [ObservableProperty] private string  _scanStatus    = "No files scanned yet.";
    [ObservableProperty] private decimal _flagSigma     = 3.0m;
    [ObservableProperty] private bool    _isScanRunning = false;

    // ── Exclusion row ─────────────────────────────────────────────────────────
    [ObservableProperty] private string  _exclusionStatus      = "0 images excluded";
    [ObservableProperty] private string  _flaggedStatus        = "0 images flagged";
    [ObservableProperty] private bool    _isDarkSubtract       = false;

    // ── VSP controls ──────────────────────────────────────────────────────────
    [ObservableProperty] private decimal _vspFov        = 60m;
    [ObservableProperty] private decimal _vspMag        = 14.0m;

    // ── Image viewer — stretch ────────────────────────────────────────────────
    [ObservableProperty] private double  _blackValue    = 0.0;
    [ObservableProperty] private double  _whiteValue    = 65535.0;
    // Per-slider ranges: centered on auto-stretch values so thumbs start at 50%
    [ObservableProperty] private double  _blackMin      = 0.0;
    [ObservableProperty] private double  _blackMax      = 65535.0;
    [ObservableProperty] private double  _whiteMin      = 0.0;
    [ObservableProperty] private double  _whiteMax      = 65535.0;
    private double _absoluteDataMin = 0.0;
    private double _absoluteDataMax = 65535.0;
    [ObservableProperty] private string  _blackLabel    = "0";
    [ObservableProperty] private string  _whiteLabel    = "65535";
    [ObservableProperty] private string  _cursorText    = "X: —  Y: —  ADU: —";
    [ObservableProperty] private double  _imageDisplayWidth  = 0;
    [ObservableProperty] private double  _imageDisplayHeight = 0;
    [ObservableProperty] private bool    _hasImage      = false;

    // Bitmaps managed manually — CommunityToolkit's SetProperty skips updates for the same reference
    private WriteableBitmap? _frameBitmapBacking;
    public WriteableBitmap? FrameBitmap
    {
        get => _frameBitmapBacking;
        private set { _frameBitmapBacking = value; OnPropertyChanged(); }
    }

    private WriteableBitmap? _histogramBitmapBacking;
    public WriteableBitmap? HistogramBitmap
    {
        get => _histogramBitmapBacking;
        private set { _histogramBitmapBacking = value; OnPropertyChanged(); }
    }

    // ── Data ──────────────────────────────────────────────────────────────────
    public ObservableCollection<FrameEntry> Frames { get; } = new();

    public FrameAnalysisViewModel()
    {
        Frames.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is not null)
                foreach (FrameEntry entry in e.NewItems)
                    entry.PropertyChanged += (_, pe) =>
                    {
                        if (pe.PropertyName == nameof(FrameEntry.IsExcluded))
                            RefreshExclusionCount();
                    };
        };
    }

    // ── FOV display ───────────────────────────────────────────────────────────
    [ObservableProperty] private string _fovText = "";

    // ── VSP star name — editable on the Image Analysis tab ────────────────────
    [ObservableProperty] private string _vspStar = "";

    // ── Injected ──────────────────────────────────────────────────────────────
    public Func<string>?  FitsDirFunc    { get; set; }
    public Func<string>?  DarksDirFunc   { get; set; }
    public Func<string>?  TargetNameFunc { get; set; }
    /// <summary>Called by code-behind to display the VSP chart in a popup. Args: (url, title, ct).</summary>
    public Func<string, string, CancellationToken, Task>? ShowVspFunc { get; set; }

    // ── VSP loading state ─────────────────────────────────────────────────────
    [ObservableProperty] private bool _isVspLoading = false;
    private CancellationTokenSource? _vspCts;

    partial void OnIsVspLoadingChanged(bool value) => OnPropertyChanged(nameof(VspButtonLabel));
    public string VspButtonLabel => IsVspLoading ? "⟳  Fetching chart…" : "VSP Chart";

    [RelayCommand]
    private void CancelVsp()
    {
        _vspCts?.Cancel();
    }
    /// <summary>Called whenever pick markers or zoom change and the canvas overlay needs a redraw.</summary>
    public Action? RedrawMarkersAction { get; set; }

    // ── Pick coordinate accessors (for overlay rendering) ─────────────────────
    public (int X, int Y)? TargetPickCoords =>
        _targetPickX.HasValue && _targetPickY.HasValue
            ? (_targetPickX.Value, _targetPickY.Value) : null;
    public IReadOnlyList<(int X, int Y)> CompPickCoords => _compPicks;

    // ── Image / zoom state ────────────────────────────────────────────────────
    private FitsImageService.FitsImage? _currentImage;
    private string _currentImagePath = "";
    private double _zoomFactor    = 1.0;
    private double _fitZoomFactor = 0.05;   // minimum zoom = fit-to-viewer
    private bool   _suppressRender    = false;
    // _programmaticChange suppresses the stretch-lock during auto-stretch and image-load clamping.
    private bool   _programmaticChange = false;
    // Set to true when the user manually adjusts sliders; cleared by the Auto Stretch button.
    private bool   _stretchLocked = false;

    // ── Pick mode ─────────────────────────────────────────────────────────────
    private bool   _isTargetPickMode = false;
    private bool   _isCompPickMode   = false;
    private int?   _targetPickX;
    private int?   _targetPickY;
    private readonly List<(int X, int Y)> _compPicks = new();

    [ObservableProperty] private string _targetPickStatus = "";
    [ObservableProperty] private string _compPickStatus   = "";
    [ObservableProperty] private int    _compPickCount    = 0;
    [ObservableProperty] private string _pickTargetLabel  = "Pick Target Star";
    [ObservableProperty] private string _pickCompLabel    = "Pick Comp Stars";

    public bool IsTargetPickMode => _isTargetPickMode;
    public bool IsCompPickMode   => _isCompPickMode;

    // ── Master dark ───────────────────────────────────────────────────────────
    private float[]? _masterDark;
    private int      _masterDarkW, _masterDarkH;
    private float[]? _displayPixels;  // dark-subtracted pixels, or null → use raw

    private float[] GetDisplayPixels()
    {
        if (_displayPixels is not null) return _displayPixels;
        return _currentImage?.Pixels ?? Array.Empty<float>();
    }

    public string CompPickCountText => $"{CompPickCount} picked";
    partial void OnCompPickCountChanged(int value) => OnPropertyChanged(nameof(CompPickCountText));

    // ── Injected ──────────────────────────────────────────────────────────────
    public EquipmentTargetViewModel? EquipmentTarget { get; set; }
    /// <summary>Shows a modal error dialog. Args: (title, message). Wired by code-behind.</summary>
    public Func<string, string, Task>? ShowPickErrorFunc { get; set; }

    private double _histMin = 0.0;          // percentile-clipped range for histogram x-axis
    private double _histMax = 65535.0;

    public double ZoomFactor => _zoomFactor;

    /// <summary>Set zoom factor (clamped to fit-zoom minimum) and update display dimensions.</summary>
    public void SetZoomFactor(double factor)
    {
        _zoomFactor = Math.Clamp(factor, _fitZoomFactor, 16.0);
        ApplyZoom();
    }

    private double _sliderStep = 1.0;   // dynamic: recalculated per frame based on data range
    public  double SliderStep => _sliderStep;

    // ── Property callbacks ────────────────────────────────────────────────────
    partial void OnBlackValueChanged(double value)
    {
        if (!_programmaticChange) _stretchLocked = true;
        if (value > WhiteValue - _sliderStep)
        {
            _suppressRender = true;
            BlackValue = WhiteValue - _sliderStep;
            _suppressRender = false;
        }
        // Re-center slider so thumb stays visible after every value change
        BlackMax = Math.Min(_absoluteDataMax, Math.Max(2.0 * BlackValue - _absoluteDataMin, WhiteValue - _sliderStep));
        BlackLabel = ((int)BlackValue).ToString("N0");
        if (!_suppressRender) { RenderImage(); RenderHistogram(); }
    }

    partial void OnWhiteValueChanged(double value)
    {
        if (!_programmaticChange) _stretchLocked = true;
        if (value < BlackValue + _sliderStep)
        {
            _suppressRender = true;
            WhiteValue = BlackValue + _sliderStep;
            _suppressRender = false;
        }
        // Re-center slider so thumb stays visible after every value change
        WhiteMin = Math.Max(_absoluteDataMin, 2.0 * WhiteValue - _absoluteDataMax);
        WhiteLabel = ((int)WhiteValue).ToString("N0");
        if (!_suppressRender) { RenderImage(); RenderHistogram(); }
    }

    partial void OnIsScanRunningChanged(bool value) => ScanFilesCommand.NotifyCanExecuteChanged();

    // ── Public methods (called by ObservationViewModel) ───────────────────────

    /// <summary>
    /// Auto-scans the FITS directory if not yet done, auto-excludes all flagged
    /// frames, then returns the path of the first non-excluded file (or null).
    /// </summary>
    public async Task<string?> ScanExcludeAndGetFirstAsync()
    {
        if (IsScanRunning)
        {
            // Wait for the in-progress scan to finish rather than returning partial results
            while (IsScanRunning)
                await Task.Delay(100);
        }
        else
        {
            var dir = FitsDirFunc?.Invoke() ?? "";
            if (!Directory.Exists(dir)) return null;

            // Re-scan only if frames list is empty or belongs to a different directory
            bool needsScan = Frames.Count == 0;
            if (!needsScan && Frames.Count > 0)
            {
                var fp = Frames[0].Path;
                needsScan = !fp.StartsWith(dir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
            }

            if (needsScan)
                await ScanFiles();
            // !needsScan: frames already loaded — preserve user's exclusion choices
        }

        return GetFirstNonExcludedPath();
    }

    /// <summary>Marks all Flagged frames as excluded. User-initiated via button — not auto-applied on scan.</summary>
    [RelayCommand]
    private void AutoExcludeFlagged()
    {
        Services.SessionLogService.Write("[Scan] User clicked Exclude Flagged");
        foreach (var f in Frames)
            if (f.Status == "Flagged") f.IsExcluded = true;
        RefreshExclusionCount();
    }

    public string? GetFirstNonExcludedPath()
        => Frames.FirstOrDefault(f => !f.IsExcluded)?.Path;

    // ── Public methods (called from code-behind) ───────────────────────────────

    // Sequence counter: each new load request increments this; after the await we
    // check it hasn't moved on, so rapid clicks can't stale-write the histogram.
    private int _loadSeq;

    public async Task LoadFrameAsync(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        // Already displaying this file — preserve the user's stretch settings.
        if (path == _currentImagePath && _currentImage is not null)
        {
            // Always re-render on same-path: handles tab-switch where Avalonia may have
            // dropped the visual without firing a property change.
            RenderImage();
            RenderHistogram();
            if (!HasImage) HasImage = true;
            RedrawMarkersAction?.Invoke();
            return;
        }
        int mySeq = ++_loadSeq;
        _currentImagePath = path;

        // Only blank the display on first load; keep existing image visible while new one loads.
        bool isFirstLoad = _currentImage is null;
        if (isFirstLoad)
        {
            HasImage        = false;
            FrameBitmap     = null;
            HistogramBitmap = null;
        }
        CursorText = "X: —  Y: —  ADU: —";
        FovText    = "";

        try
        {
            var img = await Task.Run(() => FitsImageService.Load(path));
            // If the user clicked a different frame while this one was loading, discard.
            if (_loadSeq != mySeq) return;
            _currentImage = img;

            // Compute dark-subtracted display pixels
            _displayPixels = null;
            if (IsDarkSubtract && _masterDark is not null && _masterDark.Length == img.Pixels.Length)
            {
                var dp = img.Pixels.ToArray();
                for (int i = 0; i < dp.Length; i++) dp[i] -= _masterDark[i];
                _displayPixels = dp;
            }
            var displayPix = GetDisplayPixels();

            _absoluteDataMin = displayPix.Min();
            _absoluteDataMax = displayPix.Max();
            _sliderStep = Math.Max(1.0, (_absoluteDataMax - _absoluteDataMin) / 500.0);

            if (!_stretchLocked)
            {
                // ApplyAutoStretch pre-computes _histMin/_histMax from the sorted array
                // so that the RenderHistogram() fired by the WhiteValue setter already
                // has the correct x-axis for this frame (not the previous frame's bounds).
                ApplyAutoStretch(displayPix);   // sets _histMin/_histMax, BlackValue, WhiteValue
            }
            else
            {
                // User has set custom levels — clamp to the new image's data range.
                // Compute histogram bounds BEFORE the WhiteValue setter fires a render.
                _programmaticChange = true;
                _suppressRender = true;
                BlackValue = Math.Clamp(BlackValue, _absoluteDataMin, _absoluteDataMax - _sliderStep);
                double newWhite = Math.Clamp(WhiteValue, BlackValue + _sliderStep, _absoluteDataMax);
                double srL = Math.Max(1.0, newWhite - BlackValue);
                _histMin = BlackValue - 0.5 * srL;
                _histMax = newWhite + 1.5 * srL;
                _suppressRender = false;
                WhiteValue = newWhite;   // RenderHistogram now sees the correct bounds
                _programmaticChange = false;
                // Explicitly render: if the clamped values didn't change, the property
                // setters are no-ops and OnWhiteValueChanged never fires.
                RenderImage();
                RenderHistogram();
            }

            // Slider bounds: centered on current Black/White so thumb starts at ~50%.
            // Full data range is always reachable by dragging; bounds re-center on each +/− click.
            BlackMin = _absoluteDataMin;
            BlackMax = Math.Min(_absoluteDataMax, Math.Max(2.0 * BlackValue - _absoluteDataMin, WhiteValue - _sliderStep));
            WhiteMax = _absoluteDataMax;
            WhiteMin = Math.Max(_absoluteDataMin, 2.0 * WhiteValue - _absoluteDataMax);

            // Make panels visible BEFORE the final render so Avalonia assigns bitmaps
            // to live controls. Rendering while IsVisible=false can cause the histogram
            // to not appear when the border later becomes visible (rapid-click scenario).
            HasImage = true;

            // Final unconditional render: covers (a) same-stretch frames where the
            // MVVM property setters were no-ops so OnWhiteValueChanged never fired,
            // and (b) first-load where the histogram panel was invisible during any
            // earlier render calls from ApplyAutoStretch / the stretchLocked path.
            RenderImage();
            RenderHistogram();

            RedrawMarkersAction?.Invoke();
            ComputeFov(img);
        }
        catch (Exception ex)
        {
            ScanStatus = $"✗  {ex.Message}";
        }
    }

    public void FitToViewer(double viewerWidth, double viewerHeight)
    {
        if (_currentImage is null || viewerWidth <= 0 || viewerHeight <= 0) return;
        _fitZoomFactor     = Math.Min(viewerWidth  / _currentImage.Width,
                                      viewerHeight / _currentImage.Height);
        _zoomFactor        = _fitZoomFactor;
        ImageDisplayWidth  = _currentImage.Width  * _zoomFactor;
        ImageDisplayHeight = _currentImage.Height * _zoomFactor;
    }

    public void UpdateCursor(double imageX, double imageY)
    {
        if (_currentImage is null) { CursorText = "X: —  Y: —  ADU: —"; return; }
        int px = (int)imageX;
        int py = (int)imageY;
        if (px < 0 || px >= _currentImage.Width || py < 0 || py >= _currentImage.Height)
        { CursorText = "X: —  Y: —  ADU: —"; return; }
        CursorText = $"X: {px + 1}  Y: {py + 1}  ADU: {GetDisplayPixels()[py * _currentImage.Width + px]:N0}";
    }

    // ── Commands ─────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanScanFiles))]
    private async Task ScanFiles()
    {
        Services.SessionLogService.Write("[Scan] User clicked Scan Frames");
        var dir = FitsDirFunc?.Invoke() ?? "";
        if (!Directory.Exists(dir))
        {
            ScanStatus = "⚠  No FITS directory set. Configure one on the Data tab.";
            return;
        }

        IsScanRunning   = true;
        ScanStatus      = "⟳  Scanning…";
        Frames.Clear();
        ExclusionStatus = "0 images excluded";
        _currentImage   = null;
        _displayPixels  = null;
        HasImage        = false;
        FrameBitmap     = null;
        HistogramBitmap = null;

        // Load master dark before scan if dark subtract is enabled
        if (IsDarkSubtract && _masterDark is null)
            await LoadMasterDarkAsync();

        await Task.Run(() =>
        {
            var files = Directory.GetFiles(dir, "*.*", SearchOption.TopDirectoryOnly)
                .Where(f => f.EndsWith(".fits", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".fit",  StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f)
                .ToList();

            if (files.Count == 0)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    ScanStatus    = "No FITS files found in directory.";
                    IsScanRunning = false;
                });
                return;
            }

            var backgrounds = new double[files.Count];
            for (int i = 0; i < files.Count; i++)
            {
                try
                {
                    if (IsDarkSubtract && _masterDark is not null)
                    {
                        var img = FitsImageService.Load(files[i]);
                        var pix = img.Pixels.ToArray();
                        if (_masterDark.Length == pix.Length)
                            for (int j = 0; j < pix.Length; j++) pix[j] -= _masterDark[j];
                        backgrounds[i] = FitsImageService.EstimateBackgroundFromPixels(pix);
                    }
                    else
                    {
                        backgrounds[i] = FitsImageService.EstimateBackground(files[i]);
                    }
                }
                catch { backgrounds[i] = double.NaN; }
                int idx = i;
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    ScanStatus = $"⟳  Scanning {idx + 1} / {files.Count}…");
            }

            var valid = backgrounds.Where(b => !double.IsNaN(b)).OrderBy(b => b).ToArray();
            double median = 0, sigma = 1;
            if (valid.Length > 0)
            {
                median = valid[valid.Length / 2];
                var mads = valid.Select(b => Math.Abs(b - median)).OrderBy(x => x).ToArray();
                sigma = mads[mads.Length / 2] * 1.4826;
                if (sigma < 1) sigma = 1;
            }

            double threshold = (double)FlagSigma;

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                int flagged = 0;
                for (int i = 0; i < files.Count; i++)
                {
                    bool isErr     = double.IsNaN(backgrounds[i]);
                    bool isFlagged = !isErr && Math.Abs(backgrounds[i] - median) > threshold * sigma;
                    Frames.Add(new FrameEntry
                    {
                        Filename   = Path.GetFileName(files[i]),
                        Background = isErr ? "Error" : backgrounds[i].ToString("N0"),
                        Status     = isErr ? "Error" : (isFlagged ? "Flagged" : "OK"),
                        Path       = files[i],
                    });
                    if (isFlagged) flagged++;
                }
                ScanStatus    = $"✓  {files.Count} files";
                FlaggedStatus = flagged == 0
                    ? "0 images flagged"
                    : $"⚠  {flagged} image{(flagged == 1 ? "" : "s")} flagged";
                IsScanRunning = false;
            });
        });
    }

    private bool CanScanFiles() => !IsScanRunning;

    /// <summary>Returns 1-based frame number and filename for every excluded frame.</summary>
    public List<(int Number, string Filename)> GetExcludedFrameInfo()
        => Frames
            .Select((f, i) => (Number: i + 1, f.Filename, f.IsExcluded))
            .Where(x => x.IsExcluded && !string.IsNullOrEmpty(x.Filename))
            .Select(x => (x.Number, x.Filename))
            .ToList();

    /// <summary>Returns full paths of all currently-excluded frames.</summary>
    public List<string> GetExcludedPaths()
        => Frames.Where(f => f.IsExcluded && !string.IsNullOrEmpty(f.Path))
                 .Select(f => f.Path)
                 .ToList();

    /// <summary>Un-excludes all currently-excluded images in the frame list.</summary>
    [RelayCommand]
    private void RestoreExcluded()
    {
        Services.SessionLogService.Write("[Scan] User clicked Restore Excluded");
        var excluded = Frames.Where(f => f.IsExcluded).ToList();
        if (excluded.Count == 0) return;
        foreach (var f in excluded) f.IsExcluded = false;
        RefreshExclusionCount();
    }


    [RelayCommand]
    private async Task ShowVsp()
    {
        // Auto-fill from target name if the star field is still empty
        if (string.IsNullOrWhiteSpace(VspStar))
        {
            var auto = TargetNameFunc?.Invoke() ?? "";
            if (!string.IsNullOrWhiteSpace(auto))
                VspStar = auto;
        }

        if (string.IsNullOrWhiteSpace(VspStar))
        {
            // Briefly surface hint in ScanStatus without permanently replacing it
            var prev = ScanStatus;
            ScanStatus = "⚠  Enter a target star name in the Star field (top bar) to load the VSP chart.";
            _ = System.Threading.Tasks.Task.Delay(4000).ContinueWith(_ =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (ScanStatus.StartsWith("⚠  Enter a target star name"))
                        ScanStatus = prev;
                }));
            return;
        }

        var raw  = VspStar.Trim();
        var star = Regex.Replace(raw, @"\s+[a-z]$", "").Trim();
        int    fov = (int)VspFov;
        double mag = (double)VspMag;
        var pngUrl = "https://www.aavso.org/apps/vsp/chart/" +
                     $"?star={Uri.EscapeDataString(star)}" +
                     $"&fov={fov}&maglimit={mag:0.0}&orientation=ccd&format=png";

        _vspCts    = new CancellationTokenSource();
        IsVspLoading = true;
        try
        {
            if (ShowVspFunc is not null)
            {
                await ShowVspFunc(pngUrl, $"VSP Chart — {star}", _vspCts.Token);
                return;
            }

            // Fallback: open browser (no &format=png)
            var webUrl = "https://www.aavso.org/apps/vsp/chart/" +
                         $"?star={Uri.EscapeDataString(star)}" +
                         $"&fov={fov}&maglimit={mag:0.0}&orientation=ccd";
            try { Process.Start(new ProcessStartInfo(webUrl) { UseShellExecute = true }); }
            catch (Exception ex) { ScanStatus = $"✗  Could not open browser: {ex.Message}"; }
        }
        catch (OperationCanceledException) { /* user cancelled — no message needed */ }
        finally
        {
            IsVspLoading = false;
            _vspCts.Dispose();
            _vspCts = null;
        }
    }

    [RelayCommand]
    private void AutoStretch()
    {
        if (_currentImage is null) return;
        _stretchLocked = false;   // re-enable auto-stretch for subsequent frames too
        ApplyAutoStretch(GetDisplayPixels());
    }

    [RelayCommand]
    private void ZoomIn()  { _zoomFactor = Math.Min(_zoomFactor * 1.25, 16.0); ApplyZoom(); }

    [RelayCommand]
    private void ZoomOut() { _zoomFactor = Math.Max(_zoomFactor / 1.25, _fitZoomFactor); ApplyZoom(); }

    [RelayCommand]
    private void ZoomReset() { _zoomFactor = _fitZoomFactor; ApplyZoom(); }

    [RelayCommand]
    private void StepBlackMinus() => BlackValue = Math.Max(_absoluteDataMin, BlackValue - _sliderStep);
    [RelayCommand]
    private void StepBlackPlus()  => BlackValue = Math.Min(WhiteValue - _sliderStep, BlackValue + _sliderStep);
    [RelayCommand]
    private void StepWhiteMinus() => WhiteValue = Math.Max(BlackValue + _sliderStep, WhiteValue - _sliderStep);
    [RelayCommand]
    private void StepWhitePlus()  => WhiteValue = Math.Min(_absoluteDataMax, WhiteValue + _sliderStep);

    // ── Pick commands ─────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task PickTargetStar()
    {
        var firstPath = GetFirstNonExcludedPath();
        if (firstPath is not null &&
            !string.Equals(_currentImagePath, firstPath, StringComparison.OrdinalIgnoreCase))
        {
            _isTargetPickMode = false;
            PickTargetLabel   = "Pick Target Star";
            if (ShowPickErrorFunc is not null)
                await ShowPickErrorFunc("Wrong Frame",
                    "Star picks must be made on the first non-excluded image, because EXOTIC aligns all other frames to it.\n\nSelect the first image in the file list and then click Pick Target Star.");
            return;
        }
        _isTargetPickMode = !_isTargetPickMode;
        if (_isTargetPickMode)
        {
            _isCompPickMode = false;
            PickCompLabel   = "Pick Comp Stars";
            PickTargetLabel = "◉ Picking — click the target star";
            TargetPickStatus = "Click the target star in the image";
        }
        else
        {
            PickTargetLabel  = "Pick Target Star";
            TargetPickStatus = "";
        }
    }

    [RelayCommand]
    private void SendTargetPick()
    {
        if (_targetPickX is null || _targetPickY is null)
        {
            TargetPickStatus = "⚠  Enable Pick Target Star and click the target star first";
            return;
        }
        if (EquipmentTarget is not null)
            EquipmentTarget.TargetXY = $"[{_targetPickX}, {_targetPickY}]";
        TargetPickStatus  = $"✓  Target star sent: [{_targetPickX}, {_targetPickY}]";
        _isTargetPickMode = false;
        PickTargetLabel   = "Pick Target Star";
    }

    [RelayCommand]
    private void ClearTargetPick()
    {
        _targetPickX      = null;
        _targetPickY      = null;
        _isTargetPickMode = false;
        PickTargetLabel   = "Pick Target Star";
        TargetPickStatus  = "";
        RedrawMarkersAction?.Invoke();
    }

    [RelayCommand]
    private async Task PickCompStars()
    {
        var firstPath = GetFirstNonExcludedPath();
        if (firstPath is not null &&
            !string.Equals(_currentImagePath, firstPath, StringComparison.OrdinalIgnoreCase))
        {
            _isCompPickMode = false;
            PickCompLabel   = "Pick Comp Stars";
            if (ShowPickErrorFunc is not null)
                await ShowPickErrorFunc("Wrong Frame",
                    "Star picks must be made on the first non-excluded image, because EXOTIC aligns all other frames to it.\n\nSelect the first image in the file list and then click Pick Comp Stars.");
            return;
        }
        _isCompPickMode = !_isCompPickMode;
        if (_isCompPickMode)
        {
            _isTargetPickMode = false;
            PickTargetLabel   = "Pick Target Star";
            TargetPickStatus  = "";
            PickCompLabel     = "◉ Picking — click stars to add";
            CompPickStatus    = "Click a star in the image to add it as a comp";
        }
        else
        {
            PickCompLabel  = "Pick Comp Stars";
            CompPickStatus = "";
        }
    }

    [RelayCommand]
    private void ClearCompPicks()
    {
        _compPicks.Clear();
        CompPickCount  = 0;
        CompPickStatus = "";
        RedrawMarkersAction?.Invoke();
    }

    [RelayCommand]
    private void SendCompPicks()
    {
        if (_compPicks.Count == 0)
        {
            CompPickStatus = "⚠  No comp stars picked yet";
            return;
        }
        if (EquipmentTarget is not null)
        {
            EquipmentTarget.CompXY = "[" + string.Join(", ",
                _compPicks.Select(p => $"[{p.X}, {p.Y}]")) + "]";
        }
        int n = _compPicks.Count;
        CompPickStatus  = $"✓  {n} comp star(s) sent to Equipment & Target tab";
        _isCompPickMode = false;
        PickCompLabel   = "Pick Comp Stars";
    }

    /// <summary>Called from code-behind when user clicks the image in pick mode.</summary>
    public void HandleImageClick(int fitsX, int fitsY)
    {
        if (_isTargetPickMode)
        {
            _targetPickX     = fitsX;
            _targetPickY     = fitsY;
            TargetPickStatus = $"Target: pixel [{fitsX}, {fitsY}]   — click Send to Target Star";
            RedrawMarkersAction?.Invoke();
            return;
        }
        if (_isCompPickMode)
        {
            if (_compPicks.Count >= 10) { CompPickStatus = "Maximum 10 comp stars reached"; return; }
            foreach (var (px, py) in _compPicks)
                if (Math.Abs(px - fitsX) <= 5 && Math.Abs(py - fitsY) <= 5)
                { CompPickStatus = "Duplicate — already picked near that position"; return; }
            _compPicks.Add((fitsX, fitsY));
            CompPickCount = _compPicks.Count;
            CompPickStatus = $"#{_compPicks.Count}: pixel [{fitsX}, {fitsY}]   (max 10 — click Send when done)";
            RedrawMarkersAction?.Invoke();
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private void ApplyAutoStretch(float[] pixels)
    {
        var sorted = pixels.ToArray();
        Array.Sort(sorted);
        int    n  = sorted.Length;
        double lo = sorted[Math.Max(0, (int)(n * 0.005))];
        double hi = sorted[Math.Min(n - 1, (int)(n * 0.995))];
        if (hi <= lo) hi = lo + 1.0;

        // Pre-compute histogram x-axis bounds so the RenderHistogram() that fires
        // inside the WhiteValue setter already has the correct range for this frame.
        double sr = Math.Max(1.0, hi - lo);
        _histMin = lo - 0.5 * sr;
        _histMax = hi + 1.5 * sr;

        _programmaticChange = true;
        _suppressRender = true;
        BlackValue = lo;
        _suppressRender = false;
        WhiteValue = hi;   // triggers RenderImage + RenderHistogram
        _programmaticChange = false;
    }

    private void ApplyZoom()
    {
        if (_currentImage is null) return;
        ImageDisplayWidth  = _currentImage.Width  * _zoomFactor;
        ImageDisplayHeight = _currentImage.Height * _zoomFactor;
        RedrawMarkersAction?.Invoke();
    }

    /// <summary>True when a FITS image has been loaded into memory (even if HasImage is false).</summary>
    public bool HasCurrentImage => _currentImage is not null;

    /// <summary>
    /// Re-render the current frame bitmap and histogram without reloading the file.
    /// Always creates fresh bitmaps so Avalonia is forced to repaint after tab re-attach.
    /// </summary>
    public void ForceRerender()
    {
        if (_currentImage is null) return;
        RenderImage();
        RenderHistogram();
        if (!HasImage) HasImage = true;
    }

    private void RenderImage()
    {
        if (_currentImage is null) return;
        int   w     = _currentImage.Width;
        int   h     = _currentImage.Height;
        float black = (float)BlackValue;
        float white = (float)WhiteValue;
        float range = Math.Max(1f, white - black);

        var bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96),
                                      PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using (var fb = bmp.Lock())
        {
            unsafe
            {
                byte*  ptr      = (byte*)fb.Address;
                int    rowBytes = fb.RowBytes;
                float[] pixels  = GetDisplayPixels();
                for (int y = 0; y < h; y++)
                {
                    byte* row = ptr + y * rowBytes;
                    for (int x = 0; x < w; x++)
                    {
                        float v = (pixels[y * w + x] - black) / range;
                        byte  b = (byte)(Math.Clamp(v, 0f, 1f) * 255f + 0.5f);
                        int   p = x * 4;
                        row[p] = b; row[p+1] = b; row[p+2] = b; row[p+3] = 255;
                    }
                }
            }
        }
        FrameBitmap = bmp;
    }

    private void RenderHistogram()
    {
        if (_currentImage is null) return;

        const int W    = 1024;
        const int H    = 120;
        const int BINS = 512;

        // Fixed x-axis from percentile-clipped data range so bars stay stable during drag.
        double dMin  = _histMin;
        double dMax  = _histMax;
        double range = dMax - dMin;
        if (range < 1) range = 1;

        // Bin the pixel data
        var counts = new int[BINS];
        foreach (var p in GetDisplayPixels())
        {
            int bin = (int)((p - dMin) / range * BINS);
            counts[Math.Clamp(bin, 0, BINS - 1)]++;
        }

        // Sqrt-scale heights so sky peak doesn't dominate
        double maxSqrt = 0;
        for (int i = 0; i < BINS; i++)
        {
            double s = Math.Sqrt(counts[i]);
            if (s > maxSqrt) maxSqrt = s;
        }
        if (maxSqrt < 1) maxSqrt = 1;

        // Black / White marker x-positions mapped onto bitmap
        int bx = Math.Clamp((int)((BlackValue - dMin) / range * W), 0, W - 1);
        int wx = Math.Clamp((int)((WhiteValue - dMin) / range * W), 0, W - 1);

        var bmp = new WriteableBitmap(new PixelSize(W, H), new Vector(96, 96),
                                      PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using (var fb = bmp.Lock())
        {
            unsafe
            {
                byte* ptr      = (byte*)fb.Address;
                int   rowBytes = fb.RowBytes;

                // Background — Nord BrushBg2 #3b4252 → BGRA: B=82 G=66 R=59
                for (int y = 0; y < H; y++)
                {
                    byte* row = ptr + y * rowBytes;
                    for (int x = 0; x < W; x++)
                    { int p = x * 4; row[p]=82; row[p+1]=66; row[p+2]=59; row[p+3]=255; }
                }

                // Bars — Nord BrushBtn #5e81ac → BGRA: B=172 G=129 R=94
                for (int bin = 0; bin < BINS; bin++)
                {
                    int xStart = bin * W / BINS;
                    int xEnd   = Math.Max(xStart + 1, (bin + 1) * W / BINS);
                    int barH   = (int)(Math.Sqrt(counts[bin]) / maxSqrt * (H - 8));
                    for (int x = xStart; x < xEnd && x < W; x++)
                        for (int y = H - barH - 1; y < H - 1; y++)
                        { byte* row = ptr + y * rowBytes; int p = x * 4; row[p]=172; row[p+1]=129; row[p+2]=94; row[p+3]=255; }
                }

                // Black point marker — bright red RGB(255,60,60), 3 px wide
                for (int dx = -1; dx <= 1; dx++)
                {
                    int mx = Math.Clamp(bx + dx, 0, W - 1);
                    for (int y = 0; y < H; y++)
                    { byte* row = ptr + y * rowBytes; int p = mx * 4; row[p]=60; row[p+1]=60; row[p+2]=255; row[p+3]=255; }
                }

                // White point marker — bright cyan RGB(60,220,255), 3 px wide
                for (int dx = -1; dx <= 1; dx++)
                {
                    int mx = Math.Clamp(wx + dx, 0, W - 1);
                    for (int y = 0; y < H; y++)
                    { byte* row = ptr + y * rowBytes; int p = mx * 4; row[p]=255; row[p+1]=220; row[p+2]=60; row[p+3]=255; }
                }
            }
        }
        HistogramBitmap = bmp;
    }

    private void RefreshExclusionCount()
    {
        int excluded = 0, flagged = 0;
        foreach (var f in Frames)
        {
            if (f.IsExcluded)  excluded++;
            if (f.Status == "Flagged") flagged++;
        }
        ExclusionStatus = $"{excluded} image{(excluded == 1 ? "" : "s")} excluded";
        FlaggedStatus   = flagged == 0
            ? "0 images flagged"
            : $"⚠  {flagged} image{(flagged == 1 ? "" : "s")} flagged";
    }

    // ── Dark subtract ─────────────────────────────────────────────────────────

    partial void OnIsDarkSubtractChanged(bool value)
    {
        _ = HandleDarkSubtractToggledAsync(value);
    }

    private async Task HandleDarkSubtractToggledAsync(bool enabled)
    {
        if (enabled)
            await LoadMasterDarkAsync();
        else
        {
            _masterDark    = null;
            _displayPixels = null;
        }

        // Re-scan if we have frames
        if (Frames.Count > 0)
            await ScanFiles();

        // Reload current frame so display pixels are recomputed
        if (!string.IsNullOrEmpty(_currentImagePath))
        {
            var path = _currentImagePath;
            _currentImagePath = "";   // force reload even if path is the same
            await LoadFrameAsync(path);
        }
    }

    private async Task LoadMasterDarkAsync()
    {
        _masterDark = null;
        var dir = DarksDirFunc?.Invoke() ?? "";
        if (!Directory.Exists(dir)) return;

        var darkFiles = Directory.GetFiles(dir, "*.fits", SearchOption.TopDirectoryOnly)
            .Concat(Directory.GetFiles(dir, "*.fit", SearchOption.TopDirectoryOnly))
            .ToArray();
        if (darkFiles.Length == 0) return;

        ScanStatus = "⟳  Loading master dark…";
        await Task.Run(() =>
        {
            float[]? sum   = null;
            int      count = 0;
            int      w = 0, h = 0;
            foreach (var f in darkFiles)
            {
                try
                {
                    var img = FitsImageService.Load(f);
                    if (sum is null)
                    { sum = img.Pixels.ToArray(); w = img.Width; h = img.Height; }
                    else if (img.Width == w && img.Height == h)
                    { for (int i = 0; i < sum.Length; i++) sum[i] += img.Pixels[i]; }
                    count++;
                }
                catch { /* skip bad frames */ }
            }
            if (sum is not null && count > 0)
            {
                float scale = 1f / count;
                for (int i = 0; i < sum.Length; i++) sum[i] *= scale;
                _masterDark  = sum;
                _masterDarkW = w;
                _masterDarkH = h;
            }
        });
        ScanStatus = _masterDark is null ? "⚠  No usable dark frames found" : $"✓  Master dark loaded ({darkFiles.Length} frames)";
    }

    // ── FOV computation ───────────────────────────────────────────────────────

    private void ComputeFov(FitsImageService.FitsImage img)
    {
        try
        {
            if (string.IsNullOrEmpty(_currentImagePath)) return;
            var hdr = FitsHeaderService.Read(_currentImagePath);
            var wcs = WcsService.ReadWcs(hdr);
            if (wcs is not null)
            {
                // Pixel scale from CD matrix
                double scaleRa  = Math.Sqrt(wcs.Cd11 * wcs.Cd11 + wcs.Cd21 * wcs.Cd21) * 3600.0;
                double scaleDec = Math.Sqrt(wcs.Cd12 * wcs.Cd12 + wcs.Cd22 * wcs.Cd22) * 3600.0;
                double scale    = (scaleRa + scaleDec) / 2.0;
                double fovW     = scale * img.Width  / 60.0;
                double fovH     = scale * img.Height / 60.0;
                FovText = $"FOV: {fovW:F1}′ × {fovH:F1}′";
                return;
            }

            // Fallback: FOCALLEN + XPIXSZ
            var focal = hdr.GetDouble("FOCALLEN");
            var xpix  = hdr.GetDouble("XPIXSZ");
            if (focal.HasValue && xpix.HasValue && focal.Value > 0)
            {
                double scale2 = (206265.0 / focal.Value) * (xpix.Value / 1000.0);
                double fovW2  = scale2 * img.Width  / 3600.0 * 60.0;
                double fovH2  = scale2 * img.Height / 3600.0 * 60.0;
                FovText = $"FOV: {fovW2:F1}′ × {fovH2:F1}′";
            }
        }
        catch { FovText = ""; }
    }
}
