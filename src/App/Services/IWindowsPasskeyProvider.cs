namespace App.Services;

/// <summary>Exposes platform lifecycle and availability without coupling views to native COM.</summary>
public interface IWindowsPasskeyProvider
{
    WindowsPasskeyProviderStatus GetStatus();
    Task StartAsync();
    Task StopAsync();
    void Register();
    void Unregister();
    void SynchronizeMetadata();
    Task OnSessionLockedAsync();
}
