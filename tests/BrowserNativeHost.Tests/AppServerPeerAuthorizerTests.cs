using System.IO.Pipes;
using Core.BrowserIntegration;
using Xunit;

namespace BrowserNativeHost.Tests;

public sealed class AppServerPeerAuthorizerTests
{
    private const string Sid = "S-1-5-21-100-200-300-1001";
    private const string App = @"C:\Program Files\WinVaultWarden\App.exe";
    private const string Host = @"C:\Program Files\WinVaultWarden\BrowserNativeHost.exe";

    // The pipe client (native host) authorizing the server (App) must read the SERVER pid
    // (GetNamedPipeServerProcessId) and return exactly that identity.
    [Fact]
    public async Task AuthorizeServer_UsesServerProcessId_AndReturnsThatIdentity()
    {
        const uint serverPid = 4321;
        var reader = new RecordingReader(
            local: Identity(1000, Host),
            peers: new() { [serverPid] = Identity(serverPid, App) },
            serverPid: serverPid,
            clientPid: 9999);
        var authorizer = new AppServerPeerAuthorizer(reader);

        await using var pipe = new NamedPipeClientStream(
            ".", Guid.NewGuid().ToString("N"), PipeDirection.InOut, PipeOptions.Asynchronous);
        var identity = await authorizer.AuthorizeServerAsync(pipe, App, TestContext.Current.CancellationToken);

        Assert.Equal(serverPid, identity.ProcessId);
        Assert.True(reader.ServerProcessIdRequested);
        Assert.False(reader.ClientProcessIdRequested);
    }

    // The pipe server (App) authorizing the client (native host) must read the CLIENT pid
    // (GetNamedPipeClientProcessId) and return exactly that identity.
    [Fact]
    public async Task AuthorizeClient_UsesClientProcessId_AndReturnsThatIdentity()
    {
        const uint clientPid = 8765;
        var reader = new RecordingReader(
            local: Identity(1000, App),
            peers: new() { [clientPid] = Identity(clientPid, Host) },
            serverPid: 1111,
            clientPid: clientPid);
        var authorizer = new AppServerPeerAuthorizer(reader);

        await using var pipe = new NamedPipeServerStream(
            Guid.NewGuid().ToString("N"), PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var identity = await authorizer.AuthorizeClientAsync(pipe, Host, TestContext.Current.CancellationToken);

        Assert.Equal(clientPid, identity.ProcessId);
        Assert.True(reader.ClientProcessIdRequested);
        Assert.False(reader.ServerProcessIdRequested);
    }

    [Fact]
    public async Task AuthorizeServer_RejectsUnexpectedExecutablePath()
    {
        const uint serverPid = 4321;
        var reader = new RecordingReader(
            local: Identity(1000, Host),
            peers: new() { [serverPid] = Identity(serverPid, @"C:\Temp\Evil.exe") },
            serverPid: serverPid,
            clientPid: 0);
        var authorizer = new AppServerPeerAuthorizer(reader);

        await using var pipe = new NamedPipeClientStream(
            ".", Guid.NewGuid().ToString("N"), PipeDirection.InOut, PipeOptions.Asynchronous);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await authorizer.AuthorizeServerAsync(pipe, App, TestContext.Current.CancellationToken));
    }

    // The production authorizers must never carry a signature/skip bypass switch.
    [Fact]
    public void ProductionAuthorizers_HaveNoBypassSwitch()
    {
        var sources = string.Join('\n',
            File.ReadAllText(FindRepoFile("src", "App", "Services", "ClientPeerAuthorizer.cs")),
            File.ReadAllText(FindRepoFile("src", "BrowserNativeHost", "AppServerPeerAuthorizer.cs")));

        Assert.DoesNotContain("RequireSignature", sources);
        Assert.DoesNotContain("Skip", sources);
    }

    private static PeerIdentity Identity(uint pid, string path) =>
        new(pid, Sid, path, "WinVaultWarden_abc", false, null);

    private static string FindRepoFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                new[] { directory.FullName }.Concat(segments).ToArray());
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate {string.Join('/', segments)} from {AppContext.BaseDirectory}.");
    }

    private sealed class RecordingReader(
        PeerIdentity local,
        Dictionary<uint, PeerIdentity> peers,
        uint serverPid,
        uint clientPid) : IProcessIdentityReader
    {
        public bool ServerProcessIdRequested { get; private set; }
        public bool ClientProcessIdRequested { get; private set; }

        public PeerIdentity ReadCurrentProcess() => local;

        public PeerIdentity ReadProcess(uint processId) => peers[processId];

        public uint GetServerProcessId(NamedPipeClientStream pipe)
        {
            ServerProcessIdRequested = true;
            return serverPid;
        }

        public uint GetClientProcessId(NamedPipeServerStream pipe)
        {
            ClientProcessIdRequested = true;
            return clientPid;
        }
    }
}
