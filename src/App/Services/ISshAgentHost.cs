using Vault;

namespace App.Services;

/// <summary>Connects SSH settings to an owned agent endpoint.</summary>
public interface ISshAgentHost
{
    bool IsRunning { get; }
    SshAgentStartResult Start();
    Task StopAsync();
}

public sealed class WindowsSshAgentHost(SshAgentServer server) : ISshAgentHost
{
    public bool IsRunning => server.IsRunning;
    public SshAgentStartResult Start() => server.Start();
    public Task StopAsync() => server.StopAsync();
}
