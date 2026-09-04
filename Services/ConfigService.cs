using TransitLab.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TransitLab.Services;

/// <summary>Loads and saves config.json in %AppData%\TransitLab\ on Windows, ~/.config/TransitLab/ on Mac/Linux.</summary>
public static class ConfigService
{
    internal static readonly string AppDataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TransitLab");

    private static readonly string ConfigPath =
        Path.Combine(AppDataDir, "config.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented          = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static AppConfig Load()
    {
        Directory.CreateDirectory(AppDataDir);

        // One-time migration from old exe-directory location
        var oldPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        if (!File.Exists(ConfigPath) && File.Exists(oldPath))
            try { File.Copy(oldPath, ConfigPath); } catch { }

        if (File.Exists(ConfigPath))
        {
            try
            {
                var json = File.ReadAllText(ConfigPath);
                var cfg  = JsonSerializer.Deserialize<AppConfig>(json, JsonOpts);
                if (cfg is not null)
                {
                    MigrateToExoticEnvironments(cfg);
                    SessionLogService.Write($"[Config] Loaded: {ConfigPath}");
                    return cfg;
                }
                SessionLogService.Write($"[Config] WARNING — deserialization returned null; using defaults.");
            }
            catch (Exception ex)
            {
                SessionLogService.Write($"[Config] ERROR loading config: {ex.Message} — using defaults.");
            }
        }
        else
        {
            SessionLogService.Write($"[Config] No config file found; creating defaults at {ConfigPath}");
        }
        var defaults = new AppConfig();
        Save(defaults);
        return defaults;
    }

    /// <summary>
    /// One-time migration (v2.8.0+): wraps a pre-existing single global Python/EXOTIC install
    /// into a "Stable" entry in ExoticEnvironments, so upgrading users don't lose their setup.
    /// This migrated entry is NOT an isolated venv — it just points at the same interpreter
    /// that was already resolved/working — until the user reinstalls it via the new Setup UI.
    /// </summary>
    private static void MigrateToExoticEnvironments(AppConfig cfg)
    {
        if (cfg.ExoticEnvironments.Count > 0) return;
        if (string.IsNullOrWhiteSpace(cfg.PythonExePath)) return;

        cfg.ExoticEnvironments.Add(new ExoticEnvironment
        {
            Name          = "Stable",
            VenvPath      = "",   // not a managed venv — see summary above
            PythonExePath = cfg.PythonExePath,
        });
        cfg.ActiveEnvironmentName = "Stable";
        SessionLogService.Write("[Config] Migrated existing Python/EXOTIC install into a \"Stable\" environment entry.");
    }

    public static void Save(AppConfig cfg)
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(cfg, JsonOpts));
        }
        catch (Exception ex)
        {
            SessionLogService.Write($"[Config] ERROR saving config: {ex.Message}");
        }
    }
}

/// <summary>All persisted launcher settings.</summary>
public class AppConfig
{
    public List<Observatory>     Observatories   { get; set; } = [];
    public List<string>          PixelScales     { get; set; } = [];
    public List<string>          Notes           { get; set; } = [];
    public List<string>          SecondaryCodes  { get; set; } = [];
    public List<string>          AavsoCode       { get; set; } = [];
    public List<string>          BranchUrls      { get; set; } = [];
    public List<SessionRecord>   SessionHistory  { get; set; } = [];
    public string                MobsDownloadDir       { get; set; } = "";
    public bool                  MobsIsDefaultFolder   { get; set; } = false;
    public int                   MobsLookbackDays      { get; set; } = 5;
    public string                LastInitsDir    { get; set; } = "";
    public string                LastDiagnosticsLogDir { get; set; } = "";
    public string                AavsoUsername   { get; set; } = "";
    public bool                  SavePassword    { get; set; } = false;
    public string                AavsoPassword   { get; set; } = "";
    public string                EwSite          { get; set; } = "";
    public string                EwEquipment     { get; set; } = "";
    public double                FlagSigma       { get; set; } = 3.0;
    public string                PythonExePath      { get; set; } = "";
    public string                ExoticExePath      { get; set; } = "";

    // Multiple isolated EXOTIC installs (v2.8.0+) — each entry is a Python venv with its
    // own EXOTIC install, so stable and pre-release builds can coexist without reinstalling.
    // PythonExePath/ExoticExePath above are kept for one-time migration of a pre-existing
    // single global install into an initial "Stable" entry here.
    public List<ExoticEnvironment> ExoticEnvironments  { get; set; } = [];
    public string                  ActiveEnvironmentName { get; set; } = "Stable";
    public bool                  AutoAnswerPrompts  { get; set; } = true;
    public string                CompletionSound     { get; set; } = "Tada";
    public string                CompletionSoundPath { get; set; } = "";
    public bool                  StatusAlertsEnabled { get; set; } = true;

    // Automation
    public string AutoMonitorFolder    { get; set; } = "";
    public string AutoStartTime        { get; set; } = "";   // "HH:mm" 24-hour, empty = immediate

    // Advanced EXOTIC options (Settings → Advanced) — all four are CLI switches that only
    // exist in the EXOTIC 4.3.2 pre-release dev build; LaunchExoticAsync only appends them
    // when the active runtime actually resolves to that build, regardless of these values.
    public bool NonInteractiveRun             { get; set; } = false;
    public bool UseNextAstroVariabilityServer { get; set; } = false;
    public int? MultiprocessTransformations   { get; set; } = null;
    public int? MultiprocessLightcurveFits    { get; set; } = null;
    // These two default to true (unlike the switches above) — with both on, EXOTIC uses
    // exactly the Stone-vetted comp stars TransitLab supplies as a combined ensemble,
    // rather than re-ranking them itself and picking (or capping to) a subset.
    public bool UseEnsemblePhotometry         { get; set; } = true;
    public bool UseExactlyTheCompsProvided    { get; set; } = true;

