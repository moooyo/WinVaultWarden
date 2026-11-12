using App.ViewModels.Models;
using Core.Enums;
using Core.Models;
using Core.Session;
using Vault;

namespace App.Services;

/// <summary>
/// Holds emergency-access plaintext only while its recipient's unlocked session is current.
/// The item list contains metadata; values are projected only for the selected, authorized item.
/// </summary>
public sealed class RecoveredVaultViewSession : IDisposable, ISensitiveState
{
    private readonly object _gate = new();
    private readonly VaultSession _session;
    private readonly VaultSessionLease _lease;
    private readonly IClipboardService _clipboard;
    private readonly CancellationTokenRegistration _cancellation;
    private Cipher?[] _ciphers;
    private RecoveredCipherListItem[] _items;
    private RecoveredCipherField[] _fields = [];
    private int _selectedIndex = -1;
    private long _selectionVersion;
    private bool _authorized;
    private bool _cleared;

    public RecoveredVaultViewSession(
        VaultSession session,
        VaultSessionLease lease,
        RecoveredVault recovered,
        IClipboardService clipboard)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(recovered);
        ArgumentNullException.ThrowIfNull(clipboard);
        _session = session;
        _lease = lease;
        _clipboard = clipboard;
        _ciphers = recovered.Ciphers.Cast<Cipher?>().ToArray();
        _items = recovered.Ciphers.Select(RecoveredCipherListItem.FromCipher).ToArray();
        _cancellation = lease.CancellationToken.Register(ClearSensitive);
        if (!session.IsUnlocked(lease))
            ClearSensitive();
    }

    public event Action? Invalidated;

    public IReadOnlyList<RecoveredCipherListItem> Items
    {
        get { lock (_gate) return _items; }
    }

    public IReadOnlyList<RecoveredCipherField> Fields
    {
        get { lock (_gate) return _fields; }
    }

    public bool RequiresVerification
    {
        get
        {
            lock (_gate)
                return !_cleared && SelectedCipherNoLock() is { Reprompt: true } && !_authorized;
        }
    }

    public bool Select(int index)
    {
        var selected = false;
        _session.TryRunIfUnlocked(_lease, () =>
        {
            lock (_gate)
            {
                if (_cleared || index < 0 || index >= _ciphers.Length || _ciphers[index] is not { } cipher)
                    return;
                ClearFieldsNoLock();
                _selectedIndex = index;
                _selectionVersion++;
                _authorized = !cipher.Reprompt;
                if (_authorized)
                    _fields = ProjectFields(cipher);
                selected = true;
            }
        });
        return selected;
    }

    /// <summary>
    /// Verifies the recipient's current account before opening a reprompt-protected recovered item.
    /// A delayed answer cannot authorize another selection or a new account session.
    /// </summary>
    public async Task<bool> VerifySelectionAsync(
        Func<CancellationToken, Task<bool>> verify,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(verify);
        long version = -1;
        var needsVerification = true;
        if (!_session.TryRunIfUnlocked(_lease, () =>
        {
            lock (_gate)
            {
                if (_cleared || SelectedCipherNoLock() is null)
                    return;
                version = _selectionVersion;
                needsVerification = !_authorized;
            }
        }) || version < 0 || ct.IsCancellationRequested)
            return false;
        if (!needsVerification)
            return true;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lease.CancellationToken);
        try
        {
            if (!await verify(linked.Token).ConfigureAwait(false) || linked.IsCancellationRequested)
                return false;
            var authorized = false;
            _session.TryRunIfUnlocked(_lease, () =>
            {
                lock (_gate)
                {
                    if (_cleared || linked.IsCancellationRequested || version != _selectionVersion
                        || SelectedCipherNoLock() is not { } cipher)
                        return;
                    _authorized = true;
                    _fields = ProjectFields(cipher);
                    authorized = true;
                }
            });
            return authorized;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            return false;
        }
    }

    public bool ToggleReveal(RecoveredCipherField expectedField)
    {
        ArgumentNullException.ThrowIfNull(expectedField);
        var changed = false;
        _session.TryRunIfUnlocked(_lease, () =>
        {
            lock (_gate)
            {
                if (!IsCurrentFieldNoLock(expectedField) || !expectedField.IsSecret)
                    return;
                expectedField.ToggleReveal();
                changed = true;
            }
        });
        return changed;
    }

    public bool Copy(RecoveredCipherField expectedField)
    {
        ArgumentNullException.ThrowIfNull(expectedField);
        var copied = false;
        _session.TryRunIfUnlocked(_lease, () =>
        {
            lock (_gate)
            {
                if (!IsCurrentFieldNoLock(expectedField))
                    return;
                if (expectedField.IsSecret)
                    _clipboard.SetSecretText(expectedField.CopyValue);
                else
                    _clipboard.SetText(expectedField.CopyValue);
                copied = true;
            }
        });
        return copied;
    }

    private bool IsCurrentFieldNoLock(RecoveredCipherField field) =>
        !_cleared && _authorized && Array.IndexOf(_fields, field) >= 0;

    private Cipher? SelectedCipherNoLock() =>
        _selectedIndex >= 0 && _selectedIndex < _ciphers.Length ? _ciphers[_selectedIndex] : null;

    public void ClearSensitive()
    {
        var changed = false;
        lock (_gate)
        {
            if (_cleared)
                return;
            _cleared = true;
            ClearFieldsNoLock();
            Array.Clear(_ciphers);
            _ciphers = [];
            _items = [];
            _selectedIndex = -1;
            _selectionVersion++;
            _authorized = false;
            changed = true;
        }
        if (changed && Invalidated is { } invalidated)
            foreach (Action handler in invalidated.GetInvocationList())
                try { handler(); }
                catch { /* An observer must not prevent sensitive-state cleanup. */ }
    }

    private void ClearFieldsNoLock()
    {
        foreach (var field in _fields)
            field.Clear();
        _fields = [];
    }

    public void Dispose()
    {
        ClearSensitive();
        _cancellation.Dispose();
    }

    private static RecoveredCipherField[] ProjectFields(Cipher cipher)
    {
        var fields = new List<RecoveredCipherField>();
        void Add(string label, string? value, bool secret = false)
        {
            if (!string.IsNullOrEmpty(value) && (!secret || cipher.CanViewPassword))
                fields.Add(new(label, value, secret));
        }

        switch (cipher.Type)
        {
            case CipherType.Login when cipher.Login is { } login:
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextE3B89E9D33F88E52), login.Username);
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextE7CF3EF4F17C3999), login.Password, true);
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text09791D0B7C4BEE84), login.Totp, true);
                foreach (var uri in login.Uris)
                    Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB5A229AC8BECC603), uri.Uri);
                foreach (var passkey in login.Fido2Credentials)
                {
                    Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextCAAB28C7FD945B1A), passkey.RpId);
                    Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text96B35384FED857D2), passkey.UserDisplayName ?? passkey.UserName);
                }
                break;
            case CipherType.Card when cipher.Card is { } card:
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextC6C63EAE30590071), card.CardholderName);
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text090ED4316F1D99FD), card.Brand);
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text816FCA150C81AF71), card.Number, true);
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2D5B735EBD9B97AF), card.ExpMonth);
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text3085AC77D8FF1F5A), card.ExpYear);
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text125926006A27EF0D), card.Code, true);
                break;
            case CipherType.Identity when cipher.Identity is { } identity:
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text7E8CD2056DA73A7F), identity.Title);
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text702EF921ED1D89BF), identity.FirstName);
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text11453CA9F34A71BC), identity.MiddleName);
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text7B4888049459F04A), identity.LastName);
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextE3B89E9D33F88E52), identity.Username);
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextDE4743C879734DC3), identity.Company);
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text46F5E212FF2BC02E), identity.Ssn, true);
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text0B5CB6AC48B3B65C), identity.PassportNumber, true);
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextAB62B58FA8A9B81E), identity.LicenseNumber, true);
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text969CCBD3CF6300EC), identity.Email);
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text63DCEB8800B2DD4A), identity.Phone);
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextEE760B6E81B981E2), identity.Address1);
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text350B2B85C4AF1724), identity.Address2);
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text272E14F3A7704556), identity.Address3);
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextFC33F73246F48295), identity.City);
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextA3B50C476732C740), identity.State);
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextAE53CEA8228BCFC3), identity.PostalCode);
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text701D021D08C54579), identity.Country);
                break;
            case CipherType.SshKey when cipher.Ssh is { } ssh:
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextBA7AF0B7046D3441), ssh.Fingerprint);
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text4EE252FB736EFFA2), ssh.PublicKey);
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text477BF990A2B3A489), ssh.PrivateKey, true);
                break;
            case CipherType.BankAccount when cipher.BankAccount is { } bank:
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text676C471BC8DC3D13), bank.BankName);
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text53477CA834FA478D), bank.NameOnAccount);
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextDAC7998FBA76F342), bank.AccountType);
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextF7573B7F5DB0B550), bank.AccountNumber, true);
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextC07A0BFFD713E09D), bank.RoutingNumber);
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text8FFBDECFD1D90113), bank.BranchNumber);
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text709E58ACB65EF4A5), bank.Pin, true);
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text11037C55CE93269F), bank.SwiftCode);
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text7E345C3BA789219B), bank.Iban, true);
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text5D95890423A543BA), bank.BankContactPhone);
                break;
            case CipherType.DriversLicense when cipher.DriversLicense is { } license:
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text702EF921ED1D89BF), license.FirstName);
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text11453CA9F34A71BC), license.MiddleName);
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text7B4888049459F04A), license.LastName);
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextEB4C4E239122D42D), license.DateOfBirth, true);
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextAB62B58FA8A9B81E), license.LicenseNumber, true);
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB195B09894D5FB5C), license.IssuingCountry);
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text675ECE8B2A16A6C9), license.IssuingState);
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextF0A6C4753ADFBD8C), license.IssueDate);
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text8CCC3FD43A9EE1FA), license.ExpirationDate);
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text4BDBB2A85118E1DB), license.IssuingAuthority);
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text1DAC95A6D6E6F199), license.LicenseClass);
                break;
            case CipherType.Passport when cipher.Passport is { } passport:
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextF762DA05F4D47AF5), passport.Surname);
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text71E454FE83950C08), passport.GivenName);
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextEB4C4E239122D42D), passport.DateOfBirth, true);
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text953DD6F2B46151DA), passport.Sex);
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text7DAFB93A1AB9A373), passport.BirthPlace);
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text8FB1B6A148C21813), passport.Nationality);
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB195B09894D5FB5C), passport.IssuingCountry);
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text0B5CB6AC48B3B65C), passport.PassportNumber, true);
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text394E2D901736B461), passport.PassportType);
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextAD3B764EE0F5174A), passport.NationalIdentificationNumber, true);
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text4BDBB2A85118E1DB), passport.IssuingAuthority);
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextF0A6C4753ADFBD8C), passport.IssueDate);
                Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text8CCC3FD43A9EE1FA), passport.ExpirationDate);
                break;
        }

        Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text8A7525B1492FB848), cipher.Notes, true);
        foreach (var field in cipher.Fields)
        {
            var value = field.Type == CipherFieldType.Linked ? ResolveLinked(cipher, field.LinkedId) : field.Value;
            Add(string.IsNullOrWhiteSpace(field.Name) ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text6163A41C0E5EE07F) : field.Name, value,
                field.Type is CipherFieldType.Hidden or CipherFieldType.Linked or CipherFieldType.Unsupported);
        }
        foreach (var history in cipher.PasswordHistory)
            Add(history.LastUsedDate is { } date ? global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text7091CDB6FF677EE9,date) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text07CAE13A2E7E1460), history.Password, true);
        foreach (var attachment in cipher.Attachments)
            Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text7C0FDE5442280BC0), attachment.FileName);
        return fields.ToArray();
    }

    private static string? ResolveLinked(Cipher cipher, int? id) => id switch
    {
        100 => cipher.Login?.Username,
        101 => cipher.Login?.Password,
        300 => cipher.Card?.CardholderName,
        301 => cipher.Card?.ExpMonth,
        302 => cipher.Card?.ExpYear,
        303 => cipher.Card?.Code,
        304 => cipher.Card?.Brand,
        305 => cipher.Card?.Number,
        400 => cipher.Identity?.Title,
        401 => cipher.Identity?.MiddleName,
        402 => cipher.Identity?.Address1,
        403 => cipher.Identity?.Address2,
        404 => cipher.Identity?.Address3,
        405 => cipher.Identity?.City,
        406 => cipher.Identity?.State,
        407 => cipher.Identity?.PostalCode,
        408 => cipher.Identity?.Country,
        409 => cipher.Identity?.Company,
        410 => cipher.Identity?.Email,
        411 => cipher.Identity?.Phone,
        412 => cipher.Identity?.Ssn,
        413 => cipher.Identity?.Username,
        414 => cipher.Identity?.PassportNumber,
        415 => cipher.Identity?.LicenseNumber,
        416 => cipher.Identity?.FirstName,
        417 => cipher.Identity?.LastName,
        418 => string.Join(" ", new[] { cipher.Identity?.FirstName, cipher.Identity?.MiddleName, cipher.Identity?.LastName }
            .Where(value => !string.IsNullOrWhiteSpace(value))),
        _ => null,
    };
}

/// <summary>A field exposes a masked display value until an explicit reveal action.</summary>
public sealed class RecoveredCipherField
{
    public const string SecretMask = "••••••••";
    private string _value;

    internal RecoveredCipherField(string label, string value, bool isSecret)
    {
        Label = label;
        _value = value;
        IsSecret = isSecret;
    }

    public string Label { get; }
    public bool IsSecret { get; }
    public bool IsRevealed { get; private set; }
    public string DisplayValue => _value.Length == 0 ? string.Empty : IsSecret && !IsRevealed ? SecretMask : _value;
    internal string CopyValue => _value;
    internal void ToggleReveal() => IsRevealed = !IsRevealed;
    internal void Clear() { _value = string.Empty; IsRevealed = false; }
    public override string ToString() => Label;
}
