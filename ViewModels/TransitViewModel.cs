using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.ComponentModel;

namespace TransitLab.ViewModels;

public partial class TransitViewModel : ViewModelBase
{
    // ── Parsed orbital parameters (from Parameters tab) ───────────────────────
    [ObservableProperty] private double _rpRs   = 0.10;
    [ObservableProperty] private double _aRs    = 10.0;
    [ObservableProperty] private double _incDeg = 88.0;
    [ObservableProperty] private double _period = 3.0;

    // ── Sandbox/Visualizer parameters (decoupled from Parameters tab) ─────────
    [ObservableProperty] private double _visRpRs   = 0.10;
    [ObservableProperty] private double _visB      = 0.0;    // derived: VisARs × cos(VisIncDeg)
    [ObservableProperty] private double _visARs    = 10.0;
    [ObservableProperty] private double _visIncDeg = 88.0;   // fundamental angle
    [ObservableProperty] private double _visPeriod = 3.0;    // used for stellar density / timing only
    [ObservableProperty] private double _visU1     = 0.40;
    [ObservableProperty] private double _visU2     = 0.30;

    // ── Starspot parameters ────────────────────────────────────────────────────
    [ObservableProperty] private bool   _visSpotEnabled = false;
    [ObservableProperty] private double _visSpotRadius  = 0.10;   // stellar radii
    [ObservableProperty] private double _visSpotDeltaT  = 500.0;  // K cooler than photosphere
    [ObservableProperty] private double _visSpotX       = 0.0;    // stellar radii, horizontal
    [ObservableProperty] private double _visSpotY       = 0.0;    // stellar radii, vertical

    private bool _suppressVisRecompute = false;

    partial void OnVisRpRsChanged(double value)
    {
        if (!_suppressVisRecompute) RecomputeVisualization();
    }

    // Gold ring is drawn at R*1.18; chord at b*R — so b=1.18 is the visual boundary.
    // Cap there to prevent the planet path from escaping the decorated star area.
    private double GrazingLimit => Math.Min(1.0 + VisRpRs, 1.18);

    partial void OnVisIncDegChanged(double value)
    {
        if (_suppressVisRecompute) return;
        double rawB = VisARs * Math.Cos(value * Math.PI / 180.0);
        _suppressVisRecompute = true;
        VisB = Math.Clamp(rawB, -GrazingLimit, GrazingLimit);
        _suppressVisRecompute = false;
        RecomputeVisualization();
    }

    partial void OnVisBChanged(double value)
    {
        if (_suppressVisRecompute) return;
        // b slider moved → clamp to gold ring boundary then back-calculate inclination
        double maxB     = GrazingLimit;
        double clampedB = Math.Clamp(value, -maxB, maxB);
        double cosI     = Math.Clamp(clampedB / Math.Max(VisARs, 1.5), -1.0, 1.0);
        _suppressVisRecompute = true;
        if (clampedB != value) VisB = clampedB;
        VisIncDeg = Math.Acos(cosI) * 180.0 / Math.PI;
        _suppressVisRecompute = false;
        RecomputeVisualization();
    }

    partial void OnVisARsChanged(double value)
    {
        if (_suppressVisRecompute) return;
        // a/Rs changed → keep inclination fixed, recalculate b
        _suppressVisRecompute = true;
        VisB = value * Math.Cos(VisIncDeg * Math.PI / 180.0);
        _suppressVisRecompute = false;
        RecomputeVisualization();
    }

    partial void OnVisPeriodChanged(double value)      { if (!_suppressVisRecompute) RecomputeVisualization(); }
    partial void OnVisU1Changed(double value)          { if (!_suppressVisRecompute) RecomputeVisualization(); }
    partial void OnVisU2Changed(double value)          { if (!_suppressVisRecompute) RecomputeVisualization(); }
    partial void OnVisSpotEnabledChanged(bool value)   { if (!_suppressVisRecompute) RecomputeVisualization(); }
    partial void OnVisSpotRadiusChanged(double value)  { if (!_suppressVisRecompute) RecomputeVisualization(); }
    partial void OnVisSpotDeltaTChanged(double value)  { if (!_suppressVisRecompute) RecomputeVisualization(); }
    partial void OnVisSpotXChanged(double value)       { if (!_suppressVisRecompute) RecomputeVisualization(); }
    partial void OnVisSpotYChanged(double value)       { if (!_suppressVisRecompute) RecomputeVisualization(); }

