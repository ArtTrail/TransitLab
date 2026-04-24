using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using TransitLab.ViewModels;
using System;

namespace TransitLab.Views.Controls;

/// <summary>
/// Renders the transit light curve with an animated phase cursor.
/// Shows flux vs. orbital phase (0..1), mid-transit at 0.5.
/// </summary>
public class LightCurveControl : Control
{
    public static readonly StyledProperty<TransitViewModel?> ViewModelProperty =
        AvaloniaProperty.Register<LightCurveControl, TransitViewModel?>(nameof(ViewModel));

    public TransitViewModel? ViewModel
    {
        get => GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    static LightCurveControl()
    {
        AffectsRender<LightCurveControl>(ViewModelProperty);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ViewModelProperty)
        {
            if (change.OldValue is TransitViewModel old)
            {
                old.LightCurveInvalidated -= OnCurveInvalidated;
                old.GeometryInvalidated   -= OnPhaseInvalidated;
            }
            if (change.NewValue is TransitViewModel nv)
            {
                nv.LightCurveInvalidated += OnCurveInvalidated;
                nv.GeometryInvalidated   += OnPhaseInvalidated;
            }
        }
    }

    private void OnCurveInvalidated()  => InvalidateVisual();
    private void OnPhaseInvalidated()  => InvalidateVisual();

    // ── Static resources ──────────────────────────────────────────────────────
    private static readonly IBrush _bgBrush      = new SolidColorBrush(Color.Parse("#0d0d1a"));
    private static readonly IBrush _axisBrush    = new SolidColorBrush(Color.Parse("#4a5568"));
    private static readonly IBrush _gridBrush    = new SolidColorBrush(Color.FromArgb(40, 150, 170, 200));
    private static readonly IBrush _curveBrush   = new SolidColorBrush(Color.Parse("#88c0d0"));
    private static readonly IBrush _cursorBrush  = new SolidColorBrush(Color.FromArgb(200, 191, 97, 106));
    private static readonly IBrush _labelBrush   = new SolidColorBrush(Color.Parse("#8892a0"));
    private static readonly IBrush _accentBrush  = new SolidColorBrush(Color.Parse("#a3be8c"));
    private static readonly IBrush _transitFill  = new SolidColorBrush(Color.FromArgb(30, 136, 192, 208));
    private static readonly IBrush _noDataBrush  = new SolidColorBrush(Color.Parse("#4a4a5a"));
    private static readonly IBrush _greetBrush   = new SolidColorBrush(Color.Parse("#6677aa"));
    private static readonly IBrush _photomBrush  = new SolidColorBrush(Color.FromArgb(200, 255, 200, 80));

    private static readonly IPen _curvePen  = new Pen(_curveBrush,  1.8);
    private static readonly IPen _axisPen   = new Pen(_axisBrush,   1.0);
    private static readonly IPen _gridPen   = new Pen(_gridBrush,   1.0, dashStyle: DashStyle.Dash);
    private static readonly IPen _cursorPen = new Pen(_cursorBrush, 1.5);

