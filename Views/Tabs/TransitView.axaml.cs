using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using TransitLab.ViewModels;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace TransitLab.Views.Tabs;

public partial class TransitView : UserControl
{
    private DispatcherTimer? _timer;
    private readonly Stopwatch _stopwatch = new();

    public TransitView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) => _timer?.Stop();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        _timer?.Stop();

        if (DataContext is not MainWindowViewModel vm) return;

        var transit = vm.Transit;

        transit.PlayPauseRequested -= OnPlayPauseRequested;
        transit.PlayPauseRequested += OnPlayPauseRequested;

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16),
                                     DispatcherPriority.Render,
                                     OnTick);
        _stopwatch.Restart();
    }

    private void OnPlayPauseRequested(bool play)
    {
        if (play) { _stopwatch.Restart(); _timer?.Start(); }
        else        _timer?.Stop();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        double dt = _stopwatch.Elapsed.TotalSeconds;
        _stopwatch.Restart();

        if (DataContext is MainWindowViewModel vm)
            vm.Transit.Tick(dt);
    }

    private void OnParametersClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var owner = TopLevel.GetTopLevel(this) as Window;

        var popup = new VisualizerSandboxView
        {
            DataContext           = vm.Transit,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        if (owner is not null)
            popup.Show(owner);
        else
            popup.Show();
    }

    private async void OnLoadPhotometryClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title             = "Load Photometry CSV",
            AllowMultiple     = false,
            FileTypeFilter    = new[]
            {
                new FilePickerFileType("CSV files") { Patterns = new[] { "*.csv", "*.txt" } },
                new FilePickerFileType("All files") { Patterns = new[] { "*" } },
            }
        });

        if (files.Count == 0) return;

        try
        {
            var path  = files[0].Path.LocalPath;
            ParsePhotometryCsv(path, out var phase, out var flux);
            if (phase.Length > 0)
                vm.Transit.LoadPhotometry(phase, flux);
        }
        catch (Exception ex)
        {
            // silently ignore parse errors — partial data is already loaded
            System.Diagnostics.Debug.WriteLine($"Photometry load error: {ex.Message}");
        }
    }

    // ── [?] Help button ───────────────────────────────────────────────────────

    private void OnHelpVisualizer_Click(object? sender, RoutedEventArgs e) =>
        ShowHelpPopup("Visualizer",
            "Play / Pause — starts and stops the transit animation.\n\n" +
            "Reset (⏮) — returns the planet to the beginning of the transit window.\n\n" +
            "Speed — controls animation playback rate. At 1×, the full transit (T14) takes approximately 8 seconds to animate.\n\n" +
            "Orbit (φ) — drag to manually scrub the planet to any orbital phase. φ = 0.5 is mid-transit. Use this to step through the transit frame by frame.\n\n" +
            "Parameters ▶ — opens the Parameters popup where you can manually adjust orbital and stellar values (Rp/Rs, a/Rs, inclination, impact parameter, Teff, etc.) and see their effect on the transit geometry and light curve in real time.\n\n" +
            "Load Photometry — loads a photometry CSV and overlays the real data points on the light curve panel. Accepts AAVSO EXOTIC output files (phase is computed automatically from Tc and Period) or any generic two-column phase/flux CSV.\n\n" +
            "Transit geometry panel (left) — shows the stellar disk with quadratic limb darkening, the planet silhouette in motion, and T₁/T₂/T₃/T₄ contact markers at the limb.\n\n" +
            "Light curve panel (right) — the theoretical transit light curve computed from the current parameters. T contact markers are shown along the curve. Loaded photometry data points are overlaid in a contrasting colour.\n\n" +
            "Derived quantities (bottom) — computed live from the current parameters:\n" +
            "  • Impact parameter b — perpendicular distance from the transit chord to the stellar centre (0 = central, 1 = grazing)\n" +
            "  • Transit Depth — fractional dimming = (Rp/Rs)²\n" +
            "  • T₁₄ — full transit duration, first to fourth contact\n" +
            "  • T₂₃ — flat-bottom duration, second to third contact\n" +
            "  • Stellar Density — derived from a/Rs and orbital period\n" +
            "  • Spectral Type — estimated from Teff");

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

    private static void ParsePhotometryCsv(string path, out double[] phase, out float[] flux)
    {
        var lines = File.ReadAllLines(path);

        // ── Detect AAVSO EXOTIC format ─────────────────────────────────────────
        bool isAavso = false;
        char delim   = ',';
        double tc    = double.NaN;
        double period = double.NaN;
        int dateCol  = -1, diffCol = -1;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (!line.StartsWith('#')) break;   // headers are all at the top

            if (line.StartsWith("#TYPE=EXOPLANET", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("#DATE_TYPE=BJD", StringComparison.OrdinalIgnoreCase))
                isAavso = true;

            if (line.StartsWith("#DELIM=", StringComparison.OrdinalIgnoreCase))
            {
                var d = line.Substring("#DELIM=".Length).Trim();
                if (d.Length > 0) delim = d[0];
            }

            // #RESULTS=Tc=2461036.874 +/- 0.0039,...
            if (line.StartsWith("#RESULTS=", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var seg in line.Substring("#RESULTS=".Length).Split(','))
                {
                    var kv = seg.Trim();
                    if (kv.StartsWith("Tc=", StringComparison.OrdinalIgnoreCase))
                    {
                        var val = kv.Substring(3).Split('+')[0].Trim();
                        double.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out tc);
                    }
                }
            }

            // #PRIORS=Period=1.75540644 +/- 1.6e-07,...
            if (line.StartsWith("#PRIORS=", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var seg in line.Substring("#PRIORS=".Length).Split(','))
                {
                    var kv = seg.Trim();
                    if (kv.StartsWith("Period=", StringComparison.OrdinalIgnoreCase))
                    {
                        var val = kv.Substring(7).Split('+')[0].Trim();
                        double.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out period);
                    }
                }
            }

            // Column header line: "#DATE,DIFF,ERR,..."
            if (line.StartsWith('#') && line.Length > 1 && !line.StartsWith("# "))
            {
                var cols = line.TrimStart('#').Split(delim);
                for (int i = 0; i < cols.Length; i++)
                {
                    var h = cols[i].Trim().ToUpperInvariant();
                    if (h == "DATE")  dateCol = i;
                    if (h == "DIFF")  diffCol = i;
                }
            }
        }

        if (isAavso && !double.IsNaN(tc) && !double.IsNaN(period) && dateCol >= 0 && diffCol >= 0)
        {
            var phases = new System.Collections.Generic.List<double>();
            var fluxes = new System.Collections.Generic.List<float>();

            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith('#')) continue;

                var parts = line.Split(delim);
                if (dateCol >= parts.Length || diffCol >= parts.Length) continue;

                if (double.TryParse(parts[dateCol].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var bjd) &&
                    float.TryParse(parts[diffCol].Trim(),  NumberStyles.Any, CultureInfo.InvariantCulture, out var fl))
                {
                    phases.Add(0.5 + (bjd - tc) / period);
                    fluxes.Add(fl);
                }
            }

            phase = phases.ToArray();
            flux  = fluxes.ToArray();
            return;
        }

        // ── Generic CSV / TSV fallback ─────────────────────────────────────────
        {
            var phases = new System.Collections.Generic.List<double>();
            var fluxes = new System.Collections.Generic.List<float>();

            int phaseCol = -1, fluxCol = -1;
            bool headerParsed = false;

            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith('#')) continue;

                var parts = line.Split(new[] { ',', '\t', ' ' },
                                       StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;

                if (!headerParsed)
                {
                    for (int i = 0; i < parts.Length; i++)
                    {
                        var h = parts[i].ToLowerInvariant();
                        if (h is "phase" or "phi" or "bjd_phase")                     phaseCol = i;
                        if (h is "flux" or "relflux" or "normflux" or "rel_flux")     fluxCol  = i;
                    }

                    headerParsed = true;
                    if (phaseCol >= 0 && fluxCol >= 0) continue;

                    // No recognisable header — assume col 0 = phase, col 1 = flux
                    phaseCol = 0;
                    fluxCol  = 1;
                }

                if (phaseCol < parts.Length && fluxCol < parts.Length &&
                    double.TryParse(parts[phaseCol], NumberStyles.Any, CultureInfo.InvariantCulture, out var ph) &&
                    float.TryParse(parts[fluxCol],   NumberStyles.Any, CultureInfo.InvariantCulture, out var fl))
                {
                    phases.Add(ph);
                    fluxes.Add(fl);
                }
            }

            phase = phases.ToArray();
            flux  = fluxes.ToArray();
        }
    }
}
