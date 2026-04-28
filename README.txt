TransitLab  v2.6.3
==================
© Art Trail 2026
Powered by EXOTIC (Zellem, Pearson, Blaser et al. 2020, PASP 132)


WHAT IS TRANSITLAB?
-------------------
TransitLab is a desktop launcher and workflow assistant for the EXOTIC
exoplanet transit reduction pipeline.  It guides you through every step
of a reduction run — loading raw FITS frames, performing image analysis
and dark subtraction, entering target and equipment parameters, running
EXOTIC, and reviewing the light curve results — all from a single
tabbed interface.

Additional features include:
  • Automatic plate solving (astrometry.net or ASTAP) for precise sky
    coordinate determination
  • Automatic NEA target-parameter lookup via the NASA Exoplanet Archive
  • Automatic AAVSO comparison-star selection
  • Transit geometry visualizer (real-time animated transit preview)
  • Session history browser with EXOTIC output viewer
  • Python & EXOTIC setup wizard (install, upgrade, uninstall)
  • Automation mode for hands-off, time-triggered reduction runs
  • Automatic update checker with one-click download
  • In-app feedback submission (Help → Submit Feedback)


SYSTEM REQUIREMENTS
-------------------
Windows  : Windows 10 (64-bit) or later; Windows 11 fully supported
macOS    : macOS 11 (Big Sur) or later; Apple Silicon (ARM64)
Linux    : Ubuntu 20.04 or later (x64)

.NET runtime : None required — TransitLab is self-contained and
               includes the .NET 8 runtime in this folder

Disk space   : ~150 MB for TransitLab itself
               ~500 MB additional for Python + EXOTIC

Internet     : Required for NEA queries, AAVSO comp-star lookups,
               and astrometry.net plate solving
               (ASTAP plate solving works offline)


PYTHON REQUIREMENT
------------------
TransitLab requires Python 3.8–3.10 (Python 3.10.11 recommended).
Python is NOT included in this package.

Windows / macOS:
  Open Tools → Python & EXOTIC Setup and click "Download & Install Python".
  TransitLab will download and install Python 3.10.11 automatically.

Linux:
  Install Python manually before using the Setup wizard:
    sudo apt-get install python3.10 python3.10-venv python3-pip


EXOTIC REQUIREMENT
------------------
EXOTIC (EXOplanet Transit Interpretation Code) is the reduction engine
that TransitLab drives.  It is a Python package and is NOT included in
this package.

To install EXOTIC, open Tools → Python & EXOTIC Setup and click
"Install EXOTIC".  TransitLab will upgrade pip, install prerequisites,
and then install EXOTIC from PyPI.

EXOTIC project:  https://github.com/rzellem/EXOTIC
Reference:       Zellem, Pearson, Blaser et al. 2020, PASP 132, 1017


GETTING STARTED
---------------
1. Launch TransitLab
2. Open Tools → Python & EXOTIC Setup
3. Click "Check System" — if Python or EXOTIC are missing, install them
4. Load your FITS frames in the Data tab
5. Work through the tabs left to right (Data → Image Analysis →
   Parameters → Results)
6. Click "Save & Run EXOTIC" in the top bar when ready
7. Review results in the Results and Visualizer tabs


SUPPORT
-------
Help → Submit Feedback  — submit a bug report or feature request
Tools → Diagnostics     — view and save the session log for troubleshooting
For issues with EXOTIC itself, visit https://github.com/rzellem/EXOTIC
