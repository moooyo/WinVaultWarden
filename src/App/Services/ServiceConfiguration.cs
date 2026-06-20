using Api;
using Core.Abstractions;
using Core.Services;
using Crypto;
using Microsoft.Extensions.DependencyInjection;
using App.ViewModels;

namespace App.Services;

public static class ServiceConfiguration
{
    public static IServiceProvider Build()
    {
        var services = new ServiceCollection();

        services.AddSingleton<ICryptoService, CryptoService>();
        services.AddSingleton<CryptoService>();
        services.AddSingleton<HttpClient>();
        services.AddSingleton<IApiClient, ApiClient>();
        services.AddSingleton<IAuthService, AuthService>();
        services.AddSingleton<ISyncService, SyncService>();
        services.AddSingleton<IVaultService, VaultService>();

        services.AddTransient<LoginViewModel>();
        services.AddTransient<VaultViewModel>();

        return services.BuildServiceProvider();
    }
}
