using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace App.ViewModels.Models;

public enum CipherEditorFieldType
{
    Text,
    Hidden,
    Boolean,
}

public sealed partial class CipherEditorDraft : ObservableObject
{
    [ObservableProperty]
    public partial VaultItemKind Type { get; set; }

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? FolderId { get; set; }

    [ObservableProperty]
    public partial bool Favorite { get; set; }

    [ObservableProperty]
    public partial bool Reprompt { get; set; }

    [ObservableProperty]
    public partial string Notes { get; set; } = string.Empty;

    public LoginEditorDraft Login { get; } = new();
    public CardEditorDraft Card { get; } = new();
    public IdentityEditorDraft Identity { get; } = new();
    public SecureNoteEditorDraft SecureNote { get; } = new();
    public SshKeyEditorDraft SshKey { get; } = new();
    public ObservableCollection<CustomFieldEditorDraft> CustomFields { get; } = new();

    public bool IsLogin => Type == VaultItemKind.Login;
    public bool IsCard => Type == VaultItemKind.Card;
    public bool IsIdentity => Type == VaultItemKind.Identity;
    public bool IsSecureNote => Type == VaultItemKind.Note;
    public bool IsSshKey => Type == VaultItemKind.Ssh;

    public static CipherEditorDraft CreateDefault(VaultItemKind type) => new() { Type = type };

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(Name))
            errors.Add("项目名称为必填项。");

        if (Type == VaultItemKind.Ssh)
        {
            if (string.IsNullOrWhiteSpace(SshKey.PrivateKey))
                errors.Add("SSH 私钥为必填项。");
            if (string.IsNullOrWhiteSpace(SshKey.PublicKey))
                errors.Add("SSH 公钥为必填项。");
            if (string.IsNullOrWhiteSpace(SshKey.KeyFingerprint))
                errors.Add("SSH 指纹为必填项。");
        }

        return errors;
    }

    public bool HasRequiredData() => Validate().Count == 0;

    partial void OnTypeChanged(VaultItemKind value)
    {
        OnPropertyChanged(nameof(IsLogin));
        OnPropertyChanged(nameof(IsCard));
        OnPropertyChanged(nameof(IsIdentity));
        OnPropertyChanged(nameof(IsSecureNote));
        OnPropertyChanged(nameof(IsSshKey));
    }
}

public sealed partial class LoginEditorDraft : ObservableObject
{
    [ObservableProperty]
    public partial string Username { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Password { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Totp { get; set; } = string.Empty;

    public ObservableCollection<LoginUriEditorDraft> Uris { get; } = new()
    {
        new LoginUriEditorDraft(),
    };

    public string PrimaryUri
    {
        get => Uris.Count > 0 ? Uris[0].Uri : string.Empty;
        set
        {
            if (Uris.Count == 0)
                Uris.Add(new LoginUriEditorDraft());
            Uris[0].Uri = value;
            OnPropertyChanged(nameof(PrimaryUri));
        }
    }
}

public sealed partial class LoginUriEditorDraft : ObservableObject
{
    [ObservableProperty]
    public partial string Uri { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int? Match { get; set; }
}

public sealed partial class CardEditorDraft : ObservableObject
{
    [ObservableProperty]
    public partial string CardholderName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Number { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ExpMonth { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ExpYear { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Code { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Brand { get; set; } = string.Empty;
}

public sealed partial class IdentityEditorDraft : ObservableObject
{
    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FirstName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string MiddleName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LastName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Username { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Company { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Ssn { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PassportNumber { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LicenseNumber { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Email { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Phone { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Address1 { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Address2 { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Address3 { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string City { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string State { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PostalCode { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Country { get; set; } = string.Empty;
}

public sealed partial class SecureNoteEditorDraft : ObservableObject
{
    [ObservableProperty]
    public partial int Type { get; set; }
}

public sealed partial class SshKeyEditorDraft : ObservableObject
{
    [ObservableProperty]
    public partial string PrivateKey { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PublicKey { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string KeyFingerprint { get; set; } = string.Empty;
}

public sealed partial class CustomFieldEditorDraft : ObservableObject
{
    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial CipherEditorFieldType Type { get; set; } = CipherEditorFieldType.Text;

    [ObservableProperty]
    public partial string Value { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool BooleanValue { get; set; }
}
