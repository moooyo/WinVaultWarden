namespace App.Services;

/// <summary>Serializes external passkey and SSH approval dialogs within the main XAML root.</summary>
internal static class PasskeyApprovalDialogGate
{
    internal static readonly SemaphoreSlim Shared = new(1, 1);
}
