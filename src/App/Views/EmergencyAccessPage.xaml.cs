using App.ViewModels;
using Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace App.Views;

public sealed partial class EmergencyAccessPage : Page
{
    public EmergencyAccessViewModel ViewModel { get; }

    public EmergencyAccessPage()
    {
        ViewModel = global::App.App.Services.GetRequiredService<EmergencyAccessViewModel>();
        InitializeComponent();
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        _ = ViewModel.LoadAsync();
    }

    // ── 选中项同步 ────────────────────────────────────────────────────────────

    private void OnMyContactsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MyContactsList.SelectedItem is EmergencyContact contact)
        {
            ViewModel.SelectedContactId = contact.Id;
            ViewModel.SelectedGranteeId = contact.GranteeId ?? string.Empty;
        }
    }

    private void OnTrustedByOthersSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TrustedByOthersList.SelectedItem is GrantedAccess granted)
        {
            ViewModel.SelectedGrantedId = granted.Id;
            ViewModel.SelectedGrantorEmail = granted.Email ?? string.Empty;
        }
    }

    // ── 邀请对话框 ────────────────────────────────────────────────────────────

    private async void OnInviteClick(object sender, RoutedEventArgs e)
    {
        var emailBox = new TextBox
        {
            PlaceholderText = "联系人邮箱",
            MinWidth = 320,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(emailBox, "InviteEmailBox");

        var typeCombo = new ComboBox { MinWidth = 200, SelectedIndex = 0 };
        typeCombo.Items.Add(new ComboBoxItem { Content = "查看 (View)", Tag = EmergencyAccessType.View });
        typeCombo.Items.Add(new ComboBoxItem { Content = "接管 (Takeover)", Tag = EmergencyAccessType.Takeover });
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(typeCombo, "InviteTypeCombo");

        var waitDaysBox = new NumberBox
        {
            Header = "等待天数",
            Minimum = 1,
            Maximum = 90,
            Value = 7,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            SmallChange = 1,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(waitDaysBox, "InviteWaitDaysBox");

        var errorText = new TextBlock { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        errorText.SetValue(ForegroundProperty, Application.Current.Resources["SystemFillColorCriticalBrush"]);

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(emailBox);
        panel.Children.Add(new TextBlock { Text = "访问类型" });
        panel.Children.Add(typeCombo);
        panel.Children.Add(waitDaysBox);
        panel.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            Title = "邀请紧急联系人",
            Content = panel,
            PrimaryButtonText = "邀请",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        ViewModel.OperationError = null;

        while (true)
        {
            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
                return;

            ViewModel.InviteEmail = emailBox.Text.Trim();

            if (typeCombo.SelectedItem is ComboBoxItem selectedType && selectedType.Tag is EmergencyAccessType t)
                ViewModel.InviteType = t;

            ViewModel.InviteWaitTimeDays = double.IsNaN(waitDaysBox.Value) ? 7 : (int)waitDaysBox.Value;

            await ViewModel.InviteCommand.ExecuteAsync(null);

            if (!ViewModel.HasError)
                return;

            errorText.Text = ViewModel.OperationError ?? string.Empty;
            errorText.Visibility = Visibility.Visible;
        }
    }

    // ── 授予方按钮 ────────────────────────────────────────────────────────────

    private async void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is EmergencyContact contact)
        {
            ViewModel.SelectedContactId = contact.Id;
            ViewModel.SelectedGranteeId = contact.GranteeId ?? string.Empty;
        }
        await ViewModel.ConfirmCommand.ExecuteAsync(null);
    }

    private async void OnReinviteClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is EmergencyContact contact)
            ViewModel.SelectedContactId = contact.Id;
        await ViewModel.ReinviteCommand.ExecuteAsync(null);
    }

    private async void OnApproveClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is EmergencyContact contact)
            ViewModel.SelectedContactId = contact.Id;
        await ViewModel.ApproveCommand.ExecuteAsync(null);
    }

    private async void OnRejectClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is EmergencyContact contact)
            ViewModel.SelectedContactId = contact.Id;
        await ViewModel.RejectCommand.ExecuteAsync(null);
    }

    private async void OnRemoveContactClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is EmergencyContact contact)
            ViewModel.SelectedContactId = contact.Id;
        await ViewModel.RemoveCommand.ExecuteAsync(null);
    }

    // ── 受托方按钮 ────────────────────────────────────────────────────────────

    private async void OnInitiateClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is GrantedAccess granted)
            ViewModel.SelectedGrantedId = granted.Id;
        await ViewModel.InitiateCommand.ExecuteAsync(null);
    }

    private async void OnViewClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is GrantedAccess granted)
        {
            ViewModel.SelectedGrantedId = granted.Id;
            ViewModel.SelectedGrantorEmail = granted.Email ?? string.Empty;
        }
        await ViewModel.ViewCommand.ExecuteAsync(null);

        if (ViewModel.HasRecoveredVault)
            await ShowViewResultDialogAsync();
    }

    private async Task ShowViewResultDialogAsync()
    {
        var vault = ViewModel.RecoveredVault;
        if (vault is null) return;

        var list = new ListView
        {
            ItemsSource = vault.Ciphers,
            SelectionMode = ListViewSelectionMode.None,
            MaxHeight = 400,
        };

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = $"授予方：{vault.GrantorEmail}　共 {vault.Ciphers.Count} 条目",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(list);

        var dialog = new ContentDialog
        {
            Title = "已恢复的密码库",
            Content = panel,
            CloseButtonText = "关闭",
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
    }

    // ── 接管对话框 ────────────────────────────────────────────────────────────

    private async void OnTakeoverClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is GrantedAccess granted)
        {
            ViewModel.SelectedGrantedId = granted.Id;
            ViewModel.SelectedGrantorEmail = granted.Email ?? string.Empty;
        }
        await ShowTakeoverDialogAsync();
    }

    private async Task ShowTakeoverDialogAsync()
    {
        var pwBox = new PasswordBox { PlaceholderText = "新主密码（为对方设置）", MinWidth = 320 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(pwBox, "TakeoverNewPasswordBox");

        var confirmBox = new PasswordBox { PlaceholderText = "确认新主密码" };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(confirmBox, "TakeoverConfirmPasswordBox");

        var errorText = new TextBlock { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        errorText.SetValue(ForegroundProperty, Application.Current.Resources["SystemFillColorCriticalBrush"]);

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = $"您即将重置 {ViewModel.SelectedGrantorEmail} 的主密码，此操作不可逆。",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(pwBox);
        panel.Children.Add(confirmBox);
        panel.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            Title = "接管账户 — 重置主密码",
            Content = panel,
            PrimaryButtonText = "确认接管",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        ViewModel.OperationError = null;

        while (true)
        {
            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
                return;

            if (pwBox.Password != confirmBox.Password)
            {
                errorText.Text = "两次输入的密码不一致";
                errorText.Visibility = Visibility.Visible;
                continue;
            }

            ViewModel.TakeoverNewPassword = pwBox.Password;
            await ViewModel.TakeoverCommand.ExecuteAsync(null);

            if (!ViewModel.HasError)
                return;

            errorText.Text = ViewModel.OperationError ?? string.Empty;
            errorText.Visibility = Visibility.Visible;
        }
    }
}
