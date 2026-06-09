using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using TransitLab.ViewModels;
using System;

namespace TransitLab.Views.Controls;

/// <summary>
/// Renders the transit geometry: star, planet, orbital chord, impact parameter,
/// and contact-point markers. Driven by TransitViewModel.
/// </summary>
public class TransitGeometryControl : Control
{
    // ── Avalonia properties ───────────────────────────────────────────────────
    public static readonly StyledProperty<TransitViewModel?> ViewModelProperty =
        AvaloniaProperty.Register<TransitGeometryControl, TransitViewModel?>(nameof(ViewModel));

    public TransitViewModel? ViewModel
    {
        get => GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    static TransitGeometryControl()
    {
        AffectsRender<TransitGeometryControl>(ViewModelProperty);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ViewModelProperty)
        {
            if (change.OldValue is TransitViewModel old) old.GeometryInvalidated -= OnInvalidated;
            if (change.NewValue is TransitViewModel nv)  nv.GeometryInvalidated  += OnInvalidated;
        }
    }

    private void OnInvalidated() => InvalidateVisual();

    // ── Static brushes / pens ─────────────────────────────────────────────────
    private static readonly IBrush _bgBrush      = new SolidColorBrush(Color.Parse("#0d0d1a"));
    private static readonly IBrush _starGlow1    = new SolidColorBrush(Color.FromArgb(60,  255, 220, 80));
    private static readonly IBrush _planetRim    = new SolidColorBrush(Color.Parse("#7aa0cc"));
    private static readonly IBrush _chordBrush   = new SolidColorBrush(Color.FromArgb(120, 180, 220, 255));
    private static readonly IBrush _bParamBrush  = new SolidColorBrush(Color.FromArgb(180, 255, 160,  40));
    private static readonly IBrush _labelBrush   = new SolidColorBrush(Color.Parse("#aabbcc"));
    private static readonly IBrush _contactBrush = new SolidColorBrush(Color.FromArgb(180, 120, 220, 120));
    private static readonly IBrush _noParamBrush = new SolidColorBrush(Color.Parse("#4a4a5a"));
    private static readonly IBrush _greetBrush   = new SolidColorBrush(Color.Parse("#6677aa"));
    private static readonly IBrush _titleBrush   = new SolidColorBrush(Color.Parse("#c8d8e8"));

    private static readonly IPen _chordPen   = new Pen(_chordBrush,  1.2, dashStyle: DashStyle.Dash);
    private static readonly IPen _bParamPen  = new Pen(_bParamBrush, 1.5, dashStyle: DashStyle.DashDot);
    private static readonly IPen _contactPen = new Pen(_contactBrush, 1.5);
    private static readonly IPen _starPen    = new Pen(new SolidColorBrush(Color.FromArgb(80, 255, 240, 140)), 1);
    private static readonly IPen _planetPen  = new Pen(_planetRim, 1.5);

