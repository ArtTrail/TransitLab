# TransitLab

**TransitLab** is a cross-platform desktop launcher and workflow assistant for the [EXOTIC](https://github.com/rzellem/EXOTIC) exoplanet transit reduction pipeline. It guides you through every step of a reduction run — from loading raw FITS frames to submitting results to AAVSO — all from a single tabbed interface. Runs on Windows, macOS (Apple Silicon), and Linux.

> Powered by EXOTIC (Zellem, Pearson, Blaser et al. 2020, PASP 132)

---

## Screenshots

![Visualizer](Assets/Screenshots/Visualizer.png)
*Transit geometry visualizer with real-time animated transit preview and light curve*

![Image Analysis](Assets/Screenshots/ImageAnalysis.png)
*Image Analysis tab — scan, flag, and exclude frames; pick target and comparison stars*

![Parameters](Assets/Screenshots/Parameters.png)
*Parameters tab — automatic target lookup from the NASA Exoplanet Archive*

![Results](Assets/Screenshots/Results.png)
*Results tab — EXOTIC output log and AAVSO submission*

![History](Assets/Screenshots/History.png)
*Session history browser with sortable observation records*

---

## Features

- **Guided workflow** — tabbed interface walks you through Data → Image Analysis → Parameters → Results
- **Automatic plate solving** — via astrometry.net (online) or ASTAP (local, offline)
- **NASA Exoplanet Archive integration** — target parameters fetched automatically by planet name
- **AAVSO comparison star selection** — comparison stars fetched and selected automatically
- **Transit geometry visualizer** — real-time animated transit preview with Mandel & Agol light curve physics
- **Session history** — browse, export, and import past reduction records
- **Automation mode** — unattended reduction; monitors a folder and runs the full pipeline automatically when frames stop arriving
- **Python & EXOTIC setup wizard** — install, upgrade, or uninstall EXOTIC without touching the command line
- **AAVSO submission** — log in and upload results to AAVSO Exoplanet Watch directly from the app
- **Submit Feedback** — send bug reports and feature requests directly from the app (Help → Submit Feedback)

---

## Requirements

| | |
|---|---|
| **Windows** | Windows 10 (64-bit) or later |
| **macOS** | macOS 11 Big Sur or later, Apple Silicon (ARM64) |
| **Linux** | Ubuntu 20.04 or later (x64) |
| **Python** | 3.8 or later (3.10.x recommended) |
| **EXOTIC** | Installed via the built-in setup wizard |
| **.NET** | Not required — runtime is bundled in the download |

---

## Installation

1. Download the latest release for your platform from **[Releases](https://github.com/ArtTrail/TransitLab/releases/latest)**
   - **Windows:** `TransitLab-v2.7.0-win-x64.zip` — extract and run `TransitLab.exe`
   - **macOS:** `TransitLab-v2.7.0-osx-arm64.dmg` — drag to Applications, right-click → Open the first time
   - **Linux:** `TransitLab-v2.7.0-linux-x64.zip` — extract and run `./TransitLab`
2. Open **Tools → Python & EXOTIC Setup** and click **Check System** to verify or install Python and EXOTIC

No installer required. No .NET installation required.

---

## Windows Security Warning

TransitLab is not code-signed (that requires a paid certificate plus a hardware security key, not currently justified for a free, open-source project), so Windows may flag it as coming from an unrecognized publisher. This is expected — here's how to get past it:

**Most users see this — "Windows protected your PC" (SmartScreen):**
Click **More info**, then **Run anyway**.

**Less common — the app opens briefly then silently closes, no error at all:**
This is **Smart App Control**, a stricter Windows 11 feature enabled by default on some newer installs. To fix:
1. **Settings → Privacy & security → Windows Security → App & browser control → Smart App Control** → turn it **Off**.
2. **Restart your PC** — the change needs a reboot to take effect.
3. Delete your existing TransitLab folder and **re-download and re-extract a fresh copy** (a blocked file may already have been partially removed).
4. Right-click `TransitLab.exe` → **Properties** → check **Unblock** at the bottom → **Apply**, then launch again.

If you still have trouble, use **Help → Submit Feedback** from a working install, or open a GitHub issue.

---

## Getting Started

1. Load your calibrated FITS frames in the **Data** tab
2. Scan and review frames in the **Image Analysis** tab
3. Enter target and equipment details in the **Parameters** tab (or let TransitLab fetch them automatically)
4. Click **Save & Run EXOTIC** in the top bar
5. Review the light curve in the **Results** and **Visualizer** tabs
6. Submit to AAVSO directly from the Results tab

See the built-in User Guide (**Help → User Guide**) for full documentation.

---

## License

This project is licensed under the MIT License — see the [LICENSE](LICENSE) file for details.

---

## About

© Art Trail 2026  
Built for [EXOTIC](https://github.com/rzellem/EXOTIC) — Zellem, Pearson, Blaser et al. 2020, PASP 132, 1017  
Developed with [Avalonia UI](https://avaloniaui.net) and .NET 8
