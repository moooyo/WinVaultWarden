using Api;
using Core.Abstractions;
using Core.Passkeys;
using Core.Services;
using Crypto;
using Crypto.PasswordStrength;
using Vault;
using Microsoft.Extensions.DependencyInjection;
using App.ViewModels;
using Microsoft.UI.Dispatching;

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
        services.AddSingleton<IAccountApiClient>(sp => sp.GetRequiredService<ApiClient>());
        services.AddSingleton<ITwoFactorApiClient>(sp => sp.GetRequiredService<ApiClient>());
        services.AddSingleton<IAuthRequestApiClient>(sp => sp.GetRequiredService<ApiClient>());
        services.AddSingleton<IEmergencyAccessApiClient>(sp => sp.GetRequiredService<ApiClient>());
        services.AddSingleton<CipherEncryptor>();
        services.AddSingleton<IVaultWriteService, Vault.VaultWriteService>();
        services.AddSingleton<VaultDecryptor>();
        services.AddSingleton<AttachmentCryptoService>();
        services.AddSingleton<VaultBootstrapper>();
        services.AddSingleton<VaultTimeoutService>();
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
        services.AddSingleton<IAttachmentService, Vault.AttachmentService>();
        services.AddSingleton<IAccountService, Vault.AccountService>();
        services.AddSingleton<ITwoFactorService, Vault.TwoFactorService>();
        services.AddSingleton<IAuthRequestService, Vault.AuthRequestService>();
        services.AddSingleton<IEmergencyAccessService, Vault.EmergencyAccessService>();
        services.AddSingleton<IPinService, Vault.PinService>();
        services.AddSingleton<ITwoFactorUiService>(sp =>
            new TwoFactorUiService(sp.GetRequiredService<ITwoFactorService>()));
        services.AddSingleton<IAuthRequestUiService>(sp =>
            new AuthRequestUiService(sp.GetRequiredService<IAuthRequestService>()));
        services.AddTransient<IEmergencyAccessUiService, EmergencyAccessUiService>();
        services.AddTransient<EmergencyAccessViewModel>();
        services.AddSingleton<IPasskeyApprovalService, PasskeyApprovalDialogService>();
        services.AddSingleton<BrowserPasskeyRequestHandler>();
        services.AddSingleton<PasskeyBridgeServer>();
        services.AddSingleton<IVaultUiService, VaultUiService>();
        services.AddSingleton<ISendUiService>(sp => new SendUiService(
            sp.GetRequiredService<ISendService>(),
            sp.GetRequiredService<ISendWriteService>(),
            sp.GetRequiredService<ISendAccessService>(),
            sp.GetRequiredService<VaultSession>().Account?.ServerUrl ?? string.Empty));
        services.AddSingleton<IAttachmentUiService>(sp =>
            new AttachmentUiService(sp.GetRequiredService<IAttachmentService>()));
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
        services.AddSingleton<IFaviconCache>(sp => new Vault.FaviconCache(new HttpClient(), sp.GetRequiredService<VaultSession>()));

        services.AddSingleton<IRegisterService, Vault.RegisterService>();
        services.AddSingleton<IRegisterUiService>(sp =>
            new RegisterUiService(sp.GetRequiredService<IRegisterService>()));
        services.AddTransient<RegisterViewModel>(sp =>
            new RegisterViewModel(sp.GetRequiredService<IRegisterUiService>()));

        services.AddTransient<LoginViewModel>();
        services.AddTransient<VaultViewModel>();
        services.AddTransient<SendViewModel>();
        services.AddTransient<GeneratorViewModel>();
        services.AddTransient<SettingsViewModel>(sp =>
            new SettingsViewModel(
                sp.GetRequiredService<IAccountUiService>(),
                sp.GetRequiredService<ITwoFactorUiService>(),
                sp.GetRequiredService<IPinService>()));
        services.AddTransient<DevicesViewModel>(sp =>
            new DevicesViewModel(
                sp.GetRequiredService<IDeviceUiService>(),
                sp.GetRequiredService<IAuthRequestUiService>()));

        // ── Vault 健康报告 ──────────────────────────────────────────────────────
        services.AddSingleton<PasswordStrengthEvaluator>(_ =>
            new PasswordStrengthEvaluator(
                new Omnimatch(
                    new DictionaryMatcher(FrequencyDictionaries.Load()))));
        services.AddSingleton<IPwnedPasswordsClient>(_ =>
            new PwnedPasswordsClient(new HttpClient()));
        services.AddSingleton<IVaultHealthService, Vault.VaultHealthService>();
        services.AddTransient<IVaultHealthUiService, VaultHealthUiService>();
        services.AddSingleton<ISavedSearchStore, AppPreferencesSavedSearchStore>();
        services.AddTransient<SecurityReportViewModel>();

        // ── 导入 / 导出 ──────────────────────────────────────────────────────────
        services.AddSingleton<IVaultExportService, Vault.VaultExportService>();
        services.AddSingleton<IVaultImportService, Vault.VaultImportService>();
        services.AddTransient<ImportExportViewModel>();

        // WebSocket 推送通知
        services.AddSingleton<INotificationDispatcher>(sp =>
            new NotificationDispatcher(
                sp.GetRequiredService<IAttachmentApiClient>(),
                sp.GetRequiredService<IReadonlyApiClient>(),
                sp.GetRequiredService<VaultDecryptor>(),
                sp.GetRequiredService<VaultSession>(),
                sp.GetRequiredService<ISyncService>()));
        services.AddSingleton<INotificationsService>(sp =>
            new NotificationsService(
                () => new NotificationsConnection(),
                sp.GetRequiredService<INotificationDispatcher>(),
                sp.GetRequiredService<VaultSession>(),
                sp.GetRequiredService<ITokenStore>(),
                sp.GetRequiredService<ITokenRefresher>()));
        services.AddSingleton<NotificationsHost>(sp =>
        {
            var svc = sp.GetRequiredService<INotificationsService>();

            void Dispatch(Microsoft.UI.Dispatching.DispatcherQueueHandler handler)
            {
                var dq = global::App.App.MainWindow?.DispatcherQueue;
                if (dq is not null)
                    dq.TryEnqueue(handler);
            }

            return new NotificationsHost(
                svc,
                onVaultChanged: () => Dispatch(() =>
                    global::App.App.MainWindow?.RefreshVaultList()),
                onSendsChanged: () => Dispatch(() =>
                    global::App.App.MainWindow?.RefreshSendList()),
                onAuthRequestsChanged: () => Dispatch(() =>
                    global::App.App.MainWindow?.RefreshRequestsList()),
                onLoggedOut: () => Dispatch(async () =>
                {
                    await global::App.App.Services.GetRequiredService<IAuthService>().LogoutAsync();
                    global::App.App.MainWindow?.ShowLogin();
                }));
        });

        return services.BuildServiceProvider();
    }
}
