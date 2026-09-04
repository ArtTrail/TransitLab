using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using TransitLab.ViewModels;
using System.Threading.Tasks;

namespace TransitLab.Views.Tabs;

public partial class ObservationView : UserControl
{
    public ObservationView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ObservationViewModel vm)
            {
                vm.FolderPickerFunc = PickFolderAsync;
                vm.NameDialogFunc   = ShowNameDialogAsync;
            }
        };
    }

    private async Task<string?> PickFolderAsync(string title, string startDir)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return null;

        IStorageFolder? startFolder = null;
        if (!string.IsNullOrEmpty(startDir))
            startFolder = await topLevel.StorageProvider.TryGetFolderFromPathAsync(startDir);

        var results = await topLevel.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = title, AllowMultiple = false, SuggestedStartLocation = startFolder });
        return results.Count > 0 ? results[0].Path.LocalPath : null;
    }

    private async Task<string?> ShowNameDialogAsync(string defaultName)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return null;

        var tcs = new TaskCompletionSource<string?>();

        // Controls
        var tb = new TextBox
        {
            Text        = defaultName,
            Watermark   = "e.g.  Trail Observatory",
            Margin      = new Thickness(12, 12, 12, 8),
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

        var layout = new StackPanel
        {
            Children = { tb, buttonRow },
        };

        var dialog = new Window
        {
            Title                   = "Save Observatory",
            Content                 = layout,
            Width                   = 380,
            SizeToContent           = SizeToContent.Height,
            WindowStartupLocation   = WindowStartupLocation.CenterOwner,
            CanResize               = false,
            ShowInTaskbar           = false,
        };

        btnOk.Click     += (_, _) => { tcs.TrySetResult(tb.Text?.Trim()); dialog.Close(); };
        btnCancel.Click += (_, _) => { tcs.TrySetResult(null);            dialog.Close(); };
        dialog.Closed   += (_, _) => tcs.TrySetResult(null);

        // Select all text so user can immediately retype
        tb.AttachedToVisualTree += (_, _) =>
        {
            tb.Focus();
            tb.SelectAll();
        };

        // Enter key confirms
        tb.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Return) { tcs.TrySetResult(tb.Text?.Trim()); dialog.Close(); }
        };

        await dialog.ShowDialog(owner);
        return await tcs.Task;
    }
}
