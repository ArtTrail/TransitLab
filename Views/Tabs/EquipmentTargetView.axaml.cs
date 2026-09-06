using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using TransitLab.Services;
using TransitLab.ViewModels;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace TransitLab.Views.Tabs;

public partial class EquipmentTargetView : UserControl
{
    public EquipmentTargetView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.EquipmentTarget.ShowCompStarDetailsFunc = ShowCompStarDetailsAsync;
                vm.Observation.NameDialogFunc              = ShowNameDialogAsync;
            }
        };
    }

    private async Task ShowCompStarDetailsAsync(List<GaiaCompService.CompStarInfo> stars)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;
        var win = new CompStarDetailsWindow(stars);
        await win.ShowDialog(owner);
    }

    private async Task<string?> ShowNameDialogAsync(string defaultName)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return null;

        var tcs = new TaskCompletionSource<string?>();

        var tb = new TextBox
        {
            Text      = defaultName,
            Watermark = "e.g.  Trail Observatory",
            Margin    = new Thickness(12, 12, 12, 8),
        };
        var btnOk = new Button
        {
            Content             = "Save",
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin              = new Thickness(0, 0, 8, 0),
            MinWidth            = 70,
        };
        var btnCancel = new Button
        {
            Content             = "Cancel",
            HorizontalAlignment = HorizontalAlignment.Right,
            MinWidth            = 70,
        };
        var buttonRow = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin              = new Thickness(12, 0, 12, 12),
            Spacing             = 0,
            Children            = { btnOk, btnCancel },
        };
        var layout = new StackPanel { Children = { tb, buttonRow } };
        var dialog = new Window
        {
            Title                 = "Save Observatory",
            Content               = layout,
            Width                 = 380,
            SizeToContent         = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize             = false,
            ShowInTaskbar         = false,
        };

        btnOk.Click     += (_, _) => { tcs.TrySetResult(tb.Text?.Trim()); dialog.Close(); };
        btnCancel.Click += (_, _) => { tcs.TrySetResult(null);            dialog.Close(); };
        dialog.Closed   += (_, _) => tcs.TrySetResult(null);

        tb.AttachedToVisualTree += (_, _) => { tb.Focus(); tb.SelectAll(); };
        tb.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Return) { tcs.TrySetResult(tb.Text?.Trim()); dialog.Close(); }
        };

        await dialog.ShowDialog(owner);
        return await tcs.Task;
    }

    // ── [?] Help buttons ──────────────────────────────────────────────────

    private void OnHelpQuickLook_Click(object? sender, RoutedEventArgs e) =>
        ShowHelpPopup("Quick Look",
            "Runs a fast least-squares fit instead of EXOTIC's full ultranest posterior inference — useful for a quick preliminary look at a light curve without waiting for the full reduction.\n\n" +
            "• No AAVSO report is generated — Quick Look results are preliminary and can't be submitted.\n\n" +
            "• Requires at least one comparison star that's already passed vetting on the Image Analysis tab.\n\n" +
            "• Uses the same FITS directories, target/comp star selections, and planet parameters as Save & Run EXOTIC — nothing else needs to be reconfigured.\n\n" +
            "Requires the EXOTIC 4.3.2 pre-release dev build. Install it via Tools → Python & EXOTIC Setup → Pre-release / Development Build, then select it as the active environment.");

    private void OnHelpEquipment_Click(object? sender, RoutedEventArgs e) =>
        ShowHelpPopup("Equipment",
            "Camera Type — CCD or DSLR. If your camera is a CMOS sensor, select CCD and note the actual camera model in Observing Notes.\n\n" +
            "Pixel Binning — how many pixels are combined into one. 1×1 = full resolution.\n\n" +
            "Filter — the bandpass used for your observations (e.g. V, R, I, CBB). Selecting a filter auto-fills the wavelength range.\n\n" +
            "Filter Min / Max (nm) — wavelength passband limits. Auto-filled from the filter selection.\n\n" +
            "Pixel Scale (arcsec/px) — your imaging scale, set by focal length and sensor size. Saved values persist between sessions.\n\n" +
            "Observing Notes — free-form notes attached to the EXOTIC output (e.g. thin clouds, tracking issues).");

    private void OnHelpObserver_Click(object? sender, RoutedEventArgs e) =>
        ShowHelpPopup("Observer Information",
            "AAVSO Observer Code — your unique 3–5 character code issued by AAVSO. Required for submitting observations to the AAVSO database.\n\n" +
            "Secondary Observer Codes — codes for co-observers or collaborators who contributed to this dataset.\n\n" +
            "Observation Date — the calendar date of your observation in DD-MON-YYYY format (e.g. 27-FEB-2026). Used in EXOTIC output and AAVSO reports.");

    private void OnHelpObservatory_Click(object? sender, RoutedEventArgs e) =>
        ShowHelpPopup("Observatory / Location",
            "Your observatory's geographic coordinates are required for precise barycentric time corrections — converting timestamps from UTC to Barycentric Julian Date (BJD) — and for computing airmass across the observation.\n\n" +
            "Select a saved observatory from the dropdown, or enter coordinates manually and click 'Save current as' to store the site for future sessions.\n\n" +
            "Latitude (+N / −S) and Longitude (+E / −W) in decimal degrees.\n\nElevation in meters above sea level.");

    private void OnHelpPlateSolve_Click(object? sender, RoutedEventArgs e) =>
        ShowHelpPopup("Plate Solve",
            "Plate solving matches your FITS image against a star catalog (Gaia / 2MASS) to determine the precise sky coordinates of every pixel.\n\n" +
            "This step is required before using Auto Select Target, and ensures that the star positions passed to EXOTIC are accurate.\n\n" +
            "Plate solve starts automatically when you click 'Read FITS Header' — you do not need to click 'Plate Solve' separately unless you want to re-solve or the automatic solve failed.\n\n" +
            "The active solver (Astrometry.net, ASTAP, or NextAstro) is shown to the right of the status. Configure solvers via the Tools menu.");

    private void OnHelpStarSelection_Click(object? sender, RoutedEventArgs e) =>
        ShowHelpPopup("Star Selection",
            "Defines which pixel positions contain the target star and comparison (comp) stars.\n\n" +

            "── Comp Star Methods ──────────────────────────\n\n" +

            "AAVSO VSP — queries the AAVSO Variable Star Plotter directly. Returns stars that AAVSO has vetted for this target. Fast and reliable when an AAVSO sequence exists for the field. No PSF validation is performed.\n\n" +

            "VSP + Stone — the full pipeline (named after Geoffrey Stone, author of CompStarSelector). Queries Gaia DR3, APASS DR9, Gaia GSPC synthetic photometry, and AAVSO VSP. VSP stars are given priority — they fill the first available slots; the Stone pipeline (Gaia + APASS) fills remaining slots up to the configured maximum (Tools → Settings → Comp Stars). Includes PSF validation. Recommended for most targets.\n\n" +

            "Stone — same full Gaia + APASS + GSPC pipeline without the VSP query. Includes PSF validation. Useful when no AAVSO sequence exists or you prefer a purely catalog-driven selection.\n\n" +

            "── Candidate Quality Gates (Stone methods) ─────\n\n" +

            "Before scoring, Gaia candidates must pass: a tiered RUWE astrometric-quality cut (tries < 1.1 first, relaxing to < 1.2 then < 1.4 only if too few candidates survive the stricter tier), Gaia flux-over-error > 200, not flagged as a Gaia variable, and no match within 5″ of a known AAVSO VSX variable (a separate cross-check, since a star can be missing Gaia's own variability flag but still be a documented variable in VSX).\n\n" +

            "── Scoring (Stone methods) ─────────────────────\n\n" +

            "Candidates are scored on 5 criteria. Weights when target color is known:\n" +
            "  Color similarity (BP-RP)  30%\n" +
            "  Magnitude match           25%\n" +
            "  Flux SNR quality          15%\n" +
            "  RUWE astrometric quality  10%\n" +
            "  Field centrality          20%\n" +
            "  VSP bonus                +10 pts\n\n" +
            "Color matching requires the target's Gaia BP-RP index. If the target is too bright for Gaia or not found in the catalog, color scoring is disabled and the weights redistribute to magnitude, flux, RUWE, and centrality.\n\n" +

            "── Magnitude Sources ──────────────────────────\n\n" +

            "The Stone pipeline assigns the best available magnitude to each comp star using this priority chain:\n" +
            "  1. GSPC   — Gaia synthetic photometry (~0.01 mag accuracy)\n" +
            "  2. APASS  — APASS DR9 catalog (~0.03 mag accuracy)\n" +
            "  3. G→V    — Gaia G-band polynomial, Evans et al. 2018\n" +
            "  4. VSP    — AAVSO catalog magnitude (fallback)\n\n" +

            "── PSF Validation (Stone methods only) ────────\n\n" +

            "After selecting candidates, Stone measures each star's PSF directly from the FITS image. Stars that are saturated, have low SNR (< 75), an elongated PSF, or an outlier FWHM are rejected and replaced from a ranked backfill pool. Up to 3 passes are run. In VSP + Stone mode, a rejected slot is refilled from a VSP candidate first if one is available, so VSP stars stay ahead of Stone stars even after backfill. The target star's PSF is also measured to estimate photon-limited precision.\n\n" +

            "Click ⊞ Comp Details after a Stone fetch to see the magnitude source, PSF quality, and catalog origin for each selected comp star.\n\n" +

            "── Image Quality Panel ─────────────────────────\n\n" +

            "After a Stone query, a panel appears below the status bar:\n" +
            "  FWHM       — mean FWHM across comps (uniform / mild / significant)\n" +
            "  Color match — active when target BP-RP was found in Gaia\n" +
            "  Precision  — photon-limited mmag from target SNR (1000 / SNR)\n" +
            "  PSF grade  — good / acceptable / marginal / poor\n\n" +

            "── Results Log ────────────────────────────────\n\n" +

            "During a Stone query the Results tab log streams a verbose 8-stage pipeline report: Gaia query results, VSP enrichment, APASS query, frame projection and isolation counts, top-N scored candidates, GSPC match counts, per-star PSF pass/fail for each validation pass, and the target PSF with precision estimate.\n\n" +

            "── Other Controls ─────────────────────────────\n\n" +

            "Target Star X,Y — pixel coordinate of the host star (e.g. [1438, 884]).\n\n" +
            "Auto Select Target — places the target using the plate solve and NEA coordinates.\n\n" +
            "Comparison Stars X,Y — up to the configured maximum comp stars (set via Tools → Settings → Comp Stars). Format: [[x1,y1],[x2,y2], ...]\n\n" +
            "Still need more comps? Go to the Image Analysis tab, scan frames, select an image, enable Pick Comp Stars, click stars, then Send to Comp Stars.");

    private void OnHelpPlanetParameters_Click(object? sender, RoutedEventArgs e) =>
        ShowHelpPopup("Planet Parameters",
            "Orbital and stellar properties of the transiting planet system.\n\n" +
            "Fetch from NEA — retrieves values from the NASA Exoplanet Archive using the planet name you enter (e.g. WASP-24 b). Auto-fills all fields below. The NEA fetch also triggers automatically when you click 'Read FITS Header', so in normal use you do not need to click it manually.\n\n" +
            "These parameters seed EXOTIC's MCMC fitting. EXOTIC fits Rp/Rs, a/Rs, inclination, mid-transit time, and orbital period — the values you provide are starting points and priors.\n\n" +
            "Target RA / Dec — sky coordinates of the host star, used for plate solving and AAVSO reporting.\n\n" +
            "Stellar parameters (Teff, [Fe/H], log(g)) — used by EXOTIC to compute quadratic limb-darkening coefficients that shape the transit light curve model.\n\n" +
            "All values can be edited manually if you have more precise data than the NEA provides.");

    private void ShowHelpPopup(string title, string message)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;

        var tb = new TextBlock
        {
            Text        = message,
            TextWrapping = TextWrapping.Wrap,
            Margin      = new Thickness(16, 14, 16, 10),
            MaxWidth    = 440,
            FontSize    = 15,
        };
        var scroll = new ScrollViewer
        {
            Content                       = tb,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
        };
        var btn = new Button
        {
            Content             = "OK",
            HorizontalAlignment = HorizontalAlignment.Center,
            MinWidth            = 70,
            Margin              = new Thickness(0, 2, 0, 14),
        };
        var layout = new DockPanel();
        DockPanel.SetDock(btn, Dock.Bottom);
        layout.Children.Add(btn);
        layout.Children.Add(scroll);
        var dialog = new Window
        {
            Title                 = title,
            Content               = layout,
            Width                 = 480,
            MaxHeight             = 700,
            SizeToContent         = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize             = true,
            ShowInTaskbar         = false,
        };
        btn.Click += (_, _) => dialog.Close();
        _ = dialog.ShowDialog(owner);
    }
}
