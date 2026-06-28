using Api;
using Core.Abstractions;
using Core.Passkeys;
using Core.Services;
using Crypto;
using Vault;
using Microsoft.Extensions.DependencyInjection;
using App.ViewModels;

namespace App.Services;

public static class ServiceConfiguration
{
    public static IServiceProvider Build()
    {
        var services = new ServiceCollection();

        services.AddSingleton<VaultSession>();
        services.AddSingleton<ICryptoService, CryptoService>();
        services.AddSingleton<CryptoService>();
        services.AddSingleton<ITokenStore, DpapiTokenStore>();
        services.AddSingleton(sp =>
        {
            var session = sp.GetRequiredService<VaultSession>();
            var handler = new AuthHeaderHandler(
                () => session.AccessToken,
                ct => sp.GetRequiredService<ITokenRefresher>().TryRefreshAsync(ct))
            {
                InnerHandler = new HttpClientHandler(),
            };
            return new HttpClient(handler);
        });
        services.AddSingleton<ApiClient>();
        services.AddSingleton<IApiClient>(sp => sp.GetRequiredService<ApiClient>());
        services.AddSingleton<IReadonlyApiClient>(sp => sp.GetRequiredService<ApiClient>());
        services.AddSingleton<IVaultWriteApiClient>(sp => sp.GetRequiredService<ApiClient>());
        services.AddSingleton<ISendApiClient>(sp => sp.GetRequiredService<ApiClient>());
        services.AddSingleton<IAttachmentApiClient>(sp => sp.GetRequiredService<ApiClient>());
        services.AddSingleton<CipherEncryptor>();
        services.AddSingleton<IVaultWriteService, Vault.VaultWriteService>();
        services.AddSingleton<VaultDecryptor>();
        services.AddSingleton<VaultBootstrapper>();
#if DEBUG
        services.AddSingleton<IDemoVaultSessionService, DemoVaultSessionService>();
#endif
        services.AddSingleton<ITokenRefresher, Vault.TokenRefreshService>();
        services.AddSingleton<IAuthService, Vault.AuthService>();
        services.AddSingleton<ISyncService, Vault.SyncService>();
        services.AddSingleton<IVaultService, Vault.VaultService>();
        services.AddSingleton<SendCryptoService>();
        services.AddSingleton<ISendService, Vault.SendService>();
        services.AddSingleton<ISendWriteService, Vault.SendWriteService>();
        services.AddSingleton<ISendAccessService, Vault.SendAccessService>();
        services.AddSingleton<IPasskeyApprovalService, PasskeyApprovalDialogService>();
        services.AddSingleton<BrowserPasskeyRequestHandler>();
        services.AddSingleton<PasskeyBridgeServer>();
        services.AddSingleton<IVaultUiService, VaultUiService>();
        services.AddSingleton<ISendUiService>(sp => new SendUiService(
            sp.GetRequiredService<ISendService>(),
            sp.GetRequiredService<ISendWriteService>(),
            sp.GetRequiredService<ISendAccessService>(),
            sp.GetRequiredService<VaultSession>().Account?.ServerUrl ?? string.Empty));
        services.AddTransient<IDeviceUiService>(sp =>
        {
            var vault = sp.GetRequiredService<IVaultService>();
            var tokenStore = sp.GetRequiredService<ITokenStore>();
            return tokenStore.TryLoad(out var session)
                ? new DeviceUiService(vault, session.DeviceIdentifier)
                : new DeviceUiService(vault);
        });
        services.AddTransient<IAccountUiService, AccountUiService>();
        services.AddSingleton<IClipboardService, ClipboardService>();

        services.AddTransient<LoginViewModel>();
        services.AddTransient<VaultViewModel>();
        services.AddTransient<SendViewModel>();
        services.AddTransient<GeneratorViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<DevicesViewModel>();

        return services.BuildServiceProvider();
    }
}
