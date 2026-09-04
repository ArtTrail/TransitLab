using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using System.Collections.Generic;
using System.Linq;
using TransitLab.Services;

namespace TransitLab.Views;

public partial class CompStarDetailsWindow : Window
{
    // Required by the Avalonia XAML compiler / designer
    public CompStarDetailsWindow() => InitializeComponent();

    public CompStarDetailsWindow(List<GaiaCompService.CompStarInfo> stars)
    {
        InitializeComponent();

        // Build display rows and set title
        DataContext = new CompStarDetailsVm(stars);

        var filter = stars.Count > 0 ? stars[0].FilterBand : "?";
        Title = $"Comp Star Details — {stars.Count} star{(stars.Count == 1 ? "" : "s")} · {filter}-band";
    }

    private void OnClose_Click(object? sender, RoutedEventArgs e) => Close();

    private void OnHelp_Click(object? sender, RoutedEventArgs e)
    {
        const string helpText =
            "# — Star index (1 = highest priority). In VSP + Stone mode, all VSP stars fill the lowest numbers before Stone stars.\n\n" +

            "Name — the star's AAVSO VSX designation if its position matches a known VSX entry, otherwise its full Gaia DR3 source ID. '—' means neither was available.\n\n" +

            "Magnitude — calibrated brightness in your observation filter band, formatted as value ± uncertainty. '—' means no catalog match was found.\n\n" +

            "Source — where the magnitude came from:\n" +
            "  • GSPC — Gaia synthetic photometry (~0.01 mag accuracy)\n" +
            "  • APASS — APASS DR9 catalog (~0.03 mag accuracy)\n" +
            "  • G→V — Gaia G-band → V polynomial, Evans et al. 2018\n" +
            "  • VSP — AAVSO Variable Star Plotter catalog magnitude\n\n" +

            "FWHM — full-width at half-maximum of the star's PSF in pixels. Good comp stars should be round and close in FWHM to the target.\n\n" +

            "SNR — aperture photometry signal-to-noise ratio. Stars below SNR 75 are rejected by the PSF validation loop and replaced from the backfill pool.\n\n" +

            "Sep ″ — angular separation from the target star in arcseconds.\n\n" +

            "Cat. — which catalog nominated this star:\n" +
            "  • Gaia — Stone pipeline (Gaia DR3 + APASS DR9 + GSPC)\n" +
            "  • VSP — AAVSO comparison sequence\n\n" +

            "PSF — result of the PSF quality check run on the FITS image:\n" +
            "  • ✓ — All checks passed\n" +
            "  • ⚠ Sat. — Saturated, peak ADU at or above the detector limit\n" +
            "  • ⚠ Low SNR — Measured, but below the SNR 75 floor. Only appears when the entire candidate pool ran dry and this star was kept as a forced fallback (matches an overall PSF grade of poor).\n" +
            "  • N/M — Not measured (star came from the backfill pool after the final validation pass, or PSF data wasn't available)";

        var tb = new TextBlock
        {
            Text         = helpText,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(16, 14, 16, 10),
            MaxWidth     = 440,
            FontSize     = 15,
        };
        var btn = new Button
        {
            Content             = "OK",
            HorizontalAlignment = HorizontalAlignment.Center,
            MinWidth            = 70,
            Margin              = new Thickness(0, 2, 0, 14),
        };
        var layout = new StackPanel { Children = { tb, btn } };
        var dialog = new Window
        {
            Title                 = "Comp Details — Column Reference",
            Content               = layout,
            Width                 = 490,
            SizeToContent         = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize             = false,
            ShowInTaskbar         = false,
        };
        btn.Click   += (_, _) => dialog.Close();
        dialog.Closed += (_, _) => { /* ensure clean close */ };
        _ = dialog.ShowDialog(this);
    }
}

// ── Lightweight view model ────────────────────────────────────────────────────

internal sealed class CompStarDetailsVm
{
    public List<CompStarRowVm> Rows { get; }

    public CompStarDetailsVm(List<GaiaCompService.CompStarInfo> stars) =>
        Rows = stars.Select((s, i) => new CompStarRowVm(i + 1, s)).ToList();
}

internal sealed class CompStarRowVm
{
    public int    Num        { get; }
    public string Name       { get; }   // VSX designation, else "Gaia <full DR3 source ID>", else "—"
    public string MagDisplay { get; }   // "14.23 ± 0.012"  or "—"
    public string Source     { get; }   // GSPC / APASS / G→V / VSP
    public string Fwhm       { get; }   // "3.2 px"  or "—"
    public string Snr        { get; }   // "312"  or "—"
    public string Sep        { get; }   // "48 ″"
    public string Catalog    { get; }   // Gaia / VSP
    public string Status     { get; }   // "✓"  "⚠ Sat."  "⚠ Low SNR"  "N/M"

    public CompStarRowVm(int num, GaiaCompService.CompStarInfo s)
    {
        Num = num;

        // Name: VSX designation takes priority (a human-readable catalog name), falling
        // back to the full Gaia DR3 source ID (not truncated — the ID is only useful for
        // cross-referencing a star in Simbad/Aladin if it's shown in full).
        if (s.VsxName is not null)
            Name = s.VsxName;
        else if (s.GaiaId is not null)
            Name = $"Gaia {s.GaiaId}";
        else
            Name = "—";

        // Magnitude + error
        if (s.Mag.HasValue)
        {
            var errPart = s.MagErr.HasValue ? $" ± {s.MagErr.Value:F3}" : "";
            MagDisplay = $"{s.Mag.Value:F3}{errPart}";
        }
        else
        {
            MagDisplay = "—";
        }

        Source  = s.MagSource  ?? "—";
        Fwhm    = s.FwhmPx.HasValue ? $"{s.FwhmPx.Value:F1} px" : "—";
        Snr     = s.Snr.HasValue    ? s.Snr.Value.ToString("F0") : "—";
        Sep     = $"{s.SepArcsec:F0} ″";
        Catalog = s.CatalogSource;

        // Status: drives the StatusBrushConverter (✓ = green, ⚠ = orange, — = hint)
        if (s.Saturated)
            Status = "⚠ Sat.";
        else if (!s.FwhmPx.HasValue)
            // FwhmPx is null either because PSF measurement was never attempted
            // (star came from backfill after the final validation pass) or because
            // it failed.  Use "N/M" (not measured) to distinguish from a failed check.
            Status = "N/M";
        else if (s.Snr is > 0 and < 75)
            // Was measured, but never cleared the PSF validation loop's own SNR floor —
            // happens when the entire candidate pool ran dry and the final selection is a
            // forced fallback (GaiaCompService's noneValidated case). Without this check
            // a measured-but-failing star fell into the plain "✓" branch below, contradicting
            // the aggregate PSF grade shown just above this table.
            Status = "⚠ Low SNR";
        else
            Status = "✓";
    }
}