    // One-time "What's New" style alert shown the first time v2.9.1 detects the active
    // EXOTIC environment is the 4.3.2 pre-release dev build, introducing the six
    // pre-release-only features above (Quick Look + the five Advanced settings).
    public bool HasSeenWbomFeaturesAlert      { get; set; } = false;

    // Tip of the Day
    public bool ShowTipsAtStartup { get; set; } = true;
    public int  NextTipIndex      { get; set; } = 0;

    // First-launch "What's New" announcements, keyed by version
    public bool HasSeenV270WhatsNew { get; set; } = false;
    public bool HasSeenV271WhatsNew { get; set; } = false;

    // Stellar Variability Only warning popup
    public bool SuppressStellarVariabilityWarning { get; set; } = false;

    // Stone comp star selection
    public int MaxCompStars { get; set; } = 10;

    // Plate solver settings
    public string PlateSolver       { get; set; } = "AstrometryNet";  // "AstrometryNet" | "ASTAP" | "NextAstro"
    public string AstapExePath      { get; set; } = "";
    public string AstapCatalogDir   { get; set; } = "";  // blank = same dir as exe
    public int    AstapSearchRadius { get; set; } = 60;   // arcminutes
    public int    AstapDownsample    { get; set; } = 0;
    public bool   AstapSolveAllFrames { get; set; } = false;

    // Last-used UI values (restored on next launch)
    public LastUi                LastUi          { get; set; } = new();
}

public class SessionRecord
{
    public string Label { get; set; } = "";   // e.g. "HAT-P-36 b  2026-03-22"
    public string Path  { get; set; } = "";   // absolute path to the inits_*.json
}

/// <summary>One isolated EXOTIC install — a Python venv with EXOTIC pip-installed into it.</summary>
public class ExoticEnvironment
{
    public string  Name              { get; set; } = "";   // "Stable" / "Pre-release"
    public string  VenvPath           { get; set; } = "";   // e.g. %AppData%\TransitLab\python_envs\stable
    public string  PythonExePath     { get; set; } = "";   // <VenvPath>\Scripts\python.exe or <VenvPath>/bin/python
    public string? CachedVersion     { get; set; }         // last-known "import exotic; __version__" result
    public string? SourceBranchUrl   { get; set; }          // git+ URL used for a pre-release install; null for stable
    public string? BasePythonExePath { get; set; }          // base interpreter this slot's venv was created from (Pre-release only today — Stable's base is re-detected each session via Check System, not persisted per-slot)
}

public class ExclusionSession
{
    public string Date        { get; set; } = "";   // "YYYYMMDD_HHmmss"
    public string ExclFolder  { get; set; } = "";   // full path to _excl_* folder
    public string FitsDir     { get; set; } = "";   // original FITS dir
    public List<string> Files { get; set; } = [];   // basenames of moved files
}

/// <summary>All field values that should survive between sessions.</summary>
public class LastUi
{
    // Observation
    public string FitsDir      { get; set; } = "";
    public string SaveDir      { get; set; } = "";
    public bool   SaveDirUserSet { get; set; } = false;
    public string DarksDir     { get; set; } = "";
    public string FlatsDir     { get; set; } = "";
    public string BiasDir      { get; set; } = "";
    public string AavsoCode    { get; set; } = "";
    public string SecondaryCode{ get; set; } = "";
    public string ObsDate      { get; set; } = "";
    public string Latitude     { get; set; } = "";
    public string Longitude    { get; set; } = "";
    public string Elevation    { get; set; } = "";

    // Equipment
    public string CameraType   { get; set; } = "CCD";
    public string Binning      { get; set; } = "1x1";
    public string Filter       { get; set; } = "CV";
    public string FilterMin    { get; set; } = "";
    public string FilterMax    { get; set; } = "";
    public string PixelScale   { get; set; } = "";
    public string Notes        { get; set; } = "";
    public string CompMethod   { get; set; } = "AAVSO VSP";

    // Star selection
    public string TargetXY     { get; set; } = "";
    public string CompXY       { get; set; } = "";

    // Planet parameters
    public string PlanetName   { get; set; } = "";
    public string TargetRa     { get; set; } = "";
    public string TargetDec    { get; set; } = "";
    public string HostStarName { get; set; } = "";
    public string OrbitalPeriod     { get; set; } = "";
    public string OrbitalPeriodUnc  { get; set; } = "";
    public string MidTransitTime    { get; set; } = "";
    public string MidTransitTimeUnc { get; set; } = "";
    public string RpRs              { get; set; } = "";
    public string RpRsUnc           { get; set; } = "";
    public string ARs               { get; set; } = "";
    public string ARsUnc            { get; set; } = "";
    public string Inclination       { get; set; } = "";
    public string InclinationUnc    { get; set; } = "";
    public string Eccentricity      { get; set; } = "";
    public string ArgPeriastron     { get; set; } = "";
    public string Teff              { get; set; } = "";
    public string TeffPlus          { get; set; } = "";
    public string TeffMinus         { get; set; } = "";
    public string Metallicity       { get; set; } = "";
    public string MetallicityPlus   { get; set; } = "";
    public string MetallicityMinus  { get; set; } = "";
    public string Logg              { get; set; } = "";
    public string LoggPlus          { get; set; } = "";
    public string LoggMinus         { get; set; } = "";
    public string StarDistance      { get; set; } = "";
    public string PmRa              { get; set; } = "";
    public string PmDec             { get; set; } = "";
}
