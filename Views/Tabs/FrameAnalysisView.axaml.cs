using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using TransitLab.Models;
using TransitLab.ViewModels;
using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace TransitLab.Views.Tabs;

public partial class FrameAnalysisView : UserControl
{
    // ── Pan state ─────────────────────────────────────────────────────────────
    private bool   _isPanning;
    private Point  _panStartPtr;
    private Vector _panStartOffset;

    public FrameAnalysisView()
    {
        InitializeComponent();

        // DataGrid selection → load image + fit to viewer
        FrameGrid.SelectionChanged += OnFrameGridSelectionChanged;

        // Attach all image interactions to the container Grid, not the Image element.
        // Image controls are non-interactive by default; the transparent-background Grid
        // is reliably hit-testable across the full image area.
        ImageContainer.PointerWheelChanged += OnImageScrollWheel;
        ImageContainer.PointerPressed      += OnImagePointerPressed;
        ImageContainer.PointerMoved        += OnImagePointerMoved;
        ImageContainer.PointerReleased     += OnImagePointerReleased;
        ImageContainer.PointerExited       += OnImagePointerExited;
        DataContextChanged    += OnDataContextChanged;
        AttachedToVisualTree  += OnAttachedToVisualTree;
    }

    // ── Tab-activation recovery ───────────────────────────────────────────────
    // Hook the parent TabControl's SelectionChanged — the only event that reliably
    // fires whenever this tab becomes active, regardless of Avalonia's internal
    // detach/reattach vs. IsVisible strategy.

