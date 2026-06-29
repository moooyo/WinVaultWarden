using App.Services;
using Core.Services;
using Xunit;

namespace App.Tests;

/// <summary>
/// NotificationsHost 纯 C# 测试：验证事件桥接逻辑，不依赖 WinUI 或 DispatcherQueue。
/// </summary>
public class NotificationsHostTests
{
    // ── FakeNotificationsService ──────────────────────────────────────────────

    private sealed class FakeNotificationsService : INotificationsService
    {
        public event Action? VaultChanged;
        public event Action? SendsChanged;
        public event Action? AuthRequestsChanged;
        public event Action? LoggedOut;

        public bool StartCalled { get; private set; }
        public bool StopCalled { get; private set; }

        public Task StartAsync(CancellationToken ct = default)
        {
            StartCalled = true;
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            StopCalled = true;
            return Task.CompletedTask;
        }

        public void RaiseVaultChanged()        => VaultChanged?.Invoke();
        public void RaiseSendsChanged()        => SendsChanged?.Invoke();
        public void RaiseAuthRequestsChanged() => AuthRequestsChanged?.Invoke();
        public void RaiseLoggedOut()           => LoggedOut?.Invoke();
    }

    // ── 辅助工厂 ─────────────────────────────────────────────────────────────

    private static (NotificationsHost host, FakeNotificationsService svc,
        List<string> log)
        Build()
    {
        var svc = new FakeNotificationsService();
        var log = new List<string>();

        var host = new NotificationsHost(
            svc,
            onVaultChanged:        () => log.Add("vault"),
            onSendsChanged:        () => log.Add("sends"),
            onAuthRequestsChanged: () => log.Add("auth"),
            onLoggedOut:           () => log.Add("logout"));

        return (host, svc, log);
    }

    // ── 事件桥接测试 ──────────────────────────────────────────────────────────

    [Fact]
    public void VaultChanged_Event_InvokesOnVaultChangedExactlyOnce()
    {
        var (_, svc, log) = Build();

        svc.RaiseVaultChanged();

        Assert.Equal(["vault"], log);
    }

    [Fact]
    public void SendsChanged_Event_InvokesOnSendsChangedExactlyOnce()
    {
        var (_, svc, log) = Build();

        svc.RaiseSendsChanged();

        Assert.Equal(["sends"], log);
    }

    [Fact]
    public void AuthRequestsChanged_Event_InvokesOnAuthRequestsChangedExactlyOnce()
    {
        var (_, svc, log) = Build();

        svc.RaiseAuthRequestsChanged();

        Assert.Equal(["auth"], log);
    }

    [Fact]
    public void LoggedOut_Event_InvokesOnLoggedOutExactlyOnce()
    {
        var (_, svc, log) = Build();

        svc.RaiseLoggedOut();

        Assert.Equal(["logout"], log);
    }

    [Fact]
    public void MultipleEvents_EachInvokesMatchingCallbackInOrder()
    {
        var (_, svc, log) = Build();

        svc.RaiseVaultChanged();
        svc.RaiseSendsChanged();
        svc.RaiseAuthRequestsChanged();
        svc.RaiseLoggedOut();

        Assert.Equal(["vault", "sends", "auth", "logout"], log);
    }

    [Fact]
    public void RaisingEventTwice_CallbackInvokedTwice()
    {
        var (_, svc, log) = Build();

        svc.RaiseVaultChanged();
        svc.RaiseVaultChanged();

        Assert.Equal(2, log.Count(e => e == "vault"));
    }

    // ── StartAsync / StopAsync 委托测试 ──────────────────────────────────────

    [Fact]
    public async Task StartAsync_DelegatesToService()
    {
        var (host, svc, _) = Build();

        await host.StartAsync();

        Assert.True(svc.StartCalled);
    }

    [Fact]
    public async Task StopAsync_DelegatesToService()
    {
        var (host, svc, _) = Build();

        await host.StopAsync();

        Assert.True(svc.StopCalled);
    }

    [Fact]
    public async Task StartAsync_DoesNotThrow_WhenServiceThrows()
    {
        var svc = new ThrowingNotificationsService();
        var host = new NotificationsHost(svc, () => { }, () => { }, () => { }, () => { });

        // 不应抛出
        await host.StartAsync();
    }

    [Fact]
    public async Task StopAsync_DoesNotThrow_WhenServiceThrows()
    {
        var svc = new ThrowingNotificationsService();
        var host = new NotificationsHost(svc, () => { }, () => { }, () => { }, () => { });

        await host.StopAsync();
    }

    // ── 抛异常的假服务 ────────────────────────────────────────────────────────

    private sealed class ThrowingNotificationsService : INotificationsService
    {
#pragma warning disable CS0067 // 接口约定要求声明，测试中不触发
        public event Action? VaultChanged;
        public event Action? SendsChanged;
        public event Action? AuthRequestsChanged;
        public event Action? LoggedOut;
#pragma warning restore CS0067

        public Task StartAsync(CancellationToken ct = default) =>
            Task.FromException(new InvalidOperationException("start error"));

        public Task StopAsync() =>
            Task.FromException(new InvalidOperationException("stop error"));
    }
}