    // ── Rendering ─────────────────────────────────────────────────────────────
    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 10 || h < 10) return;

        // Background
        ctx.DrawRectangle(_bgBrush, null, new Rect(0, 0, w, h));

        var vm = ViewModel;

        if (vm is null || !vm.HasParams)
        {
            DrawNoParams(ctx, w, h);
            return;
        }

        DrawTransit(ctx, vm, w, h);
    }

    private void DrawNoParams(DrawingContext ctx, double w, double h)
    {
        double cx = w / 2, cy = h / 2;
        double r  = Math.Min(w, h) * 0.32;
        ctx.DrawEllipse(_noParamBrush, null, new Point(cx, cy), r * 1.6, r * 1.6);
        ctx.DrawEllipse(new SolidColorBrush(Color.Parse("#1c1c2a")), null, new Point(cx, cy), r, r);

        var ft = MakeText("No planet parameters available.\nEnter Rp/Rs, a/Rs, and Inclination\non the Parameters tab.", 23, _greetBrush);
        ctx.DrawText(ft, new Point(cx - ft.Width / 2, cy + r + 14));
    }

    private void DrawTransit(DrawingContext ctx, TransitViewModel vm, double w, double h)
    {
        double k    = vm.VisRpRs;
        double aRs  = vm.VisARs;
        double b    = vm.VisB;
        double phase = vm.Phase;

        double incRad = vm.VisIncDeg * Math.PI / 180.0;
        double cosI   = Math.Cos(incRad);

        // Layout: star centred in panel
        double cx = w * 0.50;
        double cy = h * 0.52;

        // Star radius in pixels
        double R = Math.Min(w * 0.34, h * 0.38);

        // Planet radius
        double Rp = R * k;

        // Contact x positions (planet-center offsets, in star radii)
        double x4Norm = Math.Sqrt(Math.Max(0, (1 + k) * (1 + k) - b * b));
        double x2Norm = (1 - k) * (1 - k) > b * b
            ? Math.Sqrt((1 - k) * (1 - k) - b * b) : double.NaN;

        // Chord y-position
        double chordY = cy + b * R;

        // ── Target name ───────────────────────────────────────────────────────
        if (!string.IsNullOrEmpty(vm.TargetName))
        {
            var nameLbl = MakeText(vm.TargetName, 22, _titleBrush);
            ctx.DrawText(nameLbl, new Point(cx - nameLbl.Width / 2, 10));
        }

        // ── Star glow (limb-darkening halo) ──────────────────────────────────
        ctx.DrawEllipse(_starGlow1, null, new Point(cx, cy), R * 1.18, R * 1.18);

        // ── Orbital chord (dashed) ────────────────────────────────────────────
        double chordExtent = Math.Min(x4Norm * 1.4 + 1.0, 2.8) * R;
        ctx.DrawLine(_chordPen,
            new Point(cx - chordExtent, chordY),
            new Point(cx + chordExtent, chordY));

        // ── Impact parameter arrow ────────────────────────────────────────────
        if (Math.Abs(b) > 0.02)
        {
            ctx.DrawLine(_bParamPen, new Point(cx, cy), new Point(cx, chordY));
            var bLabel = MakeText($"b = {b:F3}", 20, _bParamBrush);
            ctx.DrawText(bLabel, new Point(cx + 6, (cy + chordY) / 2 - 12));
        }

        // ── Contact point ticks ───────────────────────────────────────────────
        // Ticks are placed at the planet's edge position at the moment of contact
        // (not the planet center), so the tick aligns with the star's limb.
        //
        // T1/T4 (outer): planet-center at ±x4Norm·R → edge-contact at ±(x4Norm·R − Rp)
        // T2/T3 (inner): planet-center at ±x2Norm·R → edge-contact at ±(x2Norm·R + Rp)
        double t1x = cx - (x4Norm * R - Rp);
        double t4x = cx + (x4Norm * R - Rp);
        double tickH = 10;

        ctx.DrawLine(_contactPen, new Point(t1x, chordY - tickH), new Point(t1x, chordY + tickH));
        ctx.DrawLine(_contactPen, new Point(t4x, chordY - tickH), new Point(t4x, chordY + tickH));
        DrawSmallLabel(ctx, "T₁", t1x, chordY + tickH + 4, _contactBrush);
        DrawSmallLabel(ctx, "T₄", t4x, chordY + tickH + 4, _contactBrush);

        // Only draw T2/T3 when they are visually separated from T1/T4 by at least 20px.
        // At high inclination (b ≈ 0) the two inner contacts converge onto T1/T4 at the
        // stellar limb and become redundant clutter.
        double t2t1PixSep = (x4Norm * R - Rp) - (x2Norm * R + Rp);
        if (!double.IsNaN(x2Norm) && x2Norm > 0.02 && t2t1PixSep > 20)
        {
            double t2x = cx - (x2Norm * R + Rp);
            double t3x = cx + (x2Norm * R + Rp);
            ctx.DrawLine(_contactPen,
                new Point(t2x, chordY - tickH * 0.6), new Point(t2x, chordY + tickH * 0.6));
            ctx.DrawLine(_contactPen,
                new Point(t3x, chordY - tickH * 0.6), new Point(t3x, chordY + tickH * 0.6));
            DrawSmallLabel(ctx, "T₂", t2x, chordY - tickH - 22, _contactBrush);
            DrawSmallLabel(ctx, "T₃", t3x, chordY - tickH - 22, _contactBrush);
        }

        // ── Star ──────────────────────────────────────────────────────────────
        int starSeed = string.IsNullOrEmpty(vm.TargetName) ? 42 : Math.Abs(vm.TargetName.GetHashCode());
        DrawStar(ctx, cx, cy, R, vm.StarTeff, starSeed);

        // ── Active starspot ───────────────────────────────────────────────────
        if (vm.VisSpotEnabled)
        {
            double spotDist2 = vm.VisSpotX * vm.VisSpotX + vm.VisSpotY * vm.VisSpotY;
            double spotPxR   = vm.VisSpotRadius * R;
            if (spotDist2 < 1.0 && spotPxR > 1.5)
            {
                double sx = cx + vm.VisSpotX * R;
                double sy = cy + vm.VisSpotY * R;
                DrawActiveSpot(ctx, sx, sy, spotPxR, vm.VisSpotDeltaT);
            }
        }

        // ── Planet position ───────────────────────────────────────────────────
        double phi  = 2 * Math.PI * (phase - 0.5);
        double xSky = aRs * Math.Sin(phi);
        double ySky = aRs * Math.Cos(phi) * cosI;

        double px = cx + xSky * R;
        double py = cy + ySky * R;

        bool inFront = Math.Cos(phi) > 0;

        double visibleR = Math.Max(0, Math.Min(Rp, w / 2 - Math.Abs(px - cx)));

        if (visibleR > 1)
        {
            if (inFront)
            {
                DrawPlanet(ctx, px, py, Rp, k);
            }
            else
            {
                var fadedPlanet = new SolidColorBrush(Color.FromArgb(60, 74, 111, 165));
                ctx.DrawEllipse(fadedPlanet, null, new Point(px, py), Rp, Rp);
            }
        }

        // ── Limb darkening annotation ─────────────────────────────────────────
        {
            double ldTipX = cx - R * 0.884;
            double ldTipY = cy - R * 0.884;
            var ldBrush  = new SolidColorBrush(Color.FromArgb(180, 255, 210, 80));
            var ldPen    = new Pen(new SolidColorBrush(Color.FromArgb(120, 255, 210, 80)), 1.0);
            var ldLbl    = MakeText("Limb darkening", 18, ldBrush);
            double ldLblX = Math.Max(4, ldTipX - ldLbl.Width / 2);
            double ldLblY = ldTipY - ldLbl.Height - 8;
            ctx.DrawLine(ldPen,
                new Point(ldLblX + ldLbl.Width / 2, ldLblY + ldLbl.Height + 2),
                new Point(ldTipX, ldTipY));
            ctx.DrawText(ldLbl, new Point(ldLblX, ldLblY));
        }

        // ── Labels ────────────────────────────────────────────────────────────
        double labelY = cy - R - 44;
        var starLabel   = MakeText("R★", 20, _labelBrush);
        var planetLabel = MakeText($"Rp/R★ = {k:F3}", 20, _labelBrush);
        ctx.DrawText(starLabel,   new Point(cx - starLabel.Width / 2, labelY));
        ctx.DrawText(planetLabel, new Point(cx - planetLabel.Width / 2, labelY + 24));

        // ── Time offset from mid-transit ──────────────────────────────────────
        if (!string.IsNullOrEmpty(vm.TimeOffsetText))
        {
            var timeLbl = MakeText(vm.TimeOffsetText, 18, _labelBrush);
            ctx.DrawText(timeLbl, new Point(w - timeLbl.Width - 10, h - timeLbl.Height - 8));
        }
    }

    // ── Star rendering ────────────────────────────────────────────────────────

    private static void DrawStar(DrawingContext ctx, double cx, double cy, double R, double teff, int seed)
    {
        Color core, mid, outer, edge, deep;

        if (teff > 7500)        // A/F type — white-blue
        {
            core  = Color.FromRgb(230, 240, 255);
            mid   = Color.FromRgb(180, 210, 255);
            outer = Color.FromRgb(120, 160, 240);
            edge  = Color.FromRgb( 60,  90, 180);
            deep  = Color.FromRgb( 20,  30, 100);
        }
        else if (teff > 5700)   // G type — solar yellow
        {
            core  = Color.FromRgb(255, 252, 220);
            mid   = Color.FromRgb(255, 230,  80);
            outer = Color.FromRgb(255, 185,  30);
            edge  = Color.FromRgb(210, 100,  10);
            deep  = Color.FromRgb( 60,  15,   0);
        }
        else if (teff > 4500)   // K type — orange
        {
            core  = Color.FromRgb(255, 230, 160);
            mid   = Color.FromRgb(255, 165,  30);
            outer = Color.FromRgb(220, 100,  10);
            edge  = Color.FromRgb(160,  55,   5);
            deep  = Color.FromRgb( 70,  15,   0);
        }
        else                    // M type — red-orange
        {
            core  = Color.FromRgb(255, 180, 100);
            mid   = Color.FromRgb(240, 100,  30);
            outer = Color.FromRgb(180,  50,  10);
            edge  = Color.FromRgb(100,  20,   5);
            deep  = Color.FromRgb( 40,   8,   0);
        }

        var limbBrush = new RadialGradientBrush
        {
            GradientStops = new GradientStops
            {
                new GradientStop(core,  0.00),
                new GradientStop(mid,   0.40),
                new GradientStop(outer, 0.72),
                new GradientStop(edge,  0.90),
                new GradientStop(deep,  0.98),
                new GradientStop(deep,  1.00),
            }
        };
        ctx.DrawEllipse(limbBrush, _starPen, new Point(cx, cy), R, R);

        // Sunspots — count, position, and size randomised per target name
        var rng = new Random(seed);
        int spotCount = rng.Next(8, 16);  // 8–15 spots
        for (int i = 0; i < spotCount; i++)
        {
            // Polar coords; cap at 78% of R so spots stay off the dark limb
            double r     = rng.NextDouble() * 0.78 * R;
            double theta = rng.NextDouble() * 2 * Math.PI;
            double sx    = cx + r * Math.Cos(theta);
            double sy    = cy + r * Math.Sin(theta);
            double size  = (0.005 + rng.NextDouble() * 0.015) * R;  // 0.5%–2.0% of R
            DrawSunspot(ctx, sx, sy, size);
        }
    }

    private static void DrawSunspot(DrawingContext ctx, double x, double y, double r)
    {
        // Penumbra — soft halo, slightly asymmetric
        ctx.DrawEllipse(new SolidColorBrush(Color.FromArgb(130, 90, 40, 4)), null,
            new Point(x - r * 0.10, y + r * 0.08), r * 1.8, r * 1.5);
        // Umbra — off-centre for a natural look
        ctx.DrawEllipse(new SolidColorBrush(Color.FromArgb(220, 14, 5, 0)), null,
            new Point(x + r * 0.08, y - r * 0.08), r * 0.95, r * 0.80);
        // Dark inner core
        ctx.DrawEllipse(new SolidColorBrush(Color.FromArgb(245, 4, 1, 0)), null,
            new Point(x - r * 0.04, y + r * 0.05), r * 0.46, r * 0.38);
    }

    // ── Active starspot rendering ─────────────────────────────────────────────

    private static void DrawActiveSpot(DrawingContext ctx, double x, double y, double r, double deltaT)
    {
        // Intensity scales with temperature contrast
        byte pa = (byte)Math.Clamp((int)(90 + deltaT * 0.05), 70, 180);
        byte ua = (byte)Math.Clamp((int)(150 + deltaT * 0.05), 140, 235);

        // Penumbra halo
        ctx.DrawEllipse(new SolidColorBrush(Color.FromArgb(pa, 80, 28, 4)), null,
            new Point(x, y), r * 1.65, r * 1.45);
        // Umbra
        ctx.DrawEllipse(new SolidColorBrush(Color.FromArgb(ua, 16, 5, 0)), null,
            new Point(x, y), r, r);
        // Highlight ring so the spot is identifiable at any star color
        ctx.DrawEllipse(null,
            new Pen(new SolidColorBrush(Color.FromArgb(170, 255, 130, 40)), 1.5),
            new Point(x, y), r, r);
        // Label
        var lbl = MakeText("spot", 16, new SolidColorBrush(Color.FromArgb(210, 255, 160, 60)));
        ctx.DrawText(lbl, new Point(x - lbl.Width / 2, y + r + 3));
    }

    // ── Planet rendering ──────────────────────────────────────────────────────

    private static void DrawPlanet(DrawingContext ctx, double px, double py, double Rp, double k)
    {
        GradientStops stops;

        if (k >= 0.09)          // Gas giant — Jupiter-style blue-grey banding
        {
            stops = new GradientStops
            {
                new GradientStop(Color.FromRgb( 28,  42,  75), 0.00),
                new GradientStop(Color.FromRgb( 48,  72, 118), 0.12),
                new GradientStop(Color.FromRgb( 95, 138, 195), 0.24),
                new GradientStop(Color.FromRgb( 55,  88, 145), 0.35),
                new GradientStop(Color.FromRgb( 72, 108, 165), 0.48),
                new GradientStop(Color.FromRgb( 45,  70, 125), 0.58),
                new GradientStop(Color.FromRgb( 88, 128, 185), 0.70),
                new GradientStop(Color.FromRgb( 50,  76, 128), 0.84),
                new GradientStop(Color.FromRgb( 28,  42,  75), 1.00),
            };
        }
        else if (k >= 0.05)     // Neptune-class — teal-blue banding
        {
            stops = new GradientStops
            {
                new GradientStop(Color.FromRgb( 15,  35,  80), 0.00),
                new GradientStop(Color.FromRgb( 25,  80, 150), 0.20),
                new GradientStop(Color.FromRgb( 55, 130, 190), 0.45),
                new GradientStop(Color.FromRgb( 30, 100, 160), 0.60),
                new GradientStop(Color.FromRgb( 20,  60, 120), 0.80),
                new GradientStop(Color.FromRgb( 10,  30,  70), 1.00),
            };
        }
        else                    // Small / rocky — grey-blue
        {
            stops = new GradientStops
            {
                new GradientStop(Color.FromRgb( 70,  80, 100), 0.00),
                new GradientStop(Color.FromRgb(120, 135, 155), 0.40),
                new GradientStop(Color.FromRgb( 85,  98, 118), 0.70),
                new GradientStop(Color.FromRgb( 45,  55,  72), 1.00),
            };
        }

        var bandBrush = new LinearGradientBrush
        {
            StartPoint    = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint      = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = stops,
        };
        ctx.DrawEllipse(bandBrush, _planetPen, new Point(px, py), Rp, Rp);

        // Storm oval — gas giants only, when large enough to show detail
        if (k >= 0.09 && Rp >= 14)
            ctx.DrawEllipse(new SolidColorBrush(Color.FromArgb(145, 175, 85, 50)), null,
                new Point(px + Rp * 0.22, py + Rp * 0.12), Rp * 0.22, Rp * 0.14);

        // Polar highlight
        if (Rp >= 10)
            ctx.DrawEllipse(new SolidColorBrush(Color.FromArgb(50, 180, 220, 255)), null,
                new Point(px, py - Rp * 0.68), Rp * 0.55, Rp * 0.22);

        // Atmospheric limb glow
        ctx.DrawEllipse(new SolidColorBrush(Color.FromArgb(25, 140, 190, 255)), null,
            new Point(px, py), Rp * 1.08, Rp * 1.08);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void DrawSmallLabel(DrawingContext ctx, string text, double x, double y, IBrush brush)
    {
        var ft = MakeText(text, 18, brush);
        ctx.DrawText(ft, new Point(x - ft.Width / 2, y));
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
