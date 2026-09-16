namespace App.ViewModels.Models;

// Registration metadata from the server; it is not a last-activity timestamp.
public sealed record DeviceItem(
    string Id,
    string Name,
    string Glyph,
    string LastActive,
    bool IsCurrent,
    bool IsTrusted = false)
{
    public string RegisteredLabel => LastActive;
}
