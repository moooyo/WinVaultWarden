using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace App.Views;

/// <summary>Collects a fallback password inside an already tracked dialog.</summary>
internal sealed partial class DialogPasswordPrompt : IDisposable
{
    private readonly ContentDialog _dialog;
    private readonly FrameworkElement _originalContent;
    private readonly Func<bool> _isCurrent;
    private readonly StackPanel _promptPanel;
    private readonly PasswordBox _password = new() { PlaceholderText = "主密码" };
    private TaskCompletionSource<string?>? _pending;
    private bool _cleared;
    private bool _disposed;

    public DialogPasswordPrompt(ContentDialog dialog, FrameworkElement content, Func<bool> isCurrent)
    {
        _dialog = dialog;
        _originalContent = content;
        _isCurrent = isCurrent;
        _promptPanel = new StackPanel { Spacing = 12, Visibility = Visibility.Collapsed };
        _promptPanel.Children.Add(new TextBlock
        {
            Text = "请输入主密码以继续。", TextWrapping = TextWrapping.Wrap,
        });
        AutomationProperties.SetName(_password, "主密码");
        AutomationProperties.SetAutomationId(_password, "DialogVerificationPassword");
        _promptPanel.Children.Add(_password);
        Content = new Grid();
        dialog.Content = null;
        Content.Children.Add(content);
        Content.Children.Add(_promptPanel);
        dialog.Content = Content;
        dialog.PrimaryButtonClick += OnPrimaryButtonClick;
        dialog.CloseButtonClick += OnCloseButtonClick;
        dialog.Closing += OnClosing;
    }

    public Grid Content { get; }

    public async Task<string?> PromptAsync(CancellationToken ct)
    {
        if (_disposed || _cleared || _pending is not null || ct.IsCancellationRequested || !_isCurrent())
            return null;

        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = completion;
        var title = _dialog.Title;
        var primaryText = _dialog.PrimaryButtonText;
        var closeText = _dialog.CloseButtonText;
        var defaultButton = _dialog.DefaultButton;
        var visibility = _originalContent.Visibility;
        var focus = FocusManager.GetFocusedElement(_dialog.XamlRoot) as Control;
        _password.Password = string.Empty;
        _originalContent.Visibility = Visibility.Collapsed;
        _promptPanel.Visibility = Visibility.Visible;
        _dialog.Title = "确认是你本人";
        _dialog.PrimaryButtonText = "验证";
        _dialog.CloseButtonText = "取消";
        _dialog.DefaultButton = ContentDialogButton.Primary;
        _password.Focus(FocusState.Programmatic);
        try
        {
            var password = await completion.Task.WaitAsync(ct);
            return !_cleared && !ct.IsCancellationRequested && _isCurrent() ? password : null;
        }
        finally
        {
            if (ReferenceEquals(_pending, completion))
                _pending = null;
            _password.Password = string.Empty;
            if (!_disposed && !_cleared)
            {
                _promptPanel.Visibility = Visibility.Collapsed;
                _originalContent.Visibility = visibility;
                _dialog.Title = title;
                _dialog.PrimaryButtonText = primaryText;
                _dialog.CloseButtonText = closeText;
                _dialog.DefaultButton = defaultButton;
                if (_isCurrent() && focus is { IsLoaded: true, IsEnabled: true })
                    focus.Focus(FocusState.Programmatic);
            }
        }
    }

    public void ClearSensitive()
    {
        _cleared = true;
        _password.Password = string.Empty;
        var pending = _pending;
        _pending = null;
        pending?.TrySetResult(null);
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_pending is not { } pending)
            return;
        args.Cancel = true;
        pending.TrySetResult(_isCurrent() ? _password.Password : null);
        _password.Password = string.Empty;
    }

    private void OnCloseButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_pending is not { } pending)
            return;
        args.Cancel = true;
        pending.TrySetResult(null);
    }

    private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (_pending is not { } pending)
            return;
        args.Cancel = _isCurrent();
        pending.TrySetResult(null);
        _password.Password = string.Empty;
    }

    public void Dispose()
    {
        _disposed = true;
        ClearSensitive();
        _dialog.PrimaryButtonClick -= OnPrimaryButtonClick;
        _dialog.CloseButtonClick -= OnCloseButtonClick;
        _dialog.Closing -= OnClosing;
    }
}
