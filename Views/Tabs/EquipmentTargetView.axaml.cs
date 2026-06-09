using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
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
                vm.EquipmentTarget.FilePickerFunc = PickFileAsync;
                vm.Observation.NameDialogFunc     = ShowNameDialogAsync;
            }
        };
    }

    private async Task<string?> PickFileAsync(string title)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return null;
        var results = await topLevel.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title         = title,
                AllowMultiple = false,
                FileTypeFilter = new List<FilePickerFileType>
                {
                    new("CSV files") { Patterns = ["*.csv"] },
                    new("All files") { Patterns = ["*"]     },
                }
            });
        return results.Count > 0 ? results[0].Path.LocalPath : null;
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
            "This step is required before using Auto Select Target or Auto Select Comps, and ensures that the star positions passed to EXOTIC are accurate.\n\n" +
            "Plate solve starts automatically when you click 'Read FITS Header' — you do not need to click 'Plate Solve' separately unless you want to re-solve or the automatic solve failed.\n\n" +
            "The active solver (Astrometry.net or ASTAP) is shown to the right of the status. Configure solvers via the Tools menu.");

    private void OnHelpStarSelection_Click(object? sender, RoutedEventArgs e) =>
        ShowHelpPopup("Star Selection",
            "Defines which pixel positions in your images contain the target star and comparison (comp) stars.\n\n" +
            "Target Star X,Y — pixel coordinate of the exoplanet host star (e.g. [1438, 884]).\n\n" +
            "Comparison Stars X,Y — up to 10 comp stars used to correct for atmospheric and instrumental variations. Format: [[x1,y1],[x2,y2], ...]\n\n" +
            "AAVSO comp stars are queried automatically and added to the Comparison Stars list when you click 'Read FITS Header'. No manual fetch is needed in normal use.\n\n" +
            "Auto Select Target / Auto Select Comps — automatically identify stars using the plate solve result and NEA coordinates. Both buttons become available once 'Read FITS Header' has completed, which triggers the plate solve, NEA fetch, and AAVSO comp query automatically. Comp auto-selection finds stars with similar brightness to the target.\n\n" +
            "Import NINA Star List — loads a CSV from N.I.N.A. to populate comp star positions directly.\n\n" +
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
