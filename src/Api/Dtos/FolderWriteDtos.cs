namespace Api.Dtos;

// Vaultwarden FolderData:仅 name(加密后的 EncString 文本)。
public sealed class FolderRequest
{
    public string Name { get; init; } = string.Empty;
}
