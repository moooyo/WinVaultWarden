using System.Text.Json.Serialization;

namespace Api.Dtos;

// Vaultwarden FolderData uses an encrypted name and an optional existing ID during import.
public sealed class FolderRequest
{
    /// <summary>Used only by the import endpoint to reference an existing owned folder.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; init; }
    public string Name { get; init; } = string.Empty;
}
