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
                vm.BrowseExoticFunc                = BrowseExoticAsync;
                vm.SelectTabFunc                   = idx => MainTabs.SelectedIndex = idx;
                vm.ShowErrorFunc                   = ShowErrorAsync;
                vm.ShowConfirmFunc                 = ShowConfirmAsync;
                vm.BrowseUpdateFolderFunc          = BrowseUpdateFolderAsync;
                vm.ShowInfoFunc                    = ShowInfoAsync;
                vm.EquipmentTarget.ShowErrorFunc   = ShowErrorAsync;
                vm.EquipmentTarget.ShowInfoFunc    = ShowErrorAsync;
                vm.PlaySoundAction         = PlayCompletionSound;
                vm.PlayStatusSoundAction   = PlayStatusSound;
                // History file pickers
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
            Title                 = "Notifications",
            Width                 = 480,
            SizeToContent         = Avalonia.Controls.SizeToContent.Height,
            CanResize             = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content               = new SettingsView { DataContext = settingsVm },
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

        var (solver, astapPath, catalogDir, searchRadius, downsample, solveAllFrames) = vm.GetPlateSolverConfig();
        var setupVm = new PlateSolveSetupViewModel();
        setupVm.LoadFromConfig(solver, astapPath, catalogDir, searchRadius, downsample, solveAllFrames);

        Window? win = null;
        setupVm.BrowseAstapFunc      = () => BrowseAstapExeAsync();
        setupVm.BrowseCatalogDirFunc = () => BrowseCatalogDirAsync();
        setupVm.SaveCallback         = (s, p, c, r, d, a) => vm.ApplyPlateSolverSettings(s, p, c, r, d, a);
        setupVm.CloseCallback        = () => win?.Close();

        win = new Window
        {
            Title                 = "Plate Solve Setup",
            Width                 = 680,
            SizeToContent         = Avalonia.Controls.SizeToContent.Height,
            MinHeight             = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content               = new PlateSolveSetupView { DataContext = setupVm },
        };
        win.Show(this);
    }

    private async Task<string?> BrowseAstapExeAsync()
    {
        var isMac     = OperatingSystem.IsMacOS();
        var isWindows = OperatingSystem.IsWindows();
        var results = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title          = "Locate ASTAP Executable",
            AllowMultiple  = false,
            FileTypeFilter = new List<FilePickerFileType>
            {
                isWindows
                    ? new("ASTAP executable")  { Patterns = ["astap_cli.exe", "astap.exe", "astap"] }
                    : isMac
                        ? new("ASTAP application") { Patterns = ["ASTAP.app", "*.app", "astap", "astap_cli"] }
                        : new("ASTAP executable")  { Patterns = ["astap", "astap_cli"] },
                new("All files") { Patterns = ["*"] },
            }
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

    // ── Info dialog (centered text + centered OK button) ──────────────────────

    private async Task ShowInfoAsync(string title, string message)
    {
        var btn = new Avalonia.Controls.Button
        {
            Content                    = "OK",
            HorizontalAlignment        = Avalonia.Layout.HorizontalAlignment.Center,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            MinWidth                   = 80,
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
                        MinWidth            = 80,
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
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title             = "Save Session Log",
            SuggestedFileName = $"TransitLab_diagnostics_log_{System.DateTime.Now:yyyyMMdd_HHmmss}.txt",
            FileTypeChoices   = new List<FilePickerFileType>
            {
                new("Text files") { Patterns = ["*.txt"] },
                new("All files")  { Patterns = ["*"]     },
            }
        });
        return file?.Path.LocalPath;
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
