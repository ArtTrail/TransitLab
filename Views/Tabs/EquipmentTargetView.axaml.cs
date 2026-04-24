using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using TransitLab.ViewModels;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace TransitLab.Views.Tabs;

public partial class EquipmentTargetView : UserControl
{
    public EquipmentTargetView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.EquipmentTarget.FilePickerFunc = PickFileAsync;
                vm.Observation.NameDialogFunc     = ShowNameDialogAsync;
            }
        };
    }

    private async Task<string?> PickFileAsync(string title)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return null;
        var results = await topLevel.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title         = title,
                AllowMultiple = false,
                FileTypeFilter = new List<FilePickerFileType>
                {
                    new("CSV files") { Patterns = ["*.csv"] },
                    new("All files") { Patterns = ["*"]     },
                }
            });
        return results.Count > 0 ? results[0].Path.LocalPath : null;
    }

    private async Task<string?> ShowNameDialogAsync(string defaultName)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return null;

        var tcs = new TaskCompletionSource<string?>();

        var tb = new TextBox
        {
            Text      = defaultName,
            Watermark = "e.g.  Trail Observatory",
            Margin    = new Thickness(12, 12, 12, 8),
        };
        var btnOk = new Button
        {
            Content             = "Save",
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin              = new Thickness(0, 0, 8, 0),
            MinWidth            = 70,
        };
        var btnCancel = new Button
        {
            Content             = "Cancel",
            HorizontalAlignment = HorizontalAlignment.Right,
            MinWidth            = 70,
        };
        var buttonRow = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin              = new Thickness(12, 0, 12, 12),
            Spacing             = 0,
            Children            = { btnOk, btnCancel },
        };
        var layout = new StackPanel { Children = { tb, buttonRow } };
        var dialog = new Window
        {
            Title                 = "Save Observatory",
            Content               = layout,
            Width                 = 380,
            SizeToContent         = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize             = false,
            ShowInTaskbar         = false,
        };

        btnOk.Click     += (_, _) => { tcs.TrySetResult(tb.Text?.Trim()); dialog.Close(); };
        btnCancel.Click += (_, _) => { tcs.TrySetResult(null);            dialog.Close(); };
        dialog.Closed   += (_, _) => tcs.TrySetResult(null);

        tb.AttachedToVisualTree += (_, _) => { tb.Focus(); tb.SelectAll(); };
        tb.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Return) { tcs.TrySetResult(tb.Text?.Trim()); dialog.Close(); }
        };

        await dialog.ShowDialog(owner);
        return await tcs.Task;
    }
}
