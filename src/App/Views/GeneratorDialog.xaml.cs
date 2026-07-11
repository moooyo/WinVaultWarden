using App.Services;
using App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace App.Views;

public sealed partial class GeneratorDialog : ContentDialog
{
    public GeneratorViewModel ViewModel { get; }

    public GeneratorDialog()
    {
        ViewModel = global::App.App.Services.GetRequiredService<GeneratorViewModel>();
        InitializeComponent();
        GeneratorTabs.SelectedItem ??= PasswordTab;
        UpdatePrimaryButtonState();
        Opened += (_, _) => ApplyResponsiveWidth();
        SizeChanged += (_, _) => ApplyResponsiveWidth();
    }

    // 按 XamlRoot 可用宽度钳制内容宽度,避免高 DPI/窄窗口下对话框水平裁切。
    private void ApplyResponsiveWidth()
    {
        var rootWidth = XamlRoot?.Size.Width ?? 0;
        GeneratorContent.Width = ResponsiveDialogSizing.Clamp(
            rootWidth,
            preferredWidth: 620,
            horizontalMargin: 48);
    }

    private void OnGeneratorTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel.Mode = GeneratorTabs.SelectedIndex switch
        {
            1 => GeneratorMode.Passphrase,
            2 => GeneratorMode.Username,
            _ => GeneratorMode.Password,
        };
        UpdatePrimaryButtonState();
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        ViewModel.CopyCommand.Execute(null);
    }

    private void UpdatePrimaryButtonState() => IsPrimaryButtonEnabled = true;

    // "查看历史"请求标志。WinUI 同一时刻只允许一个 ContentDialog 打开,
    // 因此不能在本对话框仍打开时嵌套 ShowAsync 历史对话框(会抛 COMException 使整进程崩溃)。
    private bool _historyRequested;

    /// <summary>返回并清除"请求查看历史"标志,供宿主在本对话框关闭后决定是否打开历史对话框。</summary>
    public bool ConsumeHistoryRequest()
    {
        var requested = _historyRequested;
        _historyRequested = false;
        return requested;
    }

    // 置标志并关闭本对话框;由 MainWindow.ShowGeneratorDialogAsync 在关闭后打开历史对话框、再重开生成器。
    // 不再嵌套 ShowAsync,故此处也无需 async void(消除其吞异常/崩溃风险)。
    private void OnHistoryClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        _historyRequested = true;
        Hide();
    }
}
