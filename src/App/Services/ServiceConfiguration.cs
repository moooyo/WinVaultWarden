using Api;
using Core.Abstractions;
using Core.BrowserIntegration;
using Core.Passkeys;
using Core.Services;
using Core.Session;
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
        services.AddSingleton<SessionTerminationSignal>();
        services.AddSingleton<ISessionTerminationSource>(sp =>
            sp.GetRequiredService<SessionTerminationSignal>());
        services.AddSingleton<ISessionTerminationSink>(sp =>
            sp.GetRequiredService<SessionTerminationSignal>());
        services.AddSingleton<CryptoService>();
        services.AddSingleton<FingerprintService>();
        services.AddSingleton<ITokenStore, DpapiTokenStore>();
        services.AddSingleton<ICacheStore, DpapiVaultCacheStore>();
        services.AddSingleton<IApiClientFactory, ApiClientFactory>();
        // 会话绑定的 Api client 供给器:匿名角色按 origin、认证角色按当前 lease 解析。
        // 401 刷新回调绑定显式 lease(Task 7),仅当租约仍当前时提交轮换后的 token。
        services.AddSingleton<IVaultApiClientProvider>(sp =>
            new VaultApiClientProvider(
                sp.GetRequiredService<IApiClientFactory>(),
                sp.GetRequiredService<VaultSession>(),
                (lease, failed, ct) => sp.GetRequiredService<ITokenRefresher>()
                    .TryRefreshAsync(lease, failed, ct)));
        services.AddSingleton<CipherEncryptor>();
        services.AddSingleton<IVaultWriteService, Vault.VaultWriteService>();
        services.AddSingleton<VaultDecryptor>();
        services.AddSingleton<VaultCacheService>();
        services.AddSingleton<AttachmentCryptoService>();
        services.AddSingleton<VaultBootstrapper>();
        services.AddSingleton<VaultTimeoutService>();
#if DEBUG
        services.AddSingleton<IDemoVaultSessionService, DemoVaultSessionService>();
