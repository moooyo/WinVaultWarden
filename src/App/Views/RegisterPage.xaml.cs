using App.ViewModels;
using Core.Models;
using Core.Services;
using Crypto.PasswordStrength;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.ComponentModel;

namespace App.Views;

public sealed partial class RegisterPage : Page
{
    private readonly PasswordStrengthEvaluator _strengthEvaluator;
    private bool _isInitialized;

    public RegisterViewModel ViewModel { get; }

    public RegisterPage()
    {
        ViewModel = global::App.App.Services.GetRequiredService<RegisterViewModel>();
        _strengthEvaluator = global::App.App.Services.GetRequiredService<PasswordStrengthEvaluator>();
        ViewModel.ConfigurePasswordBreachCheck(
            global::App.App.Services.GetRequiredService<Vault.IPwnedPasswordsClient>().GetBreachCountAsync);
        if (global::App.App.Services.GetRequiredService<global::App.Services.AccountPrefill>().Take() is { } account)
        {
            ViewModel.SelectedServerOptionIndex = account.ServerUrl.TrimEnd('/') switch
            {
                "https://vault.bitwarden.com" => 0,
                "https://vault.bitwarden.eu" => 1,
                _ => 2,
            };
            ViewModel.ServerUrl = account.ServerUrl;
            ViewModel.Email = account.Email;
        }
        InitializeComponent();
        _isInitialized = true;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += OnActualThemeChanged;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    // Compiled binding functions must be instance methods.
    private bool Not(bool value) => !value;

    private string RegisterButtonText(bool isBusy) => isBusy ? "正在创建…" : "创建账户";

    private double RegisterButtonOpacity(bool isBusy) => isBusy ? .45 : 1;

    private string RegistrationServerTextValue(string serverUrl) =>
        $"账户会创建在 {(Uri.TryCreate(serverUrl, UriKind.Absolute, out var server) ? server.Authority : serverUrl)}";

    private void OnRegisterViewportSizeChanged(object sender, SizeChangedEventArgs e) =>
        RegisterContentRoot.MinHeight = e.NewSize.Height;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateStrength();
        if (ViewModel.IsEditing)
        {
            DispatcherQueue.TryEnqueue(
                () => _ = EmailTextBox.Focus(FocusState.Programmatic));
        }
        else
        {
            DispatcherQueue.TryEnqueue(FocusRegistrationHandoffAction);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isInitialized = false;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        ActualThemeChanged -= OnActualThemeChanged;
        ViewModel.ClearSensitive();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RegisterViewModel.FocusRequestId))
            DispatcherQueue.TryEnqueue(ApplyFocusRequest);
        else if (e.PropertyName == nameof(RegisterViewModel.State))
            DispatcherQueue.TryEnqueue(() =>
            {
                ResetPasswordPresentation();
                if (!ViewModel.IsEditing)
                    FocusRegistrationHandoffAction();
            });
    }

    // Preserve the view model's first-invalid-field focus contract.
    private void ApplyFocusRequest()
    {
        if (ViewModel.FocusTarget == RegisterFocusTarget.ServerUrl)
            RegistrationServerPanel.Visibility = Visibility.Visible;

        Control target = ViewModel.FocusTarget switch
        {
            RegisterFocusTarget.ServerUrl => ServerUrlTextBox,
            RegisterFocusTarget.Email => EmailTextBox,
            RegisterFocusTarget.Name => NameTextBox,
            RegisterFocusTarget.MasterPassword => MasterPasswordBox,
            RegisterFocusTarget.ConfirmPassword => ConfirmPasswordBox,
            _ => EmailTextBox,
        };
        DispatcherQueue.TryEnqueue(() => _ = target.Focus(FocusState.Programmatic));
    }

    // Keep Enter and the submit button under the same busy-state guard.
    private async void OnSubmitAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!ViewModel.CanEditForm)
            return;
        args.Handled = true;
        await SubmitAsync();
    }

    private void FocusRegistrationHandoffAction()
    {
        if (ViewModel.IsAwaitingEmailVerification)
            _ = RegisterContinueToLoginButton.Focus(FocusState.Programmatic);
        else if (ViewModel.IsOutcomeUncertain)
            _ = RegisterUncertainLoginButton.Focus(FocusState.Programmatic);
    }

    private async void OnRegisterClick(object sender, RoutedEventArgs e) => await SubmitAsync();

    private async Task SubmitAsync()
    {
        if (!ViewModel.CanEditForm || !ValidatePasswordFields())
            return;

        await ViewModel.RegisterAsync();

        if (ViewModel.Registered)
            NavigateToLoginWithPrefill();
    }

    private void OnContinueToLoginClick(object sender, RoutedEventArgs e) =>
        NavigateToLoginWithPrefill();

    private void OnResumeEditingClick(object sender, RoutedEventArgs e) =>
        ViewModel.ResumeEditing();

    private void NavigateToLoginWithPrefill()
    {
        if (string.IsNullOrWhiteSpace(ViewModel.RegisteredServerUrl)
            || string.IsNullOrWhiteSpace(ViewModel.RegisteredEmail))
        {
            return;
        }

        global::App.App.Services.GetRequiredService<global::App.Services.AccountPrefill>()
            .Set(ViewModel.RegisteredServerUrl, ViewModel.RegisteredEmail);
        global::App.App.Services.GetRequiredService<global::App.Services.INavigationService>()
            .GoToLoginPage();
    }

    private void OnBackToLoginClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanEditForm)
            return;
        global::App.App.Services.GetRequiredService<global::App.Services.AccountPrefill>()
            .Set(ViewModel.ServerUrl, ViewModel.Email);
        global::App.App.Services.GetRequiredService<global::App.Services.INavigationService>().GoToLoginPage();
    }

    private void ResetPasswordPresentation()
    {
        if (!_isInitialized)
            return;
        RevealMasterPasswordButton.IsChecked = false;
        RevealConfirmPasswordButton.IsChecked = false;
        ClearFieldError(MasterPasswordErrorText, MasterPasswordErrorUnderline);
        ClearFieldError(ConfirmPasswordErrorText, ConfirmPasswordErrorUnderline);
        ClearHintError();
    }

    private void OnChangeServerClick(object sender, RoutedEventArgs e)
    {
        RegistrationServerPanel.Visibility = RegistrationServerPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args) => UpdateStrength();

    private void OnMasterPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized)
            return;
        ClearFieldError(MasterPasswordErrorText, MasterPasswordErrorUnderline);
        ClearFieldError(ConfirmPasswordErrorText, ConfirmPasswordErrorUnderline);
        ClearHintError();
        UpdateStrength();
    }

    private void OnConfirmPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitialized)
            ClearFieldError(ConfirmPasswordErrorText, ConfirmPasswordErrorUnderline);
    }

    private void OnMasterPasswordHintChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitialized)
            ClearHintError();
    }

    private void ClearHintError()
    {
        ClearFieldError(PasswordHintErrorText, PasswordHintErrorUnderline);
        PasswordHintDescription.Visibility = Visibility.Visible;
    }

    private static void ClearFieldError(TextBlock message, Border underline)
    {
        message.Text = string.Empty;
        message.Visibility = Visibility.Collapsed;
        underline.Visibility = Visibility.Collapsed;
    }

    private static bool ShowFieldError(Control field, TextBlock message, Border underline, string error)
    {
        message.Text = error;
        message.Visibility = Visibility.Visible;
        underline.Visibility = Visibility.Visible;
        _ = field.Focus(FocusState.Programmatic);
        return false;
    }

    private bool ValidatePasswordFields()
    {
        // Let existing account validation keep priority and its focus request behavior.
        try
        {
            if (!ServerEndpoint.TryParse(ViewModel.ServerUrl, out _))
                return true;
        }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException)
        {
            return true;
        }
        if (string.IsNullOrWhiteSpace(ViewModel.Email)
            || ViewModel.Email.Trim().Length > MasterPasswordSaltPolicy.MaxLength
            || !ViewModel.Email.Contains('@'))
            return true;

        if (MasterPasswordBox.Password.Length < 12)
            return ShowFieldError(MasterPasswordBox, MasterPasswordErrorText, MasterPasswordErrorUnderline,
                "主密码至少需要 12 个字符");

        if (MasterPasswordBox.Password != ConfirmPasswordBox.Password)
            return ShowFieldError(ConfirmPasswordBox, ConfirmPasswordErrorText, ConfirmPasswordErrorUnderline,
                "两次输入的主密码不一致");

        if (MasterPasswordHintTextBox.Text.Contains(MasterPasswordBox.Password, StringComparison.OrdinalIgnoreCase))
        {
            PasswordHintDescription.Visibility = Visibility.Collapsed;
            return ShowFieldError(MasterPasswordHintTextBox, PasswordHintErrorText, PasswordHintErrorUnderline,
                "提示不能包含主密码");
        }

        return true;
    }

    private void OnRevealMasterPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized)
            return;
        var reveal = RevealMasterPasswordButton.IsChecked == true;
        MasterPasswordBox.PasswordRevealMode = reveal ? PasswordRevealMode.Visible : PasswordRevealMode.Hidden;
        var label = reveal ? "隐藏主密码" : "显示主密码";
        AutomationProperties.SetName(RevealMasterPasswordButton, label);
        ToolTipService.SetToolTip(RevealMasterPasswordButton, label);
        RevealMasterPasswordIcon.Glyph = reveal ? "\uED1A" : "\uE890";
    }

    private void OnRevealConfirmPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized)
            return;
        var reveal = RevealConfirmPasswordButton.IsChecked == true;
        ConfirmPasswordBox.PasswordRevealMode = reveal ? PasswordRevealMode.Visible : PasswordRevealMode.Hidden;
        var label = reveal ? "隐藏确认主密码" : "显示确认主密码";
        AutomationProperties.SetName(RevealConfirmPasswordButton, label);
        ToolTipService.SetToolTip(RevealConfirmPasswordButton, label);
        RevealConfirmPasswordIcon.Glyph = reveal ? "\uED1A" : "\uE890";
    }

    private void UpdateStrength()
    {
        if (!_isInitialized)
            return;
        var hasPassword = MasterPasswordBox.Password.Length > 0;
        PasswordStrengthRow.Visibility = hasPassword ? Visibility.Visible : Visibility.Collapsed;
        if (!hasPassword)
            return;

        var score = _strengthEvaluator.Evaluate(MasterPasswordBox.Password).Score;
        var segmentsFilled = Math.Clamp(score, 1, 4);
        var brushKey = score <= 1 ? "SystemFillColorCriticalBrush"
            : score == 2 ? "SystemFillColorCautionBrush" : "SystemFillColorSuccessBrush";
        var filledBrush = (Brush)Application.Current.Resources[brushKey];
        var emptyBrush = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"];
        Border[] segments = [StrengthSegment1, StrengthSegment2, StrengthSegment3, StrengthSegment4];
        for (var index = 0; index < segments.Length; index++)
            segments[index].Background = index < segmentsFilled ? filledBrush : emptyBrush;

        StrengthLabel.Text = score switch { <= 1 => "弱", 2 => "中等", 3 => "强", _ => "非常强" };
        StrengthLabel.Foreground = filledBrush;
        AutomationProperties.SetName(StrengthLabel, $"密码强度：{StrengthLabel.Text}");
    }
}
