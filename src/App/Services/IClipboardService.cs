namespace App.Services;

/// <summary>
/// Writes vault-derived text to the clipboard without allowing clipboard history or roaming.
/// Every write uses the user's clipboard clear timeout and remains eligible for an
/// ownership-checked clear when the vault locks or the account signs out.
/// </summary>
public interface IClipboardService
{
    /// <summary>
    /// Writes vault-derived text using the protected clipboard policy.
    /// </summary>
    void SetText(string text);

    /// <summary>
    /// Compatibility alias for callers that explicitly identify a secret value.
    /// The default timeout comes from the user's clipboard clear setting.
    /// </summary>
    void SetSecretText(string text, int? autoClearSeconds = null) => SetText(text);

    /// <summary>
    /// Clears the latest application-owned clipboard value during a synchronous lifecycle transition.
    /// </summary>
    void ClearSecretsNow() { }

    /// <summary>
    /// Clears the latest application-owned clipboard value during an asynchronous lifecycle transition.
    /// </summary>
    Task ClearSecretsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ClearSecretsNow();
        return Task.CompletedTask;
    }
}
