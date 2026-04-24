using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TransitLab.Services;

public static class ExclusionService
{
    /// <summary>
    /// Move all files in <paramref name="filePaths"/> to a new timestamped
    /// _excl_YYYYMMDD_HHmmss folder that is a sibling of <paramref name="fitsDir"/>.
    /// Returns the ExclusionSession describing the operation, or null if nothing to move.
    /// </summary>
    public static ExclusionSession? MoveExcluded(IEnumerable<string> filePaths, string fitsDir)
    {
        var files = filePaths.Where(File.Exists).ToList();
        if (files.Count == 0) return null;

        var parent    = Path.GetDirectoryName(fitsDir.TrimEnd('\\', '/')) ?? fitsDir;
        var ts        = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var exclDir   = Path.Combine(parent, $"_excl_{ts}");
        Directory.CreateDirectory(exclDir);

        var basenames = new List<string>();
        foreach (var src in files)
        {
            var dst = Path.Combine(exclDir, Path.GetFileName(src));
            File.Move(src, dst, overwrite: true);
            basenames.Add(Path.GetFileName(src));
        }

        return new ExclusionSession
        {
            Date       = ts,
            ExclFolder = exclDir,
            FitsDir    = fitsDir,
            Files      = basenames,
        };
    }

    /// <summary>
    /// Restore all files from an exclusion session back to the original FITS directory.
    /// Deletes the exclusion folder if it becomes empty.
    /// Returns true if at least one file was restored.
    /// </summary>
    public static bool RestoreSession(ExclusionSession session)
    {
        if (!Directory.Exists(session.ExclFolder)) return false;

        bool any = false;
        foreach (var name in session.Files)
        {
            var src = Path.Combine(session.ExclFolder, name);
            var dst = Path.Combine(session.FitsDir,    name);
            if (File.Exists(src))
            {
                try { File.Move(src, dst, overwrite: false); any = true; }
                catch { /* best-effort */ }
            }
        }

        // Remove folder if empty
        try
        {
            if (Directory.Exists(session.ExclFolder) &&
                !Directory.EnumerateFileSystemEntries(session.ExclFolder).Any())
                Directory.Delete(session.ExclFolder);
        }
        catch { /* best-effort */ }

        return any;
    }

    /// <summary>Permanently delete all files in an exclusion session folder.</summary>
    public static void DeleteSession(ExclusionSession session)
    {
        if (!Directory.Exists(session.ExclFolder)) return;
        try { Directory.Delete(session.ExclFolder, recursive: true); } catch { /* best-effort */ }
    }
}
