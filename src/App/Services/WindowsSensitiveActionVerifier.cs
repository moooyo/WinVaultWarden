using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Core.Services;
using Windows.Security.Credentials.UI;

namespace App.Services;

/// <summary>Uses the system consent prompt without borrowing mandatory passkey verification state.</summary>
public sealed class WindowsSensitiveActionVerifier(IAccountService account) : ISensitiveActionVerifier
{
    public async Task<bool> VerifyAsync(
        nint windowHandle,
        string message,
        Func<CancellationToken, Task<string?>> prompt,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            if (windowHandle != 0
                && await UserConsentVerifier.CheckAvailabilityAsync().AsTask(ct)
                    == UserConsentVerifierAvailability.Available)
            {
                var result = await UserConsentVerifierInterop
                    .RequestVerificationForWindowAsync(windowHandle, message).AsTask(ct);
                // Cancellation and a rejected gesture must never open a fallback dialog.
                return result == UserConsentVerificationResult.Verified && !ct.IsCancellationRequested;
            }
        }
        catch (COMException)
        {
            // A device without a working system verifier can still use its master password.
        }

        ct.ThrowIfCancellationRequested();
        var password = await prompt(ct);
        return password is not null
            && await account.VerifyMasterPasswordAsync(password, ct)
            && !ct.IsCancellationRequested;
    }
}
