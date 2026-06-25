namespace BrowserNativeHost;

internal static class Program
{
    public static async Task<int> Main()
    {
        return await NativeMessagingHost.RunAsync(
            Console.OpenStandardInput(),
            Console.OpenStandardOutput(),
            Console.Error,
            CancellationToken.None);
    }
}
