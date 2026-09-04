namespace TransitLab;

/// <summary>
/// Single source of truth for the app version — every displayed version string
/// (window title, footer, About, Bug Report, diagnostics log) reads from here.
/// Bump exactly this one constant per release.
/// </summary>
public static class AppInfo
{
    public const string Version = "2.10.0";
}
