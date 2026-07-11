namespace BrowserNativeHost;

internal static class Program
{
    public static async Task<int> Main()
    {
        try
        {
            // Explicit real bridge client so the production path always carries the Windows peer
            // authorizer (Task 3) — the App server is verified before any passkey traffic. One instance
            // lives for the whole Host process and owns the single persistent App pipe connection.
            await using var bridge = new NamedPipeAppPasskeyBridgeClient();
            return await NativeMessagingHost.RunAsync(
                Console.OpenStandardInput(),
                Console.OpenStandardOutput(),
                Console.Error,
                CancellationToken.None,
                bridge);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Belt-and-braces: RunAsync already converts almost everything to a code-only stderr note
            // and an exit code, but a failure constructing/disposing the bridge itself (outside
            // RunAsync's own try) must not fall through to the runtime's default unhandled-exception
            // printer, which would dump the full exception. OOM is deliberately not caught here.
            await Console.Error.WriteLineAsync("Native messaging host encountered an unexpected error.");
            return 1;
        }
    }
}
