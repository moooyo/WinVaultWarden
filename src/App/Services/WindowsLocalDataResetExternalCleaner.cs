using Microsoft.Win32;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Security.Credentials;

namespace App.Services;

/// <summary>
/// Removes Windows-managed application credentials that are outside the local data root.
/// Any failure is propagated so the reset request remains pending for the next startup.
/// </summary>
internal sealed class WindowsLocalDataResetExternalCleaner
{
    internal const string ChromeNativeMessagingHostRegistryPath =
        @"Software\Google\Chrome\NativeMessagingHosts\com.winvaultwarden.browser";
    internal const string EdgeNativeMessagingHostRegistryPath =
        @"Software\Microsoft\Edge\NativeMessagingHosts\com.winvaultwarden.browser";

    public void Clean()
    {
        KeyCredentialManager.DeleteAsync(WindowsHelloAuthenticator.CredentialName)
            .AsTask()
            .GetAwaiter()
            .GetResult();

        DeletePortableNativeMessagingRegistration(
            ChromeNativeMessagingHostRegistryPath);
        DeletePortableNativeMessagingRegistration(
            EdgeNativeMessagingHostRegistryPath);
    }

    private static void DeletePortableNativeMessagingRegistration(string subKeyPath) =>
        Registry.CurrentUser.DeleteSubKey(
            subKeyPath,
            throwOnMissingSubKey: false);
}