    // ── Animation state ───────────────────────────────────────────────────────
    [ObservableProperty] private bool   _isPlaying  = false;
    [ObservableProperty] private double _phase      = 0.5;   // 0..1; 0.5 = mid-transit
    [ObservableProperty] private double _animSpeed  = 1.0;
    [ObservableProperty] private bool   _hasParams  = false;

    // Phase fraction occupied by T14 — used for speed scaling and x-axis zoom
    public double T14PhaseFraction  { get; private set; } = 0.03;
    // Half-width of the display window: ±PhaseWindowHalf around mid-transit
    public double PhaseWindowHalf   { get; private set; } = 0.06;
    [ObservableProperty] private double _phaseSliderMin = 0.5 - 0.06;
    [ObservableProperty] private double _phaseSliderMax = 0.5 + 0.06;
    private double _basePhasePerSec = 0.003;

    // Post-transit auto-loop
    private double _postTransitPauseSec = 0.0;
    private const  double PostTransitPause = 1.5;

    // ── Derived quantities (display strings) ──────────────────────────────────
    [ObservableProperty] private string _impactParamText    = "—";
    [ObservableProperty] private string _depthText          = "—";
    [ObservableProperty] private string _t14Text            = "—";
    [ObservableProperty] private string _t23Text            = "—";
    [ObservableProperty] private string _statusText         = "Enter planet parameters on the Parameters tab.";
    [ObservableProperty] private string _stellarDensityText = "—";
    [ObservableProperty] private string _timeOffsetText     = "";

    partial void OnPhaseChanged(double value)
    {
        GeometryInvalidated?.Invoke();
        UpdateTimeOffsetText();
    }

    private void UpdateTimeOffsetText()
    {
        double hoursFromMid = (Phase - 0.5) * VisPeriod * 24.0;
        TimeOffsetText = hoursFromMid >= 0
            ? $"+{hoursFromMid:F2} h"
            : $"{hoursFromMid:F2} h";
    }

    // ── Display metadata ──────────────────────────────────────────────────────
    public string TargetName { get; private set; } = "";
    public double StarTeff   { get; private set; } = 5778.0;
    [ObservableProperty] private string _spectralTypeText = "—";

    // ── Light curve (pre-computed) ─────────────────────────────────────────────
    public float[]? LightCurveFlux { get; private set; }
    public int      LightCurveN    => LightCurveFlux?.Length ?? 0;

    // ── Observed photometry overlay ───────────────────────────────────────────
    public double[]? ObservedPhase { get; private set; }
    public float[]?  ObservedFlux  { get; private set; }

    public void LoadPhotometry(double[] phase, float[] flux)
    {
        ObservedPhase = phase;
        ObservedFlux  = flux;
        LightCurveInvalidated?.Invoke();
    }

    public void ClearPhotometry()
    {
        ObservedPhase = null;
        ObservedFlux  = null;
        LightCurveInvalidated?.Invoke();
    }

    // ── Notifications to controls ─────────────────────────────────────────────
    public event Action? GeometryInvalidated;
    public event Action? LightCurveInvalidated;

    // ── Wiring ────────────────────────────────────────────────────────────────
    private EquipmentTargetViewModel? _et;

    public void Connect(EquipmentTargetViewModel et)
    {
        if (_et is not null) _et.PropertyChanged -= OnEtChanged;
        _et = et;
        _et.PropertyChanged += OnEtChanged;
        Recompute();
    }

