using CommunityToolkit.Mvvm.ComponentModel;
using TransitLab.Services;
using System.IO;

namespace TransitLab.Models;

/// <summary>Row model for the ExclusionSessionDialog DataGrid.</summary>
public partial class ExclusionSessionRow : ObservableObject
{
    [ObservableProperty] private bool _isSelected;

    public ExclusionSession Session     { get; }
    public string           DateStr     { get; }
    public int              FileCount   { get; }
    public string           FolderName  { get; }
    public string           RestoresTo  { get; }
    public bool             FolderExists{ get; }

    public ExclusionSessionRow(ExclusionSession session)
    {
        Session      = session;
        DateStr      = session.Date;
        FileCount    = session.Files.Count;
        FolderExists = Directory.Exists(session.ExclFolder);
        FolderName   = Path.GetFileName(session.ExclFolder)
                       + (FolderExists ? "" : "  \u2717 folder missing");
        RestoresTo   = Path.GetFileName(session.FitsDir.TrimEnd('\\', '/'));
    }
}
