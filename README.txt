TransitLab  v2.6.1
==================
© Art Trail 2026
Powered by EXOTIC (Zellem, Pearson, Blaser et al. 2020, PASP 132)


WHAT IS TRANSITLAB?
-------------------
TransitLab is a Windows desktop launcher and workflow assistant for the
EXOTIC exoplanet transit reduction pipeline.  It guides you through every
step of a reduction run — loading raw FITS frames, performing image
analysis and dark subtraction, entering target and equipment parameters,
running EXOTIC, and reviewing the light curve results — all from a single
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


SYSTEM REQUIREMENTS
-------------------
Operating system : Windows 10 (version 1607) or later, 64-bit
                   Windows 11 fully supported
                   Windows 7 / 8 / 8.1 are NOT supported

.NET runtime     : None required — TransitLab is self-contained and
                   includes the .NET 8 runtime in this folder

Disk space       : ~150 MB for TransitLab itself
                   ~500 MB additional for Python + EXOTIC

Internet         : Required for NEA queries, AAVSO comp-star lookups,
                   and astrometry.net plate solving
                   (ASTAP plate solving works offline)


PYTHON REQUIREMENT
------------------
TransitLab requires Python 3.8 or later (Python 3.10.x recommended).
Python is NOT included in this package.

If Python is not already installed on your machine, open
Tools → Python & EXOTIC Setup and click "Download & Install Python".
TransitLab will download the Python 3.10.11 installer and run it
silently.  No manual steps are required.

Python 3.12+ is supported but not yet widely tested with EXOTIC.
The recommended version is Python 3.10.11.


EXOTIC REQUIREMENT
------------------
EXOTIC (EXOplanet Transit Interpretation Code) is the reduction engine
that TransitLab drives.  It is a Python package and is NOT included in
this package.

To install EXOTIC, open Tools → Python & EXOTIC Setup and click
"Install EXOTIC".  TransitLab will upgrade pip, install prerequisites,
and then install EXOTIC from PyPI.  The full pip output is streamed to
the log box so you can monitor progress.

EXOTIC project:  https://github.com/rzellem/EXOTIC
Reference:       Zellem, Pearson, Blaser et al. 2020, PASP 132, 1017


GETTING STARTED
---------------
1. Run TransitLab.exe
2. Open Tools → Python & EXOTIC Setup
3. Click "Check System" — if Python or EXOTIC are missing, install them
4. Load your FITS frames in the Data tab
5. Work through the tabs left to right (Data → Image Analysis →
   Parameters → Results)
6. Click "Save & Run EXOTIC" in the top bar when ready
7. Review results in the Results and Visualizer tabs


SUPPORT
-------
For issues with TransitLab, contact the developer.
For issues with EXOTIC itself, visit https://github.com/rzellem/EXOTIC
