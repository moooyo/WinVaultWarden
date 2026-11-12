using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Core.Enums;

namespace App.ViewModels.Models;

public enum CipherEditorFieldType
{
    Text,
    Hidden,
    Boolean,
    Linked,
    Unsupported,
}

public sealed class CipherEditBaseline
{
    private readonly IReadOnlyList<string> _collectionIds;

    public CipherEditBaseline(
        string cipherId,
        CipherType cipherType,
        DateTimeOffset revisionDate,
        string? organizationId,
        string? key,
        IEnumerable<string> collectionIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cipherId);
        ArgumentNullException.ThrowIfNull(collectionIds);

        CipherId = cipherId;
        CipherType = cipherType;
        RevisionDate = revisionDate;
        OrganizationId = organizationId;
        Key = key;
        _collectionIds = Array.AsReadOnly(collectionIds.ToArray());
    }

    public string CipherId { get; }
    public CipherType CipherType { get; }
    public DateTimeOffset RevisionDate { get; }
    public string? OrganizationId { get; }
    public string? Key { get; }
    public IReadOnlyList<string> CollectionIds => _collectionIds;
}

public sealed partial class CipherEditorDraft : ObservableObject
{
    public CipherEditBaseline? EditBaseline { get; private set; }

    [ObservableProperty]
    public partial VaultItemKind Type { get; set; }

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? FolderId { get; set; }

    [ObservableProperty]
    public partial string? OrganizationId { get; set; }

    [ObservableProperty]
    public partial string? CollectionId { get; set; }

    public ObservableCollection<string> CollectionIds { get; } = new();

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
    public BankAccountEditorDraft BankAccount { get; } = new();
    public DriversLicenseEditorDraft DriversLicense { get; } = new();
    public PassportEditorDraft Passport { get; } = new();
    public ObservableCollection<CustomFieldEditorDraft> CustomFields { get; } = new();

    public bool IsLogin => Type == VaultItemKind.Login;
    public bool IsCard => Type == VaultItemKind.Card;
    public bool IsIdentity => Type == VaultItemKind.Identity;
    public bool IsSecureNote => Type == VaultItemKind.Note;
    public bool IsSshKey => Type == VaultItemKind.Ssh;
    public bool IsBankAccount => Type == VaultItemKind.BankAccount;
    public bool IsDriversLicense => Type == VaultItemKind.DriversLicense;
    public bool IsPassport => Type == VaultItemKind.Passport;

    public static CipherEditorDraft CreateDefault(VaultItemKind type) => new() { Type = type };

    internal void SetEditBaseline(CipherEditBaseline baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        if (EditBaseline is not null)
            throw new InvalidOperationException("The edit baseline has already been captured.");
        EditBaseline = baseline;
    }

    public void ReplaceCollectionIds(IEnumerable<string> collectionIds)
    {
        ArgumentNullException.ThrowIfNull(collectionIds);
        CollectionIds.Clear();
        foreach (var collectionId in collectionIds)
        {
            if (!string.IsNullOrWhiteSpace(collectionId))
                CollectionIds.Add(collectionId);
        }
        CollectionId = CollectionIds.FirstOrDefault();
    }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(Name))
            errors.Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text6A88C361CBDA48A7));

        if (!string.IsNullOrWhiteSpace(OrganizationId))
        {
            if (CollectionIds.Count == 0 && string.IsNullOrWhiteSpace(CollectionId))
                errors.Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text973F96163ED9F3D9));
            if (CollectionIds.Distinct(StringComparer.Ordinal).Count() != CollectionIds.Count)
                errors.Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text5703FA02FE6E40C9));
        }

        if (Type == VaultItemKind.Ssh)
        {
            if (string.IsNullOrWhiteSpace(SshKey.PrivateKey))
                errors.Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextBC2775F74A9BA05C));
            if (string.IsNullOrWhiteSpace(SshKey.PublicKey))
                errors.Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text7E84CC4F12012CC1));
            if (string.IsNullOrWhiteSpace(SshKey.KeyFingerprint))
                errors.Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextBBD2E77499D1327F));
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
        OnPropertyChanged(nameof(IsBankAccount));
        OnPropertyChanged(nameof(IsDriversLicense));
        OnPropertyChanged(nameof(IsPassport));
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
    [NotifyPropertyChangedFor(nameof(MatchIndex))]
    [NotifyPropertyChangedFor(nameof(MatchSummary))]
    public partial int? Match { get; set; }

    public int MatchIndex
    {
        get => Match is null ? 0 : Match is >= 0 and <= 5 ? Match.Value + 1 : -1;
        set
        {
            // Unsupported server values must survive an untouched form.
            if (value is >= 0 and <= 6)
                Match = value == 0 ? null : value - 1;
        }
    }

    public string MatchSummary => Match switch
    {
        null => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextFF5C3B1DD0B13451),
        0 => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text3B65A1F4FFF42BF4),
        1 => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text7E69F1EAB51DCC3C),
        2 => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextE000AD805D71B27D),
        3 => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text3F2CC439D29B760B),
        4 => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text73CEB6F830AD385F),
        5 => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text4F539898C61FB248),
        _ => global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.TextD9B9FB8A9578A716,Match),
    };
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

