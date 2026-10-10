using App.Services;
using App.ViewModels;
using Crypto.PasswordStrength;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.ComponentModel;
using Windows.System;

namespace App.Views;

public sealed partial class SettingsPasswordPage : Page
{
    private readonly PasswordStrengthEvaluator _strengthEvaluator;
    private SettingsViewModel? _viewModel;
    private Action? _onCancel;
    private IDisposable? _transientRegistration;
    private CancellationTokenSource? _operationCts;
    private bool _isActive;

    public SettingsPasswordPage()
    {
        _strengthEvaluator = global::App.App.Services.GetRequiredService<PasswordStrengthEvaluator>();
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += OnActualThemeChanged;
    }

    public void Initialize(SettingsViewModel viewModel, Action onCancel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(onCancel);
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            CancelOperation();
            ClearSensitive();
        }

        _viewModel = viewModel;
        _onCancel = onCancel;
        if (_isActive)
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdateEditingState();
        FocusCurrentPassword();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isActive = true;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        _transientRegistration?.Dispose();
        _transientRegistration = global::App.App.Services
            .GetRequiredService<ITransientOperationRegistry>()
            .Register(CancelOperation, CloseForLifecycle, ClearSensitive);
        UpdateEditingState();
        FocusCurrentPassword();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isActive = false;
        CancelOperation();
        _transientRegistration?.Dispose();
        _transientRegistration = null;
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ClearSensitive();
    }

    private void CloseForLifecycle()
    {
        _isActive = false;
        UpdateEditingState();
    }

    private void CancelOperation()
    {
        try { Volatile.Read(ref _operationCts)?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private void ClearSensitive()
    {
        CurrentPasswordBox.Password = string.Empty;
        NewPasswordBox.Password = string.Empty;
        ConfirmPasswordBox.Password = string.Empty;
        PasswordHintBox.Text = string.Empty;
        RevealPasswordsButton.IsChecked = false;
        SetRevealMode(false);
        ClearFieldErrors();
        OperationErrorInfoBar.IsOpen = false;
        OperationErrorInfoBar.Visibility = Visibility.Collapsed;
        OperationErrorInfoBar.Message = string.Empty;
        UpdateStrength();
    }

    private void FocusCurrentPassword()
    {
        if (!_isActive || _viewModel is null)
            return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isActive && PasswordFields.IsEnabled)
                _ = CurrentPasswordBox.Focus(FocusState.Programmatic);
        });
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.IsBusy))
            UpdateEditingState();
    }

    private void UpdateEditingState()
    {
        var canEdit = _isActive && _viewModel is not null
            && !_viewModel.IsBusy && _operationCts is null;
        PasswordFields.IsEnabled = canEdit;
        ChangePasswordButton.IsEnabled = canEdit;
        ChangePasswordButton.Content = _operationCts is null ? "更改主密码" : "正在更改…";
        CancelButton.IsEnabled = _isActive;
    }

    private void OnCurrentPasswordChanged(object sender, RoutedEventArgs e) =>
        SetFieldError(CurrentPasswordBox, CurrentPasswordError, CurrentErrorUnderline, string.Empty);

    private void OnNewPasswordChanged(object sender, RoutedEventArgs e)
    {
        SetFieldError(NewPasswordBox, NewPasswordError, NewErrorUnderline, string.Empty, "至少 12 个字符");
        UpdateStrength();
    }

    private void OnConfirmPasswordChanged(object sender, RoutedEventArgs e) =>
        SetFieldError(ConfirmPasswordBox, ConfirmPasswordError, ConfirmErrorUnderline, string.Empty);

    private void OnPasswordHintChanged(object sender, TextChangedEventArgs e) =>
        SetFieldError(PasswordHintBox, PasswordHintError, HintErrorUnderline, string.Empty,
            "忘记主密码时可以发到你的邮箱，不能包含主密码本身");

    private void OnRevealPasswordsChanged(object sender, RoutedEventArgs e) =>
        SetRevealMode(RevealPasswordsButton.IsChecked == true);

    private void SetRevealMode(bool reveal)
    {
        var mode = reveal ? PasswordRevealMode.Visible : PasswordRevealMode.Hidden;
        NewPasswordBox.PasswordRevealMode = mode;
        ConfirmPasswordBox.PasswordRevealMode = mode;
        var label = reveal ? "隐藏新主密码和确认主密码" : "显示新主密码和确认主密码";
        AutomationProperties.SetName(RevealPasswordsButton, label);
        ToolTipService.SetToolTip(RevealPasswordsButton, label);
        RevealPasswordsIcon.Glyph = reveal ? "\uED1A" : "\uE890";
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args) => UpdateStrength();

    private void UpdateStrength()
    {
        var hasPassword = NewPasswordBox.Password.Length > 0;
        var score = hasPassword ? _strengthEvaluator.Evaluate(NewPasswordBox.Password).Score : 0;
        var filledSegments = hasPassword ? Math.Clamp(score, 1, 4) : 0;
        var resourceKey = score <= 1 ? "SystemFillColorCriticalBrush"
            : score == 2 ? "SystemFillColorCautionBrush" : "SystemFillColorSuccessBrush";
        var filledBrush = (Brush)Application.Current.Resources[resourceKey];
        var emptyBrush = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"];
        Border[] segments = [StrengthSegment1, StrengthSegment2, StrengthSegment3, StrengthSegment4];
        for (var index = 0; index < segments.Length; index++)
            segments[index].Background = index < filledSegments ? filledBrush : emptyBrush;

        StrengthLabel.Text = !hasPassword ? string.Empty : score switch
        {
            <= 1 => "弱",
            2 => "中等",
            3 => "强",
            _ => "非常强",
        };
        StrengthLabel.Foreground = filledBrush;
        AutomationProperties.SetName(StrengthLabel,
            hasPassword ? $"密码强度：{StrengthLabel.Text}" : "尚未输入新主密码");
    }

    private bool ValidateForm()
    {
        ClearFieldErrors();
        Control? firstInvalid = null;
        if (string.IsNullOrEmpty(CurrentPasswordBox.Password))
        {
            SetFieldError(CurrentPasswordBox, CurrentPasswordError, CurrentErrorUnderline, "请输入当前主密码");
            firstInvalid = CurrentPasswordBox;
        }

        var nextError = NewPasswordBox.Password.Length < 12
            ? "新主密码至少需要 12 个字符"
            : string.Equals(NewPasswordBox.Password, CurrentPasswordBox.Password, StringComparison.Ordinal)
                ? "新主密码不能和当前的一样" : string.Empty;
        if (nextError.Length > 0)
        {
            SetFieldError(NewPasswordBox, NewPasswordError, NewErrorUnderline, nextError);
            firstInvalid ??= NewPasswordBox;
        }

        if (!string.Equals(NewPasswordBox.Password, ConfirmPasswordBox.Password, StringComparison.Ordinal))
        {
            SetFieldError(ConfirmPasswordBox, ConfirmPasswordError, ConfirmErrorUnderline, "两次输入的主密码不一致");
            firstInvalid ??= ConfirmPasswordBox;
        }

        if (NewPasswordBox.Password.Length > 0
            && PasswordHintBox.Text.Contains(NewPasswordBox.Password, StringComparison.OrdinalIgnoreCase))
        {
            SetFieldError(PasswordHintBox, PasswordHintError, HintErrorUnderline, "提示不能包含主密码");
            firstInvalid ??= PasswordHintBox;
        }

        if (firstInvalid is null)
            return true;
        _ = firstInvalid.Focus(FocusState.Programmatic);
        return false;
    }

    private void ClearFieldErrors()
    {
        SetFieldError(CurrentPasswordBox, CurrentPasswordError, CurrentErrorUnderline, string.Empty);
        SetFieldError(NewPasswordBox, NewPasswordError, NewErrorUnderline, string.Empty, "至少 12 个字符");
        SetFieldError(ConfirmPasswordBox, ConfirmPasswordError, ConfirmErrorUnderline, string.Empty);
        SetFieldError(PasswordHintBox, PasswordHintError, HintErrorUnderline, string.Empty,
            "忘记主密码时可以发到你的邮箱，不能包含主密码本身");
    }

    private static void SetFieldError(Control field, TextBlock message, Border underline, string error, string help = "")
    {
        message.Text = error;
        message.Visibility = underline.Visibility = error.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetHelpText(field, error.Length > 0 ? error : help);
    }

    private async void OnChangePasswordClick(object sender, RoutedEventArgs e) => await SubmitAsync();

    private async void OnPasswordHintKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
            return;
        e.Handled = true;
        await SubmitAsync();
    }

    private async Task SubmitAsync()
    {
        if (!_isActive || _viewModel is null || _viewModel.IsBusy || _operationCts is not null)
            return;
        OperationErrorInfoBar.IsOpen = false;
        OperationErrorInfoBar.Visibility = Visibility.Collapsed;
        if (!ValidateForm())
            return;

        using var operationCts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _operationCts, operationCts, null) is not null)
            return;

        UpdateEditingState();
        try
        {
            var operation = await _viewModel.ChangePasswordAsync(
                CurrentPasswordBox.Password,
                NewPasswordBox.Password,
                ConfirmPasswordBox.Password,
                string.IsNullOrWhiteSpace(PasswordHintBox.Text) ? null : PasswordHintBox.Text.Trim(),
                operationCts.Token);

            if (operation.IsSuccess)
            {
                // The view model requests CredentialChanged and owns the logout transition.
                ClearSensitive();
                return;
            }

            if (!_isActive || operationCts.IsCancellationRequested)
                return;
            if (operation.Status == UiOperationStatus.Failed)
            {
                OperationErrorInfoBar.Message = operation.Error ?? "操作失败，请稍后重试";
                OperationErrorInfoBar.Visibility = Visibility.Visible;
                OperationErrorInfoBar.IsOpen = true;
            }
        }
        finally
        {
            _ = Interlocked.CompareExchange(ref _operationCts, null, operationCts);
            UpdateEditingState();
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => CancelAndReturn();

    private void OnCancelAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!_isActive)
            return;
        args.Handled = true;
        CancelAndReturn();
    }

    private void CancelAndReturn()
    {
        if (!_isActive)
            return;
        CancelOperation();
        ClearSensitive();
        _onCancel?.Invoke();
    }
}
