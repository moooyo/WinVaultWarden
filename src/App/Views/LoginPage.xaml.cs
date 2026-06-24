using App.ViewModels;
using Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace App.Views;

public sealed partial class LoginPage : Page
{
    public LoginViewModel ViewModel { get; }

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
    }

    private async void OnLogin(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoginCommand.ExecuteAsync(null);
    }
}
