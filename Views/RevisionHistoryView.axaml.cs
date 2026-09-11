using Avalonia.Controls;
using TransitLab.Services;

namespace TransitLab.Views;

public partial class RevisionHistoryView : UserControl
{
    public RevisionHistoryView()
    {
        InitializeComponent();
        VersionsList.ItemsSource = RevisionHistoryData.All;
    }
}
