using Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace App.Services;

public sealed class PasskeyApprovalDialogService : IPasskeyApprovalService
{
    private readonly SemaphoreSlim _dialogGate = new(1, 1);
    private readonly PasskeyApprovalThrottle _throttle = new(TimeSpan.FromSeconds(2));

    public async Task<PasskeyUseApprovalResult> ConfirmUseAsync(
        PasskeyApprovalRequest request,
        CancellationToken ct = default)
    {
        await _dialogGate.WaitAsync(ct);

        try
        {
            // 按 rpId(而非完整 origin)键控冷却:轮换同一 rpId 的子域 origin 无法绕过节流。
            if (!_throttle.TryBegin(request.RpId))
                return RejectedUse();

            var window = global::App.App.MainWindow;
            if (window is null)
                return RejectedUse();

            if (window.DispatcherQueue.HasThreadAccess)
            {
                var approved = await ShowDialogAsync(window, request);
                return BuildUseResult(request, approved);
            }

            var result = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (!window.DispatcherQueue.TryEnqueue(async () =>
                {
                    try
                    {
                        result.SetResult(await ShowDialogAsync(window, request));
                    }
                    catch (Exception ex)
                    {
                        result.SetException(ex);
                    }
                }))
            {
                return RejectedUse();
            }

            using var registration = ct.Register(() => result.TrySetCanceled(ct));
            return BuildUseResult(request, await result.Task);
        }
        finally
        {
            _dialogGate.Release();
        }
    }

    public async Task<PasskeyCreateApprovalResult> ConfirmCreateAsync(
        PasskeyCreateApprovalRequest request,
        CancellationToken ct = default)
    {
        // 与 ConfirmUseAsync 共用同一把闸门:WinUI 同一 XamlRoot 下不能同时展示两个 ContentDialog,
        // 必须串行化,避免"使用 passkey"与"创建 passkey"弹窗并发触发时抛异常。
        await _dialogGate.WaitAsync(ct);

        try
        {
            var window = global::App.App.MainWindow;
            if (window is null)
                return RejectedCreate();

            if (window.DispatcherQueue.HasThreadAccess)
                return await ShowCreateDialogAsync(window, request);

            var result = new TaskCompletionSource<PasskeyCreateApprovalResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (!window.DispatcherQueue.TryEnqueue(async () =>
                {
                    try
                    {
                        result.SetResult(await ShowCreateDialogAsync(window, request));
                    }
                    catch (Exception ex)
                    {
                        result.SetException(ex);
                    }
                }))
            {
                return RejectedCreate();
            }

            using var registration = ct.Register(() => result.TrySetCanceled(ct));
            return await result.Task;
        }
        finally
        {
            _dialogGate.Release();
        }
    }

    private static async Task<bool> ShowDialogAsync(MainWindow window, PasskeyApprovalRequest request)
    {
        if (window.Content is not FrameworkElement root || root.XamlRoot is null)
            return false;

        window.Activate();

        var dialog = new ContentDialog
        {
            XamlRoot = root.XamlRoot,
            Title = $"使用此 passkey 登录 {request.RpId}？",
            PrimaryButtonText = "使用 passkey",
            CloseButtonText = "取消",
            // 默认按钮为“取消”:回车不再批准安全敏感操作,须显式点击“使用 passkey”。
            DefaultButton = ContentDialogButton.Close,
            Content = CreateDialogContent(request),
        };

        var result = await global::App.Views.DialogHelper.ShowTrackedAsync(dialog);
        return result == ContentDialogResult.Primary;
    }