#endif
        // 必须单例:401 并发去重依赖 TokenRefreshService 实例级 SemaphoreSlim + 双检
        // (见 Vault/TokenRefreshService.cs)。改为 transient 会让每个 401 各自刷新,破坏去重。
        services.AddSingleton<ITokenRefresher, Vault.TokenRefreshService>();
        services.AddSingleton<IAuthService, Vault.AuthService>();
        services.AddSingleton<Vault.SyncService>();
        services.AddSingleton<ISyncService>(sp => sp.GetRequiredService<Vault.SyncService>());
        services.AddSingleton<IVaultService, Vault.VaultService>();
        services.AddSingleton<SendCryptoService>();
        services.AddSingleton<ITransferIdleTimeoutPolicy>(
            new TransferIdleTimeoutPolicy(TransferIdleTimeoutPolicy.DefaultIdleTimeout));
        services.AddSingleton<ISendService, Vault.SendService>();
        // 孤儿 Send 补偿删除的窄客户端(冻结 binding + 一次性认证客户端):见 Vault/SendCompensationClient.cs。
        // 独立于 IVaultApiClientProvider,绝不暴露通用 stale-token 旁路。
        services.AddSingleton<ISendCompensationClient, SendCompensationClient>();
        services.AddSingleton<ISendWriteService, Vault.SendWriteService>();
        services.AddSingleton<ISendAccessService, Vault.SendAccessService>();
        services.AddSingleton<IAttachmentCompensationClient, AttachmentCompensationClient>();
        services.AddSingleton<IAttachmentService, Vault.AttachmentService>();
        services.AddSingleton<IAccountService, Vault.AccountService>();
        // 条目级主密码重提示门禁(Task 4):依赖 IAccountService + VaultSession,二者均已单例注册。
        // 登记到 ISensitiveStateRegistry,使 Lock/登出时立即清空授权缓存(与 generation 键控双重失效)。
        services.AddSingleton<IItemRepromptGate>(sp => TrackSensitive(sp,
            new ItemRepromptGate(
                sp.GetRequiredService<IAccountService>(),
                sp.GetRequiredService<VaultSession>())));
        // 真实用户验证(Task 4):Windows Hello 优先、主密码回退,供 required passkey / 详情解锁产出真实 UV。
        services.AddSingleton<IUserVerificationService>(sp =>
            new UserVerificationService(
                sp.GetRequiredService<IWindowsHelloAuthenticator>(),
                sp.GetRequiredService<IAccountService>()));
        services.AddSingleton<ITwoFactorService, Vault.TwoFactorService>();
        services.AddSingleton<IAuthRequestService, Vault.AuthRequestService>();
        services.AddSingleton<IEmergencyAccessService, Vault.EmergencyAccessService>();
        services.AddSingleton<IPinService, Vault.PinService>();
        services.AddSingleton<IHelloUnlockService, HelloUnlockService>();
        services.AddSingleton<IWindowsHelloAuthenticator, WindowsHelloAuthenticator>();
        services.AddSingleton<ITwoFactorUiService>(sp =>
            new TwoFactorUiService(sp.GetRequiredService<ITwoFactorService>()));
        services.AddSingleton<IAuthRequestUiService>(sp =>
            new AuthRequestUiService(sp.GetRequiredService<IAuthRequestService>()));
        services.AddTransient<IEmergencyAccessUiService, EmergencyAccessUiService>();
        services.AddTransient<EmergencyAccessViewModel>(sp =>
            TrackSensitive(sp, ActivatorUtilities.CreateInstance<EmergencyAccessViewModel>(sp)));
        services.AddSingleton<IPasskeyApprovalService, PasskeyApprovalDialogService>();
        // Task 1 origin/RP policy + Task 4 ceremony gate for the passkey handler. The gate MUST be a
        // singleton so its per-(tab,frame,RP) in-flight/rate-limit state is process-wide.
        services.AddSingleton(WebAuthnRequestPolicy.CreateDefault());
        services.AddSingleton(_ => new PasskeyRequestGate(TimeProvider.System));
        services.AddSingleton<BrowserPasskeyRequestHandler>();
        // Task 3：管道 peer 双向鉴别。生产环境只注册真实 authorizer + Windows 身份读取器,
        // 无任何 fake / 降级开关(fake 仅存在于测试程序集)。
        services.AddSingleton<IPipePeerAuthorizer>(_ =>
            new ClientPeerAuthorizer(new WindowsAppProcessIdentityReader()));
        services.AddSingleton<PasskeyBridgeServer>();
        services.AddSingleton<IVaultUiService, VaultUiService>();
        services.AddSingleton<ISendUiService>(sp => new SendUiService(
            sp.GetRequiredService<ISendService>(),
            sp.GetRequiredService<ISendWriteService>(),
            sp.GetRequiredService<ISendAccessService>(),
            sp.GetRequiredService<VaultSession>()));
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
            TrackSensitive(sp, new RegisterViewModel(sp.GetRequiredService<IRegisterUiService>())));

        services.AddSingleton<AccountPrefill>(); // 注册→登录 的一次性预填暂存(R10)
        // 显式工厂(而非裸 AddTransient<LoginViewModel>()):确保 Hello 服务被真正注入,
        // 否则登录页解锁阶段的 Windows Hello 按钮在生产环境永远不会显示(H7)。
        services.AddTransient<LoginViewModel>(sp => TrackSensitive(sp, new LoginViewModel(
            sp.GetRequiredService<IAuthService>(),
            sp.GetService<IDemoVaultSessionService>(),
            sp.GetRequiredService<IPinService>(),
            sp.GetRequiredService<AccountPrefill>(),
            sp.GetRequiredService<IWindowsHelloAuthenticator>(),
            sp.GetRequiredService<IHelloUnlockService>(),
            sp.GetRequiredService<IAppLifecycleCoordinator>())));
        services.AddTransient<VaultViewModel>(sp =>
            TrackSensitive(sp, ActivatorUtilities.CreateInstance<VaultViewModel>(sp)));
        services.AddTransient<SendViewModel>(sp => TrackSensitive(sp, new SendViewModel(
            sp.GetRequiredService<ISendUiService>(),
            sp.GetRequiredService<IClipboardService>(),
            sp.GetRequiredService<VaultSession>(),
            sp.GetRequiredService<IAppLifecycleCoordinator>())));
        services.AddTransient<GeneratorViewModel>(sp =>
            TrackSensitive(sp, ActivatorUtilities.CreateInstance<GeneratorViewModel>(sp)));
        services.AddTransient<SettingsViewModel>(sp =>
            new SettingsViewModel(
                sp.GetRequiredService<IAccountUiService>(),
                sp.GetRequiredService<ITwoFactorUiService>(),
                sp.GetRequiredService<IPinService>(),
                sp.GetRequiredService<IWindowsHelloAuthenticator>(),
                sp.GetRequiredService<IHelloUnlockService>(),
                sp.GetRequiredService<IAppLifecycleCoordinator>(),
                browserIntegrationSupported: !AboutInfo.IsPackaged));
        services.AddTransient<DevicesViewModel>(sp =>
            new DevicesViewModel(
                sp.GetRequiredService<IDeviceUiService>(),
                sp.GetRequiredService<IAuthRequestUiService>(),
                sp.GetRequiredService<VaultSession>()));

        // ── Vault 健康报告 ──────────────────────────────────────────────────────
        services.AddSingleton<PasswordStrengthEvaluator>(_ =>
            new PasswordStrengthEvaluator(
                new Omnimatch(
                    new DictionaryMatcher(FrequencyDictionaries.Load()))));
        services.AddSingleton<IPwnedPasswordsClient>(_ =>
            new PwnedPasswordsClient(new HttpClient()));
        services.AddSingleton<IVaultHealthService, Vault.VaultHealthService>();
        services.AddTransient<IVaultHealthUiService>(sp =>
            new VaultHealthUiService(
                sp.GetRequiredService<IVaultHealthService>(),
                sp.GetRequiredService<VaultSession>()));
        services.AddSingleton<ISavedSearchStore, AppPreferencesSavedSearchStore>();
        services.AddTransient<SecurityReportViewModel>(sp =>
            TrackSensitive(sp, ActivatorUtilities.CreateInstance<SecurityReportViewModel>(sp)));

        // ── 导入 / 导出 ──────────────────────────────────────────────────────────
        services.AddSingleton<IVaultExportService, Vault.VaultExportService>();
        services.AddSingleton<IVaultImportService, Vault.VaultImportService>();
        services.AddTransient<ImportExportViewModel>(sp =>
            TrackSensitive(sp, ActivatorUtilities.CreateInstance<ImportExportViewModel>(sp)));

        // WebSocket 推送通知
        services.AddSingleton<INotificationDispatcher>(sp =>
            new NotificationDispatcher(
                sp.GetRequiredService<IVaultApiClientProvider>(),
                sp.GetRequiredService<VaultDecryptor>(),
                sp.GetRequiredService<VaultSession>(),
                sp.GetRequiredService<ISyncService>(),
                sp.GetRequiredService<ISessionTerminationSink>()));
        services.AddSingleton<INotificationsService>(sp =>
            new NotificationsService(
                () => new NotificationsConnection(),
                sp.GetRequiredService<INotificationDispatcher>(),
                sp.GetRequiredService<VaultSession>(),
                sp.GetRequiredService<ITokenRefresher>()));
        // ── 导航服务 ──────────────────────────────────────────────────────────
        // 单例:MainWindow 解析具体类型注入 Frame,页面解析接口下沉导航动作。
        services.AddSingleton<FrameNavigationService>();
        services.AddSingleton<INavigationService>(sp => sp.GetRequiredService<FrameNavigationService>());
        // UI 线程派发:具体类型供 MainWindow 注入 DispatcherQueue,接口供组合根/页面下沉派发。
        services.AddSingleton<UiDispatcher>();
        services.AddSingleton<IUiDispatcher>(sp => sp.GetRequiredService<UiDispatcher>());

        services.AddSingleton<NotificationsHost>(sp =>
        {
            var svc = sp.GetRequiredService<INotificationsService>();
            var ui = sp.GetRequiredService<IUiDispatcher>();

            void Dispatch(Microsoft.UI.Dispatching.DispatcherQueueHandler handler)
                => ui.Enqueue(() => handler());

            return new NotificationsHost(
                svc,
                onVaultChanged: () => Dispatch(() =>
                    global::App.App.MainWindow?.RefreshVaultList()),
                onSendsChanged: () => Dispatch(() =>
                    global::App.App.MainWindow?.RefreshSendList()),
                onAuthRequestsChanged: () => Dispatch(() =>
                    global::App.App.MainWindow?.RefreshRequestsList()),
                onLoggedOut: () => { });
        });

        services.AddSingleton<SessionTerminationHost>(sp =>
            new SessionTerminationHost(
                sp.GetRequiredService<ISessionTerminationSource>(),
                notice => sp.GetRequiredService<IUiDispatcher>().Enqueue(() =>
                {
                    _ = sp.GetRequiredService<IAppLifecycleCoordinator>()
                        .RequestAsync(
                            AppLifecycleIntent.RemoteLogout,
                            notice.InvalidatedSession);
                })));

        // ── 生命周期协调:唯一注册点 ──────────────────────────────────────────
        // 先注册两个 registry(弱引用敏感状态 / 强引用瞬态操作),再注册全项目唯一的协调器。
        services.AddSingleton<ISensitiveStateRegistry>(_ =>
            new SensitiveStateRegistry(ex =>
                CrashLogger.WriteRedacted("SensitiveStateRegistry", ex)));
        services.AddSingleton<ITransientOperationRegistry>(_ =>
            new TransientOperationRegistry(ex =>
                CrashLogger.WriteRedacted("TransientOperationRegistry", ex)));

        services.AddSingleton<IAppLifecycleCoordinator>(sp =>
        {
            var session = sp.GetRequiredService<VaultSession>();
            var auth = sp.GetRequiredService<IAuthService>();
            var transients = sp.GetRequiredService<ITransientOperationRegistry>();
            var sensitive = sp.GetRequiredService<ISensitiveStateRegistry>();
            var clipboard = sp.GetRequiredService<IClipboardService>();
            var navigation = sp.GetRequiredService<FrameNavigationService>();

            return new AppLifecycleCoordinator(new AppLifecycleHooks(
                BlockSensitiveOperations: () =>
                    global::App.App.MainWindow?.SetSensitiveUiEnabled(false),
                AllowSensitiveOperations: () =>
                    global::App.App.MainWindow?.SetSensitiveUiEnabled(true),
                InvalidateSession: () => session.Lock(),
                StopBackgroundAsync: () => Task.WhenAll(
                    sp.GetRequiredService<NotificationsHost>().StopAsync(),
                    sp.GetRequiredService<PasskeyBridgeServer>().StopAsync()),
                CloseTransientsAsync: transients.CancelAndCloseAllAsync,
                CloseTransientsSynchronously: transients.CancelAndCloseAllSynchronously,
                ClearClipboardAsync: () => clipboard.ClearSecretsAsync(CancellationToken.None),
                ClearClipboardSynchronously: clipboard.ClearSecretsNow,
                ClearSensitiveState: sensitive.ClearAll,
                ResetFrames: navigation.ResetForLock,
                LockSessionAsync: () => auth.LockAsync(CancellationToken.None),
                LogoutSessionAsync: async () =>
                {
                    await auth.LogoutAsync(CancellationToken.None);
                    sp.GetRequiredService<IFaviconCache>().Clear();
                },
                ShowLogin: () => global::App.App.MainWindow?.ShowLoginAfterLifecycle(),
                ExitProcess: () => global::App.App.MainWindow?.ExitAfterLifecycle(),
                FailClosedSession: () => session.Lock(),
                ReportFailure: (step, ex) =>
                    CrashLogger.WriteRedacted($"Lifecycle:{step}", ex),
                TryLogoutExpectedSessionAsync: lease =>
                    auth.LogoutAsync(lease, CancellationToken.None)));
        });

        return services.BuildServiceProvider();
    }

    // 唯一敏感状态弱登记入口:构造后立即弱引用登记到 registry,锁屏/登出时统一 ClearSensitive。
    // registry 只持弱引用,不会因此阻止页面/VM 被回收。
    private static T TrackSensitive<T>(IServiceProvider sp, T instance)
        where T : class, ISensitiveState
    {
        sp.GetRequiredService<ISensitiveStateRegistry>().Register(instance);
        return instance;
    }
}
