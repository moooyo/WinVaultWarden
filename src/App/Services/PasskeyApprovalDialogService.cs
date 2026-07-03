using Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace App.Services;

public sealed class PasskeyApprovalDialogService : IPasskeyApprovalService
{
    private readonly SemaphoreSlim _dialogGate = new(1, 1);
    private readonly PasskeyApprovalThrottle _throttle = new(TimeSpan.FromSeconds(2));

    public async Task<bool> ConfirmUseAsync(PasskeyApprovalRequest request, CancellationToken ct = default)
    {
        await _dialogGate.WaitAsync(ct);

        try
        {
            // 按 rpId(而非完整 origin)键控冷却:轮换同一 rpId 的子域 origin 无法绕过节流。
            if (!_throttle.TryBegin(request.RpId))
                return false;

            var window = global::App.App.MainWindow;
            if (window is null)
                return false;

            if (window.DispatcherQueue.HasThreadAccess)
                return await ShowDialogAsync(window, request);

            var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
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
                return false;
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

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
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

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
