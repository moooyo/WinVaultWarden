namespace BrowserNativeHost;

internal static class Program
{
    public static async Task<int> Main()
    {
        // Explicit real bridge client so the production path always carries the Windows peer
        // authorizer (Task 3) — the App server is verified before any passkey traffic.
        return await NativeMessagingHost.RunAsync(
            Console.OpenStandardInput(),
            Console.OpenStandardOutput(),
            Console.Error,
            CancellationToken.None,
            new NamedPipeAppPasskeyBridgeClient());
    }
}
