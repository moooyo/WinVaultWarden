using Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace App.Services;

public sealed class PasskeyApprovalDialogService : IPasskeyApprovalService
{
    private readonly SemaphoreSlim _dialogGate = new(1, 1);
    private readonly PasskeyApprovalThrottle _throttle = new(TimeSpan.FromSeconds(2));

    private readonly IItemRepromptGate _itemReprompt;
    private readonly IVaultUiService _vault;
    private readonly IUserVerificationService _userVerification;

    public PasskeyApprovalDialogService(
        IItemRepromptGate itemReprompt,
        IVaultUiService vault,
        IUserVerificationService userVerification)
    {
        _itemReprompt = itemReprompt;
        _vault = vault;
        _userVerification = userVerification;
    }

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

            return await InvokeOnUiAsync(
                window,
                () => ConfirmUseCoreAsync(window, request, ct),
                RejectedUse(),
                ct);
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

            return await InvokeOnUiAsync(
                window,
                () => ConfirmCreateCoreAsync(window, request, ct),
                RejectedCreate(),
                ct);
        }
        finally
        {
            _dialogGate.Release();
        }
    }

    // 使用路径:先对 request.CipherId 过条目级门禁(reprompt 条目会先弹主密码,非 reprompt 立即通过),
    // 授权失败则拒绝且不产出任何 Present/Verified evidence;通过后再依 UV 要求收集 evidence。
    private async Task<PasskeyUseApprovalResult> ConfirmUseCoreAsync(
        MainWindow window,
        PasskeyApprovalRequest request,
        CancellationToken ct)
    {
        if (window.Content is not FrameworkElement root || root.XamlRoot is null)
            return RejectedUse();

        CipherAccessPolicy policy;
        try
        {
            policy = _vault.GetAccessPolicy(request.CipherId);
        }
        catch
        {
            return RejectedUse();
        }

        var itemAuthorization = await _itemReprompt.AuthorizeAsync(
            policy,
            SensitiveItemAction.UsePasskey,
            c => PromptMasterPasswordAsync(root, c),
            ct);
        if (!itemAuthorization.IsSuccess)
        {
            return new PasskeyUseApprovalResult(
                Evidence: new PasskeyApprovalEvidence(
                    Approved: false,
                    UserPresent: false,
                    UserVerified: false),
                CipherId: request.CipherId,
                CredentialId: request.CredentialId);
        }

        window.Activate();

        var uv = request.UserVerificationRequirement;
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
        // preferred:额外提供“验证并批准”次按钮,仅该分支才执行真实 UV 并置 UV=true。
        if (uv == PasskeyUserVerificationRequirement.Preferred)
            dialog.SecondaryButtonText = "验证并批准";

        var result = await global::App.Views.DialogHelper.ShowTrackedAsync(dialog, ct: ct);

        if (result == ContentDialogResult.Primary)
        {
            if (uv == PasskeyUserVerificationRequirement.Required)
            {
                // required:确认后必须执行真实 UV;未通过则不产出任何 approval。
                var verified = await _userVerification.VerifyAsync(
                    c => PromptMasterPasswordAsync(root, c), ct);
                return new PasskeyUseApprovalResult(
                    Evidence: new PasskeyApprovalEvidence(
                        Approved: verified,
                        UserPresent: verified,
                        UserVerified: verified),
                    CipherId: request.CipherId,
                    CredentialId: request.CredentialId);
            }

            // 普通确认(discouraged / preferred 主按钮):仅 UP,不置 UV。
            return new PasskeyUseApprovalResult(
                Evidence: new PasskeyApprovalEvidence(
                    Approved: true,
                    UserPresent: true,
                    UserVerified: false),
                CipherId: request.CipherId,
                CredentialId: request.CredentialId);
        }

        if (result == ContentDialogResult.Secondary
            && uv == PasskeyUserVerificationRequirement.Preferred)
        {
            // preferred 次按钮:只有本次实际验证成功才置 UV=true,否则退回 UP-only(preferred 接受仅 UP)。
            var verified = await _userVerification.VerifyAsync(
                c => PromptMasterPasswordAsync(root, c), ct);
            return new PasskeyUseApprovalResult(
                Evidence: new PasskeyApprovalEvidence(
                    Approved: true,
                    UserPresent: true,
                    UserVerified: verified),
                CipherId: request.CipherId,
                CredentialId: request.CredentialId);
        }

        return RejectedUse();
    }

    private async Task<PasskeyCreateApprovalResult> ConfirmCreateCoreAsync(
        MainWindow window,
        PasskeyCreateApprovalRequest request,
        CancellationToken ct)
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

        var result = await global::App.Views.DialogHelper.ShowTrackedAsync(dialog, ct: ct);
        if (result != ContentDialogResult.Primary)
            return RejectedCreate();

        var createNew = newRadio.IsChecked == true;
        string? cipherId = null;

        if (!createNew)
        {
            // 挂到已有条目:返回 ID 必须对应候选(取自 request.ExistingLogins),否则拒绝。
            if (existingCombo.SelectedItem is not PasskeyLoginTarget target)
                return RejectedCreate();
            cipherId = target.CipherId;

            // 先对所选既有条目执行相同的条目级门禁;新建条目无既有 cipher,跳过 gate。
            CipherAccessPolicy policy;
            try
            {
                policy = _vault.GetAccessPolicy(cipherId);
            }
            catch
            {
                return RejectedCreate();
            }

            var itemAuthorization = await _itemReprompt.AuthorizeAsync(
                policy,
                SensitiveItemAction.UsePasskey,
                c => PromptMasterPasswordAsync(root, c),
                ct);
            if (!itemAuthorization.IsSuccess)
                return RejectedCreate();
        }

        var evidence = await CollectCreateEvidenceAsync(root, request.UserVerificationRequirement, ct);
        return new PasskeyCreateApprovalResult(evidence, createNew, cipherId);
    }

    // required:确认后必须真实 UV(新建条目同样适用);preferred/discouraged:仅 UP。
    private async Task<PasskeyApprovalEvidence> CollectCreateEvidenceAsync(
        FrameworkElement root,
        PasskeyUserVerificationRequirement requirement,
        CancellationToken ct)
    {
        if (requirement == PasskeyUserVerificationRequirement.Required)
        {
            var verified = await _userVerification.VerifyAsync(
                c => PromptMasterPasswordAsync(root, c), ct);
            return new PasskeyApprovalEvidence(
                Approved: verified,
                UserPresent: verified,
                UserVerified: verified);
        }

        return new PasskeyApprovalEvidence(
            Approved: true,
            UserPresent: true,
            UserVerified: false);
    }

    // 主密码重提示对话框:仅采集主密码返回给门禁校验;经 Task 3 tracked 对话框(锁屏强制关闭并清空)。
    private static async Task<string?> PromptMasterPasswordAsync(FrameworkElement root, CancellationToken ct)
    {
        if (root.XamlRoot is null)
            return null;

        var passwordBox = new PasswordBox { PlaceholderText = "主密码", MinWidth = 320 };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = "该 passkey 条目要求主密码重提示", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(passwordBox);
        var dialog = new ContentDialog
        {
            XamlRoot = root.XamlRoot,
            Title = "验证主密码",
            Content = panel,
            PrimaryButtonText = "确认",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        try
        {
            var result = await global::App.Views.DialogHelper.ShowTrackedAsync(
                dialog,
                () => passwordBox.Password = string.Empty,
                ct);
            return result == ContentDialogResult.Primary ? passwordBox.Password : null;
        }
        finally
        {
            passwordBox.Password = string.Empty;
        }
    }

    // 在 UI 线程上执行 action;已在 UI 线程则直接 await,否则经 DispatcherQueue 派发并回传结果。
    private static async Task<T> InvokeOnUiAsync<T>(
        MainWindow window,
        Func<Task<T>> action,
        T fallback,
        CancellationToken ct)
    {
        if (window.DispatcherQueue.HasThreadAccess)
            return await action();

        var result = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!window.DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    result.SetResult(await action());
                }
                catch (Exception ex)
                {
                    result.SetException(ex);
                }
            }))
        {
            return fallback;
        }

        using var registration = ct.Register(() => result.TrySetCanceled(ct));
        try
        {
            return await result.Task;
        }
        catch (OperationCanceledException)
        {
            return fallback;
        }
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

    private static PasskeyUseApprovalResult RejectedUse() =>
        new(new PasskeyApprovalEvidence(false, false, false), null, null);

    private static PasskeyCreateApprovalResult RejectedCreate() =>
        new(new PasskeyApprovalEvidence(false, false, false), CreateNew: false, CipherId: null);
}
