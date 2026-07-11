using App.Services;
using App.ViewModels;
using App.ViewModels.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace App.Views;

public sealed partial class GeneratorHistoryDialog : ContentDialog
{
    public GeneratorViewModel ViewModel { get; }

    // 宿主(MainWindow)在本对话框关闭后读取该标志,决定是否再弹出一次清除确认对话框。
    // 本对话框自身绝不在仍打开(ShowAsync 未返回)时构造/显示第二个 ContentDialog——
    // 同一 XamlRoot 下嵌套 ShowAsync 是禁止的旁路。
    public bool ClearRequested { get; private set; }

    public GeneratorHistoryDialog(GeneratorViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Opened += (_, _) => ApplyResponsiveWidth();
        SizeChanged += (_, _) => ApplyResponsiveWidth();
    }

    // 按 XamlRoot 可用宽度钳制内容宽度,避免高 DPI/窄窗口下对话框水平裁切。
    private void ApplyResponsiveWidth()
    {
        var rootWidth = XamlRoot?.Size.Width ?? 0;
        GeneratorHistoryContent.Width = ResponsiveDialogSizing.Clamp(
            rootWidth,
            preferredWidth: 520,
            horizontalMargin: 48);
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GeneratorHistoryItem item })
            ViewModel.CopyHistoryItemCommand.Execute(item);
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        ClearRequested = true;
        Hide();
    }
}
