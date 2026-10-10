using App.ViewModels;
using App.Services;
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

    private void OnRetryClick(object sender, RoutedEventArgs e) => _ = ViewModel.LoadAsync();

    public static Visibility ShowLists(bool hasContent, bool isEmpty) =>
        hasContent || isEmpty ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility ShowDefaultContactIcon(EmergencyAccessStatus status) =>
        status == EmergencyAccessStatus.RecoveryInitiated ? Visibility.Collapsed : Visibility.Visible;

    public static string FormatContactName(string? name, string? email) =>
        string.IsNullOrWhiteSpace(name) ? email ?? "紧急联系人" : name;

    public static string FormatContactAccess(EmergencyAccessType type) =>
        type == EmergencyAccessType.Takeover ? "· 可以接管" : "· 可以查看";

    public static string FormatGrantedAccess(EmergencyAccessType type) =>
        type == EmergencyAccessType.Takeover ? "· 你可以接管" : "· 你可以查看";

    public static string FormatContactDetail(string? email, EmergencyAccessStatus status, int waitDays)
    {
        var detail = status switch
        {
            EmergencyAccessStatus.Invited => "已发送邀请，等对方接受",
            EmergencyAccessStatus.Accepted => "对方已接受，确认后才生效",
            EmergencyAccessStatus.Confirmed => $"等待期 {waitDays} 天",
            EmergencyAccessStatus.RecoveryInitiated => $"发起了访问请求 · 等待期 {waitDays} 天",
            EmergencyAccessStatus.RecoveryApproved => "已获得访问权限",
            _ => "未知状态",
        };
        return string.IsNullOrWhiteSpace(email) ? detail : $"{email} · {detail}";
    }

    public static string FormatGrantedDetail(string? email, EmergencyAccessStatus status, int waitDays)
    {
        var detail = status switch
        {
            EmergencyAccessStatus.Invited => "请通过邀请邮件接受",
            EmergencyAccessStatus.Accepted => "已接受邀请，等对方确认",
            EmergencyAccessStatus.RecoveryInitiated => $"已发起请求 · 等待期 {waitDays} 天",
            EmergencyAccessStatus.RecoveryApproved => "已获得访问权限",
            _ => $"等待期 {waitDays} 天",
        };
        return string.IsNullOrWhiteSpace(email) ? detail : $"{email} · {detail}";
    }

    public static string FormatStatus(EmergencyAccessStatus status) => status switch
    {
        EmergencyAccessStatus.Invited => "等待接受邀请",
        EmergencyAccessStatus.Accepted => "等待确认联系人",
        EmergencyAccessStatus.Confirmed => "已确认",
        EmergencyAccessStatus.RecoveryInitiated => "等待访问授权",
        EmergencyAccessStatus.RecoveryApproved => "已获准访问",
        _ => "未知状态",
    };

    public static string FormatAccessType(EmergencyAccessType type) =>
        type == EmergencyAccessType.Takeover ? "接管账户" : "查看密码库";

    public static string FormatWaitTime(int days) => $"等待期：{days} 天";

    public static string FormatStatusHelp(EmergencyAccessStatus status, EmergencyAccessType type) => status switch
    {
        EmergencyAccessStatus.Invited => "邀请需通过邮件接受，然后由账户所有者确认。",
        EmergencyAccessStatus.Accepted => "账户所有者确认后，紧急访问授权才生效。",
        EmergencyAccessStatus.RecoveryInitiated => "等待期结束或账户所有者提前批准后，才可访问。",
        EmergencyAccessStatus.RecoveryApproved when type == EmergencyAccessType.Takeover => "可重置主密码并接管账户。请在继续前核对后果。",
        EmergencyAccessStatus.RecoveryApproved => "已授权读取个人密码库。",
        _ => "发起紧急访问请求后，按等待期和授权范围处理。",
    };

    private static string AccessConsequences(EmergencyAccessType type) => type == EmergencyAccessType.Takeover
        ? "接管允许重置主密码并控制账户，同时停用该账户的两步登录，并移除该账户在非所有者组织中的成员资格。"
        : "查看允许在访问获准后读取个人密码库，不会更改主密码。";

    private static bool IsValidWaitTime(double value) =>
        double.IsFinite(value) && value >= 1 && value <= 90 && value == Math.Truncate(value);

    private async Task<bool> ConfirmEmergencyAsync(
        string title,
        string message,
        string primaryText,
        bool dangerous = false,
        CancellationToken ct = default)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primaryText,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (dangerous)
            dialog.PrimaryButtonStyle = (Style)Resources["EmergencyDangerButtonStyle"];
        return await DialogHelper.ShowTrackedAsync(dialog, ct: ct) == ContentDialogResult.Primary;
    }


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


    private async void OnInviteClick(object sender, RoutedEventArgs e)
    {
        var emailBox = new TextBox
        {
            Header = "联系人邮箱",
            PlaceholderText = "联系人邮箱",
            MinWidth = 320,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(emailBox, "InviteEmailBox");

        var typeCombo = new ComboBox { MinWidth = 200, Header = "访问权限" };
        typeCombo.Items.Add(new ComboBoxItem { Content = "查看", Tag = EmergencyAccessType.View });
        typeCombo.Items.Add(new ComboBoxItem { Content = "接管", Tag = EmergencyAccessType.Takeover });
        typeCombo.SelectedIndex = 0;
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

        var panel = new StackPanel { Spacing = 16 };
        panel.Children.Add(emailBox);
        panel.Children.Add(typeCombo);
        var accessHelp = new TextBlock { Text = AccessConsequences(EmergencyAccessType.View), TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(accessHelp);
        panel.Children.Add(waitDaysBox);
        panel.Children.Add(new TextBlock
        {
            Text = "邀请被接受后，您还需要确认联系人，授权才会生效。等待期从联系人发起紧急访问时开始。",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            Title = "添加紧急联系人",
            Content = new ScrollViewer { Content = panel, MaxHeight = 460 },
            PrimaryButtonText = "发送邀请",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            IsPrimaryButtonEnabled = false,
        };

        void UpdateInviteState()
        {
            errorText.Visibility = Visibility.Collapsed;
            var email = emailBox.Text.Trim();
            dialog.IsPrimaryButtonEnabled = System.Net.Mail.MailAddress.TryCreate(email, out var address)
                && string.Equals(address.Address, email, StringComparison.OrdinalIgnoreCase)
                && IsValidWaitTime(waitDaysBox.Value);
        }
        emailBox.TextChanged += (_, _) => UpdateInviteState();
        waitDaysBox.ValueChanged += (_, _) => UpdateInviteState();
        typeCombo.SelectionChanged += (_, _) =>
        {
            accessHelp.Text = AccessConsequences(
                typeCombo.SelectedItem is ComboBoxItem { Tag: EmergencyAccessType type } ? type : EmergencyAccessType.View);
            UpdateInviteState();
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


    private async void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not EmergencyContact item)
            return;
        if (!await DialogHelper.ConfirmAsync(XamlRoot, $"确认{FormatContactName(item.Name, item.Email)}为紧急联系人？",
            $"{item.Email}\n{AccessConsequences(item.Type)}\n{FormatWaitTime(item.WaitTimeDays)}", "确认"))
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
        if (!await ConfirmEmergencyAsync($"现在就允许{FormatContactName(item.Name, item.Email)}访问你的密码库？",
            $"{item.Email}\n不用等到等待期结束，对方马上就能获得访问权限。\n{AccessConsequences(item.Type)}", "批准",
            dangerous: item.Type == EmergencyAccessType.Takeover))
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
            (contact, ct) => ConfirmEmergencyAsync(
                "移除紧急联系人",
                $"移除 {contact.Email} 后，对方将失去紧急访问权限。如需恢复，必须重新邀请并确认。",
                "移除",
                dangerous: true,
                ct: ct),
            CancellationToken.None);
    }

    private async void OnUpdateClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not EmergencyContact item)
            return;

        var typeCombo = new ComboBox { MinWidth = 200, Header = "访问权限" };
        typeCombo.Items.Add(new ComboBoxItem { Content = "查看", Tag = EmergencyAccessType.View });
        typeCombo.Items.Add(new ComboBoxItem { Content = "接管", Tag = EmergencyAccessType.Takeover });
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

        var panel = new StackPanel { Spacing = 16 };
        panel.Children.Add(typeCombo);
        var accessHelp = new TextBlock { Text = AccessConsequences(item.Type), TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(accessHelp);
        panel.Children.Add(waitDaysBox);
        panel.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            Title = $"修改紧急访问 — {item.Email}",
            Content = new ScrollViewer { Content = panel, MaxHeight = 460 },
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        waitDaysBox.ValueChanged += (_, _) => dialog.IsPrimaryButtonEnabled = IsValidWaitTime(waitDaysBox.Value);
        typeCombo.SelectionChanged += (_, _) => accessHelp.Text = AccessConsequences(
            typeCombo.SelectedItem is ComboBoxItem { Tag: EmergencyAccessType type } ? type : item.Type);

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


    private async void OnInitiateClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not GrantedAccess item)
            return;
        if (!await DialogHelper.ConfirmAsync(XamlRoot, "发起紧急访问",
            $"将通知 {item.Email}。等待期为 {item.WaitTimeDays} 天，期间对方可以批准或拒绝请求。\n{AccessConsequences(item.Type)}", "发起请求"))
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


    private async void OnTakeoverClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not GrantedAccess item)
            return;
        await ShowTakeoverDialogAsync(item);
    }

    private async Task ShowTakeoverDialogAsync(GrantedAccess item)
    {
        var pwBox = new PasswordBox { Header = "新主密码（为对方设置）", MinWidth = 320 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(pwBox, "TakeoverNewPasswordBox");

        var confirmBox = new PasswordBox { Header = "确认新主密码" };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(confirmBox, "TakeoverConfirmPasswordBox");

        var errorText = new TextBlock { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        errorText.SetValue(ForegroundProperty, Application.Current.Resources["SystemFillColorCriticalBrush"]);

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = $"您即将接管 {item.Email}。\n{AccessConsequences(EmergencyAccessType.Takeover)}\n原主密码将失效，原账户所有者需要新主密码才能重新登录。",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(pwBox);
        panel.Children.Add(confirmBox);
        var acknowledgedBox = new CheckBox
        {
            Content = new TextBlock { Text = "我已了解主密码、两步登录和组织成员资格将发生的变化。", TextWrapping = TextWrapping.Wrap },
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(acknowledgedBox, "TakeoverConsequencesCheckBox");
        panel.Children.Add(acknowledgedBox);
        panel.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            Title = "接管账户 — 重置主密码",
            Content = new ScrollViewer { Content = panel, MaxHeight = 460 },
            PrimaryButtonText = "确认接管",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
            IsPrimaryButtonEnabled = false,
            PrimaryButtonStyle = (Style)Resources["EmergencyDangerButtonStyle"],
        };
        void UpdateTakeoverState() => dialog.IsPrimaryButtonEnabled = acknowledgedBox.IsChecked == true
            && !string.IsNullOrWhiteSpace(pwBox.Password) && pwBox.Password == confirmBox.Password;
        acknowledgedBox.Checked += (_, _) => UpdateTakeoverState();
        acknowledgedBox.Unchecked += (_, _) => UpdateTakeoverState();
        pwBox.PasswordChanged += (_, _) => UpdateTakeoverState();
        confirmBox.PasswordChanged += (_, _) => UpdateTakeoverState();

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

                if (acknowledgedBox.IsChecked != true || string.IsNullOrWhiteSpace(pwBox.Password)
                    || pwBox.Password != confirmBox.Password)
                {
                    errorText.Text = "请确认后果，并输入两次相同的新主密码。";
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
