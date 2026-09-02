using System.Runtime.InteropServices.WindowsRuntime;
using Core.Abstractions;
using Windows.Security.Credentials;
using Windows.Security.Cryptography;

namespace App.Services;

// Windows Hello adapter with one fixed application credential. Interactive failures and
// cancellations return null or false instead of exposing WinRT exceptions to the UI layer.
public sealed class WindowsHelloAuthenticator : IWindowsHelloAuthenticator
{
    internal const string CredentialName = "WinVaultWarden.HelloUnlock";

    public async Task<bool> IsAvailableAsync()
    {
        try { return await KeyCredentialManager.IsSupportedAsync(); }
        catch { return false; }
    }

    public async Task<byte[]?> CreateProtectorAsync(byte[] challenge, CancellationToken ct = default)
    {
        try
        {
            var create = await KeyCredentialManager.RequestCreateAsync(CredentialName, KeyCredentialCreationOption.ReplaceExisting);
            if (create.Status != KeyCredentialStatus.Success)
                return null;
            return await SignAsync(create.Credential, challenge);
        }
        catch { return null; }
    }

    public async Task<byte[]?> GetProtectorAsync(byte[] challenge, CancellationToken ct = default)
    {
        try
        {
            var open = await KeyCredentialManager.OpenAsync(CredentialName);
            if (open.Status != KeyCredentialStatus.Success)
                return null;
            return await SignAsync(open.Credential, challenge);
        }
        catch { return null; }
    }

    private static async Task<byte[]?> SignAsync(KeyCredential credential, byte[] challenge)
    {
        var buffer = CryptographicBuffer.CreateFromByteArray(challenge);
        var signResult = await credential.RequestSignAsync(buffer); // 触发 Hello 手势
        if (signResult.Status != KeyCredentialStatus.Success)
            return null;
        CryptographicBuffer.CopyToByteArray(signResult.Result, out var bytes);
        return bytes;
    }
}
