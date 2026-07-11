using System.IO.Pipes;
using App.Services;
using Core.BrowserIntegration;
using Xunit;

namespace App.Tests;

// App 端管道 peer 鉴别（Task 3）：ClientPeerAuthorizer 作为管道 server，鉴别连接过来的 client（native host）。
// 运行时取 SID/路径/包/签名由 IProcessIdentityReader 承担，这里注入 fake reader 单测策略接线与 PID 来源。
public class PeerAuthorizationTests
{
    private const string Sid = "S-1-5-21-100-200-300-1001";
    private const string App = @"C:\Program Files\WinVaultWarden\App.exe";
    private const string Host = @"C:\Program Files\WinVaultWarden\BrowserNativeHost.exe";

    [Fact]
    public async Task AuthorizeClient_UsesClientProcessId_AndAllowsMatchingHost()
    {
        const uint clientPid = 2468;
        var reader = new FakeReader(
            local: Identity(1000, App),
            peers: new() { [clientPid] = Identity(clientPid, Host) },
            clientPid: clientPid);
        var authorizer = new ClientPeerAuthorizer(reader);

        await using var pipe = NewServerPipe();
        var identity = await authorizer.AuthorizeClientAsync(pipe, Host, CancellationToken.None);

        Assert.Equal(clientPid, identity.ProcessId);
        Assert.True(reader.ClientProcessIdRequested);
    }

    [Fact]
    public async Task AuthorizeClient_RejectsForeignSid()
    {
        const uint clientPid = 2468;
        var reader = new FakeReader(
            local: Identity(1000, App),
            peers: new() { [clientPid] = Identity(clientPid, Host) with { UserSid = "S-1-5-21-evil" } },
            clientPid: clientPid);
        var authorizer = new ClientPeerAuthorizer(reader);

        await using var pipe = NewServerPipe();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await authorizer.AuthorizeClientAsync(pipe, Host, CancellationToken.None));
    }

    [Fact]
    public async Task AuthorizeClient_RejectsUnexpectedExecutablePath()
    {
        const uint clientPid = 2468;
        var reader = new FakeReader(
            local: Identity(1000, App),
            peers: new() { [clientPid] = Identity(clientPid, @"C:\Temp\Evil.exe") },
            clientPid: clientPid);
        var authorizer = new ClientPeerAuthorizer(reader);

        await using var pipe = NewServerPipe();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await authorizer.AuthorizeClientAsync(pipe, Host, CancellationToken.None));
    }

    private static NamedPipeServerStream NewServerPipe() =>
        new(Guid.NewGuid().ToString("N"), PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

    private static PeerIdentity Identity(uint pid, string path) =>
        new(pid, Sid, path, "WinVaultWarden_abc", false, null);

    private sealed class FakeReader(
        PeerIdentity local,
        Dictionary<uint, PeerIdentity> peers,
        uint clientPid) : IProcessIdentityReader
    {
        public bool ClientProcessIdRequested { get; private set; }

        public PeerIdentity ReadCurrentProcess() => local;

        public PeerIdentity ReadProcess(uint processId) => peers[processId];

        public uint GetServerProcessId(NamedPipeClientStream pipe) => throw new NotSupportedException();

        public uint GetClientProcessId(NamedPipeServerStream pipe)
        {
            ClientProcessIdRequested = true;
            return clientPid;
        }
    }
}
