using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Platform.Storage;
using TransitLab.Services;
using TransitLab.ViewModels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace TransitLab.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.SaveFilePickerFunc              = SaveFileAsync;
                vm.OpenFilePickerFunc              = OpenFileAsync;
                vm.OpenAavsoFilePickerFunc         = OpenAavsoFileAsync;
                vm.BrowseExoticFunc                = BrowseExoticAsync;
                vm.SelectTabFunc                   = idx => MainTabs.SelectedIndex = idx;
                vm.ShowErrorFunc                   = ShowErrorAsync;
                vm.ShowConfirmFunc                 = ShowConfirmAsync;
                vm.BrowseUpdateFolderFunc          = BrowseUpdateFolderAsync;
                vm.ShowInfoFunc                    = ShowInfoAsync;
                vm.RequestAppExitAction            = () => Close();
                vm.EquipmentTarget.ShowInfoFunc    = ShowErrorAsync;
                vm.EquipmentTarget.ShowStellarVariabilityWarningFunc = async () =>
                {
                    var dontShowAgain = await ShowStellarVariabilityWarningAsync();
                    if (dontShowAgain)
                        vm.SuppressStellarVariabilityWarning = true;
                };
                vm.PlaySoundAction         = PlayCompletionSound;
                vm.PlayStatusSoundAction   = PlayStatusSound;
                // History file pickers
                vm.Results.ShowPasswordWarningFunc = () => ShowInfoAsync(
                    "Password Not Encrypted",
                    "⚠  Your AAVSO password will be stored in plain text.\n\n" +
                    $"It is saved in config.json inside:\n{ConfigService.AppDataDir}\n\n" +
                    "and is not encrypted or otherwise protected.\n\n" +
                    "Uncheck \"Save Password\" at any time to remove it from disk.");
                vm.History.SaveCsvFunc     = () => SaveHistoryFileAsync("csv");
                vm.History.SaveXlsxFunc    = () => SaveHistoryFileAsync("xlsx");
                vm.History.OpenJsonFunc    = () => OpenHistoryFileAsync("json");
                vm.History.OpenCsvXlsxFunc = () => OpenHistoryFileAsync("csvxlsx");
            }
        };

        Opened += async (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                await vm.RunStartupUpdateCheckAsync();
                await ShowV270WhatsNewAsync(vm);
                await ShowV271WhatsNewAsync(vm);
                await ShowTipOfDayAsync(vm);
            }
        };

        Closing += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
                vm.SaveOnExit();
        };
    }

    // ── Recent Sessions flyout ────────────────────────────────────────────────

    private void OnExoticSetupClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var win = new Window
        {
            Title     = "Python & EXOTIC Setup",
            Width     = 1000,
            Height    = 900,
            MinWidth  = 700,
            MinHeight = 820,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Tabs.SetupView { DataContext = vm.ExoticSetup },
        };
        win.Show(this);
    }

    private void OnQuickStartClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var win = new Window
        {
            Title                 = "Quick Start",
            Width                 = 780,
            Height                = 760,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content               = new QuickStartView(),
        };
        win.Show(this);
    }

    private void OnUserGuideClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var win = new Window
        {
            Title        = "User Guide",
            Width        = 900,
            Height       = 700,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content      = new Tabs.InstructionsView(),
        };
        win.Show(this);
    }

    private void OnBugReportClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Window? win = null;
        var vm = new BugReportViewModel();
        vm.CloseCallback = () => win?.Close();

        win = new Window
        {
            Title                 = "Submit Feedback",
            Width                 = 480,
            SizeToContent         = Avalonia.Controls.SizeToContent.Height,
            CanResize             = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content               = new BugReportView { DataContext = vm },
        };
        win.Show(this);
    }

    private void OnAboutClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var win = new Window
        {
            Title         = "About TransitLab",
            Width         = 520,
            SizeToContent = Avalonia.Controls.SizeToContent.Height,
            CanResize     = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content      = new AboutView(),
        };
        win.Show(this);
    }

    private async Task ShowV270WhatsNewAsync(MainWindowViewModel vm)
    {
        if (vm.HasSeenV270WhatsNew) return;
        await ShowInfoAsync("What's New in TransitLab v2.7.0",
            "TransitLab v2.7.0 introduces new Comp Star Selection methods — AAVSO VSP, Stone + VSP, and Stone — for finding comparison stars. See Help → User Guide for full details.\n\n" +
            "Comparison star fetching is now manual: after your plate solve completes, select a method under Star Selection and click Fetch Comps.");
        vm.HasSeenV270WhatsNew = true;
    }

    private async Task ShowV271WhatsNewAsync(MainWindowViewModel vm)
    {
        if (vm.HasSeenV271WhatsNew) return;
        await ShowInfoAsync("What's New in TransitLab v2.7.1",
            "TransitLab v2.7.1 adds:\n\n" +
            "•  Stellar Variability Only mode — skip transit fitting for pure variability-monitoring runs.\n\n" +
            "•  A NextAstro plate-solve option (experimental, requires an EXOTIC pre-release build).\n\n" +
            "•  Download Full ldtk Library, plus automatic HTTPS fallback if EXOTIC's limb-darkening download fails.\n\n" +
            "•  A Name column in Comp Star Details, showing each comparison star's VSX or Gaia DR3 identifier.\n\n" +
            "See Help → Revision History for full details.");
        vm.HasSeenV271WhatsNew = true;
    }

    private async Task ShowTipOfDayAsync(MainWindowViewModel vm)
    {
        if (!vm.ShowTipsAtStartup) return;
        var tipVm = new TipOfDayViewModel(vm.NextTipIndex, vm.ShowTipsAtStartup);
        Window? win = null;
        win = new Window
        {
            Title                 = "Tip of the Day",
            Width                 = 500,
            SizeToContent         = Avalonia.Controls.SizeToContent.Height,
            CanResize             = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content               = new TipOfDayView { DataContext = tipVm },
        };
        win.Closed += (_, _) =>
        {
            vm.ShowTipsAtStartup = tipVm.ShowAtStartup;
            vm.NextTipIndex      = (tipVm.CurrentIndex + 1) % Services.TipService.Tips.Length;
        };
        await win.ShowDialog(this);
    }

    private void OnNotificationsSettingsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        Window? win = null;
        var settingsVm = new SettingsViewModel
        {
            SelectedSound        = NormalizeSoundPreset(vm.CompletionSound),
            CustomSoundPath      = vm.CompletionSoundPath,
            StatusAlertsEnabled  = vm.StatusAlertsEnabled,
            ShowTipsAtStartup    = vm.ShowTipsAtStartup,
            SaveCallback         = (sound, path, alerts, showTips) =>
            {
                vm.CompletionSound      = sound;
                vm.CompletionSoundPath  = path;
                vm.StatusAlertsEnabled  = alerts;
                vm.ShowTipsAtStartup    = showTips;
            },
        };
        settingsVm.IsCustom        = settingsVm.SelectedSound == "Custom…";
        settingsVm.BrowseSoundFunc = BrowseSoundFileAsync;
        settingsVm.TestSoundFunc   = PlayCompletionSound;
        settingsVm.CloseCallback   = () => win?.Close();

        win = new Window
        {
            Title                 = "Settings",
            Width                 = 480,
            SizeToContent         = Avalonia.Controls.SizeToContent.Height,
            CanResize             = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content               = new SettingsView { DataContext = settingsVm },
        };
        win.Show(this);
    }

    private void OnAdvancedSettingsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        Window? win = null;
        var advVm = new AdvancedSettingsViewModel
        {
            NonInteractiveRun                = vm.NonInteractiveRun,
            UseNextAstroVariabilityServer     = vm.UseNextAstroVariabilityServer,
            MultiprocessTransformationsText   = vm.MultiprocessTransformations?.ToString() ?? "",
            MultiprocessLightcurveFitsText    = vm.MultiprocessLightcurveFits?.ToString() ?? "",
            UseEnsemblePhotometry             = vm.UseEnsemblePhotometry,
            UseExactlyTheCompsProvided        = vm.UseExactlyTheCompsProvided,
            SaveCallback = (nonInteractive, variability, transformations, lightcurveFits, useEnsemble, useExactComps) =>
            {
                vm.NonInteractiveRun              = nonInteractive;
                vm.UseNextAstroVariabilityServer  = variability;
                vm.MultiprocessTransformations    = transformations;
                vm.MultiprocessLightcurveFits     = lightcurveFits;
                vm.UseEnsemblePhotometry          = useEnsemble;
                vm.UseExactlyTheCompsProvided     = useExactComps;
            },
        };
        advVm.CloseCallback = () => win?.Close();

        win = new Window
        {
            Title                 = "Advanced",
            Width                 = 520,
            SizeToContent         = Avalonia.Controls.SizeToContent.Height,
            CanResize             = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content               = new AdvancedSettingsView { DataContext = advVm },
        };
        win.Show(this);
    }

    private void OnStoneSettingsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        Window? win = null;
        var stoneVm = new StoneSettingsViewModel
        {
            MaxCompStars  = vm.MaxCompStars,
            SaveCallback  = maxComps => vm.MaxCompStars = maxComps,
            CloseCallback = () => win?.Close(),
        };

        win = new Window
        {
            Title                 = "Comp Stars",
            Width                 = 460,
            SizeToContent         = Avalonia.Controls.SizeToContent.Height,
            CanResize             = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content               = new StoneSettingsView { DataContext = stoneVm },
        };
        win.Show(this);
    }

    private void OnAutomationSettingsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        Window? win = null;
        vm.Automation.BrowseFolderFunc = BrowseAutomationFolderAsync;
        vm.Automation.CloseCallback    = () => win?.Close();

        win = new Window
        {
            Title                 = "Automation",
            Width                 = 700,
            SizeToContent         = Avalonia.Controls.SizeToContent.Height,
            MinWidth              = 640,
            CanResize             = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content               = new AutomationSettingsView { DataContext = vm.Automation },
        };
        win.Closing += (_, _) => vm.SaveAutomationConfig();
        win.Show(this);
    }

    private async Task<string?> BrowseSoundFileAsync()
    {
        var results = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title          = "Select Sound File",
            AllowMultiple  = false,
            FileTypeFilter = new List<FilePickerFileType>
            {
                new("Audio files") { Patterns = ["*.wav", "*.mp3", "*.aiff", "*.aif"] },
                new("All files")   { Patterns = ["*"] },
            }
        });
        return results.Count > 0 ? results[0].Path.LocalPath : null;
    }

    private async Task<string?> BrowseUpdateFolderAsync()
    {
        var defaultPath = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        var downloadsPath = System.IO.Path.Combine(defaultPath, "Downloads");
        var startPath = System.IO.Directory.Exists(downloadsPath) ? downloadsPath : defaultPath;

        var folder = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title                  = "Choose download folder",
            AllowMultiple          = false,
            SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(startPath),
        });
        return folder.Count > 0 ? folder[0].Path.LocalPath : null;
    }

    private async Task<string?> BrowseAutomationFolderAsync()
    {
        var folder = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title         = "Select Folder to Monitor",
            AllowMultiple = false,
        });
        return folder.Count > 0 ? folder[0].Path.LocalPath : null;
    }

    private void OnPlateSolveSetupClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        var (solver, astapPath, catalogDir, searchRadius, downsample, solveAllFrames, starFixPath) = vm.GetPlateSolverConfig();
        var setupVm = new PlateSolveSetupViewModel();
        setupVm.LoadFromConfig(solver, astapPath, catalogDir, searchRadius, downsample, solveAllFrames, starFixPath);

        Window? win = null;
        setupVm.BrowseAstapFunc        = () => BrowseAstapExeAsync();
        setupVm.BrowseCatalogDirFunc   = () => BrowseCatalogDirAsync();
        setupVm.BrowseStarFixFunc      = () => BrowseStarFixFolderAsync();
        setupVm.GetDownloadTempDirFunc = GetDownloadTempDir;
        setupVm.SaveCallback           = (s, p, c, r, d, a, sf) => vm.ApplyPlateSolverSettings(s, p, c, r, d, a, sf);
        setupVm.CloseCallback          = () => win?.Close();
        setupVm.ShowInfoFunc           = ShowInfoAsync;

        win = new Window
        {
            Title                 = "Plate Solve Setup",
            Width                 = 680,
            SizeToContent         = Avalonia.Controls.SizeToContent.Height,
            MinHeight             = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content               = new PlateSolveSetupView { DataContext = setupVm },
        };
        win.Opened += async (_, _) => await setupVm.CheckNextAstroSupportAsync(vm.GetActiveEnvironmentPythonExePath());
        win.Show(this);
    }

    private async Task<string?> BrowseStarFixFolderAsync()
    {
        var folder = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title         = "Select StarFix install folder (contains StarFix.exe)",
            AllowMultiple = false,
        });
        return folder.Count > 0 ? folder[0].Path.LocalPath : null;
    }

    private static string GetDownloadTempDir()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TransitLab");
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }

    private async Task<string?> BrowseAstapExeAsync()
    {
        var isMac     = OperatingSystem.IsMacOS();
        var isWindows = OperatingSystem.IsWindows();

        // On Windows the binary is always named astap_cli.exe/astap.exe, so a named filter
        // narrows the list usefully. On Linux/macOS there's no single guaranteed binary name
        // (source builds, distro packages, AppImages, user renames) — a named filter there risks
        // hiding a valid file behind a picker-backend glob quirk (issue #50), so "All files" is
        // the default/first filter and the named one is offered only as a secondary option.
        var namedFilter = isWindows
            ? new FilePickerFileType("ASTAP executable")  { Patterns = ["astap_cli.exe", "astap.exe", "astap"] }
            : isMac
                ? new FilePickerFileType("ASTAP application") { Patterns = ["ASTAP.app", "*.app", "astap", "astap_cli"] }
                : new FilePickerFileType("ASTAP executable")  { Patterns = ["astap", "astap_cli"] };
        var allFilesFilter = new FilePickerFileType("All files") { Patterns = ["*"] };

        var results = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title          = "Locate ASTAP Executable",
            AllowMultiple  = false,
            FileTypeFilter = isWindows
                ? new List<FilePickerFileType> { namedFilter, allFilesFilter }
                : new List<FilePickerFileType> { allFilesFilter, namedFilter },
        });
        if (results.Count == 0) return null;
        var path = results[0].Path.LocalPath;

        // On macOS, the user may select the .app bundle — resolve to the binary inside
        if (isMac && path.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
        {
            var binary = Path.Combine(path, "Contents", "MacOS", "astap");
            if (File.Exists(binary)) return binary;
            // Fallback: first executable (no extension) in Contents/MacOS
            var macosDir = Path.Combine(path, "Contents", "MacOS");
            if (Directory.Exists(macosDir))
            {
                var exe = Directory.GetFiles(macosDir)
                                   .FirstOrDefault(f => !Path.GetFileName(f).Contains('.'));
                if (exe is not null) return exe;
            }
        }
        return path;
    }

    private async Task<string?> BrowseCatalogDirAsync()
    {
        var folder = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title         = "Select ASTAP Catalog Folder",
            AllowMultiple = false,
        });
        return folder.Count > 0 ? folder[0].Path.LocalPath : null;
    }

    private void OnPreviousVersionsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Window? win = null;
        var vm = new PreviousVersionViewModel();
        vm.BrowseFolderFunc = BrowseUpdateFolderAsync;
        vm.CloseCallback    = () => win?.Close();

        win = new Window
        {
            Title                 = "Previous Versions",
            Width                 = 580,
            Height                = 420,
            CanResize             = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content               = new PreviousVersionView { DataContext = vm },
        };
        win.Show(this);
    }

    private void OnRevisionHistoryClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var win = new Window
        {
            Title        = "Revision History",
            Width        = 800,
            Height       = 640,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content      = new RevisionHistoryView(),
        };
        win.Show(this);
    }

    private void OnRecentSessionsMenuClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (sender is not MenuItem parent) return;

        parent.Items.Clear();
        if (vm.RecentSessions.Count == 0)
        {
            parent.Items.Add(new MenuItem { Header = "(no recent sessions)", IsEnabled = false });
            return;
        }
        foreach (var session in vm.RecentSessions)
        {
            var item = new MenuItem { Header = session.Label };
            var captured = session;
            item.Click += (_, _) => vm.LoadRecentSessionCommand.Execute(captured);
            parent.Items.Add(item);
        }
    }

    private void OnHelpQuickLook_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        ShowHelpPopup("Quick Look",
            "Runs a fast least-squares fit instead of EXOTIC's full ultranest posterior inference — useful for a quick preliminary look at a light curve without waiting for the full reduction.\n\n" +
            "• No AAVSO report is generated — Quick Look results are preliminary and can't be submitted.\n\n" +
            "• Requires at least one comparison star that's already passed vetting on the Image Analysis tab.\n\n" +
            "• Uses the same FITS directories, target/comp star selections, and planet parameters as Save & Run EXOTIC — nothing else needs to be reconfigured.\n\n" +
            "Requires the EXOTIC 4.3.2 pre-release dev build. Install it via Tools → Python & EXOTIC Setup → Pre-release / Development Build, then select it as the active environment.");

    // ── [?] help popup (scrollable text + OK button) ───────────────────────────

    private void ShowHelpPopup(string title, string message)
    {
        var tb = new Avalonia.Controls.TextBlock
        {
            Text         = message,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin       = new Avalonia.Thickness(16, 14, 16, 10),
            MaxWidth     = 440,
            FontSize     = 15,
        };
        var scroll = new Avalonia.Controls.ScrollViewer
        {
            Content                       = tb,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility   = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        var btn = new Avalonia.Controls.Button
        {
            Content             = "OK",
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            MinWidth            = 70,
            Margin              = new Avalonia.Thickness(0, 2, 0, 14),
        };
        var layout = new Avalonia.Controls.DockPanel();
        Avalonia.Controls.DockPanel.SetDock(btn, Avalonia.Controls.Dock.Bottom);
        layout.Children.Add(btn);
        layout.Children.Add(scroll);
        var dialog = new Window
        {
            Title                 = title,
            Content               = layout,
            Width                 = 480,
            MaxHeight             = 700,
            SizeToContent         = Avalonia.Controls.SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize             = true,
            ShowInTaskbar         = false,
        };
        btn.Click += (_, _) => dialog.Close();
        _ = dialog.ShowDialog(this);
    }

    // ── Info dialog (centered text + centered OK button) ──────────────────────

    private async Task ShowInfoAsync(string title, string message)
    {
        var btn = new Avalonia.Controls.Button
        {
            Content                    = "OK",
            HorizontalAlignment        = Avalonia.Layout.HorizontalAlignment.Center,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            MinWidth                   = 70,
            Classes                    = { "primary" },
        };

        var dialog = new Window
        {
            Title                 = title,
            Width                 = 320,
            SizeToContent         = Avalonia.Controls.SizeToContent.Height,
            CanResize             = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Avalonia.Controls.StackPanel
            {
                Margin   = new Avalonia.Thickness(28, 24, 28, 20),
                Spacing  = 18,
                Children =
                {
                    new Avalonia.Controls.TextBlock
                    {
                        Text              = message,
                        TextWrapping      = Avalonia.Media.TextWrapping.Wrap,
                        TextAlignment     = Avalonia.Media.TextAlignment.Center,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    },
                    btn,
                }
            },
        };

        btn.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
    }

    // ── Error dialog ──────────────────────────────────────────────────────────

    private async Task ShowErrorAsync(string title, string message)
    {
        var dialog = new Window
        {
            Title                 = title,
            Width                 = 420,
            SizeToContent         = Avalonia.Controls.SizeToContent.Height,
            CanResize             = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Avalonia.Controls.StackPanel
            {
                Margin  = new Avalonia.Thickness(28, 24, 28, 20),
                Spacing = 18,
                Children =
                {
                    new Avalonia.Controls.TextBlock
                    {
                        Text            = message,
                        TextWrapping    = Avalonia.Media.TextWrapping.Wrap,
                        TextAlignment   = Avalonia.Media.TextAlignment.Left,
                    },
                    new Avalonia.Controls.Button
                    {
                        Content             = "OK",
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        MinWidth            = 70,
                        Classes             = { "primary" },
                    },
                }
            },
        };

        // Wire OK button to close the dialog
        var panel = (Avalonia.Controls.StackPanel)dialog.Content!;
        var btn   = (Avalonia.Controls.Button)panel.Children[1];
        btn.Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(this);
    }

    // ── Confirm dialog ────────────────────────────────────────────────────────

    private async Task<bool> ShowConfirmAsync(string title, string message)
    {
        var result = false;

        var headerText = new Avalonia.Controls.TextBlock
        {
            Text         = message,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };

        var scrollViewer = new Avalonia.Controls.ScrollViewer
        {
            MaxHeight = 380,
            Content   = headerText,
        };

        var cancelBtn = new Avalonia.Controls.Button
        {
            Content  = "Cancel",
            MinWidth = 80,
        };
        var proceedBtn = new Avalonia.Controls.Button
        {
            Content  = "Proceed",
            MinWidth = 80,
            Classes  = { "primary" },
        };

        var buttonRow = new Avalonia.Controls.StackPanel
        {
            Orientation         = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing             = 10,
            Children            = { cancelBtn, proceedBtn },
        };

        var dialog = new Window
        {
            Title                 = title,
            Width                 = 460,
            CanResize             = false,
            SizeToContent         = Avalonia.Controls.SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Avalonia.Controls.StackPanel
            {
                Margin   = new Avalonia.Thickness(28, 24, 28, 20),
                Spacing  = 16,
                Children = { scrollViewer, buttonRow },
            },
        };

        cancelBtn.Click  += (_, _) => { result = false; dialog.Close(); };
        proceedBtn.Click += (_, _) => { result = true;  dialog.Close(); };

        await dialog.ShowDialog(this);
        return result;
    }

    // ── Stellar Variability Only warning ───────────────────────────────────────

    private async Task<bool> ShowStellarVariabilityWarningAsync()
    {
        var text = new Avalonia.Controls.TextBlock
        {
            Text         = "⚠  Not For Transit Data\n\n" +
                           "Stellar Variability Only skips transit fitting entirely — EXOTIC will not model or " +
                           "report a transit signal for this reduction.\n\n" +
                           "Only enable this for straight variability monitoring on data that does not contain a " +
                           "transit. If this dataset does include a transit, leave it unchecked so EXOTIC can fit it normally.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };

        var dontShowAgainBox = new Avalonia.Controls.CheckBox { Content = "Don't show this warning again" };

        var okBtn = new Avalonia.Controls.Button
        {
            Content             = "OK",
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            MinWidth            = 70,
            Classes             = { "primary" },
        };

        var dialog = new Window
        {
            Title                 = "Stellar Variability Only",
            Width                 = 440,
            CanResize             = false,
            SizeToContent         = Avalonia.Controls.SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Avalonia.Controls.StackPanel
            {
                Margin   = new Avalonia.Thickness(28, 24, 28, 20),
                Spacing  = 18,
                Children = { text, dontShowAgainBox, okBtn },
            },
        };

        okBtn.Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(this);
        return dontShowAgainBox.IsChecked == true;
    }

    // ── File pickers ──────────────────────────────────────────────────────────

    private async Task<string?> SaveFileAsync(string suggestedName, string initialDir)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title             = "Save inits.json",
            SuggestedFileName = suggestedName,
            SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(initialDir),
            FileTypeChoices   = new List<FilePickerFileType>
            {
                new("JSON files") { Patterns = ["*.json"] },
                new("All files")  { Patterns = ["*"]      },
            }
        });
        return file?.Path.LocalPath;
    }

    private async Task<string?> OpenFileAsync(string initialDir)
    {
        var results = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title          = "Load inits.json",
            AllowMultiple  = false,
            SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(initialDir),
            FileTypeFilter = new List<FilePickerFileType>
            {
                new("JSON files") { Patterns = ["*.json"] },
                new("All files")  { Patterns = ["*"]      },
            }
        });
        return results.Count > 0 ? results[0].Path.LocalPath : null;
    }

    private async Task<string?> OpenAavsoFileAsync(string initialDir)
    {
        var results = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title          = "Import AAVSO Report",
            AllowMultiple  = false,
            SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(initialDir),
            FileTypeFilter = new List<FilePickerFileType>
            {
                new("AAVSO report (AAVSO_*.txt)") { Patterns = ["AAVSO_*.txt"] },
                new("Text files") { Patterns = ["*.txt"] },
                new("All files")  { Patterns = ["*"]      },
            }
        });
        return results.Count > 0 ? results[0].Path.LocalPath : null;
    }

    private async Task<string?> BrowseExoticAsync(string title)
    {
        var results = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title          = title,
            AllowMultiple  = false,
            FileTypeFilter = new List<FilePickerFileType>
            {
                new("Python / EXOTIC executable") { Patterns = ["python.exe", "python3.exe", "python", "python3", "exotic.exe", "exotic"] },
                new("All files")                  { Patterns = ["*"] },
            }
        });
        return results.Count > 0 ? results[0].Path.LocalPath : null;
    }

    private void OnDiagnosticsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Window? win = null;
        var diagVm = new DiagnosticsViewModel();
        diagVm.SaveFileFunc    = SaveDiagnosticsLogAsync;
        diagVm.OpenLogFileFunc = OpenPreviousLogAsync;
        diagVm.CloseCallback   = () => win?.Close();

        win = new Window
        {
            Title                 = "Diagnostics — Session Log",
            Width                 = 900,
            Height                = 600,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content               = new DiagnosticsView { DataContext = diagVm },
        };
        win.Opened += (_, _) => diagVm.Connect();
        win.Closed  += (_, _) => diagVm.Disconnect();
        win.Show(this);
    }

    private void OnDownloadLdtkLibraryClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Window? win = null;
        var ldtkVm = new LdtkLibraryDownloadViewModel();
        ldtkVm.CloseCallback = () => win?.Close();

        win = new Window
        {
            Title                 = "Download Full ldtk Library",
            Width                 = 560,
            Height                = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content               = new LdtkLibraryDownloadView { DataContext = ldtkVm },
        };
        win.Opened += async (_, _) => await ldtkVm.StartAsync();
        win.Show(this);
    }

    private async Task<string?> OpenPreviousLogAsync()
    {
        var logsDir = System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
            "TransitLab", "logs");

        var results = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title          = "Open Previous Session Log",
            AllowMultiple  = false,
            SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(logsDir),
            FileTypeFilter = new List<FilePickerFileType>
            {
                new("Log files") { Patterns = ["*.log"] },
                new("All files") { Patterns = ["*"]     },
            }
        });
        return results.Count > 0 ? results[0].Path.LocalPath : null;
    }

    private async Task<string?> SaveDiagnosticsLogAsync()
    {
        var vm = DataContext as MainWindowViewModel;
        var startDir = vm?.LastDiagnosticsLogDir ?? "";

        // Never fall through to the OS's shared "last used folder" memory here — it isn't
        // scoped per-dialog, so a null SuggestedStartLocation would silently pick up
        // whatever folder was most recently browsed anywhere else in the app (e.g. the
        // Data tab). Default to the app's own logs folder instead, same as "Open Previous Log".
        if (string.IsNullOrEmpty(startDir))
            startDir = System.IO.Path.Combine(ConfigService.AppDataDir, "logs");

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title             = "Save Session Log",
            SuggestedFileName = $"TransitLab_diagnostics_log_{System.DateTime.Now:yyyyMMdd_HHmmss}.txt",
            SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(startDir),
            FileTypeChoices   = new List<FilePickerFileType>
            {
                new("Text files") { Patterns = ["*.txt"] },
                new("All files")  { Patterns = ["*"]     },
            }
        });

        var path = file?.Path.LocalPath;
        if (path is not null && vm is not null)
            vm.LastDiagnosticsLogDir = System.IO.Path.GetDirectoryName(path) ?? vm.LastDiagnosticsLogDir;

        return path;
    }

    private async Task<string?> SaveHistoryFileAsync(string type)
    {
        bool isCsv  = type == "csv";
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title             = isCsv ? "Save History as CSV" : "Save History as XLSX",
            SuggestedFileName = isCsv ? "aavso_submission_history.csv" : "aavso_submission_history.xlsx",
            FileTypeChoices   = new List<FilePickerFileType>
            {
                isCsv ? new("CSV files")  { Patterns = ["*.csv"]  }
                      : new("Excel files"){ Patterns = ["*.xlsx"] },
                new("All files") { Patterns = ["*"] },
            }
        });
        return file?.Path.LocalPath;
    }

    private async Task<string?> OpenHistoryFileAsync(string type)
    {
        bool isJson = type == "json";
        var results = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title          = isJson ? "Select FinalParams JSON" : "Import History from CSV or XLSX",
            AllowMultiple  = false,
            FileTypeFilter = isJson
                ? new List<FilePickerFileType>
                  {
                      new("JSON files")  { Patterns = ["*.json"] },
                      new("All files")   { Patterns = ["*"]      },
                  }
                : new List<FilePickerFileType>
                  {
                      new("CSV / Excel files") { Patterns = ["*.csv", "*.xlsx"] },
                      new("CSV files")         { Patterns = ["*.csv"]           },
                      new("Excel files")       { Patterns = ["*.xlsx"]          },
                      new("All files")         { Patterns = ["*"]               },
                  },
        });
        return results.Count > 0 ? results[0].Path.LocalPath : null;
    }

    // ── Sound ─────────────────────────────────────────────────────────────────

    private static string NormalizeSoundPreset(string preset)
    {
        if (System.OperatingSystem.IsMacOS() && !MacPresetFiles.ContainsKey(preset)
            && preset != "None" && preset != "Custom…")
            return "Glass";
        if (System.OperatingSystem.IsWindows() && !WindowsPresetFiles.ContainsKey(preset)
            && preset != "None" && preset != "Custom…")
            return "Tada";
        return preset;
    }

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern bool PlaySound(string pszSound, nint hmod, uint fdwSound);

    private static readonly Dictionary<string, string> WindowsPresetFiles = new()
    {
        ["Tada"]  = "tada.wav",
        ["Chime"] = "Windows Notify.wav",
        ["Ding"]  = "Windows Ding.wav",
    };

    private static readonly Dictionary<string, string> MacPresetFiles = new()
    {
        ["Glass"]  = "Glass.aiff",
        ["Ping"]   = "Ping.aiff",
        ["Tink"]   = "Tink.aiff",
        ["Funk"]   = "Funk.aiff",
        ["Hero"]   = "Hero.aiff",
        ["Sosumi"] = "Sosumi.aiff",
    };

    private static void AfPlay(string path)
    {
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("afplay", $"\"{path}\"")
                    { UseShellExecute = false };
                System.Diagnostics.Process.Start(psi)?.WaitForExit();
            }
            catch { }
        });
    }

    private static void PlayCompletionSound(string preset, string customPath)
    {
        if (preset == "None") return;

        if (System.OperatingSystem.IsMacOS())
        {
            if (preset == "Custom…")
            {
                if (!string.IsNullOrWhiteSpace(customPath) && File.Exists(customPath))
                    AfPlay(customPath);
                return;
            }
            // Unknown preset (e.g. Windows name saved on Mac) → fall back to Glass
            var macFileName = MacPresetFiles.TryGetValue(preset, out var mf) ? mf : "Glass.aiff";
            var macPath = $"/System/Library/Sounds/{macFileName}";
            if (File.Exists(macPath)) AfPlay(macPath);
            return;
        }

        if (preset == "Custom…")
        {
            if (string.IsNullOrWhiteSpace(customPath) || !File.Exists(customPath)) return;
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    using var reader = new NAudio.Wave.AudioFileReader(customPath);
                    using var output = new NAudio.Wave.WaveOutEvent();
                    output.Init(reader);
                    output.Play();
                    while (output.PlaybackState == NAudio.Wave.PlaybackState.Playing)
                        System.Threading.Thread.Sleep(50);
                }
                catch { }
            });
            return;
        }

        if (!System.OperatingSystem.IsWindows()) return;
        if (!WindowsPresetFiles.TryGetValue(preset, out var fileName)) return;
        var path = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.Windows),
            "Media", fileName);
        if (File.Exists(path))
            PlaySound(path, nint.Zero, 0x00020001u); // SND_FILENAME | SND_ASYNC
    }

    private static void PlayStatusSound(bool success)
    {
        if (System.OperatingSystem.IsMacOS())
        {
            var soundFile = success
                ? "/System/Library/Sounds/Glass.aiff"
                : "/System/Library/Sounds/Sosumi.aiff";
            if (File.Exists(soundFile)) AfPlay(soundFile);
            return;
        }
        if (!System.OperatingSystem.IsWindows()) return;
        var alias = success ? "DeviceConnect" : "DeviceDisconnect";
        PlaySound(alias, nint.Zero, 0x00010001u); // SND_ALIAS | SND_ASYNC
    }
}