public sealed partial class BankAccountEditorDraft : ObservableObject
{
    [ObservableProperty] public partial string BankName { get; set; } = string.Empty;
    [ObservableProperty] public partial string NameOnAccount { get; set; } = string.Empty;
    [ObservableProperty] public partial string AccountType { get; set; } = string.Empty;
    [ObservableProperty] public partial string AccountNumber { get; set; } = string.Empty;
    [ObservableProperty] public partial string RoutingNumber { get; set; } = string.Empty;
    [ObservableProperty] public partial string BranchNumber { get; set; } = string.Empty;
    [ObservableProperty] public partial string Pin { get; set; } = string.Empty;
    [ObservableProperty] public partial string SwiftCode { get; set; } = string.Empty;
    [ObservableProperty] public partial string Iban { get; set; } = string.Empty;
    [ObservableProperty] public partial string BankContactPhone { get; set; } = string.Empty;
}

public sealed partial class DriversLicenseEditorDraft : ObservableObject
{
    [ObservableProperty] public partial string FirstName { get; set; } = string.Empty;
    [ObservableProperty] public partial string MiddleName { get; set; } = string.Empty;
    [ObservableProperty] public partial string LastName { get; set; } = string.Empty;
    [ObservableProperty] public partial string DateOfBirth { get; set; } = string.Empty;
    [ObservableProperty] public partial string LicenseNumber { get; set; } = string.Empty;
    [ObservableProperty] public partial string IssuingCountry { get; set; } = string.Empty;
    [ObservableProperty] public partial string IssuingState { get; set; } = string.Empty;
    [ObservableProperty] public partial string IssueDate { get; set; } = string.Empty;
    [ObservableProperty] public partial string ExpirationDate { get; set; } = string.Empty;
    [ObservableProperty] public partial string IssuingAuthority { get; set; } = string.Empty;
    [ObservableProperty] public partial string LicenseClass { get; set; } = string.Empty;
}

public sealed partial class PassportEditorDraft : ObservableObject
{
    [ObservableProperty] public partial string Surname { get; set; } = string.Empty;
    [ObservableProperty] public partial string GivenName { get; set; } = string.Empty;
    [ObservableProperty] public partial string DateOfBirth { get; set; } = string.Empty;
    [ObservableProperty] public partial string Sex { get; set; } = string.Empty;
    [ObservableProperty] public partial string BirthPlace { get; set; } = string.Empty;
    [ObservableProperty] public partial string Nationality { get; set; } = string.Empty;
    [ObservableProperty] public partial string IssuingCountry { get; set; } = string.Empty;
    [ObservableProperty] public partial string PassportNumber { get; set; } = string.Empty;
    [ObservableProperty] public partial string PassportType { get; set; } = string.Empty;
    [ObservableProperty] public partial string NationalIdentificationNumber { get; set; } = string.Empty;
    [ObservableProperty] public partial string IssuingAuthority { get; set; } = string.Empty;
    [ObservableProperty] public partial string IssueDate { get; set; } = string.Empty;
    [ObservableProperty] public partial string ExpirationDate { get; set; } = string.Empty;
}

public sealed partial class CustomFieldEditorDraft : ObservableObject
{
    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial CipherEditorFieldType Type { get; set; } = CipherEditorFieldType.Text;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TextValue))]
    [NotifyPropertyChangedFor(nameof(HiddenValue))]
    public partial string Value { get; set; } = string.Empty;

    public string TextValue
    {
        get => IsText ? Value : string.Empty;
        set { if (IsText) Value = value; }
    }

    public string HiddenValue
    {
        get => IsHidden ? Value : string.Empty;
        set { if (IsHidden) Value = value; }
    }

    [ObservableProperty]
    public partial bool BooleanValue { get; set; }

    [ObservableProperty]
    public partial int? LinkedId { get; set; }

    public int? RawType { get; init; }

    public bool PreserveWhenNameBlank { get; init; }

    public int TypeIndex
    {
        get => Type switch
        {
            CipherEditorFieldType.Hidden => 1,
            CipherEditorFieldType.Boolean => 2,
            CipherEditorFieldType.Linked => 3,
            CipherEditorFieldType.Unsupported => 4,
            _ => 0,
        };
        set
        {
            var selectedType = value switch
            {
                0 => CipherEditorFieldType.Text,
                1 => CipherEditorFieldType.Hidden,
                2 => CipherEditorFieldType.Boolean,
                3 when LinkedId is not null => CipherEditorFieldType.Linked,
                _ => (CipherEditorFieldType?)null,
            };

            if (selectedType is { } type)
                Type = type;
        }
    }

    public bool IsText => Type == CipherEditorFieldType.Text;
    public bool IsHidden => Type == CipherEditorFieldType.Hidden;
    public bool IsBoolean => Type == CipherEditorFieldType.Boolean;
    public bool IsLinked => Type == CipherEditorFieldType.Linked;
    public bool IsUnsupported => Type == CipherEditorFieldType.Unsupported;

    partial void OnTypeChanged(CipherEditorFieldType value)
    {
        OnPropertyChanged(nameof(TypeIndex));
        OnPropertyChanged(nameof(IsText));
        OnPropertyChanged(nameof(IsHidden));
        OnPropertyChanged(nameof(IsBoolean));
        OnPropertyChanged(nameof(IsLinked));
        OnPropertyChanged(nameof(IsUnsupported));
        OnPropertyChanged(nameof(TextValue));
        OnPropertyChanged(nameof(HiddenValue));
    }
}
