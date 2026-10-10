using App.Services;
using App.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace App.Views;

public sealed partial class GeneratorDialog : ContentDialog
{
    private bool _historyRequested;
    public GeneratorViewModel ViewModel => Workspace.ViewModel;

    public GeneratorDialog()
    {
        InitializeComponent();
        Workspace.UseDialogLayout();
        Opened += (_, _) => ApplyResponsiveWidth();
        SizeChanged += (_, _) => ApplyResponsiveWidth();
    }

    private void ApplyResponsiveWidth()
    {
        if (XamlRoot is null)
            return;
        GeneratorContent.Width = ResponsiveDialogSizing.Clamp(
            XamlRoot.Size.Width, preferredWidth: 512, horizontalMargin: 96);
        GeneratorContent.MaxHeight = Math.Max(0, XamlRoot.Size.Height - 200);
    }

    public bool ConsumeHistoryRequest()
    {
        var requested = _historyRequested;
        _historyRequested = false;
        return requested;
    }

    // The host opens history only after this dialog has closed.
    private void OnHistoryRequested(object? sender, EventArgs e)
    {
        _historyRequested = true;
        Hide();
    }
}
