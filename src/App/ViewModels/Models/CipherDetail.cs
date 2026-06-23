namespace App.ViewModels.Models;

public record CustomField(string Label, string Value);

// 详情基类。共用:名称、文件夹、备注、自定义字段、时间。
public abstract class CipherDetail
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? FolderName { get; init; }
    public abstract VaultItemKind Kind { get; }
    public string? Notes { get; init; }
    public IReadOnlyList<CustomField> CustomFields { get; init; } = Array.Empty<CustomField>();
    public DateTimeOffset Created { get; init; }
    public DateTimeOffset Edited { get; init; }
    public bool IsDeleted { get; init; }

    public string HistoryText =>
        $"最后编辑:{Edited.LocalDateTime:yyyy/M/d HH:mm:ss}　创建于:{Created.LocalDateTime:yyyy/M/d HH:mm:ss}";
}

public sealed class LoginDetail : CipherDetail
{
    public override VaultItemKind Kind => VaultItemKind.Login;
    public string? Username { get; init; }
    public string? Password { get; init; }
    public string? TotpSecret { get; init; }
    public string? Uri { get; init; }
}

public sealed class CardDetail : CipherDetail
{
    public override VaultItemKind Kind => VaultItemKind.Card;
    public string? Cardholder { get; init; }
    public string? Number { get; init; }
    public string? Expiry { get; init; }
    public string? Brand { get; init; }
    public string? Cvv { get; init; }
}

public sealed class IdentityDetail : CipherDetail
{
    public override VaultItemKind Kind => VaultItemKind.Identity;
    public string? FullName { get; init; }
    public string? Email { get; init; }
    public string? Phone { get; init; }
    public string? IdNumber { get; init; }
    public string? Address { get; init; }
}

public sealed class NoteDetail : CipherDetail
{
    public override VaultItemKind Kind => VaultItemKind.Note;
    public string? Content { get; init; }
}

public sealed class SshDetail : CipherDetail
{
    public override VaultItemKind Kind => VaultItemKind.Ssh;
    public string? PublicKey { get; init; }
    public string? PrivateKey { get; init; }
    public string? Fingerprint { get; init; }
}
