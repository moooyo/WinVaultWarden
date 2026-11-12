namespace App.Services;

/// <summary>Prevents synthetic tests from reaching registration-changing code paths.</summary>
internal static class AboutInfo
{
    internal static bool IsPackaged => throw new InvalidOperationException(
        "Synthetic native tests must never register or unregister the Windows passkey provider.");
}