    private static async Task<PasskeyCreateApprovalResult> ShowCreateDialogAsync(MainWindow window, PasskeyCreateApprovalRequest request)
    {
        if (window.Content is not FrameworkElement root || root.XamlRoot is null)
            return RejectedCreate();

        window.Activate();

        var displayName = string.IsNullOrWhiteSpace(request.RpName) ? request.RpId : request.RpName;

        var existingCombo = new ComboBox
        {
            ItemsSource = request.ExistingLogins,
            DisplayMemberPath = nameof(PasskeyLoginTarget.Name),
            IsEnabled = false,
            MinWidth = 240,
            Margin = new Thickness(28, 0, 0, 0),
        };
        if (request.ExistingLogins.Count > 0)
            existingCombo.SelectedIndex = 0;

        var newRadio = new RadioButton
        {
            GroupName = "PasskeyCreateTarget",
            Content = $"新建条目「{displayName}」",
            IsChecked = true,
        };
        newRadio.Checked += (_, _) => existingCombo.IsEnabled = false;

        var attachRadio = new RadioButton
        {
            GroupName = "PasskeyCreateTarget",
            Content = "挂到已有条目",
            // 库中没有任何 Login 条目时无处可挂,禁用该选项,避免选中后无法提交。
            IsEnabled = request.ExistingLogins.Count > 0,
        };
        attachRadio.Checked += (_, _) => existingCombo.IsEnabled = true;

        var panel = new StackPanel
        {
            Spacing = 12,
            MaxWidth = 420,
        };
        panel.Children.Add(new TextBlock
        {
            Text = "浏览器正在请求为此网站创建一个新的 passkey。",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(CreateDetailLine("网站", displayName));
        panel.Children.Add(CreateDetailLine("账户", DisplayAccount(request)));
        panel.Children.Add(newRadio);
        panel.Children.Add(attachRadio);
        panel.Children.Add(existingCombo);

        var dialog = new ContentDialog
        {
            XamlRoot = root.XamlRoot,
            Title = "创建通行密钥",
            PrimaryButtonText = "创建",
            CloseButtonText = "取消",
            // 与 ConfirmUseAsync 一致:默认按钮为“取消”,创建凭据同样是安全敏感操作,须显式点击“创建”。
            DefaultButton = ContentDialogButton.Close,
            Content = panel,
        };

        var result = await global::App.Views.DialogHelper.ShowTrackedAsync(dialog);
        if (result != ContentDialogResult.Primary)
            return RejectedCreate();

        if (newRadio.IsChecked == true)
        {
            return new PasskeyCreateApprovalResult(
                PresenceEvidence(),
                CreateNew: true,
                CipherId: null);
        }

        if (existingCombo.SelectedItem is PasskeyLoginTarget target)
        {
            return new PasskeyCreateApprovalResult(
                PresenceEvidence(),
                CreateNew: false,
                CipherId: target.CipherId);
        }

        return RejectedCreate();
    }

    private static UIElement CreateDialogContent(PasskeyApprovalRequest request)
    {
        var panel = new StackPanel
        {
            Spacing = 12,
            MaxWidth = 420,
        };

        panel.Children.Add(new TextBlock
        {
            Text = "浏览器正在请求使用保险库中的 passkey。",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(CreateDetailLine("项目", request.CipherName));
        panel.Children.Add(CreateDetailLine("账户", DisplayAccount(request)));
        var origin = PasskeyOriginDisplay.Parse(request.Origin);
        // “未验证”:origin 仅为请求方自报(经内容脚本钉真实帧 origin),客户端未对其做 RP 验证。
        panel.Children.Add(CreateDetailLine("来源（请求方，未验证）", origin.Display));

        if (!origin.IsSecure)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "⚠ 此来源不是安全的 HTTPS 连接。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Application.Current.Resources["SystemFillColorCautionBrush"] as Microsoft.UI.Xaml.Media.Brush,
            });
        }

        return panel;
    }

    private static UIElement CreateDetailLine(string label, string value)
    {
        var panel = new StackPanel
        {
            Spacing = 2,
        };

        panel.Children.Add(new TextBlock
        {
            Text = label,
            Style = Application.Current.Resources["CaptionTextBlockStyle"] as Style,
            Foreground = Application.Current.Resources["TextFillColorSecondaryBrush"] as Microsoft.UI.Xaml.Media.Brush,
        });
        panel.Children.Add(new TextBlock
        {
            Text = value,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        return panel;
    }

    private static string DisplayAccount(PasskeyApprovalRequest request) =>
        FirstNonEmpty(request.UserDisplayName, request.UserName) ?? "未命名账户";

    private static string DisplayAccount(PasskeyCreateApprovalRequest request) =>
        FirstNonEmpty(request.UserDisplayName, request.UserName) ?? "未命名账户";

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static PasskeyApprovalEvidence PresenceEvidence() =>
        new(Approved: true, UserPresent: true, UserVerified: false);

    private static PasskeyApprovalEvidence RejectedEvidence() =>
        new(Approved: false, UserPresent: false, UserVerified: false);

    private static PasskeyUseApprovalResult BuildUseResult(
        PasskeyApprovalRequest request,
        bool approved) =>
        approved
            ? new PasskeyUseApprovalResult(
                PresenceEvidence(),
                request.CipherId,
                request.CredentialId)
            : RejectedUse();

    private static PasskeyUseApprovalResult RejectedUse() =>
        new(RejectedEvidence(), null, null);

    private static PasskeyCreateApprovalResult RejectedCreate() =>
        new(RejectedEvidence(), CreateNew: false, CipherId: null);
}