    private void OnEtChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is
            nameof(EquipmentTargetViewModel.RpRs)        or
            nameof(EquipmentTargetViewModel.ARs)         or
            nameof(EquipmentTargetViewModel.Inclination) or
            nameof(EquipmentTargetViewModel.OrbitalPeriod))
        {
            Recompute();
        }
        else if (e.PropertyName is
            nameof(EquipmentTargetViewModel.PlanetName)   or
            nameof(EquipmentTargetViewModel.HostStarName) or
            nameof(EquipmentTargetViewModel.Teff))
        {
            UpdateMetadata();
            GeometryInvalidated?.Invoke();
        }
    }

    private void UpdateMetadata()
    {
        if (_et is null) return;
        var planet = _et.PlanetName?.Trim() ?? "";
        var host   = _et.HostStarName?.Trim() ?? "";
        TargetName = !string.IsNullOrEmpty(planet) ? planet
                   : !string.IsNullOrEmpty(host)   ? host
                   : "";

        if (double.TryParse(_et.Teff,
            System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var teff) && teff > 1000)
            StarTeff = teff;
        else
            StarTeff = 5778.0;

        SpectralTypeText = StarTeff switch
        {
            >= 30000 => "O  ·  hot blue star",
            >= 10000 => "B  ·  blue-white star",
            >= 7500  => "A  ·  white star",
            >= 6000  => "F  ·  yellow-white star",
            >= 5200  => "G  ·  yellow, solar-type",
            >= 3700  => "K  ·  orange, cooler than Sun",
            >= 2400  => "M  ·  red dwarf",
            _        => "L/T  ·  brown dwarf"
        };
    }

    // ── Recompute from Parameters tab values ──────────────────────────────────
    public void Recompute()
    {
        if (_et is null) return;

        UpdateMetadata();

        double.TryParse(_et.RpRs,        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var rprs);
        double.TryParse(_et.ARs,         System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var ars);
        double.TryParse(_et.Inclination, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var inc);
        bool ok = rprs > 0.001 && rprs < 0.99 && ars > 1.0 && inc >= 0 && inc <= 90;

        HasParams = ok;

        if (!ok)
        {
            ImpactParamText    = "—";
            DepthText          = "—";
            T14Text            = "—";
            T23Text            = "—";
            StellarDensityText = "—";
            StatusText         = "Waiting for valid planet parameters (Rp/Rs, a/Rs, Inclination).";
            LightCurveFlux     = null;
            LightCurveInvalidated?.Invoke();
            GeometryInvalidated?.Invoke();
            return;
        }

        double.TryParse(_et.OrbitalPeriod, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var period);
        if (period <= 0) period = 1.0;

        RpRs   = rprs;
        ARs    = ars;
        IncDeg = inc;
        Period = period;

        double incRad = inc * Math.PI / 180.0;
        double b      = ars * Math.Cos(incRad);
        double sinI   = Math.Sin(incRad);
        double depth  = rprs * rprs;

        double t14 = double.NaN, t23 = double.NaN;
        if (sinI > 1e-10)
        {
            double inner14 = Math.Sqrt(Math.Max(0, (1 + rprs) * (1 + rprs) - b * b)) / ars / sinI;
            if (inner14 <= 1.0)
                t14 = (period / Math.PI) * Math.Asin(inner14) * 24.0;

            double inner23sq = (1 - rprs) * (1 - rprs) - b * b;
            if (inner23sq > 0)
            {
                double inner23 = Math.Sqrt(inner23sq) / ars / sinI;
                if (inner23 <= 1.0)
                    t23 = (period / Math.PI) * Math.Asin(inner23) * 24.0;
            }
        }

        bool grazing = double.IsNaN(t14) || b >= 1.0 - rprs;
        ImpactParamText = $"{b:F3}";
        DepthText       = $"{depth * 100:F4}%  ({depth * 1e6:N0} ppm)";
        T14Text         = double.IsNaN(t14) ? "Grazing / no transit" : $"{t14:F2} h";
        T23Text         = double.IsNaN(t23) ? "Grazing"              : $"{t23:F2} h";
        StatusText      = grazing
            ? $"Grazing transit — b = {b:F3} ≈ 1 − Rp/Rs = {1 - rprs:F3}"
            : $"b = {b:F3}  ·  Depth = {depth * 100:F3}%  ·  T₁₄ = {t14:F2} h";

        // Set sandbox Vis* defaults (suppressed so we don't trigger premature rebuild)
        _suppressVisRecompute = true;
        VisRpRs   = rprs;
        VisB      = b;
        VisARs    = ars;
        VisIncDeg = inc;
        VisPeriod = period;
        // VisU1, VisU2 are kept as-is (user may have adjusted them)
        _suppressVisRecompute = false;

        RecomputeVisualization();
    }

    // ── Recompute visualization (from Vis* sandbox params) ────────────────────
    public void RecomputeVisualization()
    {
        if (!HasParams) return;

        double k      = VisRpRs;
        double b      = VisB;
        double ars    = VisARs;
        double per    = VisPeriod;
        double incDeg = VisIncDeg;

        double incRad = incDeg * Math.PI / 180.0;
        double sinI   = Math.Sin(incRad);

        // T14 phase fraction for animation timing
        double t14Phase = 0.03;
        if (sinI > 1e-10)
        {
            double inner14 = Math.Sqrt(Math.Max(0, (1 + k) * (1 + k) - b * b)) / ars / sinI;
            if (inner14 <= 1.0)
            {
                double t14h = (per / Math.PI) * Math.Asin(inner14) * 24.0;
                t14Phase = t14h / (per * 24.0);
            }
        }

        T14PhaseFraction = t14Phase;
        PhaseWindowHalf  = Math.Max(t14Phase * 1.5, 0.04);
        PhaseSliderMin   = 0.5 - PhaseWindowHalf;
        PhaseSliderMax   = 0.5 + PhaseWindowHalf;
        _basePhasePerSec = t14Phase / 8.0;

        // Stellar density: ρ★/ρ☉ = 0.01341 × (a/Rs)³ / P_days²
        double densRatio = 0.01341 * Math.Pow(ars, 3) / (per * per);
        StellarDensityText = $"{densRatio:F2} ρ☉";

        BuildLightCurve(k, ars, incDeg, VisU1, VisU2);
        LightCurveInvalidated?.Invoke();
        GeometryInvalidated?.Invoke();
        UpdateTimeOffsetText();
    }

    // ── Reset sandbox to Parameters tab values ────────────────────────────────
    public void ResetVisSandbox()
    {
        if (!HasParams) return;
        double incRad = IncDeg * Math.PI / 180.0;
        double b      = ARs * Math.Cos(incRad);

        _suppressVisRecompute = true;
        VisRpRs       = RpRs;
        VisB          = b;
        VisARs        = ARs;
        VisIncDeg     = IncDeg;
        VisPeriod     = Period;
        VisU1         = 0.40;
        VisU2         = 0.30;
        VisSpotEnabled = false;
        VisSpotRadius  = 0.10;
        VisSpotDeltaT  = 500.0;
        VisSpotX       = 0.0;
        VisSpotY       = 0.0;
        _suppressVisRecompute = false;

        RecomputeVisualization();
    }

    // ── Quadratic limb-darkened transit flux (numerical radial integration) ────
    public static double QuadLDFlux(double z, double k, double u1, double u2)
    {
        if (z >= 1.0 + k)             return 1.0;
        if (k >= 1.0 && z <= k - 1.0) return 0.0;

        const int N = 300;
        double totalFlux    = 0.0;
        double occultedFlux = 0.0;

        for (int i = 0; i < N; i++)
        {
            double r  = (i + 0.5) / N;
            double mu = Math.Sqrt(Math.Max(0.0, 1.0 - r * r));
            double intensity = 1.0 - u1 * (1.0 - mu) - u2 * (1.0 - mu) * (1.0 - mu);
            double weight    = 2.0 * r / N;

            totalFlux += intensity * weight;

            double occFrac = 0.0;
            if (z > 0 && z <= k - r)
                occFrac = 1.0;
            else if (z == 0)
                occFrac = (r <= k) ? 1.0 : 0.0;
            else if (r + k > z && r < z + k)
            {
                double cosAlpha = (r * r + z * z - k * k) / (2.0 * r * z);
                cosAlpha = Math.Clamp(cosAlpha, -1.0, 1.0);
                occFrac  = Math.Acos(cosAlpha) / Math.PI;
            }

            occultedFlux += intensity * weight * occFrac;
        }

        if (totalFlux <= 0) return 1.0;
        return 1.0 - occultedFlux / totalFlux;
    }

    // ── Uniform-source transit (Mandel & Agol 2002) ───────────────────────────
    public static double UniformTransitFlux(double z, double k)
    {
        if (z >= 1.0 + k)             return 1.0;
        if (k >= 1.0 && z <= k - 1.0) return 0.0;
        if (z <= 1.0 - k && k < 1.0)  return 1.0 - k * k;

        double kk  = k * k, zz = z * z;
        double a1  = Math.Acos(Math.Clamp((1.0 - kk + zz) / (2.0 * z),      -1.0, 1.0));
        double a2  = Math.Acos(Math.Clamp((kk - 1.0 + zz) / (2.0 * k * z), -1.0, 1.0));
        double tri = 0.5 * Math.Sqrt(Math.Max(0.0,
            (z + k + 1.0) * (-z + k + 1.0) * (z - k + 1.0) * (z + k - 1.0)));
        return 1.0 - (kk * a2 + a1 - tri) / Math.PI;
    }

    private void BuildLightCurve(double k, double aRs, double incDeg, double u1, double u2)
    {
        const int N = 2000;
        var flux    = new float[N];
        double incRad = incDeg * Math.PI / 180.0;
        double cosI   = Math.Cos(incRad);

        // Starspot parameters (Silva 2003; Zellem et al. 2017)
        bool   spotOn  = VisSpotEnabled;
        double rSpot   = VisSpotRadius;
        double xSpot   = VisSpotX;
        double ySpot   = VisSpotY;

        double fLdTotal    = 1.0 - u1 / 3.0 - u2 / 6.0;
        if (fLdTotal <= 0) fLdTotal = 1.0;

        double contrast    = 0.0;
        double iSpot       = 0.0;
        double rSpotSq     = 0.0;

        if (spotOn && xSpot * xSpot + ySpot * ySpot < 1.0)
        {
            double teff   = StarTeff;
            double tSpot  = Math.Max(1.0, teff - VisSpotDeltaT);
            double tRatio = tSpot / teff;
            contrast      = 1.0 - tRatio * tRatio * tRatio * tRatio;   // Stefan-Boltzmann
            double muSpot = Math.Sqrt(Math.Max(0.0, 1.0 - xSpot * xSpot - ySpot * ySpot));
            iSpot         = 1.0 - u1 * (1.0 - muSpot) - u2 * (1.0 - muSpot) * (1.0 - muSpot);
            rSpotSq       = rSpot * rSpot;
        }
        else
        {
            spotOn = false;
        }

        for (int i = 0; i < N; i++)
        {
            double phi    = (i / (double)(N - 1) - 0.5) * 2 * Math.PI;
            double xSky   = aRs * Math.Sin(phi);
            double ySky   = aRs * Math.Cos(phi) * cosI;
            double z      = Math.Sqrt(xSky * xSky + ySky * ySky);
            bool   inFront = Math.Cos(phi) > 0;

            double f = inFront ? QuadLDFlux(z, k, u1, u2) : 1.0;

            if (spotOn)
            {
                // In-transit spot bump: planet occulting darker region recovers flux
                // (Silva 2003, A&A 400, 723)
                double aOverlap = 0.0;
                if (inFront)
                {
                    double dx   = xSky - xSpot;
                    double dy   = ySky - ySpot;
                    double dist = Math.Sqrt(dx * dx + dy * dy);
                    aOverlap = CircleIntersectionArea(k, rSpot, dist);
                }
                f += (aOverlap / Math.PI - rSpotSq) * contrast * iSpot / fLdTotal;
            }

            flux[i] = (float)f;
        }

        LightCurveFlux = flux;
    }

    // Lens-intersection area of two circles (radii r1, r2; centre separation d)
    private static double CircleIntersectionArea(double r1, double r2, double d)
    {
        if (d >= r1 + r2) return 0.0;
        if (d <= Math.Abs(r1 - r2)) return Math.PI * Math.Min(r1, r2) * Math.Min(r1, r2);
        double d2   = d * d, r1sq = r1 * r1, r2sq = r2 * r2;
        double alpha = Math.Acos(Math.Clamp((d2 + r1sq - r2sq) / (2.0 * d * r1), -1.0, 1.0));
        double beta  = Math.Acos(Math.Clamp((d2 + r2sq - r1sq) / (2.0 * d * r2), -1.0, 1.0));
        return r1sq * alpha + r2sq * beta
               - 0.5 * Math.Sqrt(Math.Max(0.0,
                   (-d + r1 + r2) * (d + r1 - r2) * (d - r1 + r2) * (d + r1 + r2)));
    }

    // ── Animation ─────────────────────────────────────────────────────────────
    [RelayCommand]
    public void PlayPause()
    {
        IsPlaying = !IsPlaying;
        PlayPauseRequested?.Invoke(IsPlaying);
    }

    [RelayCommand]
    public void Reset()
    {
        double halfT14       = T14PhaseFraction * 0.5;
        double tail          = T14PhaseFraction * 0.2;
        Phase                = HasParams ? 0.5 - halfT14 - tail : 0.5;
        IsPlaying            = false;
        _postTransitPauseSec = 0;
        PlayPauseRequested?.Invoke(false);
    }

    public event Action<bool>? PlayPauseRequested;

    public void Tick(double dt)
    {
        if (!IsPlaying || !HasParams) return;

        double halfT14   = T14PhaseFraction * 0.5;
        double tail      = T14PhaseFraction * 0.2;
        double loopExit  = 0.5 + halfT14 + tail;
        double loopEntry = 0.5 - halfT14 - tail;

        if (_postTransitPauseSec > 0)
        {
            _postTransitPauseSec -= dt;
            if (_postTransitPauseSec <= 0)
            {
                _postTransitPauseSec = 0;
                Phase = loopEntry;
            }
            GeometryInvalidated?.Invoke();
            return;
        }

        double newPhase = Phase + _basePhasePerSec * AnimSpeed * dt;
        if (newPhase >= loopExit)
        {
            Phase = loopExit;
            _postTransitPauseSec = PostTransitPause;
        }
        else
        {
            Phase = newPhase;
        }
        GeometryInvalidated?.Invoke();
    }
}
