using App.ViewModels;
using App.ViewModels.Models;
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

    // ── 状态矩阵：每个按钮只在其状态允许的场景下可见 ─────────────────────────────
    // 不存在无条件显示的行级操作；具体状态转换见 Task 7 brief 的状态矩阵。

    public static Visibility ShowReinvite(EmergencyAccessStatus status) =>
        status == EmergencyAccessStatus.Invited ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility ShowConfirm(EmergencyAccessStatus status) =>
        status == EmergencyAccessStatus.Accepted ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility ShowUpdate(EmergencyAccessStatus status) =>
        status == EmergencyAccessStatus.Confirmed ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility ShowApprove(EmergencyAccessStatus status) =>
        status == EmergencyAccessStatus.RecoveryInitiated ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility ShowReject(EmergencyAccessStatus status) =>
        status == EmergencyAccessStatus.RecoveryInitiated ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility ShowRemove(EmergencyAccessStatus status) =>
        status is EmergencyAccessStatus.Invited
            or EmergencyAccessStatus.Accepted
            or EmergencyAccessStatus.Confirmed
            or EmergencyAccessStatus.RecoveryInitiated
            or EmergencyAccessStatus.RecoveryApproved
            ? Visibility.Visible
            : Visibility.Collapsed;

    public static Visibility ShowInitiate(EmergencyAccessStatus status) =>
        status == EmergencyAccessStatus.Confirmed ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility ShowView(
        EmergencyAccessStatus status,
        EmergencyAccessType type) =>
        status == EmergencyAccessStatus.RecoveryApproved
            && type == EmergencyAccessType.View
            ? Visibility.Visible
            : Visibility.Collapsed;

    public static Visibility ShowTakeover(
        EmergencyAccessStatus status,
        EmergencyAccessType type) =>
        status == EmergencyAccessStatus.RecoveryApproved
            && type == EmergencyAccessType.Takeover
            ? Visibility.Visible
            : Visibility.Collapsed;

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

        while (true)
        {
            var dialogResult = await DialogHelper.ShowTrackedAsync(dialog);
            if (dialogResult != ContentDialogResult.Primary)
                return;

            var email = emailBox.Text.Trim();
            var type = typeCombo.SelectedItem is ComboBoxItem selectedType && selectedType.Tag is EmergencyAccessType t
                ? t
                : EmergencyAccessType.View;
            var waitDays = double.IsNaN(waitDaysBox.Value) ? 7 : (int)waitDaysBox.Value;

            var result = await ViewModel.InviteAsync(email, type, waitDays);
            if (result.IsSuccess)
                return;

            errorText.Text = result.Error ?? "邀请失败";
            errorText.Visibility = Visibility.Visible;
        }
    }

    // ── 授予方按钮（均从 Tag 取具体 item，不依赖可变 SelectedId）─────────────────

    private async void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not EmergencyContact item)
            return;
        await ViewModel.ConfirmAsync(item);
    }

    private async void OnReinviteClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not EmergencyContact item)
            return;
        await ViewModel.ReinviteAsync(item);
    }

    private async void OnApproveClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not EmergencyContact item)
            return;
        await ViewModel.ApproveAsync(item);
    }

    private async void OnRejectClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not EmergencyContact item)
            return;
        await ViewModel.RejectAsync(
            item,
            (contact, ct) => DialogHelper.ConfirmAsync(
                XamlRoot,
                "拒绝紧急访问请求",
                $"确定要拒绝来自 {contact.Email} 的紧急访问请求吗？",
                "拒绝",
                ct: ct),
            CancellationToken.None);
    }

    private async void OnRemoveContactClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not EmergencyContact item)
            return;
        await ViewModel.RemoveAsync(
            item,
            (contact, ct) => DialogHelper.ConfirmAsync(
                XamlRoot,
                "移除紧急联系人",
                $"确定要移除 {contact.Email} 吗？此操作无法撤销。",
                "移除",
                ct: ct),
            CancellationToken.None);
    }

    private async void OnUpdateClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not EmergencyContact item)
            return;

        var typeCombo = new ComboBox { MinWidth = 200 };
        typeCombo.Items.Add(new ComboBoxItem { Content = "查看 (View)", Tag = EmergencyAccessType.View });
        typeCombo.Items.Add(new ComboBoxItem { Content = "接管 (Takeover)", Tag = EmergencyAccessType.Takeover });
        typeCombo.SelectedIndex = item.Type == EmergencyAccessType.Takeover ? 1 : 0;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(typeCombo, "UpdateTypeCombo");

        var waitDaysBox = new NumberBox
        {
            Header = "等待天数",
            Minimum = 1,
            Maximum = 90,
            Value = item.WaitTimeDays,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            SmallChange = 1,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(waitDaysBox, "UpdateWaitDaysBox");

        var errorText = new TextBlock { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        errorText.SetValue(ForegroundProperty, Application.Current.Resources["SystemFillColorCriticalBrush"]);

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = "访问类型" });
        panel.Children.Add(typeCombo);
        panel.Children.Add(waitDaysBox);
        panel.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            Title = $"修改紧急访问 — {item.Email}",
            Content = panel,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        while (true)
        {
            var dialogResult = await DialogHelper.ShowTrackedAsync(dialog);
            if (dialogResult != ContentDialogResult.Primary)
                return;

            var type = typeCombo.SelectedItem is ComboBoxItem selectedType && selectedType.Tag is EmergencyAccessType t
                ? t
                : item.Type;
            var waitDays = double.IsNaN(waitDaysBox.Value) ? item.WaitTimeDays : (int)waitDaysBox.Value;

            var result = await ViewModel.UpdateAsync(item, type, waitDays);
            if (result.IsSuccess)
                return;

            errorText.Text = result.Error ?? "修改失败";
            errorText.Visibility = Visibility.Visible;
        }
    }

    // ── 受托方按钮 ────────────────────────────────────────────────────────────

    private async void OnInitiateClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not GrantedAccess item)
            return;
        await ViewModel.InitiateAsync(item);
    }

    private async void OnViewClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not GrantedAccess item)
            return;
        var result = await ViewModel.ViewAsync(item);
        if (result.IsSuccess && result.Value is { } recovered)
            await ShowViewResultDialogAsync(recovered);
    }

    private async Task ShowViewResultDialogAsync(RecoveredVault recovered)
    {
        var recoveredItems = recovered.Ciphers
            .Select(RecoveredCipherListItem.FromCipher)
            .ToArray();
        var list = new ListView
        {
            ItemsSource = recoveredItems,
            ItemTemplate = (DataTemplate)Resources["RecoveredCipherItemTemplate"],
            SelectionMode = ListViewSelectionMode.None,
            MaxHeight = 400,
        };

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = $"授予方：{recovered.GrantorEmail}　共 {recovered.Ciphers.Count} 条目",
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
        await DialogHelper.ShowTrackedAsync(dialog);
    }

    // ── 接管对话框 ────────────────────────────────────────────────────────────

    private async void OnTakeoverClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not GrantedAccess item)
            return;
        await ShowTakeoverDialogAsync(item);
    }

    private async Task ShowTakeoverDialogAsync(GrantedAccess item)
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
            Text = $"您即将重置 {item.Email} 的主密码，此操作不可逆。",
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

        // 新主密码只存在于本方法的局部变量(pwBox/confirmBox)与 TakeoverAsync 的参数中,
        // 绝不写入 ViewModel 的可观察属性,因此无论成功/失败/异常都不会有明文残留 VM 状态。
        try
        {
            while (true)
            {
                var dialogResult = await DialogHelper.ShowTrackedAsync(
                    dialog,
                    () =>
                    {
                        pwBox.Password = string.Empty;
                        confirmBox.Password = string.Empty;
                    });
                if (dialogResult != ContentDialogResult.Primary)
                    return;

                if (pwBox.Password != confirmBox.Password)
                {
                    errorText.Text = "两次输入的密码不一致";
                    errorText.Visibility = Visibility.Visible;
                    continue;
                }

                var result = await ViewModel.TakeoverAsync(item, pwBox.Password);
                if (result.IsSuccess)
                    return;

                errorText.Text = result.Error ?? "接管失败";
                errorText.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            pwBox.Password = string.Empty;
            confirmBox.Password = string.Empty;
        }
    }
}
