using Avalonia.Controls;
using TransitLab;

namespace TransitLab.Views;

public partial class AboutView : UserControl
{
    public string Version => AppInfo.Version;

    public AboutView()
    {
        InitializeComponent();
        DataContext = this;
    }
}
