namespace TransitLab.Services;

public static class TipService
{
    public static readonly string[] Tips =
    [
        // 1
        "Read FITS Header does more than read the header — it automatically triggers a plate solve, fetches planet parameters from NEA, and queries AAVSO for comparison stars. You rarely need to click those buttons manually.",
        // 2
        "Click any column header in the History table to sort by that column. Click again to reverse the sort.",
        // 3
        "The Orbit (φ) scrub slider in the Visualizer lets you step through the transit frame by frame — useful for understanding exactly when ingress and egress occur for your observation window.",
        // 4
        "Save your observatory coordinates once using 'Save current as' on the Parameters tab. They'll be available in every future session from the dropdown.",
        // 5
        "The diagnostics log (Tools → Diagnostics) captures the complete EXOTIC output for the current session. If a run fails, check there first — it often contains the exact error message from EXOTIC.",
        // 6
        "The Black and White sliders in Image Analysis only affect how the image is displayed — they have no effect on the photometry or EXOTIC processing.",
        // 7
        "Auto prompts (Results tab) silences known EXOTIC interactive questions so runs complete without interruption. Turn it off temporarily if EXOTIC is behaving unexpectedly and you want to see every prompt.",
        // 8
        "If Fetch Comps doesn't find enough stars, go to the Image Analysis tab, scan files, select a frame, enable Pick Comp Stars, click stars manually, then click Send to Comp Stars.",
        // 9
        "Pixel scale values you save on the Parameters tab persist across sessions. If you use the same telescope setup regularly, save it once and select it from the dropdown each time.",
        // 10
        "Automation (Tools → Settings → Automation) lets TransitLab wait until a configured start time and then run the full pipeline — plate solve, NEA fetch, star selection, and EXOTIC — without any user interaction.",
        // 11
        "Use Load Photometry in the Visualizer to overlay your actual EXOTIC light curve data on the theoretical transit model. It accepts the AAVSO report file produced by EXOTIC directly.",
        // 12
        "The VSP chart (Image Analysis tab) displays AAVSO comparison star magnitudes for your field. Cross-reference it with your image to confirm you're using the right comp stars.",
        // 13
        "Import FinalParams JSON in the History tab lets you backfill past EXOTIC runs that weren't automatically recorded — point it at the FinalParams_PyLCOv2.json file in your EXOTIC output folder.",
        // 14
        "Every section header has a [?] button that opens a concise explanation of every field and control in that section — a quick in-app reference without leaving your workflow.",
        // 15
        "If the Save & Run EXOTIC button is disabled, it usually means Read FITS Header hasn't been run yet, or a required field is missing. Click Read FITS Header first, then check the Parameters tab for any empty required fields.",
        // 16
        "Selecting a saved observatory from the dropdown auto-fills latitude, longitude, and elevation simultaneously — no need to re-enter coordinates each session.",
        // 17
        "The Parameters ▶ popup in the Visualizer lets you experiment with orbital values and watch the transit shape respond in real time — without modifying your saved inits.",
        // 18
        "If a plate solve fails, verify your pixel scale is close to correct and the FITS file contains a real star field. Click Retry — you don't need to re-read the FITS header to try again.",
        // 19
        "Save inits Only saves your current parameter set without launching EXOTIC. Use it to checkpoint your setup before a run or to share the inits file with a collaborator.",
        // 20
        "The MObs workflow in four steps: Fetch List → select an observation → Download Selected → Use This Data. Lights and Darks directories are populated automatically, then click Read FITS Header to continue.",
        // 21
        "Secondary observer codes are answered automatically during the EXOTIC run when Auto prompts is enabled — no manual input needed during the run.",
        // 22
        "TransitLab checks for updates automatically at startup. You can also check at any time via Help → Check for Updates without restarting the app.",
        // 23
        "Pause Log (Results tab) freezes auto-scroll so you can read earlier EXOTIC output while the run continues unaffected in the background. Click again to resume scrolling.",
        // 24
        "Filter wavelength fields auto-fill when you select a filter from the dropdown. Only enter them manually if using a custom or unlisted filter.",
        // 25
        "Export your History to CSV or XLSX regularly as a backup. The history file lives in %AppData%\\TransitLab on Windows (~/.config/TransitLab on Mac/Linux) and is not affected by app updates, but a manual export is a safe secondary copy.",
        // 26
        "The Stellar Density shown in the Visualizer is derived from a/Rs and the orbital period alone — no mass measurement needed. Compare it to the published value as a quick check that your NEA parameters are reasonable.",
        // 27
        "If EXOTIC exits with an error, the full Python traceback is captured in the diagnostics log (Tools → Diagnostics). Include it when reporting a bug via Help → Submit Feedback.",
        // 28
        "After a MObs download, Use This Data sets both the Lights and Darks directory paths in one click — no manual browsing needed.",
        // 29
        "The Spectral Type in the Visualizer is estimated from Teff. Compare it to the published host star type as a quick check that your NEA parameters are reasonable.",
        // 30
        "The Stone comp star method (Tools → Settings → Comp Stars) scores candidates by color similarity, magnitude match, flux SNR, RUWE astrometric quality, and field centrality — click Fetch Comps on the Parameters tab to run it.",
        // 31
        "Submit Feedback (Help → Submit Feedback) lets you file a bug report or feature request directly to the TransitLab GitHub repository without leaving the app. Your version number and operating system are attached automatically.",
        // 32
        "When submitting feedback, a clear description and steps to reproduce (for bugs) or a use-case explanation (for features) helps the developers address your report faster.",
        // 33
        "Plate Solve Setup (Tools → Plate Solve Setup) lets you choose between Astrometry.net (cloud-based) and ASTAP (local, faster). Switch solvers at any time without losing your other settings.",
        // 34
        "ASTAP plate solves run entirely on your machine and typically complete in 2–10 seconds. Once the executable and a star catalog are installed, no internet connection is needed for solving.",
        // 35
        "Python & EXOTIC Setup (Tools → Python & EXOTIC Setup) walks you through a first-time install. Run Check System first — it detects your current Python and EXOTIC versions and enables only the actions that are needed.",
        // 36
        "If EXOTIC was installed from a development build and behaves unexpectedly, use Install EXOTIC in the Setup wizard. It force-reinstalls the latest stable release from PyPI regardless of what version is currently present.",
        // 37
        "Uninstall EXOTIC (Python & EXOTIC Setup) removes EXOTIC only — Python is left intact. Use it before a clean reinstall if you're troubleshooting a corrupt or incompatible EXOTIC installation.",
        // 38
        "Notifications (Tools → Settings → Notifications) let you set a sound that plays when an EXOTIC run completes. Choose from built-in system sounds, silence, or a custom audio file. A Test button previews the selected sound.",
        // 39
        "The operation status alerts in Notifications play brief success or failure sounds after NEA fetches, plate solves, and AAVSO comp queries — handy for monitoring a long run from another room.",
        // 40
        "Load previous inits restores all parameters from a prior session — planet name, observer code, observatory, equipment settings, and star positions — letting you return to a target without re-entering every field.",
        // 41
        "After loading previous inits, always click Read FITS Header to refresh the plate solve and NEA data for the new night. The inits file carries the parameter values but not the plate solution from the previous session.",
        // 42
        "The flag sigma threshold in Image Analysis controls how aggressively frames are flagged for exclusion. Start at 3.0 and lower it toward 2.0 to flag more frames if clouds or tracking errors appear in the background readings.",
        // 43
        "Zoom with the mouse wheel directly on the image in Image Analysis — no need to use the Zoom +/− buttons. Click and drag anywhere on the image to pan after zooming in.",
        // 44
        "Clear All Fields (File → Clear All Fields) resets every parameter to its default. Use it when starting a new target from scratch rather than loading previous inits, to avoid carrying over settings from a previous observation.",
        // 45
        "The AAVSO Exoplanet Upload button activates as soon as a valid EXOTIC report file is found in the Save Plots directory — even if you've restarted the app since the run completed.",
        // 46
        "TransitLab automatically keeps only the five most recent diagnostics logs (%AppData%\\TransitLab\\logs\\ on Windows, ~/.config/TransitLab/logs/ on Mac/Linux), so the folder never grows unbounded. Use Tools → Diagnostics → Save Log to archive any session permanently.",
        // 47
        "After a successful AAVSO submission, a record is added automatically to the History tab with the fitted parameters from that run. Export to CSV or XLSX regularly as a backup copy of your observation history.",
    ];
}
