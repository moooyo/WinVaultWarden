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
    [ObservableProperty] private VaultItemKind _type;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string? _folderId;
    [ObservableProperty] private bool _favorite;
    [ObservableProperty] private bool _reprompt;
    [ObservableProperty] private string _notes = string.Empty;

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
    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private string _totp = string.Empty;

    public ObservableCollection<LoginUriEditorDraft> Uris { get; } = new()
    {
        new LoginUriEditorDraft(),
    };
}

public sealed partial class LoginUriEditorDraft : ObservableObject
{
    [ObservableProperty] private string _uri = string.Empty;
    [ObservableProperty] private int? _match;
}

public sealed partial class CardEditorDraft : ObservableObject
{
    [ObservableProperty] private string _cardholderName = string.Empty;
    [ObservableProperty] private string _number = string.Empty;
    [ObservableProperty] private string _expMonth = string.Empty;
    [ObservableProperty] private string _expYear = string.Empty;
    [ObservableProperty] private string _code = string.Empty;
    [ObservableProperty] private string _brand = string.Empty;
}

public sealed partial class IdentityEditorDraft : ObservableObject
{
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _firstName = string.Empty;
    [ObservableProperty] private string _middleName = string.Empty;
    [ObservableProperty] private string _lastName = string.Empty;
    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private string _company = string.Empty;
    [ObservableProperty] private string _ssn = string.Empty;
    [ObservableProperty] private string _passportNumber = string.Empty;
    [ObservableProperty] private string _licenseNumber = string.Empty;
    [ObservableProperty] private string _email = string.Empty;
    [ObservableProperty] private string _phone = string.Empty;
    [ObservableProperty] private string _address1 = string.Empty;
    [ObservableProperty] private string _address2 = string.Empty;
    [ObservableProperty] private string _address3 = string.Empty;
    [ObservableProperty] private string _city = string.Empty;
    [ObservableProperty] private string _state = string.Empty;
    [ObservableProperty] private string _postalCode = string.Empty;
    [ObservableProperty] private string _country = string.Empty;
}

public sealed partial class SecureNoteEditorDraft : ObservableObject
{
    [ObservableProperty] private int _type;
}

public sealed partial class SshKeyEditorDraft : ObservableObject
{
    [ObservableProperty] private string _privateKey = string.Empty;
    [ObservableProperty] private string _publicKey = string.Empty;
    [ObservableProperty] private string _keyFingerprint = string.Empty;
}

public sealed partial class CustomFieldEditorDraft : ObservableObject
{
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private CipherEditorFieldType _type = CipherEditorFieldType.Text;
    [ObservableProperty] private string _value = string.Empty;
    [ObservableProperty] private bool _booleanValue;
}
