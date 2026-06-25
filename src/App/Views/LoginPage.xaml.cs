using App.ViewModels;
using Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.ComponentModel;

namespace App.Views;

public sealed partial class LoginPage : Page
{
    public LoginViewModel ViewModel { get; }
    private bool _isWindowFitQueued;

    public LoginPage()
    {
        ViewModel = global::App.App.Services.GetRequiredService<LoginViewModel>();
        ViewModel.SetSuccessCallback(() =>
        {
            if (App.MainWindow is { } window)
                window.ShowVault();
        });

        var tokenStore = global::App.App.Services.GetRequiredService<ITokenStore>();
        if (tokenStore.TryLoad(out var session))
            ViewModel.PrepareUnlock(session.ServerUrl, session.Email);

        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        LoginForm.SizeChanged += OnLoginFormSizeChanged;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private async void OnLogin(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoginCommand.ExecuteAsync(null);
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => QueueFitWindowToContent();

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        LoginForm.SizeChanged -= OnLoginFormSizeChanged;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
    }

    public void RequestWindowFit() => QueueFitWindowToContent();

    private void OnLoginFormSizeChanged(object sender, SizeChangedEventArgs e) => QueueFitWindowToContent();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (IsLayoutAffectingProperty(e.PropertyName))
            QueueFitWindowToContent();
    }

    private void QueueFitWindowToContent()
    {
        if (_isWindowFitQueued)
            return;

        _isWindowFitQueued = true;
        if (!DispatcherQueue.TryEnqueue(FitWindowToContent))
            FitWindowToContent();
    }

    private void FitWindowToContent()
    {
        _isWindowFitQueued = false;
        LoginForm.UpdateLayout();
        App.MainWindow?.FitLoginWindowToContent(LoginForm, LoginSurface.Padding);
    }

    private static bool IsLayoutAffectingProperty(string? propertyName) =>
        propertyName is null
        or nameof(LoginViewModel.Stage)
        or nameof(LoginViewModel.ShowCustomServerUrl)
        or nameof(LoginViewModel.HasStatus)
        or nameof(LoginViewModel.Status)
        or nameof(LoginViewModel.IsBusy)
        or nameof(LoginViewModel.FormTitle)
        or nameof(LoginViewModel.FormSubtitle)
        or nameof(LoginViewModel.StepText)
        or nameof(LoginViewModel.PrimaryButtonText);
}
