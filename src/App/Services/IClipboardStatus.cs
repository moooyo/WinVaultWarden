namespace App.Services;

/// <summary>Observable clipboard metadata. The status never contains copied secrets.</summary>
public interface IClipboardStatus
{
    long WriteVersion { get; }
    DateTimeOffset? ClearAt { get; }
    int TotalSeconds { get; }
    bool HasContent { get; }
    DateTimeOffset? LastClearedAt { get; }
    string Label { get; }
    void SetLabel(string label);
    event EventHandler? Changed;
}
