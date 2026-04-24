using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TransitLab.Models;
using TransitLab.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TransitLab.ViewModels;

public partial class ResultsViewModel : ViewModelBase
{
    // ── EXOTIC log ───────────────────────────────────────────────────────────
    public ObservableCollection<LogLine> LogLines { get; } = new();

    [ObservableProperty] private bool _isLogFrozen = false;

    public void AppendLog(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line  = raw.TrimEnd('\r');
            var lower = line.ToLowerInvariant();
            IBrush color;
            if (lower.Contains("error")     || lower.Contains("traceback") ||
                lower.Contains("exception") || lower.Contains("failed")    ||
                lower.StartsWith("✗"))
                color = new SolidColorBrush(Color.FromRgb(0xBF, 0x61, 0x6A));
            else if (lower.Contains("warning") || lower.Contains("warn"))
                color = new SolidColorBrush(Color.FromRgb(0xEB, 0xCB, 0x8B));
            else if (lower.Contains("success") || lower.Contains("complete") ||
                     lower.Contains("done")    || lower.StartsWith("✓"))
                color = new SolidColorBrush(Color.FromRgb(0xA3, 0xBE, 0x8C));
            else if (line.TrimStart().StartsWith("▶"))
                color = new SolidColorBrush(Color.FromRgb(0x88, 0x92, 0xA0));
            else
                color = new SolidColorBrush(Color.FromRgb(0xEC, 0xEF, 0xF4));

            LogLines.Add(new LogLine { Text = line, Color = color });
        }
    }

    public void ClearLog()
    {
        LogLines.Clear();
        IsPromptActive = false;
        PromptInput    = "";
    }

    public string GetLogText() =>
        string.Join("\n", LogLines.Select(l => l.Text));

    // ── Prompt mode ───────────────────────────────────────────────────────────
    /// <summary>
    /// When true, known EXOTIC prompts (secondary codes, "Enter 1 or 2") are answered
    /// automatically. When false, every prompt is surfaced to the user.
    /// </summary>
    [ObservableProperty] private bool _autoAnswerPrompts = true;

    // ── Interactive EXOTIC prompt ─────────────────────────────────────────────
    [ObservableProperty] private bool   _isPromptActive;
    [ObservableProperty] private string _promptLabel = "";
    [ObservableProperty] private string _promptInput = "";

    private TaskCompletionSource<string>? _promptTcs;

    /// <summary>
    /// Called from the EXOTIC launch loop when a prompt is detected on stdout.
    /// Activates the input row in the Results tab and awaits the user's response.
    /// </summary>
    public async Task<string> RequestPromptAsync(string rawPromptText)
    {
        var label = StripAnsi(rawPromptText).Trim();

        _promptTcs = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            PromptLabel    = label;
            PromptInput    = "";
            IsPromptActive = true;
        });

        var response = await _promptTcs.Task;

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            IsPromptActive = false;
            PromptInput    = "";
        });

        return response;
    }

    [RelayCommand]
    private void SendPrompt()
    {
        _promptTcs?.TrySetResult(PromptInput ?? "");
    }

    /// <summary>
    /// Called by CancelExotic to unblock any pending RequestPromptAsync when the process is killed.
    /// </summary>
    public void CancelPrompt()
    {
        _promptTcs?.TrySetResult("");
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            IsPromptActive = false;
            PromptInput    = "";
            PromptLabel    = "";
        });
    }

    private static string StripAnsi(string s) =>
        Regex.Replace(s, @"\x1B\[[^m]*m", "");

    // ── Light curve images ────────────────────────────────────────────────────
    [ObservableProperty] private Avalonia.Media.Imaging.Bitmap? _lightCurveBitmap;
    [ObservableProperty] private Avalonia.Media.Imaging.Bitmap? _stellarVarBitmap;
    [ObservableProperty] private bool _hasLightCurve;
    [ObservableProperty] private bool _hasStellarVar;

    /// <summary>Invoked on the UI thread when a light curve is first loaded. Wire to sound playback.</summary>
    public Action? LightCurveReadySound { get; set; }

    partial void OnHasLightCurveChanged(bool value)
    {
        if (value) LightCurveReadySound?.Invoke();
    }

    public void ClearOutputImages()
    {
        LightCurveBitmap = null;
        StellarVarBitmap = null;
        HasLightCurve    = false;
        HasStellarVar    = false;
        ReportFile       = "Report file:  —";
        ImageFile        = "Image file:  —";
        AidFile          = "AID file:  —";
        _reportFullPath  = "";
        _lcFullPath      = "";
        _svFullPath      = "";
        UpdateUploadEnabled();
    }

    public void LoadOutputImages(string saveDir)
    {
        if (!Directory.Exists(saveDir)) return;

        var lcFiles = Directory.GetFiles(saveDir, "FinalLightCurve_*.png", SearchOption.AllDirectories);
        if (lcFiles.Length > 0)
        {
            var newest = lcFiles.OrderByDescending(File.GetLastWriteTime).First();
            try { LightCurveBitmap = new Avalonia.Media.Imaging.Bitmap(newest); HasLightCurve = true; }
            catch { }
        }

        var svFiles = Directory.GetFiles(saveDir, "Stellar_Variability.png", SearchOption.AllDirectories);
        if (svFiles.Length > 0)
        {
            try
            {
                _svFullPath      = svFiles[0];
                StellarVarBitmap = new Avalonia.Media.Imaging.Bitmap(_svFullPath);
                HasStellarVar    = true;
            }
            catch { }
        }
    }

    // ── Output file paths (display + full path for upload) ────────────────────
    [ObservableProperty] private string reportFile = "Report file:  —";
    [ObservableProperty] private string imageFile  = "Image file:  —";
    [ObservableProperty] private string aidFile    = "AID file:  —";

    private string _reportFullPath = "";
    private string _lcFullPath     = "";
    private string _svFullPath     = "";

    public void ScanOutputFiles(string saveDir)
    {
        _reportFullPath = "";
        _lcFullPath     = "";

        if (!Directory.Exists(saveDir)) return;

        var reports = Directory.GetFiles(saveDir, "AAVSO_*.txt", SearchOption.AllDirectories);
        if (reports.Length > 0)
        {
            var newest = reports.OrderByDescending(File.GetLastWriteTime).First();
            ReportFile      = "Report:  " + Path.GetFileName(newest);
            _reportFullPath = newest;
        }

        var images = Directory.GetFiles(saveDir, "FinalLightCurve_*.png", SearchOption.AllDirectories);
        if (images.Length > 0)
        {
            var newest = images.OrderByDescending(File.GetLastWriteTime).First();
            ImageFile   = "Image:  " + Path.GetFileName(newest);
            _lcFullPath = newest;
        }

        var aids = Directory.GetFiles(saveDir, "AID_AAVSO_*.txt", SearchOption.AllDirectories);
        if (aids.Length > 0)
            AidFile = "AID:  " + Path.GetFileName(aids.OrderByDescending(File.GetLastWriteTime).First());

        UpdateUploadEnabled();
    }

    // ── AAVSO credentials ────────────────────────────────────────────────────
    [ObservableProperty] private string aavsoUsername = "";
    [ObservableProperty] private string aavsoPassword = "";
    [ObservableProperty] private bool   savePassword  = false;
    [ObservableProperty] private string loginStatus   = "";

    // ── Exoplanet Watch ──────────────────────────────────────────────────────
    [ObservableProperty] private string ewSite       = "";
    [ObservableProperty] private string ewEquipment  = "";
    [ObservableProperty] private bool   gdprAccepted = false;
    [ObservableProperty] private bool   isUploadEnabled = false;
    [ObservableProperty] private string ewStatus     = "";

    public ObservableCollection<string> EwSites      { get; } = ["— select —"];
    public ObservableCollection<string> EwEquipments { get; } = ["— select —"];

    // ── AAVSO session state ───────────────────────────────────────────────────
    private AavsoExositeService?      _aavsoService;
    private List<AavsoSiteEquip>      _ewSiteItems  = [];
    private List<AavsoSiteEquip>      _ewEquipItems = [];
    private bool                       _isLoggedIn   = false;
    private CancellationTokenSource?  _loginCts;

    // ── Injected callbacks ────────────────────────────────────────────────────
    public Func<string>? ObscodeFunc          { get; set; }
    public Action?        RecordSubmissionFunc { get; set; }

    // ── Partial method hooks ─────────────────────────────────────────────────
    partial void OnGdprAcceptedChanged(bool value)   => UpdateUploadEnabled();
    partial void OnEwSiteChanged(string value)       => UpdateUploadEnabled();
    partial void OnEwEquipmentChanged(string value)  => UpdateUploadEnabled();

    private void UpdateUploadEnabled()
    {
        IsUploadEnabled = _isLoggedIn && GdprAccepted &&
                          !string.IsNullOrWhiteSpace(EwSite)      && EwSite      != "— select —" &&
                          !string.IsNullOrWhiteSpace(EwEquipment) && EwEquipment != "— select —" &&
                          !string.IsNullOrEmpty(_reportFullPath)  && File.Exists(_reportFullPath);
    }

    // ── Login command ─────────────────────────────────────────────────────────
    [RelayCommand]
    private async Task Login()
    {
        var username = AavsoUsername.Trim();
        var password = AavsoPassword;
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            LoginStatus = "Enter username and password first.";
            return;
        }

        _loginCts?.Cancel();
        _loginCts = new CancellationTokenSource();
        _aavsoService?.Dispose();
        _aavsoService = new AavsoExositeService();
        _isLoggedIn   = false;

        LoginStatus = "⟳  Connecting…";

        try
        {
            var (ok, msg, sites, equips) = await _aavsoService.LoginAsync(
                username, password, s => LoginStatus = s, _loginCts.Token);

            LoginStatus = msg;
            _isLoggedIn = ok;

            if (ok)
            {
                _ewSiteItems  = sites;
                _ewEquipItems = equips;

                EwSites.Clear();
                EwEquipments.Clear();

                if (sites.Count > 0)
                {
                    foreach (var s in sites) EwSites.Add(s.Name);
                    EwSite = sites[0].Name;
                }
                else
                {
                    EwSites.Add("— select —");
                }

                if (equips.Count > 0)
                {
                    foreach (var e in equips) EwEquipments.Add(e.Name);
                    EwEquipment = equips[0].Name;
                }
                else
                {
                    EwEquipments.Add("— select —");
                }
            }

            UpdateUploadEnabled();
        }
        catch (OperationCanceledException) { LoginStatus = "Login cancelled."; }
    }

    // ── Upload command ────────────────────────────────────────────────────────
    [RelayCommand]
    private async Task UploadExoplanet()
    {
        if (_aavsoService is null || !_isLoggedIn) { EwStatus = "Not logged in."; return; }
        if (!GdprAccepted)                         { EwStatus = "Accept GDPR terms first."; return; }
        if (string.IsNullOrEmpty(_reportFullPath) || !File.Exists(_reportFullPath))
            { EwStatus = "No report file found."; return; }

        var siteName  = EwSite.Trim();
        var equipName = EwEquipment.Trim();
        var siteId  = _ewSiteItems.FirstOrDefault(s => s.Name == siteName)?.Id ?? siteName;
        var equipId = _ewEquipItems.FirstOrDefault(e => e.Name == equipName)?.Id ?? equipName;

        IsUploadEnabled = false;
        EwStatus = "⟳  Uploading…";

        try
        {
            var obscode = ObscodeFunc?.Invoke() ?? "";
            var (ok, msg) = await _aavsoService.UploadAsync(
                _reportFullPath, string.IsNullOrEmpty(_lcFullPath) ? null : _lcFullPath,
                siteId, equipId, obscode, CancellationToken.None);

            EwStatus = msg;
            SessionLogService.Write(ok
                ? $"[AAVSO Upload] ✓  {msg}"
                : $"[AAVSO Upload] ✗  {msg}");
            if (ok) RecordSubmissionFunc?.Invoke();
        }
        finally
        {
            UpdateUploadEnabled();
        }
    }

    // ── Open plots in default viewer ─────────────────────────────────────────
    [RelayCommand]
    private void OpenLightCurve()
    {
        if (string.IsNullOrEmpty(_lcFullPath) || !File.Exists(_lcFullPath)) return;
        try { Process.Start(new ProcessStartInfo(_lcFullPath) { UseShellExecute = true }); }
        catch { }
    }

    [RelayCommand]
    private void OpenStellarVar()
    {
        if (string.IsNullOrEmpty(_svFullPath) || !File.Exists(_svFullPath)) return;
        try { Process.Start(new ProcessStartInfo(_svFullPath) { UseShellExecute = true }); }
        catch { }
    }

    // ── External links ────────────────────────────────────────────────────────
    [RelayCommand]
    private void OpenWebObs()
    {
        try { Process.Start(new ProcessStartInfo("https://www.aavso.org/webobs/file") { UseShellExecute = true }); }
        catch { }
    }

    [RelayCommand]
    private void OpenTransitPostGenerator()
    {
        try { Process.Start(new ProcessStartInfo("https://drloot.github.io/exoplanet-slacker/transitgen.html") { UseShellExecute = true }); }
        catch { }
    }
}
