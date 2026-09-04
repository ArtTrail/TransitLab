using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace TransitLab.Views;

public partial class AdvancedSettingsView : UserControl
{
    public AdvancedSettingsView()
    {
        InitializeComponent();
    }

    private void OnHelpNonInteractive_Click(object? sender, RoutedEventArgs e) =>
        ShowHelpPopup("Non-Interactive Run",
            "Some situations during an EXOTIC run normally pause and ask you a question — for example, when the target's RA/Dec looks wrong, or when the observation filter isn't one EXOTIC recognizes. This switch tells EXOTIC to resolve those automatically instead of waiting for an answer:\n\n" +
            "• Invalid RA/Dec — falls back to NASA Exoplanet Archive coordinates if available; the run aborts if there's no archive match.\n\n" +
            "• Unrecognized limb-darkening filter — the run aborts unless a minimum and maximum wavelength are already provided. TransitLab always supplies these, so this case shouldn't come up.\n\n" +
            "Target pixel-coordinate mismatches are already handled separately by TransitLab itself, so this switch mainly matters for the two cases above.\n\n" +
            "Requires the EXOTIC 4.3.2 pre-release dev build — ignored otherwise.");

    private void OnHelpVariabilityServer_Click(object? sender, RoutedEventArgs e) =>
        ShowHelpPopup("NextAstro Variability Server",
            "Before fitting a transit, EXOTIC checks that each comparison star isn't itself a variable star, which would corrupt the light curve. Normally it looks each star up individually against the AAVSO Variable Star Index (VSX).\n\n" +
            "Turning this on checks all comparison stars in a single batch request to NextAstro's variability server instead, which can be faster when several comp stars are selected. If the server returns an error, EXOTIC automatically falls back to checking each star against VSX individually — the same as when this is off.\n\n" +
            "This is unrelated to TransitLab's own NextAstro plate-solve option — that's a separate feature.\n\n" +
            "Requires the EXOTIC 4.3.2 pre-release dev build — ignored otherwise.");

    private void OnHelpEnsemble_Click(object? sender, RoutedEventArgs e) =>
        ShowHelpPopup("Use Ensemble Photometry",
            "By default, EXOTIC picks a single comparison star for the transit fit: it tests every comp star TransitLab supplies independently, ranks them by fit quality/stability, and uses only the single best-ranked one — the rest are a candidate pool, not contributors to the final light curve.\n\n" +
            "Turning this on changes that: EXOTIC combines multiple non-rejected comparison stars into one median-normalized reference and fits against that combined ensemble instead of a single star. A star can still be excluded if its own flux data is too unstable to include.\n\n" +
            "By default (with \"Use exactly the comps provided\" off), EXOTIC still re-ranks the supplied comp stars itself and caps the ensemble to its own top 5. Turn that setting on too if you want every comp star TransitLab's Stone selection vetted to be used, not just EXOTIC's own top 5.\n\n" +
            "Default: On. Requires the EXOTIC 4.3.2 pre-release dev build — ignored otherwise.");

    private void OnHelpExactComps_Click(object? sender, RoutedEventArgs e) =>
        ShowHelpPopup("Use Exactly The Comps Provided",
            "Only takes effect when \"Use ensemble photometry\" is also on.\n\n" +
            "Off: even in ensemble mode, EXOTIC re-ranks every supplied comparison star by its own fit-quality scoring and caps the ensemble to its own top 5 candidates — some of Stone's vetted comp stars may be left out.\n\n" +
            "On: EXOTIC uses every comparison star supplied, full stop — no re-ranking, no cap. If a supplied star can't actually be included (its own flux data fails quality checks), that's treated as an error rather than silently dropping it.\n\n" +
            "Default: On, since Stone's comp-star selection already does its own thorough vetting (magnitude, color, flux SNR, RUWE, field centrality, PSF, and a VSX variability cross-check) before EXOTIC ever sees these stars — there's usually no reason to let EXOTIC second-guess that selection.\n\n" +
            "Requires the EXOTIC 4.3.2 pre-release dev build — ignored otherwise.");

    private void OnHelpFrameAlignmentCount_Click(object? sender, RoutedEventArgs e) =>
        ShowHelpPopup("Frame Alignment — Process Count",
            "Frame alignment lines up each science frame against a reference before photometry can run — plus a fallback image-transformation step for frames that don't align cleanly on the first pass.\n\n" +
            "Enter a number to have EXOTIC do this work across that many CPU processes at once instead of one at a time. Leave blank to keep TransitLab's normal single-process behavior — the safest default.\n\n" +
            "Requires the EXOTIC 4.3.2 pre-release dev build — ignored otherwise.");

    private void OnHelpLightcurveFitsCount_Click(object? sender, RoutedEventArgs e) =>
        ShowHelpPopup("Light-Curve Fits — Process Count",
            "When evaluating which comparison-star candidate produces the best light-curve fit, EXOTIC can test multiple candidates in parallel instead of one at a time.\n\n" +
            "Enter a number of CPU processes to use for this step. Leave blank to keep TransitLab's normal single-process behavior — the safest default.\n\n" +
            "Requires the EXOTIC 4.3.2 pre-release dev build — ignored otherwise.");

    private void OnHelpMultiprocessing_Click(object? sender, RoutedEventArgs e) =>
        ShowHelpPopup("Multiprocessing",
            "Lets EXOTIC use multiple CPU processes for two of its more time-consuming steps, instead of doing all the work on a single process:\n\n" +
            "• Frame alignment — process count: parallelizes aligning frames to each other (and the fallback image-transformation step).\n\n" +
            "• Light-curve fits — process count: parallelizes evaluating candidate comparison-star light-curve fits.\n\n" +
            "Leave either field blank to keep TransitLab's normal single-process behavior — that's the safest default. Enter a number to use that many processes for that step. Windows multiprocessing has a history of edge cases, so it's worth trying a value on a real dataset outside of a time-critical run first.\n\n" +
            "Requires the EXOTIC 4.3.2 pre-release dev build — ignored otherwise.");

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
