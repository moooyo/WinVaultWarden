using App.Controls;
using App.ViewModels;
using Core.Models;
using Core.Services;
using Crypto.PasswordStrength;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System.ComponentModel;

namespace App.Views;

public sealed partial class RegisterPage : Page
{
    private readonly PasswordStrengthEvaluator _strengthEvaluator;
    private bool _isInitialized;
    private readonly KeyEventHandler _capsLockKeyUpHandler;

    public RegisterViewModel ViewModel { get; }

    public RegisterPage()
    {
        _capsLockKeyUpHandler = OnAuthKeyUp;
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
        GotFocus += OnAuthFocusChanged;
        LostFocus += OnAuthFocusChanged;
        AddHandler(UIElement.KeyUpEvent, _capsLockKeyUpHandler, true);
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    // Compiled binding functions must be instance methods.
    private bool Not(bool value) => !value;

    private string RegisterButtonText(bool isBusy) => isBusy ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text7C0305BC0CE607B2) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text733DEE6D88971AE1);

    private double RegisterButtonOpacity(bool isBusy) => isBusy ? .45 : 1;

    private string RegistrationServerTextValue(string serverUrl) =>
        global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text182D72AA8C3C2240,(Uri.TryCreate(serverUrl, UriKind.Absolute, out var server) ? server.Authority : serverUrl));

    private void OnRegisterViewportSizeChanged(object sender, SizeChangedEventArgs e) =>
        RegisterContentRoot.MinHeight = e.NewSize.Height;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateStrength();
        UpdateCapsLockWarnings();
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
        GotFocus -= OnAuthFocusChanged;
        LostFocus -= OnAuthFocusChanged;
        RemoveHandler(UIElement.KeyUpEvent, _capsLockKeyUpHandler);
        UpdateCapsLockWarnings();
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
                UpdateCapsLockWarnings();
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

    private void OnAuthFocusChanged(object sender, RoutedEventArgs args)
    {
        if (_isInitialized)
            DispatcherQueue.TryEnqueue(UpdateCapsLockWarnings);
    }

    private void OnAuthKeyUp(object sender, KeyRoutedEventArgs args) => UpdateCapsLockWarnings();

    private void UpdateCapsLockWarnings()
    {
        StackPanel? activeWarning = null;
        if (_isInitialized && IsLoaded && XamlRoot is not null && ViewModel.IsEditing
            && (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.CapitalLock)
                & Windows.UI.Core.CoreVirtualKeyStates.Locked) != 0)
        {
            var focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
            while (focused is not null && focused is not PasswordBox)
                focused = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(focused);
            if (focused == MasterPasswordBox)
                activeWarning = MasterPasswordCapsWarning;
            else if (focused == ConfirmPasswordBox)
                activeWarning = ConfirmPasswordCapsWarning;
        }
        MasterPasswordCapsWarning.Visibility = activeWarning == MasterPasswordCapsWarning ? Visibility.Visible : Visibility.Collapsed;
        ConfirmPasswordCapsWarning.Visibility = activeWarning == ConfirmPasswordCapsWarning ? Visibility.Visible : Visibility.Collapsed;
    }

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
                global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text3A7BBFCB76154C96));

        if (MasterPasswordBox.Password != ConfirmPasswordBox.Password)
            return ShowFieldError(ConfirmPasswordBox, ConfirmPasswordErrorText, ConfirmPasswordErrorUnderline,
                global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text61153D5C7DD3DA51));

        if (MasterPasswordHintTextBox.Text.Contains(MasterPasswordBox.Password, StringComparison.OrdinalIgnoreCase))
        {
            PasswordHintDescription.Visibility = Visibility.Collapsed;
            return ShowFieldError(MasterPasswordHintTextBox, PasswordHintErrorText, PasswordHintErrorUnderline,
                global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text190B7B43774B5AFE));
        }

        return true;
    }

    private void OnRevealMasterPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized)
            return;
        var reveal = RevealMasterPasswordButton.IsChecked == true;
        MasterPasswordBox.PasswordRevealMode = reveal ? PasswordRevealMode.Visible : PasswordRevealMode.Hidden;
        var label = reveal ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextEB26C751D7379736) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextCDA877C642C146C2);
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
        var label = reveal ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text240C7CCE608F7BED) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text866B4374B231DF49);
        AutomationProperties.SetName(RevealConfirmPasswordButton, label);
        ToolTipService.SetToolTip(RevealConfirmPasswordButton, label);
        RevealConfirmPasswordIcon.Glyph = reveal ? "\uED1A" : "\uE890";
    }

    private void UpdateStrength()
    {
        if (!_isInitialized)
            return;
        var hasPassword = MasterPasswordBox.Password.Length > 0;
        var score = hasPassword ? _strengthEvaluator.Evaluate(MasterPasswordBox.Password).Score : 0;
        var segmentsFilled = hasPassword ? Math.Clamp(score, 1, 4) : 0;
        var brushKey = score <= 1 ? "SystemFillColorCriticalBrush"
            : score == 2 ? "SystemFillColorCautionBrush" : "SystemFillColorSuccessBrush";
        var filledBrush = ThemeBrushResolver.Resolve(this, brushKey);
        var emptyBrush = ThemeBrushResolver.Resolve(this, "WardenSurfaceStrokeBrush");
        Border[] segments = [StrengthSegment1, StrengthSegment2, StrengthSegment3, StrengthSegment4];
        for (var index = 0; index < segments.Length; index++)
            segments[index].Background = index < segmentsFilled ? filledBrush : emptyBrush;

        StrengthLabel.Text = hasPassword
            ? score switch { <= 1 => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextDEFA376EABAF2FFF), 2 => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextC7092C51FDCF2F65), 3 => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextF038053BEDB1B9ED), _ => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextE6471576029B77BB) }
            : string.Empty;
        StrengthLabel.Foreground = filledBrush;
        AutomationProperties.SetName(StrengthLabel, hasPassword ? global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text6246597499366985,StrengthLabel.Text) : string.Empty);
    }
}