    // ── Rendering ─────────────────────────────────────────────────────────────
    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 20 || h < 20) return;

        ctx.DrawRectangle(_bgBrush, null, new Rect(0, 0, w, h));

        var vm = ViewModel;
        if (vm is null || !vm.HasParams || vm.LightCurveFlux is null)
        {
            DrawNoData(ctx, w, h);
            return;
        }

        DrawCurve(ctx, vm, w, h);
    }

    private void DrawNoData(DrawingContext ctx, double w, double h)
    {
        var ft = MakeText("Light curve will appear here\nonce planet parameters are set.", 14, _greetBrush);
        ctx.DrawText(ft, new Point(w / 2 - ft.Width / 2, h / 2 - ft.Height / 2));
    }

    private void DrawCurve(DrawingContext ctx, TransitViewModel vm, double w, double h)
    {
        var flux  = vm.LightCurveFlux!;
        int N     = flux.Length;
        double window = vm.PhaseWindowHalf;    // ±window around mid-transit (phase 0.5)
        double period = vm.Period;             // days

        // Flux range for y-axis
        double fluxMin = 1.0;
        for (int i = 0; i < N; i++) if (flux[i] < fluxMin) fluxMin = flux[i];
        double depthPad = (1.0 - fluxMin) * 0.35 + 0.00005;
        double yMin = fluxMin - depthPad;
        double yMax = 1.0 + depthPad * 0.4;

        // Plot area margins
        const double mL = 62, mR = 16, mT = 28, mB = 40;
        double plotW = w - mL - mR;
        double plotH = h - mT - mB;
        if (plotW < 10 || plotH < 10) return;

        // X-axis: phase offset from mid-transit (-window to +window)
        // Also expressed in hours: phaseOffset × Period × 24
        double phaseMin = 0.5 - window;
        double phaseMax = 0.5 + window;

        double PhaseToX(double p) => mL + (p - phaseMin) / (phaseMax - phaseMin) * plotW;
        double FluxToY(double f)  => mT + plotH - (f - yMin) / (yMax - yMin) * plotH;
        double HoursToPhase(double hr) => 0.5 + hr / (period * 24.0);

        // ── Shaded transit region ─────────────────────────────────────────────
        double? x1ph = null, x4ph = null;
        for (int i = 0; i < N; i++)
        {
            double p = (double)i / (N - 1);
            if (flux[i] < 0.9999) { if (!x1ph.HasValue) x1ph = p; x4ph = p; }
        }
        if (x1ph.HasValue && x4ph.HasValue)
        {
            double sx1 = PhaseToX(x1ph.Value);
            double sx4 = PhaseToX(x4ph.Value);
            if (sx4 > sx1)
                ctx.DrawRectangle(_transitFill, null, new Rect(sx1, mT, sx4 - sx1, plotH));
        }

        // ── Horizontal grid lines ─────────────────────────────────────────────
        double depthRange = 1.0 - fluxMin;
        double gridStep   = depthRange > 0.02 ? 0.005
                           : depthRange > 0.005 ? 0.001
                           : depthRange > 0.001 ? 0.0002
                           : 0.00005;
        for (double f = Math.Floor(yMin / gridStep) * gridStep; f <= yMax + 1e-10; f += gridStep)
        {
            double gy = FluxToY(f);
            if (gy < mT || gy > mT + plotH) continue;
            ctx.DrawLine(_gridPen, new Point(mL, gy), new Point(mL + plotW, gy));
        }

        // ── Axes ──────────────────────────────────────────────────────────────
        ctx.DrawLine(_axisPen, new Point(mL, mT),         new Point(mL, mT + plotH));
        ctx.DrawLine(_axisPen, new Point(mL, mT + plotH), new Point(mL + plotW, mT + plotH));

        // Y-axis tick labels
        for (double f = Math.Floor(yMin / gridStep) * gridStep; f <= yMax + 1e-10; f += gridStep)
        {
            double gy = FluxToY(f);
            if (gy < mT - 2 || gy > mT + plotH + 2) continue;
            string lbl = depthRange > 0.005 ? f.ToString("F3") : f.ToString("F4");
            var ft = MakeText(lbl, 11, _labelBrush);
            ctx.DrawText(ft, new Point(mL - ft.Width - 4, gy - ft.Height / 2));
            ctx.DrawLine(_axisPen, new Point(mL - 3, gy), new Point(mL, gy));
        }

        // X-axis: time offset in hours, 5 evenly spaced ticks
        double halfHours = window * period * 24.0;
        // Choose a nice tick step in hours
        double rawStep = halfHours / 2.5;
        double[] niceSteps = [0.25, 0.5, 1, 2, 3, 4, 6, 12, 24];
        double hStep = niceSteps[0];
        foreach (var s in niceSteps) { hStep = s; if (s >= rawStep) break; }

        for (double hr = -Math.Ceiling(halfHours / hStep) * hStep;
             hr <= halfHours + 1e-6; hr += hStep)
        {
            double ph = HoursToPhase(hr);
            if (ph < phaseMin || ph > phaseMax) continue;
            double gx = PhaseToX(ph);
            string lbl = hr == 0 ? "mid" : $"{hr:+0.##;−0.##} h";
            var ft = MakeText(lbl, 11, _labelBrush);
            ctx.DrawText(ft, new Point(gx - ft.Width / 2, mT + plotH + 4));
            ctx.DrawLine(_axisPen, new Point(gx, mT + plotH), new Point(gx, mT + plotH + 3));
        }

        // Axis labels
        var xLbl = MakeText("Hours from mid-transit", 13, _labelBrush);
        ctx.DrawText(xLbl, new Point(mL + plotW / 2 - xLbl.Width / 2, h - 15));
        var yLbl = MakeText("Flux", 13, _labelBrush);
        using (ctx.PushTransform(Matrix.CreateRotation(-Math.PI / 2) *
                                 Matrix.CreateTranslation(13, mT + plotH / 2 + yLbl.Width / 2)))
            ctx.DrawText(yLbl, new Point(0, 0));

        // ── Light curve polyline — only draw within the phase window ──────────
        var geom = new StreamGeometry();
        using (var sgc = geom.Open())
        {
            bool first = true;
            for (int i = 0; i < N; i++)
            {
                double p = (double)i / (N - 1);
                if (p < phaseMin || p > phaseMax) { first = true; continue; }
                double gx = PhaseToX(p);
                double gy = FluxToY(flux[i]);
                if (first) { sgc.BeginFigure(new Point(gx, gy), false); first = false; }
                else        sgc.LineTo(new Point(gx, gy));
            }
        }
        ctx.DrawGeometry(null, _curvePen, geom);

        // ── Phase cursor ──────────────────────────────────────────────────────
        double cursorX = PhaseToX(vm.Phase);
        if (cursorX >= mL && cursorX <= mL + plotW)
            ctx.DrawLine(_cursorPen, new Point(cursorX, mT), new Point(cursorX, mT + plotH));

        // Time offset (left of cursor) and flux (right of cursor) at cursor top
        int ci = (int)(vm.Phase * (N - 1));
        ci = Math.Clamp(ci, 0, N - 1);

        var timeLbl = MakeText(vm.TimeOffsetText, 12, _labelBrush);
        double tX = cursorX - timeLbl.Width - 5;
        tX = Math.Clamp(tX, mL, mL + plotW - timeLbl.Width);
        ctx.DrawText(timeLbl, new Point(tX, mT + 4));

        string fStr = $"Flux = {flux[ci]:F5}";
        var fLbl = MakeText(fStr, 12, _cursorBrush);
        double fX = cursorX + 5;
        if (fX + fLbl.Width > mL + plotW) fX = cursorX - fLbl.Width - 5;
        fX = Math.Clamp(fX, mL, mL + plotW - fLbl.Width);
        ctx.DrawText(fLbl, new Point(fX, mT + 4));

        // Depth annotation — centred under the transit trough (phase 0.5)
        if (fluxMin < 0.9999)
        {
            double depthPct = (1.0 - fluxMin) * 100.0;
            var depLbl = MakeText($"Transit depth  {depthPct:F4}%", 12, _accentBrush);
            double troughX = PhaseToX(0.5);
            double troughY = FluxToY(fluxMin) + 4;
            double depX = Math.Clamp(troughX - depLbl.Width / 2, mL, mL + plotW - depLbl.Width);
            ctx.DrawText(depLbl, new Point(depX, troughY));
        }

        // ── Observed photometry overlay ────────────────────────────────────────
        if (vm.ObservedPhase is { } obsPhase && vm.ObservedFlux is { } obsFlux)
        {
            for (int i = 0; i < Math.Min(obsPhase.Length, obsFlux.Length); i++)
            {
                double ph = obsPhase[i];
                if (ph < phaseMin || ph > phaseMax) continue;
                double gx = PhaseToX(ph);
                double gy = FluxToY(obsFlux[i]);
                if (gy < mT - 4 || gy > mT + plotH + 4) continue;
                ctx.DrawEllipse(_photomBrush, null, new Point(gx, gy), 3.0, 3.0);
            }

            var obsLbl = MakeText("● Observed", 11, _photomBrush);
            ctx.DrawText(obsLbl, new Point(mL + 4, mT + 4));
        }
    }

    private static FormattedText MakeText(string text, double size, IBrush brush)
        => new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Arial"),
            size,
            brush);
}
