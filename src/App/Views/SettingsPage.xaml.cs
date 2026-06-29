using App.Services;
using App.ViewModels;
using Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace App.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; }

    public SettingsPage()
    {
        ViewModel = global::App.App.Services.GetRequiredService<SettingsViewModel>();
        InitializeComponent();
    }

    private void OnExportDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(AboutInfo.ToDiagnosticsText());
        Clipboard.SetContent(package);
    }

    // ── 编辑资料 ─────────────────────────────────────────────────────────────

    private async void OnEditProfileClick(object sender, RoutedEventArgs e)
    {
        var nameBox = new TextBox
        {
            PlaceholderText = "显示名称",
            Text = string.Empty,
            MinWidth = 320,
        };
        AutomationProperties.SetAutomationId(nameBox, "EditProfileNameBox");

        var errorText = new TextBlock
        {
            Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetAutomationId(errorText, "EditProfileErrorText");
        errorText.SetValue(TextBlock.ForegroundProperty,
            Application.Current.Resources["SystemFillColorCriticalBrush"]);

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(nameBox);
        panel.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            Title = "编辑资料",
            Content = panel,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        ViewModel.OperationError = string.Empty;

        while (true)
        {
            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
                return;

            await ViewModel.RenameAsync(nameBox.Text.Trim());

            if (string.IsNullOrEmpty(ViewModel.OperationError))
                return;

            errorText.Text = ViewModel.OperationError;
            errorText.Visibility = Visibility.Visible;
        }
    }

    // ── 修改主密码 ───────────────────────────────────────────────────────────

    private async void OnChangePasswordCardClick(object sender, RoutedEventArgs e)
    {
        var currentBox = new PasswordBox { PlaceholderText = "当前主密码", MinWidth = 320 };
        AutomationProperties.SetAutomationId(currentBox, "ChangePasswordCurrentBox");

        var newBox = new PasswordBox { PlaceholderText = "新主密码" };
        AutomationProperties.SetAutomationId(newBox, "ChangePasswordNewBox");

        var confirmBox = new PasswordBox { PlaceholderText = "确认新主密码" };
        AutomationProperties.SetAutomationId(confirmBox, "ChangePasswordConfirmBox");

        var hintBox = new TextBox { PlaceholderText = "密码提示（可选）" };
        AutomationProperties.SetAutomationId(hintBox, "ChangePasswordHintBox");

        var errorText = new TextBlock { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(errorText, "ChangePasswordErrorText");
        errorText.SetValue(TextBlock.ForegroundProperty,
            Application.Current.Resources["SystemFillColorCriticalBrush"]);

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(currentBox);
        panel.Children.Add(newBox);
        panel.Children.Add(confirmBox);
        panel.Children.Add(hintBox);
        panel.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            Title = "修改主密码",
            Content = panel,
            PrimaryButtonText = "修改",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        ViewModel.OperationError = string.Empty;

        while (true)
        {
            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
                return;

            var hint = string.IsNullOrWhiteSpace(hintBox.Text) ? null : hintBox.Text.Trim();
            await ViewModel.ChangePasswordAsync(currentBox.Password, newBox.Password, confirmBox.Password, hint);

            if (string.IsNullOrEmpty(ViewModel.OperationError))
            {
                NavigateToLogin();
                return;
            }

            errorText.Text = ViewModel.OperationError;
            errorText.Visibility = Visibility.Visible;
        }
    }

    // ── 加密设置（KDF 迭代次数） ─────────────────────────────────────────────

    private async void OnChangeKdfCardClick(object sender, RoutedEventArgs e)
    {
        var currentBox = new PasswordBox { PlaceholderText = "当前主密码", MinWidth = 320 };
        AutomationProperties.SetAutomationId(currentBox, "ChangeKdfCurrentBox");

        var iterationsBox = new NumberBox
        {
            Header = "PBKDF2 迭代次数",
            Minimum = 100000,
            Maximum = 10000000,
            Value = 600000,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            SmallChange = 100000,
            LargeChange = 500000,
        };
        AutomationProperties.SetAutomationId(iterationsBox, "ChangeKdfIterationsBox");

        var errorText = new TextBlock { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(errorText, "ChangeKdfErrorText");
        errorText.SetValue(TextBlock.ForegroundProperty,
            Application.Current.Resources["SystemFillColorCriticalBrush"]);

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(currentBox);
        panel.Children.Add(iterationsBox);
        panel.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            Title = "加密设置",
            Content = panel,
            PrimaryButtonText = "应用",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        ViewModel.OperationError = string.Empty;

        while (true)
        {
            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
                return;

            var iterations = double.IsNaN(iterationsBox.Value) ? 600000 : (int)iterationsBox.Value;
            await ViewModel.ChangeIterationsAsync(currentBox.Password, iterations);

            if (string.IsNullOrEmpty(ViewModel.OperationError))
            {
                NavigateToLogin();
                return;
            }

            errorText.Text = ViewModel.OperationError;
            errorText.Visibility = Visibility.Visible;
        }
    }

    // ── 成功后导航到锁定/登录页 ──────────────────────────────────────────────

    private static void NavigateToLogin()
    {
        var auth = global::App.App.Services.GetRequiredService<IAuthService>();
        _ = auth.LockAsync().ContinueWith(_ =>
        {
            global::App.App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
                global::App.App.MainWindow?.ShowLogin());
        }, System.Threading.Tasks.TaskScheduler.Default);
    }
}
