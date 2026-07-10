using App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Core.Services;

namespace App.Views;

public sealed partial class ImportExportPage : Page
{
    public ImportExportViewModel ViewModel { get; }

    public ImportExportPage()
    {
        ViewModel = global::App.App.Services.GetRequiredService<ImportExportViewModel>();
        InitializeComponent();
    }

    // ── 导出：主密码确认 → FileSavePicker → 落盘 ────────────────────────────────

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        var root = global::App.App.MainWindow?.Content?.XamlRoot ?? XamlRoot;

        var passwordBox = new PasswordBox { PlaceholderText = "当前主密码", MinWidth = 320 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(passwordBox, "ExportMasterPasswordBox");

        var errorText = new TextBlock { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(errorText, "ExportMasterPasswordErrorText");
        errorText.SetValue(TextBlock.ForegroundProperty,
            Application.Current.Resources["SystemFillColorCriticalBrush"]);

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = "请输入当前主密码以确认导出", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(passwordBox);
        panel.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            Title = "确认导出",
            Content = panel,
            PrimaryButtonText = "确认",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root,
        };

        try
        {
            while (true)
            {
                var result = await DialogHelper.ShowTrackedAsync(
                    dialog,
                    () => passwordBox.Password = string.Empty);
                if (result != ContentDialogResult.Primary)
                    return;

                var ok = await ViewModel.VerifyMasterPasswordAsync(passwordBox.Password);
                if (!ok)
                {
                    errorText.Text = "主密码不正确";
                    errorText.Visibility = Visibility.Visible;
                    continue;
                }

                break;
            }
        }
        finally
        {
            passwordBox.Password = string.Empty;
        }

        try
        {
            var format = ViewModel.SelectedExportFormat;
            var picker = new Windows.Storage.Pickers.FileSavePicker
            {
                SuggestedFileName = "bitwarden_export",
            };
            if (format == ExportFormat.Json)
                picker.FileTypeChoices.Add("JSON", new System.Collections.Generic.List<string> { ".json" });
            else
                picker.FileTypeChoices.Add("CSV", new System.Collections.Generic.List<string> { ".csv" });

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(global::App.App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await FilePickerHelper.PickSaveAsync(picker);
            if (file is null)
                return;

            await FilePickerHelper.RunTrackedAsync<object?>(
                async ct =>
                {
                    await Windows.Storage.FileIO.WriteTextAsync(file, ViewModel.ExportToText()).AsTask(ct);
                    return null;
                });
            ViewModel.ExportStatus = "导出成功";
        }
        catch (Exception ex)
        {
            ViewModel.ExportStatus = $"导出失败：{ex.Message}";
        }
    }

    // ── 导入：FileOpenPicker → 读取文本 → 预览 ──────────────────────────────────

    private async void OnChooseImportFileClick(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add(".json");
        picker.FileTypeFilter.Add(".csv");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(global::App.App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await FilePickerHelper.PickSingleAsync(picker);
        if (file is null)
            return;

        var content = await FilePickerHelper.RunTrackedAsync(
            ct => Windows.Storage.FileIO.ReadTextAsync(file).AsTask(ct));
        if (content is null)
            return;
        ViewModel.ImportContent = content;
        ViewModel.PreviewImportCommand.Execute(null);
    }
}