    private TabControl? _parentTabControl;
    private TabItem?    _parentTabItem;
    private bool        _tabControlHooked;

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        HookTabControl();
        OnTabActivated();
    }

    private void HookTabControl()
    {
        if (_tabControlHooked) return;
        // Walk logical parents to find the TabControl that contains this view.
        var v = this.Parent;
        while (v is not null)
        {
            if (v is TabControl tc)
            {
                _parentTabControl = tc;
                // Find the TabItem whose Content is this view instance.
                foreach (object? item in tc.Items)
                {
                    if (item is TabItem ti && ti.Content == this)
                    { _parentTabItem = ti; break; }
                }
                tc.SelectionChanged += OnTabControlSelectionChanged;
                _tabControlHooked = true;
                return;
            }
            v = v.Parent;
        }
    }

    private void OnTabControlSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Re-render only when THIS tab becomes the selected one.
        if (_parentTabControl?.SelectedItem == _parentTabItem)
            OnTabActivated();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // Fallback: some Avalonia builds toggle IsVisible instead of detaching.
        if (change.Property == IsVisibleProperty && IsVisible)
            OnTabActivated();
    }

    private void OnTabActivated()
    {
        if (DataContext is not FrameAnalysisViewModel vm || !vm.HasCurrentImage) return;
        vm.ForceRerender();
        vm.RedrawMarkersAction?.Invoke();
        // Bounds may not be valid yet at this point — defer FitToViewer.
        Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is FrameAnalysisViewModel v)
                v.FitToViewer(ImageScrollViewer.Bounds.Width, ImageScrollViewer.Bounds.Height);
        }, DispatcherPriority.Background);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not FrameAnalysisViewModel vm) return;

        vm.RedrawMarkersAction = () => RedrawMarkers(vm);

        vm.ShowPickErrorFunc = async (title, message) =>
        {
            var text  = new TextBlock
            {
                Text         = message,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Margin       = new Avalonia.Thickness(20, 20, 20, 12),
            };
            var okBtn = new Button
            {
                Content             = "OK",
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                Margin              = new Avalonia.Thickness(0, 0, 20, 16),
                MinWidth            = 70,
            };
            var panel = new StackPanel { Children = { text, okBtn } };
            Window? dlg = null;
            var owner = TopLevel.GetTopLevel(this) as Window;
            dlg = new Window
            {
                Title                 = title,
                Width                 = 440,
                SizeToContent         = Avalonia.Controls.SizeToContent.Height,
                CanResize             = false,
                WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterOwner,
                Content               = panel,
            };
            okBtn.Click += (_, _) => dlg?.Close();
            if (owner is not null)
                await dlg.ShowDialog(owner);
            else
            {
                var tcs = new System.Threading.Tasks.TaskCompletionSource();
                dlg.Closed += (_, _) => tcs.TrySetResult();
                dlg.Show();
                await tcs.Task;
            }
        };


        vm.ShowVspFunc = async (url, title, ct) =>
        {
            // Build the browser-friendly URL (no format=png) for fallback use
            var webUrl = url.Replace("&format=png", "");

            try
            {
                using var http = new HttpClient();
                http.DefaultRequestHeaders.UserAgent.ParseAdd(
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

                // Step 1: query the AAVSO JSON API to get the real image_uri.
                // The direct ?format=png chart URL returns HTML, not raw PNG bytes.
                var apiUrl = url
                    .Replace("/apps/vsp/chart/", "/apps/vsp/api/chart/")
                    .Replace("format=png", "format=json");

                string? imageUri = null;
                try
                {
                    var json = await http.GetStringAsync(apiUrl, ct);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("image_uri", out var uriEl))
                        imageUri = uriEl.GetString();
                }
                catch (OperationCanceledException) { throw; }
                catch { /* JSON API unavailable — fall through to direct URL */ }

                // Step 2: download the PNG
                var downloadUrl = imageUri ?? url;
                var bytes = await http.GetByteArrayAsync(downloadUrl, ct);

                var bmp = new Bitmap(new MemoryStream(bytes));
                var img = new Image
                {
                    Source  = bmp,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                    VerticalAlignment   = Avalonia.Layout.VerticalAlignment.Stretch,
                };
                var win = new Window
                {
                    Title   = title,
                    Width   = Math.Clamp(bmp.PixelSize.Width,  400, 1200),
                    Height  = Math.Clamp(bmp.PixelSize.Height, 300,  900),
                    Content = img,
                };
                var owner = TopLevel.GetTopLevel(this) as Window;
                if (owner is not null) win.Show(owner);
                else                   win.Show();
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // Chart not found — most likely the star name was not recognised by AAVSO
                var errText = new TextBlock
                {
                    Text         = "The VSP chart could not be loaded.\n\n" +
                                   "This usually means the star name was not recognised by AAVSO. " +
                                   "Try using just the host star name without the planet letter " +
                                   "(e.g. 'HAT-P-36' instead of 'HAT-P-36 b'), or check the " +
                                   "exact name on aavso.org/apps/vsp.",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Margin       = new Avalonia.Thickness(20, 20, 20, 12),
                };
                var okBtn = new Button
                {
                    Content             = "OK",
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Margin              = new Avalonia.Thickness(0, 0, 20, 16),
                    MinWidth            = 70,
                };
                var panel = new StackPanel { Children = { errText, okBtn } };
                Window? errWin = null;
                errWin = new Window
                {
                    Title         = "VSP Chart — Star Not Found",
                    Width         = 440,
                    SizeToContent = Avalonia.Controls.SizeToContent.Height,
                    CanResize     = false,
                    Content       = panel,
                };
                okBtn.Click += (_, _) => errWin?.Close();
                var owner2 = TopLevel.GetTopLevel(this) as Window;
                if (owner2 is not null) errWin.Show(owner2);
                else                    errWin.Show();
            }
        };

    }

    // ── DataGrid selection ────────────────────────────────────────────────────

    private async void OnFrameGridSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not FrameAnalysisViewModel vm) return;
        if (FrameGrid.SelectedItem is not FrameEntry entry) return;
        if (string.IsNullOrEmpty(entry.Path)) return;

        await vm.LoadFrameAsync(entry.Path);
        Dispatcher.UIThread.Post(
            () => vm.FitToViewer(ImageScrollViewer.Bounds.Width, ImageScrollViewer.Bounds.Height),
            DispatcherPriority.Background);
    }

    // ── Scroll-wheel zoom ─────────────────────────────────────────────────────

    private void OnImageScrollWheel(object? sender, PointerWheelEventArgs e)
    {
        if (DataContext is not FrameAnalysisViewModel vm || !vm.HasImage) { e.Handled = true; return; }

        // Cursor position inside the viewport
        var vp = e.GetPosition(ImageScrollViewer);

        double oldZoom  = vm.ZoomFactor;
        double factor   = e.Delta.Y > 0 ? 1.15 : 1.0 / 1.15;
        double newZoom  = System.Math.Clamp(oldZoom * factor, 0.05, 16.0);
        double ratio    = newZoom / oldZoom;

        // Scroll offset that keeps the pixel under the cursor stationary:
        //   newScrollX = (oldScrollX + vpX) * ratio − vpX
        double oldSx    = ImageScrollViewer.Offset.X;
        double oldSy    = ImageScrollViewer.Offset.Y;
        double newSx    = (oldSx + vp.X) * ratio - vp.X;
        double newSy    = (oldSy + vp.Y) * ratio - vp.Y;

        vm.SetZoomFactor(newZoom);

        // Apply scroll after layout has updated the ScrollViewer's extent
        Dispatcher.UIThread.Post(() =>
        {
            ImageScrollViewer.Offset = new Vector(
                System.Math.Max(0, newSx),
                System.Math.Max(0, newSy));
        }, DispatcherPriority.Background);

        e.Handled = true;   // prevent ScrollViewer from also scrolling
    }

    // ── Pan (click + drag) ────────────────────────────────────────────────────

    private void OnImagePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(ImageContainer).Properties.IsLeftButtonPressed) return;

        if (DataContext is FrameAnalysisViewModel vm &&
            (vm.IsTargetPickMode || vm.IsCompPickMode) &&
            vm.FrameBitmap is not null)
        {
            var pos   = e.GetPosition(ImageContainer);
            int fitsX = (int)(pos.X / vm.ImageDisplayWidth  * vm.FrameBitmap.PixelSize.Width)  + 1;
            int fitsY = (int)(pos.Y / vm.ImageDisplayHeight * vm.FrameBitmap.PixelSize.Height) + 1;
            vm.HandleImageClick(fitsX, fitsY);
            e.Handled = true;
            return;
        }

        _isPanning      = true;
        _panStartPtr    = e.GetPosition(ImageScrollViewer);
        _panStartOffset = ImageScrollViewer.Offset;
        ImageContainer.Cursor = new Cursor(StandardCursorType.SizeAll);
        e.Pointer.Capture(ImageContainer);
        e.Handled = true;
    }

    private void OnImagePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isPanning) return;
        _isPanning = false;
        ImageContainer.Cursor = new Cursor(StandardCursorType.Arrow);
        e.Pointer.Capture(null);
    }

    // ── Pointer move: pan OR cursor readout ───────────────────────────────────

    private void OnImagePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_isPanning)
        {
            var pos = e.GetPosition(ImageScrollViewer);
            double dx = _panStartPtr.X - pos.X;
            double dy = _panStartPtr.Y - pos.Y;
            ImageScrollViewer.Offset = new Vector(
                System.Math.Max(0, _panStartOffset.X + dx),
                System.Math.Max(0, _panStartOffset.Y + dy));
            return;
        }

        if (DataContext is not FrameAnalysisViewModel vm) return;
        var bmp = vm.FrameBitmap;
        if (bmp is null) return;

        double imgW = vm.ImageDisplayWidth;
        double imgH = vm.ImageDisplayHeight;
        if (imgW <= 0 || imgH <= 0) return;

        var pos2 = e.GetPosition(ImageContainer);
        vm.UpdateCursor(pos2.X / imgW * bmp.PixelSize.Width,
                        pos2.Y / imgH * bmp.PixelSize.Height);
    }

    private void OnImagePointerExited(object? sender, PointerEventArgs e)
    {
        if (_isPanning) return;
        if (DataContext is FrameAnalysisViewModel vm)
            vm.CursorText = "X: —  Y: —  ADU: —";
    }

    // ── Slider trough intercept (Avalonia ignores LargeChange for trough-click) ──
    // Overlay Border intercepts all pointer events; slider itself has IsHitTestVisible=False.
    // On press: if click is within the thumb area → drag; otherwise → step ±5 with repeat.

    private const double ThumbHalfWidth  = 10.0;   // approx half the Fluent thumb width
    private const int    RepeatDelayMs   = 400;
    private const int    RepeatIntervalMs= 80;

    private Slider?          _activeSlider;
    private int              _sliderDir;            // +1 or -1 for trough repeat
    private bool             _sliderDragging;
    private double           _sliderDragStartX;
    private double           _sliderDragStartValue;
    private DispatcherTimer? _repeatTimer;
    private bool             _repeatPhase2;         // false = delay, true = fast repeat

    private void OnBlackTroughPressed(object? sender, PointerPressedEventArgs e)
        => BeginSliderInteraction(BlackSlider, sender as Border, e);

    private void OnWhiteTroughPressed(object? sender, PointerPressedEventArgs e)
        => BeginSliderInteraction(WhiteSlider, sender as Border, e);

    private void BeginSliderInteraction(Slider slider, Border? overlay, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(slider).Properties.IsLeftButtonPressed) return;

        _activeSlider = slider;
        double range    = slider.Maximum - slider.Minimum;
        double fraction = range > 0 ? (slider.Value - slider.Minimum) / range : 0.5;
        double trackW   = slider.Bounds.Width - ThumbHalfWidth * 2;
        double thumbCX  = ThumbHalfWidth + fraction * System.Math.Max(trackW, 0);
        double clickX   = e.GetPosition(slider).X;

        if (System.Math.Abs(clickX - thumbCX) <= ThumbHalfWidth)
        {
            // ── Thumb drag ────────────────────────────────────────────────────
            _sliderDragging      = true;
            _sliderDragStartX    = clickX;
            _sliderDragStartValue= slider.Value;
            if (overlay is not null)
                overlay.Cursor = new Cursor(StandardCursorType.SizeWestEast);
        }
        else
        {
            // ── Trough: step once then repeat ─────────────────────────────────
            _sliderDragging = false;
            _sliderDir      = clickX < thumbCX ? -1 : 1;
            ApplySliderStep();
            StartRepeatTimer(RepeatDelayMs);
        }

        e.Pointer.Capture(overlay);
        e.Handled = true;
    }

    private void OnSliderTroughMoved(object? sender, PointerEventArgs e)
    {
        if (!_sliderDragging || _activeSlider is null) return;
        double range  = _activeSlider.Maximum - _activeSlider.Minimum;
        double trackW = System.Math.Max(_activeSlider.Bounds.Width - ThumbHalfWidth * 2, 1);
        double dx     = e.GetPosition(_activeSlider).X - _sliderDragStartX;
        double newVal = System.Math.Clamp(
            _sliderDragStartValue + dx / trackW * range,
            _activeSlider.Minimum, _activeSlider.Maximum);
        SetSliderValue(newVal);
        e.Handled = true;
    }

    private void OnSliderTroughReleased(object? sender, PointerReleasedEventArgs e)
    {
        StopRepeatTimer();
        _sliderDragging = false;
        _activeSlider   = null;
        if (sender is Border b) b.Cursor = new Cursor(StandardCursorType.Arrow);
        e.Pointer.Capture(null);
    }

    private void ApplySliderStep()
    {
        if (_activeSlider is null || DataContext is not FrameAnalysisViewModel vm) return;
        double current = _activeSlider == BlackSlider ? vm.BlackValue : vm.WhiteValue;
        double newVal  = System.Math.Clamp(
            current + _sliderDir * vm.SliderStep,
            _activeSlider.Minimum, _activeSlider.Maximum);
        SetSliderValue(newVal);
    }

    private void SetSliderValue(double value)
    {
        if (DataContext is not FrameAnalysisViewModel vm || _activeSlider is null) return;
        if (_activeSlider == BlackSlider) vm.BlackValue = value;
        else if (_activeSlider == WhiteSlider) vm.WhiteValue = value;
    }

    private void StartRepeatTimer(int intervalMs)
    {
        StopRepeatTimer();
        _repeatPhase2 = intervalMs == RepeatIntervalMs;
        _repeatTimer  = new DispatcherTimer(TimeSpan.FromMilliseconds(intervalMs),
                                            DispatcherPriority.Background, OnRepeatTick);
        _repeatTimer.Start();
    }

    private void OnRepeatTick(object? sender, EventArgs e)
    {
        if (!_repeatPhase2)
        {
            // Switch from delay → fast repeat
            StartRepeatTimer(RepeatIntervalMs);
            return;
        }
        ApplySliderStep();
    }

    private void StopRepeatTimer()
    {
        _repeatTimer?.Stop();
        _repeatTimer = null;
    }

    // ── Pick marker overlay ───────────────────────────────────────────────────

    private static readonly IBrush _targetBrush = new SolidColorBrush(Color.FromRgb(255, 80, 80));
    private static readonly IBrush _compBrush   = new SolidColorBrush(Color.FromRgb(80, 220, 255));

    private void RedrawMarkers(FrameAnalysisViewModel vm)
    {
        PickOverlay.Children.Clear();
        if (!vm.HasImage || vm.FrameBitmap is null) return;

        double scaleX = vm.ImageDisplayWidth  / vm.FrameBitmap.PixelSize.Width;
        double scaleY = vm.ImageDisplayHeight / vm.FrameBitmap.PixelSize.Height;

        // Target — red crosshair circle
        var target = vm.TargetPickCoords;
        if (target.HasValue)
        {
            double cx = (target.Value.X - 0.5) * scaleX;
            double cy = (target.Value.Y - 0.5) * scaleY;
            AddCrosshair(cx, cy, 10, _targetBrush);
        }

        // Comp stars — cyan numbered circles
        var comps = vm.CompPickCoords;
        for (int i = 0; i < comps.Count; i++)
        {
            double cx = (comps[i].X - 0.5) * scaleX;
            double cy = (comps[i].Y - 0.5) * scaleY;
            AddCompCircle(cx, cy, i + 1);
        }
    }

    private void AddCrosshair(double cx, double cy, double r, IBrush brush)
    {
        const double thickness = 1.5;
        // Circle
        var circle = new Ellipse
        {
            Width = r * 2, Height = r * 2,
            Stroke = brush, StrokeThickness = thickness,
        };
        Canvas.SetLeft(circle, cx - r);
        Canvas.SetTop (circle, cy - r);
        PickOverlay.Children.Add(circle);

        // Horizontal arm
        var h = new Line
        {
            StartPoint = new Point(cx - r * 2, cy),
            EndPoint   = new Point(cx + r * 2, cy),
            Stroke = brush, StrokeThickness = thickness,
        };
        PickOverlay.Children.Add(h);

        // Vertical arm
        var v = new Line
        {
            StartPoint = new Point(cx, cy - r * 2),
            EndPoint   = new Point(cx, cy + r * 2),
            Stroke = brush, StrokeThickness = thickness,
        };
        PickOverlay.Children.Add(v);
    }

    private void AddCompCircle(double cx, double cy, int n)
    {
        const double r = 7.0;
        var circle = new Ellipse
        {
            Width = r * 2, Height = r * 2,
            Stroke = _compBrush, StrokeThickness = 1.5,
        };
        Canvas.SetLeft(circle, cx - r);
        Canvas.SetTop (circle, cy - r);
        PickOverlay.Children.Add(circle);

        var label = new TextBlock
        {
            Text       = n.ToString(),
            Foreground = _compBrush,
            FontSize   = 10,
        };
        Canvas.SetLeft(label, cx + r + 2);
        Canvas.SetTop (label, cy - 6);
        PickOverlay.Children.Add(label);
    }

    // ── [?] Help button ───────────────────────────────────────────────────────

    private void OnHelpImageAnalysis_Click(object? sender, RoutedEventArgs e) =>
        ShowHelpPopup("Image Analysis",
            "Scan Files — reads all FITS images in the FITS Directory and computes the background sky level for each frame. Frames with a background significantly above the median are flagged as potential outliers.\n\n" +
            "Flag σ — the threshold (in standard deviations above the median background) at which a frame is flagged. Lower values flag more frames; 3.0 is a typical starting point.\n\n" +
            "Exclude Flagged — marks all flagged frames as excluded so EXOTIC will skip them. Review the list first to confirm the flagged frames are genuinely bad.\n\n" +
            "Restore Excluded — un-excludes all frames so you can start the exclusion process fresh.\n\n" +
            "Dark-subtract before background estimate — subtracts the dark frame from each image before computing the background. Use this if your dark library is well-matched to the science frames.\n\n" +
            "Frame list — shows each scanned frame with its background value and status. Check the 'Excl' box on individual rows to manually exclude specific frames.\n\n" +
            "VSP chart (FOV / Mag / Star / Show VSP) — loads the AAVSO Variable Star Plotter comparison chart for the host star at the chosen field-of-view and magnitude limit. Useful for visually identifying comparison star magnitudes in the field.\n\n" +
            "Image viewer — click a row in the frame list to display that frame. Use the Black / White sliders or Auto Stretch to adjust the display contrast. Zoom with +/− /Reset or mouse wheel. Click and drag to pan.\n\n" +
            "Pick Target Star — enables click-to-pick mode. Click the target star in the image, then click 'Send to Target Star' to populate the Target Star X,Y field on the Parameters tab.\n\n" +
            "Pick Comp Stars — enables multi-click comp picking mode. Click up to 10 comparison stars in the image, then click 'Send to Comp Stars' to append them to the Comparison Stars list on the Parameters tab.");

    private void ShowHelpPopup(string title, string message)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;

        var tb = new TextBlock
        {
            Text         = message,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(16, 14, 16, 10),
            MaxWidth     = 440,
            FontSize     = 15,
        };
        var btn = new Button
        {
            Content             = "OK",
            HorizontalAlignment = HorizontalAlignment.Center,
            MinWidth            = 80,
            Margin              = new Thickness(0, 2, 0, 14),
        };
        var layout = new StackPanel { Children = { tb, btn } };
        var dialog = new Window
        {
            Title                 = title,
            Content               = layout,
            Width                 = 480,
            SizeToContent         = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize             = false,
            ShowInTaskbar         = false,
        };
        btn.Click += (_, _) => dialog.Close();
        _ = dialog.ShowDialog(owner);
    }
}
