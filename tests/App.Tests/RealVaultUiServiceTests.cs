using App.Services;
using App.ViewModels;
using App.ViewModels.Models;
using Core.Enums;
using Core.Models;
using Core.Services;
using Core.Session;
using Crypto;
using Vault;
using Xunit;

namespace App.Tests;

public class RealVaultUiServiceTests
{
    [Fact]
    public void CipherDestinations_ApplyPersonalOwnershipAndCollectionWritePermissions()
    {
        var session = new VaultSession();
        session.BeginAccountSession("account", "me@example.com", new ServerOrigin("https://vault.example"));
        session.SetUnlockedKey(new SymmetricCryptoKey(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray()));
        session.SetSnapshot(new DecryptedVault(session.Account, [], [], 0, 0)
        {
            Organizations =
            [
                new VaultOrganization("org-1", "Team", 2, 2, true)
                {
                    EncryptedKey = "2.encrypted-key",
                },
            ],
            Collections =
            [
                new VaultCollection("writable", "org-1", "Shared", false, false, false, null),
                new VaultCollection("readonly", "org-1", "Archive", true, false, false, null),
            ],
            Policies =
            [
                new VaultOrganizationPolicy(
                    "policy-1",
                    "org-1",
                    (int)OrganizationPolicyType.PersonalOwnership,
                    true,
                    "{}"),
            ],
        });
        var service = new VaultUiService(
            new VaultService(session),
            new NoopWriteService(),
            new NoopSyncService(),
            session);

        var destination = Assert.Single(service.GetCipherDestinations());

        Assert.Equal("org-1", destination.OrganizationId);
        Assert.Null(destination.CollectionId);
        Assert.Contains("Team", destination.DisplayName);
        var collection = Assert.Single(service.GetCipherCollectionOptions("org-1"));
        Assert.Equal("writable", collection.Id);
    }

    [Fact]
    public void CipherDestinations_OrganizationOwnerStillRequiresWritableCollection()
    {
        var session = new VaultSession();
        session.BeginAccountSession("account", "me@example.com", new ServerOrigin("https://vault.example"));
        session.SetUnlockedKey(new SymmetricCryptoKey(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray()));
        session.SetSnapshot(new DecryptedVault(session.Account, [], [], 0, 0)
        {
            Organizations =
            [
                new VaultOrganization("org-1", "Team", 2, 0, true)
                {
                    EncryptedKey = "2.encrypted-key",
                },
            ],
            Collections =
            [
                new VaultCollection("writable", "org-1", "Shared", false, false, false, null),
            ],
        });
        var service = new VaultUiService(
            new VaultService(session),
            new NoopWriteService(),
            new NoopSyncService(),
            session);

        var destinations = service.GetCipherDestinations();

        Assert.Contains(destinations, option =>
            option.OrganizationId == "org-1" && option.CollectionId is null);
    }

    [Fact]
    public void ProviderOnlyOrganizationsAreReadOnlyGraphNodesWhileDualRoleMembersRemainVisible()
    {
        var session = new VaultSession();
        session.BeginAccountSession("account", "me@example.com", new ServerOrigin("https://vault.example"));
        session.SetUnlockedKey(new SymmetricCryptoKey(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray()));
        session.SetSnapshot(new DecryptedVault(session.Account, [], [], 0, 0)
        {
            Organizations =
            [
                new VaultOrganization("provider-only", "Managed Customer", 2, 2, true)
                {
                    EncryptedKey = "2.provider-key",
                    IsMember = false,
                    IsProviderUser = true,
                    IsProviderOrganization = true,
                },
                new VaultOrganization("dual-role", "My Managed Team", 2, 2, true)
                {
                    EncryptedKey = "2.dual-key",
                    IsMember = true,
                    IsProviderUser = true,
                    IsProviderOrganization = true,
                },
            ],
            Collections =
            [
                new VaultCollection("provider-collection", "provider-only", "Provider", false, false, true, null),
                new VaultCollection("member-collection", "dual-role", "Member", false, false, true, null),
            ],
        });
        var service = new VaultUiService(
            new VaultService(session),
            new NoopWriteService(),
            new NoopSyncService(),
            session);

        var destinations = service.GetCipherDestinations();
        var filters = service.GetFilters();

        Assert.DoesNotContain(destinations, option => option.OrganizationId == "provider-only");
        Assert.Contains(destinations, option => option.OrganizationId == "dual-role");
        Assert.DoesNotContain(filters, filter => filter.OrganizationId == "provider-only");
        Assert.Contains(filters, filter => filter.OrganizationId == "dual-role");
        Assert.DoesNotContain(filters, filter => filter.CollectionId == "provider-collection");
        Assert.Contains(filters, filter => filter.CollectionId == "member-collection");
    }

    [Fact]
    public void CollectionOptions_LockAssignedReadOnlyCollectionButAllowWritableSelections()
    {
        var session = new VaultSession();
        session.BeginAccountSession("account", "me@example.com", new ServerOrigin("https://vault.example"));
        session.SetUnlockedKey(new SymmetricCryptoKey(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray()));
        session.SetSnapshot(new DecryptedVault(session.Account, [], [], 0, 0)
        {
            Collections =
            [
                new VaultCollection("writable", "org-1", "Shared", false, false, false, null),
                new VaultCollection("readonly", "org-1", "Archive", true, false, false, null),
            ],
        });
        var service = new VaultUiService(
            new VaultService(session),
            new NoopWriteService(),
            new NoopSyncService(),
            session);

        var options = service.GetCipherCollectionOptions(
            "org-1",
            ["writable", "readonly"],
            lockReadOnlyAssignments: true);

        var writable = options.Single(option => option.Id == "writable");
        var readOnly = options.Single(option => option.Id == "readonly");
        Assert.True(writable.IsSelected);
        Assert.True(writable.CanSelect);
        Assert.True(readOnly.IsSelected);
        Assert.False(readOnly.CanSelect);
    }

    [Fact]
    public void GetItemsAndFilters_MapSnapshotCounts()
    {
        var service = new VaultUiService(new TestVaultService(), new NoopWriteService(), new NoopSyncService());

        var items = service.GetItems();
        var filters = service.GetFilters();

        Assert.Contains(items, item => item.Kind == VaultItemKind.Login && item.Name == "GitHub" && item.Favorite);
        Assert.Contains(items, item => item.Kind == VaultItemKind.Card);
        Assert.Contains(items, item => item.Kind == VaultItemKind.Identity);
        Assert.Contains(items, item => item.Kind == VaultItemKind.Note);
        Assert.Contains(items, item => item.Kind == VaultItemKind.Ssh);
        Assert.Contains(items, item => item.IsDeleted);

        Assert.Equal(5, filters.Single(f => f.Kind == FilterKind.AllItems).Count);
        Assert.Equal(1, filters.Single(f => f.Kind == FilterKind.Favorites).Count);
        Assert.Equal(1, filters.Single(f => f.Kind == FilterKind.Trash).Count);
        Assert.Equal(1, filters.Single(f => f.Kind == FilterKind.Folder && f.FolderId == "f1").Count);
        Assert.Equal(1, filters.Single(f => f.Kind == FilterKind.Type && f.TypeFilter == VaultItemKind.Ssh).Count);
    }

    [Fact]
    public void GetDetail_MapsAllCipherTypes()
    {
        var service = new VaultUiService(new TestVaultService(), new NoopWriteService(), new NoopSyncService());

        var login = Assert.IsType<LoginDetail>(service.GetDetail("login", includeSensitive: true));
        Assert.Equal("octo", login.Username);
        Assert.Equal("pw", login.Password);
        Assert.Equal("otpauth", login.TotpSecret);
        Assert.Equal("https://github.com", login.Uri);
        Assert.Equal("Work", login.FolderName);
        Assert.True(login.Reprompt);
        Assert.True(Assert.Single(login.CustomFields).IsSecret);
        var passkey = Assert.Single(login.Passkeys);
        Assert.Equal("github.com", passkey.RpId);
        Assert.Equal("Octo Cat", passkey.DisplayName);
        Assert.Equal("是", passkey.DiscoverableText);

        var card = Assert.IsType<CardDetail>(service.GetDetail("card", includeSensitive: true));
        Assert.Equal("Jane", card.Cardholder);
        Assert.Equal("4111", card.Number);
        Assert.Equal("12/2030", card.Expiry);
        Assert.Equal("Visa", card.Brand);
        Assert.Equal("123", card.Cvv);

        var identity = Assert.IsType<IdentityDetail>(service.GetDetail("identity", includeSensitive: true));
        Assert.Equal("Jane Doe", identity.FullName);
        Assert.Equal("jane@example.com", identity.Email);
        Assert.Equal("123-45-6789", identity.IdNumber);
        Assert.Equal("Road 1, Beijing, China", identity.Address);

        var note = Assert.IsType<NoteDetail>(service.GetDetail("note", includeSensitive: true));
        Assert.Equal("note body", note.Content);

        var ssh = Assert.IsType<SshDetail>(service.GetDetail("ssh", includeSensitive: true));
        Assert.Equal("public", ssh.PublicKey);
        Assert.Equal("private", ssh.PrivateKey);
        Assert.Equal("SHA256:fp", ssh.Fingerprint);
    }

    [Fact]
    public void GetItems_LoginWithUri_SetsIconDomain()
    {
        var vault = new IconDomainVaultService(new[]
        {
            new Cipher
            {
                Id = "1", Type = CipherType.Login, Name = "A",
                Login = new CipherLogin("u", "p", null, new[] { new CipherLoginUri("https://www.example.com/x", null) }),
            },
            new Cipher
            {
                Id = "2", Type = CipherType.Card, Name = "B",
                Card = new CipherCard(null, null, null, null, null, null),
            },
        });
        var service = new VaultUiService(vault, new NoopWriteService(), new NoopSyncService());

        var items = service.GetItems();

        Assert.Equal("www.example.com", items.First(i => i.Id == "1").IconDomain);
        Assert.Null(items.First(i => i.Id == "2").IconDomain);
    }

    [Fact]
    public void GetDetail_Login_MapsPasswordHistory()
    {
        var vault = new IconDomainVaultService(new[]
        {
            new Cipher
            {
                Id = "1", Type = CipherType.Login, Name = "A",
                Login = new CipherLogin("u", "cur", null, Array.Empty<CipherLoginUri>()),
                PasswordHistory = new[] { new PasswordHistoryEntry("old1", new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero)) },
            },
        });
        var svc = new VaultUiService(vault, new NoopWriteService(), new NoopSyncService());

        var detail = Assert.IsType<LoginDetail>(svc.GetDetail("1", includeSensitive: true));

        Assert.True(detail.HasPasswordHistory);
        Assert.Equal(1, detail.PasswordHistoryCount);
        Assert.Equal("old1", detail.PasswordHistory[0].Password);
    }

    [Fact]
    public void OrganizationVault_ExposesFiltersAndHonorsViewPasswordPermission()
    {
        var cipher = new Cipher
        {
            Id = "org-item",
            Type = CipherType.Login,
            OrganizationId = "org-1",
            CollectionIds = ["collection-1"],
            Name = "Shared login",
            CanEdit = false,
            CanViewPassword = false,
            CanDelete = false,
            CanRestore = false,
            Login = new CipherLogin("shared-user", "shared-password", null, []),
        };
        var vault = new IconDomainVaultService(
            [cipher],
            [new VaultOrganization("org-1", "Team", 2, 2, true)],
            [new VaultCollection("collection-1", "org-1", "Engineering", true, true, false, null)]);
        var service = new VaultUiService(vault, new NoopWriteService(), new NoopSyncService());

        var item = Assert.Single(service.GetItems());
        Assert.Equal("org-1", item.OrganizationId);
        Assert.False(item.CanModify);
        Assert.False(item.CanClone);
        var filters = service.GetFilters();
        Assert.Contains(filters, filter =>
            filter.Kind == FilterKind.Organization && filter.OrganizationId == "org-1");
        Assert.Contains(filters, filter =>
            filter.Kind == FilterKind.Collection && filter.CollectionId == "collection-1");

        var detail = Assert.IsType<LoginDetail>(service.GetDetail("org-item", includeSensitive: true));
        Assert.Equal("shared-user", detail.Username);
        Assert.Null(detail.Password);
        Assert.False(detail.CanEdit);
    }

    [Fact]
    public void OrganizationVault_EditableCipherEnablesContentEditing()
    {
        var cipher = new Cipher
        {
            Id = "org-editable",
            Type = CipherType.Login,
            OrganizationId = "org-1",
            CollectionIds = ["collection-1"],
            Name = "Shared login",
            CanEdit = true,
            CanViewPassword = true,
            CanDelete = true,
            CanRestore = true,
            Login = new CipherLogin("shared-user", "shared-password", null, []),
        };
        var vault = new IconDomainVaultService(
            [cipher],
            [new VaultOrganization("org-1", "Team", 2, 2, true)],
            [new VaultCollection("collection-1", "org-1", "Engineering", false, false, false, null)]);
        var service = new VaultUiService(vault, new NoopWriteService(), new NoopSyncService());

        var item = Assert.Single(service.GetItems());

        Assert.True(item.CanModify);
        Assert.True(item.CanDelete);
        Assert.True(service.GetDetail(item.Id, includeSensitive: true).CanEdit);
    }

    [Fact]
    public void OrganizationVault_HiddenPasswordsDisableEditingCloningAndDraftMaterialization()
    {
        var cipher = new Cipher
        {
            Id = "org-hidden",
            Type = CipherType.Login,
            OrganizationId = "org-1",
            CollectionIds = ["managed"],
            Name = "Shared login",
            Notes = "protected-note",
            CanEdit = true,
            CanViewPassword = false,
            Login = new CipherLogin("shared-user", "shared-password", "protected-totp", []),
            Fields = [new CipherField("Secret", "protected-custom-value", CipherFieldType.Hidden)],
        };
        var vault = new IconDomainVaultService(
            [cipher],
            [new VaultOrganization("org-1", "Team", 2, 2, true)],
            [new VaultCollection("managed", "org-1", "Managed", false, false, true, null)]);
        var service = new VaultUiService(vault, new NoopWriteService(), new NoopSyncService());

        var item = Assert.Single(service.GetItems());

        Assert.False(item.CanModify);
        Assert.False(item.CanClone);
        Assert.False(item.CanViewPassword);
        Assert.False(item.HasTotp);
        Assert.Contains("shared-user", item.SearchHaystack);
        Assert.DoesNotContain("protected-note", item.SearchHaystack);
        Assert.DoesNotContain("protected-custom-value", item.SearchHaystack);
        Assert.Throws<OrganizationCipherOperationException>(() => service.GetDraft(item.Id));
    }

    [Fact]
    public void GetItems_DocumentSubtitlesUseOnlyNonSensitiveMetadata()
    {
        var ciphers = new Cipher[]
        {
            new()
            {
                Id = "bank", Type = CipherType.BankAccount, Name = "Bank",
                BankAccount = new CipherBankAccount("Example Bank", "Ada", "Checking", "secret-account", "secret-routing", "secret-branch", "secret-pin", "secret-swift", "secret-iban", "555"),
            },
            new()
            {
                Id = "license", Type = CipherType.DriversLicense, Name = "License",
                DriversLicense = new CipherDriversLicense("Ada", "M", "Lovelace", "secret-birth", "secret-license-number", "GB", "London", "secret-issued", "secret-expires", "DVLA", "B"),
            },
            new()
            {
                Id = "passport", Type = CipherType.Passport, Name = "Passport",
                Passport = new CipherPassport("Lovelace", "Ada", "secret-birth", "F", "London", "British", "GB", "secret-passport-number", "P", "secret-national-id", "HMPO", "secret-issued", "secret-expires"),
            },
        };
        var service = new VaultUiService(
            new IconDomainVaultService(ciphers),
            new NoopWriteService(),
            new NoopSyncService());

        var items = service.GetItems();
        var bank = items.Single(item => item.Id == "bank");
        var license = items.Single(item => item.Id == "license");
        var passport = items.Single(item => item.Id == "passport");

        Assert.Equal("Example Bank", bank.Subtitle);
        Assert.Equal("Ada M Lovelace · London, GB", license.Subtitle);
        Assert.Equal("Ada Lovelace · GB", passport.Subtitle);
        var visibleSubtitles = string.Join('|', items.Select(item => item.Subtitle));
        Assert.DoesNotContain("secret-account", visibleSubtitles);
        Assert.DoesNotContain("secret-license-number", visibleSubtitles);
        Assert.DoesNotContain("secret-passport-number", visibleSubtitles);
    }

    [Fact]
    public void OrganizationVault_CloneRequiresManagePermissionOnAnAssignedCollection()
    {
        var cipher = new Cipher
        {
            Id = "org-item",
            Type = CipherType.Login,
            OrganizationId = "org-1",
            CollectionIds = ["managed"],
            Name = "Shared login",
            Login = new CipherLogin("user", "password", null, []),
        };
        var vault = new IconDomainVaultService(
            [cipher],
            [new VaultOrganization("org-1", "Team", 2, 2, true)],
            [new VaultCollection("managed", "org-1", "Managed", false, false, true, null)]);

        var item = Assert.Single(new VaultUiService(
            vault,
            new NoopWriteService(),
            new NoopSyncService()).GetItems());

        Assert.True(item.CanClone);
    }

    [Theory]
    [InlineData(CipherType.BankAccount)]
    [InlineData(CipherType.DriversLicense)]
    [InlineData(CipherType.Passport)]
    public void CipherTypesWithoutNegotiatedWriteSupportAreReadOnly(CipherType type)
    {
        var cipher = new Cipher { Id = "unsupported", Type = type, Name = "Read only" };
        var service = new VaultUiService(
            new IconDomainVaultService([cipher]),
            new NoopWriteService(),
            new NoopSyncService());

        var item = Assert.Single(service.GetItems());

        Assert.False(item.CanModify);
        Assert.False(item.CanClone);
        Assert.False(service.GetDetail(item.Id, includeSensitive: true).CanEdit);
    }

    [Theory]
    [InlineData(CipherType.BankAccount)]
    [InlineData(CipherType.DriversLicense)]
    [InlineData(CipherType.Passport)]
    public void NegotiatedNewCipherTypesAreEditable(CipherType type)
    {
        var service = NewCapabilityService(
            new VaultCapabilities(true, false, false),
            NewCipherType(type));

        var item = Assert.Single(service.GetItems());

        Assert.True(item.CanModify);
        Assert.True(item.CanClone);
        Assert.True(service.GetDetail(item.Id, includeSensitive: true).CanEdit);
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, true, true)]
    public void ArchiveActionsReflectProtocolAndEntitlement(
        bool supportsProtocol,
        bool hasEntitlement,
        bool canArchive,
        bool canUnarchive)
    {
        var service = NewCapabilityService(
            new VaultCapabilities(false, supportsProtocol, hasEntitlement),
            new Cipher
            {
                Id = "login",
                Type = CipherType.Login,
                Name = "Login",
                Login = new CipherLogin(null, null, null, []),
            });

        var item = Assert.Single(service.GetItems());

        Assert.Equal(canArchive, item.CanArchive);
        Assert.Equal(canUnarchive, item.CanUnarchive);
    }

    [Fact]
    public void GetDetail_NewCipherTypes_MapsAndMasksSensitiveFields()
    {
        var ciphers = new[]
        {
            new Cipher { Id = "b", Type = CipherType.BankAccount, Name = "Bank", Reprompt = true, BankAccount = new CipherBankAccount("Example", "Ada", "Checking", "123", "456", "001", "7890", "SWIFT", "IBAN", "555") },
            new Cipher { Id = "d", Type = CipherType.DriversLicense, Name = "License", Reprompt = true, DriversLicense = new CipherDriversLicense("Ada", "M", "Lovelace", "1815", "DL-1", "GB", "London", "2025", "2030", "DVLA", "B") },
            new Cipher { Id = "p", Type = CipherType.Passport, Name = "Passport", Reprompt = true, Passport = new CipherPassport("Lovelace", "Ada", "1815", "F", "London", "British", "GB", "P-1", "P", "N-1", "HMPO", "2025", "2035") },
        };
        var service = new VaultUiService(
            new IconDomainVaultService(ciphers),
            new NoopWriteService(),
            new NoopSyncService());

        Assert.Equal("123", Assert.IsType<BankAccountDetail>(service.GetDetail("b", true)).AccountNumber);
        Assert.Equal("DL-1", Assert.IsType<DriversLicenseDetail>(service.GetDetail("d", true)).LicenseNumber);
        Assert.Equal("P-1", Assert.IsType<PassportDetail>(service.GetDetail("p", true)).PassportNumber);
        Assert.Null(Assert.IsType<BankAccountDetail>(service.GetDetail("b", false)).AccountNumber);
        Assert.Null(Assert.IsType<DriversLicenseDetail>(service.GetDetail("d", false)).LicenseNumber);
        Assert.Null(Assert.IsType<PassportDetail>(service.GetDetail("p", false)).PassportNumber);
    }

    [Fact]
    public void GetDetail_LinkedFields_ResolveValuesAndRemainSensitive()
    {
        var vault = new IconDomainVaultService(new[]
        {
            new Cipher
            {
                Id = "1",
                Type = CipherType.Login,
                Name = "A",
                Reprompt = true,
                Login = new CipherLogin("user", "password", null, []),
                Fields =
                [
                    new CipherField("Linked username", null, CipherFieldType.Linked, LinkedId: 100),
                    new CipherField("Linked password", null, CipherFieldType.Linked, LinkedId: 101),
                    new CipherField("Future", "opaque", CipherFieldType.Unsupported, RawType: 97),
                ],
            },
        });
        var service = new VaultUiService(vault, new NoopWriteService(), new NoopSyncService());

        var locked = Assert.IsType<LoginDetail>(service.GetDetail("1", includeSensitive: false));
        Assert.All(locked.CustomFields, field => Assert.Equal(string.Empty, field.Value));

        var unlocked = Assert.IsType<LoginDetail>(service.GetDetail("1", includeSensitive: true));
        Assert.Equal("user", unlocked.CustomFields[0].Value);
        Assert.Equal("password", unlocked.CustomFields[1].Value);
        Assert.Equal("opaque", unlocked.CustomFields[2].Value);
        Assert.All(unlocked.CustomFields, field => Assert.True(field.IsSecret));
    }

    private static VaultUiService NewCapabilityService(
        VaultCapabilities capabilities,
        params Cipher[] ciphers)
    {
        var session = new VaultSession();
        var account = new AccountInfo(
            "account",
            "me@example.com",
            new ServerOrigin("https://vault.example"),
            "User",
            string.Empty);
        session.BeginAccountSession(account.Id, account.Email, account.Endpoints!);
        session.SetUnlockedKey(new SymmetricCryptoKey(
            Enumerable.Range(0, 64).Select(i => (byte)i).ToArray()));
        session.SetSnapshot(
            new DecryptedVault(account, [], ciphers, 0, 0),
            capabilities: capabilities);
        return new VaultUiService(
            new VaultService(session),
            new NoopWriteService(),
            new NoopSyncService(),
            session);
    }

    private static Cipher NewCipherType(CipherType type) => type switch
    {
        CipherType.BankAccount => new Cipher
        {
            Id = "bank",
            Type = type,
            Name = "Bank",
            BankAccount = new CipherBankAccount(null, null, null, null, null, null, null, null, null, null),
        },
        CipherType.DriversLicense => new Cipher
        {
            Id = "license",
            Type = type,
            Name = "License",
            DriversLicense = new CipherDriversLicense(null, null, null, null, null, null, null, null, null, null, null),
        },
        CipherType.Passport => new Cipher
        {
            Id = "passport",
            Type = type,
            Name = "Passport",
            Passport = new CipherPassport(null, null, null, null, null, null, null, null, null, null, null, null, null),
        },
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    private sealed class IconDomainVaultService : IVaultService
    {
        private readonly IReadOnlyList<Cipher> _ciphers;
        private readonly IReadOnlyList<VaultOrganization> _organizations;
        private readonly IReadOnlyList<VaultCollection> _collections;

        public IconDomainVaultService(
            IReadOnlyList<Cipher> ciphers,
            IReadOnlyList<VaultOrganization>? organizations = null,
            IReadOnlyList<VaultCollection>? collections = null)
        {
            _ciphers = ciphers;
            _organizations = organizations ?? [];
            _collections = collections ?? [];
        }
        public Core.Session.IVaultSnapshot Snapshot { get; } = new VaultSession();
        public IReadOnlyList<Cipher> GetCiphers() => _ciphers;
        public IReadOnlyList<Folder> GetFolders() => System.Array.Empty<Folder>();
        public IReadOnlyList<DeviceInfo> GetDevices() => System.Array.Empty<DeviceInfo>();
        public IReadOnlyList<VaultOrganization> GetOrganizations() => _organizations;
        public IReadOnlyList<VaultCollection> GetCollections() => _collections;
        public VaultSessionLease CaptureLease() =>
            throw new InvalidOperationException("This fake has no vault session.");
        public bool IsCurrent(VaultSessionLease lease) => false;
        public void ThrowIfExpired(VaultSessionLease lease) =>
            throw new OperationCanceledException(lease.CancellationToken);
        public IReadOnlyList<Cipher> GetCiphers(VaultSessionLease lease) =>
            throw new OperationCanceledException(lease.CancellationToken);
    }

    private sealed class TestVaultService : IVaultService
    {
        public IVaultSnapshot Snapshot { get; } = new TestSnapshot();
        public IReadOnlyList<Cipher> GetCiphers() => Snapshot.Ciphers;
        public IReadOnlyList<Folder> GetFolders() => Snapshot.Folders;
        public IReadOnlyList<DeviceInfo> GetDevices() => Snapshot.Devices;
        public VaultSessionLease CaptureLease() =>
            throw new InvalidOperationException("This fake has no vault session.");
        public bool IsCurrent(VaultSessionLease lease) => false;
        public void ThrowIfExpired(VaultSessionLease lease) =>
            throw new OperationCanceledException(lease.CancellationToken);
        public IReadOnlyList<Cipher> GetCiphers(VaultSessionLease lease) =>
            throw new OperationCanceledException(lease.CancellationToken);
    }

    private sealed class TestSnapshot : IVaultSnapshot
    {
        public VaultState State => VaultState.Unlocked;
        public AccountInfo Account { get; } = new("test-account", "me@example.com", new ServerOrigin("https://vault.example"), "M", "PBKDF2 600000");
        public IReadOnlyList<Folder> Folders { get; } =
        [
            new Folder { Id = "f1", Name = "Work", RevisionDate = DateTimeOffset.Parse("2026-06-24T00:00:00Z") },
        ];
        public IReadOnlyList<DeviceInfo> Devices { get; } = [];
        public IReadOnlyList<Cipher> Ciphers { get; } =
        [
            new Cipher
            {
                Id = "login",
                Type = CipherType.Login,
                Name = "GitHub",
                Notes = "login notes",
                FolderId = "f1",
                Favorite = true,
                Reprompt = true,
                CreationDate = DateTimeOffset.Parse("2026-06-01T00:00:00Z"),
                RevisionDate = DateTimeOffset.Parse("2026-06-02T00:00:00Z"),
                Login = new CipherLogin("octo", "pw", "otpauth", [new CipherLoginUri("https://github.com", null)])
                {
                    Fido2Credentials =
                    [
                        new CipherFido2Credential("credential-id", "public-key", "ECDSA", "P-256", "private-key",
                            "github.com", "user-handle", "octo@example.com", 1, "GitHub", "Octo Cat", true,
                            DateTimeOffset.Parse("2026-06-01T00:00:00Z")),
                    ],
                },
                Fields = [new CipherField("Recovery", "secret", CipherFieldType.Hidden)],
            },
            new Cipher
            {
                Id = "card",
                Type = CipherType.Card,
                Name = "Visa",
                CreationDate = DateTimeOffset.Parse("2026-06-01T00:00:00Z"),
                RevisionDate = DateTimeOffset.Parse("2026-06-02T00:00:00Z"),
                Card = new CipherCard("Jane", "4111", "12", "2030", "123", "Visa"),
            },
            new Cipher
            {
                Id = "identity",
                Type = CipherType.Identity,
                Name = "Identity",
                CreationDate = DateTimeOffset.Parse("2026-06-01T00:00:00Z"),
                RevisionDate = DateTimeOffset.Parse("2026-06-02T00:00:00Z"),
                Identity = new CipherIdentity(null, "Jane", null, "Doe", null, null, "123-45-6789", null, null,
                    "jane@example.com", null, "Road 1", null, null, "Beijing", null, null, "China"),
            },
            new Cipher
            {
                Id = "note",
                Type = CipherType.SecureNote,
                Name = "Note",
                Notes = "note body",
                CreationDate = DateTimeOffset.Parse("2026-06-01T00:00:00Z"),
                RevisionDate = DateTimeOffset.Parse("2026-06-02T00:00:00Z"),
                SecureNote = new CipherSecureNote(0),
            },
            new Cipher
            {
                Id = "ssh",
                Type = CipherType.SshKey,
                Name = "SSH",
                CreationDate = DateTimeOffset.Parse("2026-06-01T00:00:00Z"),
                RevisionDate = DateTimeOffset.Parse("2026-06-02T00:00:00Z"),
                Ssh = new CipherSsh("private", "public", "SHA256:fp"),
            },
            new Cipher
            {
                Id = "deleted",
                Type = CipherType.Login,
                Name = "Deleted",
                DeletedDate = DateTimeOffset.Parse("2026-06-10T00:00:00Z"),
                Login = new CipherLogin("old", null, null, []),
            },
        ];

        public int SkippedCipherCount => 0;
        public bool DataStale => false;
        public long Generation => 0;
        public int SkippedFolderCount => 0;
    }
}

public class RealVaultUiServiceWriteTests
{
    [Fact]
    public async Task SaveCipherAsync_Create_SendsEmptyIdAndReturnsServerAssignedId()
    {
        var vault = new MutableVaultService();
        var write = new RecordingWriteService(vault);
        var service = new VaultUiService(vault, write, new NoopSyncService());
        var draft = new CipherEditorDraft { Type = VaultItemKind.Login, Name = "New" };

        var id = await service.SaveCipherAsync(draft, editingId: null);

        Assert.Equal("", write.LastSavedCipher!.Id);   // create = empty id on the wire
        Assert.Equal("created-1", id);                 // returned by the authoritative create response
    }

    [Fact]
    public async Task SaveCipherAsync_CreateWithoutReturnedId_FailsClosed()
    {
        var vault = new MutableVaultService();
        var write = new RecordingWriteService(vault)
        {
            CreatedCipherId = null,
        };
        var service = new VaultUiService(vault, write, new NoopSyncService());
        var draft = new CipherEditorDraft { Type = VaultItemKind.Login, Name = "New" };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.SaveCipherAsync(draft, editingId: null));
    }

    [Fact]
    public async Task SyncAndReconcileCipherCreate_DelegatesSamePendingToWriteService()
    {
        var vault = new MutableVaultService();
        var write = new RecordingWriteService(vault);
        var session = new VaultSession();
        session.BeginAccountSession("account-1", "test@example.com", ServerEndpoints.BitwardenUs);
        var service = new VaultUiService(vault, write, new NoopSyncService(), session);
        var pending = new CipherCreatePending(
            new CipherCreateWitness(
                "account-1",
                ServerEndpoints.BitwardenUs,
                CipherType.Login,
                null,
                [],
                ["existing-id"],
                "A1B2C3"),
            CommitConfirmed: false,
            ExpectedCipherId: null);
        var expected = new CipherCreateReconciliationResult(
            CipherCreateReconciliationStatus.NotFound,
            null,
            []);
        write.Reconciliation = expected;
        write.PendingCreate = pending;

        var actual = await service.SyncAndReconcileCipherCreateAsync(
            pending,
            CancellationToken.None);

        Assert.Same(pending, write.LastReconciledPending);
        Assert.Same(expected, actual);
        Assert.Same(pending, service.GetPendingCipherCreate());
    }

    [Fact]
    public void PendingCreateState_DelegatesToWriteService()
    {
        var vault = new MutableVaultService();
        var write = new RecordingWriteService(vault);
        var session = new VaultSession();
        session.BeginAccountSession("account-a", "a@example.com", ServerEndpoints.BitwardenUs);
        var service = new VaultUiService(vault, write, new NoopSyncService(), session);
        var pendingA = new CipherCreatePending(
            new CipherCreateWitness(
                "account-a",
                ServerEndpoints.BitwardenUs,
                CipherType.Login,
                null,
                [],
                [],
                "AA11"),
            CommitConfirmed: false,
            ExpectedCipherId: null);

        write.PendingCreate = pendingA;

        Assert.Same(pendingA, service.GetPendingCipherCreate());
        Assert.True(service.HasPendingOrInFlightCipherCreate());
    }

    [Fact]
    public async Task ConcurrentCreatesAcrossViewModels_AreReservedBeforeDispatchAndPendingHasNoReleaseWindow()
    {
        var vault = new MutableVaultService();
        var session = new VaultSession();
        session.BeginAccountSession("account-1", "test@example.com", ServerEndpoints.BitwardenUs);
        var pending = new CipherCreatePending(
            new CipherCreateWitness(
                "account-1",
                ServerEndpoints.BitwardenUs,
                CipherType.Login,
                null,
                [],
                [],
                "CREATE-FINGERPRINT"),
            CommitConfirmed: false,
            ExpectedCipherId: null);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var write = new RecordingWriteService(vault)
        {
            CreateEntered = entered,
            CreateRelease = release,
            CreateFailure = new CipherCreateOutcomeUncertainException(
                "Create outcome is uncertain.",
                pending,
                new HttpRequestException("Response was lost."),
                new HttpRequestException("Refresh failed.")),
        };
        var service = new VaultUiService(vault, write, new NoopSyncService(), session);
        var first = new VaultViewModel(service);
        var second = new VaultViewModel(service);
        first.BeginAdd(VaultItemKind.Login);
        second.BeginAdd(VaultItemKind.Card);
        first.EditorDraft!.Name = "First";
        second.EditorDraft!.Name = "Second";
        var secondDraft = second.EditorDraft;

        var firstSave = first.SaveDraftAsync();
        await entered.Task;
        var replacement = new VaultViewModel(service);

        Assert.True(service.HasPendingOrInFlightCipherCreate());
        Assert.False(second.CanBeginCipherCreate);
        Assert.False(second.CanSaveEditorDraft);
        Assert.True(second.CanEditEditorDraft);
        Assert.False(replacement.CanBeginCipherCreate);
        replacement.BeginAdd(VaultItemKind.Identity);
        Assert.False(replacement.IsEditing);
        Assert.Null(replacement.EditorDraft);

        var secondSave = await second.SaveDraftAsync();

        Assert.False(secondSave.IsSuccess);
        Assert.Contains("另一个新建操作", secondSave.Error ?? string.Empty);
        Assert.Equal(1, write.CreateCalls);

        release.SetResult();
        var firstResult = await firstSave;

        Assert.False(firstResult.IsSuccess);
        Assert.Equal(1, write.CreateCalls);
        Assert.Equal(pending, service.GetPendingCipherCreate());
        Assert.True(first.HasPendingCipherCreate);
        Assert.True(second.HasPendingCipherCreate);
        Assert.True(replacement.HasPendingCipherCreate);
        Assert.False(first.CanSaveEditorDraft);
        Assert.False(second.CanSaveEditorDraft);
        Assert.Same(secondDraft, second.EditorDraft);
    }

    [Fact]
    public async Task SaveCipherAsync_Edit_PreservesPasskeysAndReturnsEditingId()
    {
        var vault = new MutableVaultService();
        var passkey = new CipherFido2Credential("cred", "kt", "alg", "P-256", "kv", "github.com", "uh", "u", 1, "GitHub", "Octo", true, DateTimeOffset.UnixEpoch);
        vault.Ciphers.Add(new Cipher
        {
            Id = "edit-1", Type = CipherType.Login, Name = "Old",
            Login = new CipherLogin("octo", null, null, System.Array.Empty<CipherLoginUri>()) { Fido2Credentials = new[] { passkey } },
        });
        var write = new RecordingWriteService(vault);
        var service = new VaultUiService(vault, write, new NoopSyncService());
        var draft = service.GetDraft("edit-1");
        draft.Name = "Renamed";

        var id = await service.SaveCipherAsync(draft, editingId: "edit-1");

        Assert.Equal("edit-1", id);
        Assert.Equal("edit-1", write.LastSavedCipher!.Id);
        Assert.Equal("Renamed", write.LastSavedCipher.Name);
        Assert.Equal("cred", Assert.Single(write.LastSavedCipher.Login!.Fido2Credentials).CredentialId);
    }

    [Fact]
    public async Task SaveCipherAsync_EditWithChangedSnapshotRevision_IsRejectedBeforeWrite()
    {
        var openedRevision = DateTimeOffset.Parse("2026-08-01T12:00:00Z");
        var vault = new MutableVaultService();
        vault.Ciphers.Add(new Cipher
        {
            Id = "edit-1",
            Type = CipherType.Login,
            Name = "Opened",
            RevisionDate = openedRevision,
            Login = new CipherLogin("user", "secret", null, []),
        });
        var write = new RecordingWriteService(vault);
        var service = new VaultUiService(vault, write, new NoopSyncService());
        var draft = service.GetDraft("edit-1");
        draft.Name = "Local edit";
        vault.Ciphers[0] = new Cipher
        {
            Id = "edit-1",
            Type = CipherType.Login,
            Name = "Remote edit",
            RevisionDate = openedRevision.AddSeconds(1),
            Login = new CipherLogin("remote", "remote-secret", null, []),
        };

        await Assert.ThrowsAsync<CipherEditConflictException>(() =>
            service.SaveCipherAsync(draft, editingId: "edit-1"));

        Assert.Null(write.LastSavedCipher);
        Assert.Equal(openedRevision, draft.EditBaseline!.RevisionDate);
    }

    [Fact]
    public async Task SaveCipherAsync_EditRemovedAfterOpen_IsRejectedInsteadOfCreatingReplacement()
    {
        var vault = new MutableVaultService();
        vault.Ciphers.Add(new Cipher
        {
            Id = "edit-1",
            Type = CipherType.Login,
            Name = "Opened",
            RevisionDate = DateTimeOffset.Parse("2026-08-01T12:00:00Z"),
            Login = new CipherLogin("user", "secret", null, []),
        });
        var write = new RecordingWriteService(vault);
        var service = new VaultUiService(vault, write, new NoopSyncService());
        var draft = service.GetDraft("edit-1");
        vault.Ciphers.Clear();

        await Assert.ThrowsAsync<CipherEditConflictException>(() =>
            service.SaveCipherAsync(draft, editingId: "edit-1"));

        Assert.Null(write.LastSavedCipher);
        Assert.Empty(vault.Ciphers);
    }

    [Fact]
    public async Task SaveCipherAsync_HiddenPasswordEditIsRejectedBeforeWrite()
    {
        var vault = new MutableVaultService();
        vault.Ciphers.Add(new Cipher
        {
            Id = "hidden",
            Type = CipherType.Login,
            OrganizationId = "org-1",
            Name = "Protected",
            CanEdit = true,
            CanViewPassword = false,
            Login = new CipherLogin("user", "server-secret", "server-totp", []),
        });
        var write = new RecordingWriteService(vault);
        var service = new VaultUiService(vault, write, new NoopSyncService());
        var forgedDraft = new CipherEditorDraft { Type = VaultItemKind.Login, Name = "Overwrite" };

        await Assert.ThrowsAsync<OrganizationCipherOperationException>(() =>
            service.SaveCipherAsync(forgedDraft, editingId: "hidden"));

        Assert.Null(write.LastSavedCipher);
        Assert.Equal("server-secret", vault.Ciphers.Single().Login!.Password);
    }

    [Fact]
    public async Task DeleteRestoreFolderSync_DelegateToWriteService()
    {
        var vault = new MutableVaultService();
        var write = new RecordingWriteService(vault);
        var sync = new NoopSyncService();
        var service = new VaultUiService(vault, write, sync);

        await service.DeleteCipherAsync("x", permanent: true);
        await service.RestoreCipherAsync("y");
        await service.SaveFolderAsync(null, "Docs");
        await service.DeleteFolderAsync("f1");
        await service.SyncAsync();

        Assert.Equal(("x", true), write.LastDelete);
        Assert.Equal("y", write.LastRestore);
        Assert.Equal((null, "Docs"), write.LastFolderSave);
        Assert.Equal("f1", write.LastFolderDelete);
        Assert.Equal(1, sync.SyncCount);
    }

    [Fact]
    public async Task MoveCiphersAsync_DelegatesToWriteService()
    {
        var vault = new MutableVaultService();
        var write = new RecordingWriteService(vault);
        var service = new VaultUiService(vault, write, new NoopSyncService());

        await service.MoveCiphersAsync(new[] { "a", "b" }, "f1");

        Assert.Equal("move", write.LastOp);
        Assert.Equal(new[] { "a", "b" }, write.LastIds);
        Assert.Equal("f1", write.LastFolderId);
    }

    [Fact]
    public async Task DeleteCiphersAsync_Permanent_DelegatesWithFlag()
    {
        var vault = new MutableVaultService();
        var write = new RecordingWriteService(vault);
        var service = new VaultUiService(vault, write, new NoopSyncService());

        await service.DeleteCiphersAsync(new[] { "a" }, permanent: true);

        Assert.Equal("delete", write.LastOp);
        Assert.Equal(new[] { "a" }, write.LastIds);
        Assert.True(write.LastPermanent);
    }

    [Fact]
    public async Task RestoreCiphersAsync_Delegates()
    {
        var vault = new MutableVaultService();
        var write = new RecordingWriteService(vault);
        var service = new VaultUiService(vault, write, new NoopSyncService());

        await service.RestoreCiphersAsync(new[] { "a" });

        Assert.Equal("restore", write.LastOp);
        Assert.Equal(new[] { "a" }, write.LastIds);
    }

    [Fact]
    public async Task PlacementOperations_DelegateImmutableBaselineAndCollections()
    {
        var cipher = new Cipher
        {
            Id = "cipher-1",
            Type = CipherType.Login,
            Name = "Personal",
            RevisionDate = DateTimeOffset.Parse("2026-08-01T12:00:00Z"),
            Login = new CipherLogin("user", "secret", null, []),
        };
        var vault = new MutableVaultService();
        vault.Ciphers.Add(cipher);
        var write = new RecordingWriteService(vault);
        var service = new VaultUiService(vault, write, new NoopSyncService());
        var baseline = service.GetCipherPlacementBaseline(cipher.Id);

        await service.TransferCipherToOrganizationAsync(
            baseline,
            "org-1",
            ["collection-1", "collection-2"]);
        await service.SetCipherCollectionsAsync(
            baseline,
            ["collection-2"]);
        await service.TransferCiphersToOrganizationAsync(
            [baseline],
            "org-1",
            ["collection-1"]);
        await service.BulkUpdateCipherCollectionsAsync(
            [baseline],
            "org-1",
            ["collection-2"],
            removeCollections: true);

        Assert.Same(baseline, write.LastTransfer!.Value.Baseline);
        Assert.Equal(["collection-1", "collection-2"], write.LastTransfer.Value.CollectionIds);
        Assert.Same(baseline, write.LastCollectionUpdate!.Value.Baseline);
        Assert.Equal(["collection-2"], write.LastCollectionUpdate.Value.CollectionIds);
        Assert.Equal([baseline], write.LastBulkTransfer!.Value.Baselines);
        Assert.Equal("org-1", write.LastBulkTransfer.Value.OrganizationId);
        Assert.Equal(["collection-1"], write.LastBulkTransfer.Value.CollectionIds);
        Assert.Equal([baseline], write.LastBulkCollectionUpdate!.Value.Baselines);
        Assert.True(write.LastBulkCollectionUpdate.Value.RemoveCollections);
    }

    [Fact]
    public async Task SaveCipher_UnchangedPassword_PreservesHistory()
    {
        var original = new Cipher
        {
            Id = "1", Type = CipherType.Login, Name = "A",
            Login = new CipherLogin("u", "samePass", null, System.Array.Empty<CipherLoginUri>()),
            PasswordHistory = new[] { new PasswordHistoryEntry("old1", DateTimeOffset.UtcNow.AddDays(-1)) },
        };
        var vault = new MutableVaultService();
        vault.Ciphers.Add(original);
        var write = new RecordingWriteService(vault);
        var service = new VaultUiService(vault, write, new NoopSyncService());

        // 从原始 cipher 构造草稿(密码未变),模拟仅改收藏/改名等非密码编辑。
        var draft = CipherDraftMapper.ToDraft(original);
        await service.SaveCipherAsync(draft, "1");

        // 关键断言:未改密码的保存,写入服务收到的 Cipher 仍带完整历史(不能被静默清空)。
        var sent = write.LastSavedCipher!;
        Assert.Single(sent.PasswordHistory);
        Assert.Equal("old1", sent.PasswordHistory[0].Password);
    }

}

file sealed class MutableVaultService : IVaultService
{
    public List<Cipher> Ciphers { get; } = new();
    public List<Folder> Folders { get; } = new();
    public Core.Session.IVaultSnapshot Snapshot { get; } = new VaultSession();
    public IReadOnlyList<Cipher> GetCiphers() => Ciphers;
    public IReadOnlyList<Folder> GetFolders() => Folders;
    public IReadOnlyList<DeviceInfo> GetDevices() => System.Array.Empty<DeviceInfo>();
    public Core.Session.VaultSessionLease CaptureLease() =>
        throw new InvalidOperationException("This fake has no vault session.");
    public bool IsCurrent(Core.Session.VaultSessionLease lease) => false;
    public void ThrowIfExpired(Core.Session.VaultSessionLease lease) =>
        throw new OperationCanceledException(lease.CancellationToken);
    public IReadOnlyList<Cipher> GetCiphers(Core.Session.VaultSessionLease lease) =>
        throw new OperationCanceledException(lease.CancellationToken);
}

file sealed class RecordingWriteService : IVaultWriteService
{
    private readonly MutableVaultService _vault;
    private readonly object _createGate = new();
    private bool _createInFlight;
    public RecordingWriteService(MutableVaultService vault) => _vault = vault;

    public Cipher? LastSavedCipher { get; private set; }
    public (string Id, bool Permanent)? LastDelete { get; private set; }
    public string? LastRestore { get; private set; }
    public (string? Id, string Name)? LastFolderSave { get; private set; }
    public string? LastFolderDelete { get; private set; }

    // Bulk operation recording
    public string? LastOp { get; private set; }
    public IReadOnlyCollection<string>? LastIds { get; private set; }
    public string? LastFolderId { get; private set; }
    public bool? LastPermanent { get; private set; }
    public (CipherPlacementBaseline Baseline, string OrganizationId, IReadOnlyCollection<string> CollectionIds)? LastTransfer { get; private set; }
    public (CipherPlacementBaseline Baseline, IReadOnlyCollection<string> CollectionIds)? LastCollectionUpdate { get; private set; }
    public (IReadOnlyCollection<CipherPlacementBaseline> Baselines, string OrganizationId, IReadOnlyCollection<string> CollectionIds)? LastBulkTransfer { get; private set; }
    public (IReadOnlyCollection<CipherPlacementBaseline> Baselines, string OrganizationId, IReadOnlyCollection<string> CollectionIds, bool RemoveCollections)? LastBulkCollectionUpdate { get; private set; }
    public CipherCreatePending? LastReconciledPending { get; private set; }
    public CipherCreateReconciliationResult? Reconciliation { get; set; }
    public string? CreatedCipherId { get; set; } = "created-1";
    public int CreateCalls { get; private set; }
    public TaskCompletionSource? CreateEntered { get; set; }
    public TaskCompletionSource? CreateRelease { get; set; }
    public Exception? CreateFailure { get; set; }
    private CipherCreatePending? _pendingCreate;
    public CipherCreatePending? PendingCreate
    {
        get
        {
            lock (_createGate)
                return _pendingCreate;
        }
        set
        {
            lock (_createGate)
                _pendingCreate = value;
        }
    }
    public event EventHandler? CipherCreateGateChanged;

    public Task SaveCipherAsync(Cipher cipher, CancellationToken ct = default)
    {
        LastSavedCipher = cipher;
        if (string.IsNullOrEmpty(cipher.Id))
            _vault.Ciphers.Add(new Cipher { Id = "created-1", Type = cipher.Type, Name = cipher.Name });
        return Task.CompletedTask;
    }

    public async Task<string?> SaveCipherAndGetIdAsync(
        Cipher cipher,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(cipher.Id))
        {
            await SaveCipherAsync(cipher, ct);
            return cipher.Id;
        }

        lock (_createGate)
        {
            if (PendingCreate is not null || _createInFlight)
                throw new CipherCreatePendingOperationException();
            _createInFlight = true;
        }
        CipherCreateGateChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            CreateCalls++;
            CreateEntered?.TrySetResult();
            if (CreateRelease is not null)
                await CreateRelease.Task;
            if (CreateFailure is not null)
            {
                if (CreateFailure is CipherCreateOutcomeUncertainException uncertain)
                    PendingCreate = uncertain.Pending;
                else if (CreateFailure is CipherCreateSnapshotMismatchException mismatch)
                    PendingCreate = mismatch.Pending;
                CipherCreateGateChanged?.Invoke(this, EventArgs.Empty);
                throw CreateFailure;
            }

            await SaveCipherAsync(cipher, ct);
            return CreatedCipherId;
        }
        finally
        {
            lock (_createGate)
                _createInFlight = false;
            CipherCreateGateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public CipherCreatePending? GetPendingCipherCreate() => PendingCreate;

    public bool HasPendingOrInFlightCipherCreate()
    {
        lock (_createGate)
            return PendingCreate is not null || _createInFlight;
    }

    public Task<CipherCreateReconciliationResult> SyncAndReconcileCipherCreateAsync(
        CipherCreatePending pending,
        CancellationToken ct = default)
    {
        LastReconciledPending = pending;
        var result = Reconciliation
            ?? throw new InvalidOperationException("No create reconciliation was configured.");
        if (result.Status == CipherCreateReconciliationStatus.Confirmed)
        {
            PendingCreate = null;
            CipherCreateGateChanged?.Invoke(this, EventArgs.Empty);
        }
        return Task.FromResult(result);
    }

    public Task DeleteCipherAsync(string cipherId, bool permanent, CancellationToken ct = default)
    {
        LastDelete = (cipherId, permanent);
        return Task.CompletedTask;
    }

    public Task RestoreCipherAsync(string cipherId, CancellationToken ct = default)
    {
        LastRestore = cipherId;
        return Task.CompletedTask;
    }

    public Task SaveFolderAsync(string? folderId, string name, CancellationToken ct = default)
    {
        LastFolderSave = (folderId, name);
        return Task.CompletedTask;
    }

    public Task DeleteFolderAsync(string folderId, CancellationToken ct = default)
    {
        LastFolderDelete = folderId;
        return Task.CompletedTask;
    }

    public Task MoveCiphersAsync(IReadOnlyCollection<string> ids, string? folderId, CancellationToken ct = default)
    {
        LastOp = "move";
        LastIds = ids;
        LastFolderId = folderId;
        return Task.CompletedTask;
    }

    public Task DeleteCiphersAsync(IReadOnlyCollection<string> ids, bool permanent, CancellationToken ct = default)
    {
        LastOp = "delete";
        LastIds = ids;
        LastPermanent = permanent;
        return Task.CompletedTask;
    }

    public Task RestoreCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default)
    {
        LastOp = "restore";
        LastIds = ids;
        return Task.CompletedTask;
    }

    public Task TransferCipherToOrganizationAsync(
        CipherPlacementBaseline baseline,
        string organizationId,
        IReadOnlyCollection<string> collectionIds,
        CancellationToken ct = default)
    {
        LastTransfer = (baseline, organizationId, collectionIds);
        return Task.CompletedTask;
    }

    public Task SetCipherCollectionsAsync(
        CipherPlacementBaseline baseline,
        IReadOnlyCollection<string> collectionIds,
        CancellationToken ct = default)
    {
        LastCollectionUpdate = (baseline, collectionIds);
        return Task.CompletedTask;
    }

    public Task TransferCiphersToOrganizationAsync(
        IReadOnlyCollection<CipherPlacementBaseline> baselines,
        string organizationId,
        IReadOnlyCollection<string> collectionIds,
        CancellationToken ct = default)
    {
        LastBulkTransfer = (baselines, organizationId, collectionIds);
        return Task.CompletedTask;
    }

    public Task BulkUpdateCipherCollectionsAsync(
        IReadOnlyCollection<CipherPlacementBaseline> baselines,
        string organizationId,
        IReadOnlyCollection<string> collectionIds,
        bool removeCollections,
        CancellationToken ct = default)
    {
        LastBulkCollectionUpdate = (baselines, organizationId, collectionIds, removeCollections);
        return Task.CompletedTask;
    }
}

file sealed class NoopWriteService : IVaultWriteService
{
    public Task SaveCipherAsync(Cipher cipher, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeleteCipherAsync(string cipherId, bool permanent, CancellationToken ct = default) => Task.CompletedTask;
    public Task RestoreCipherAsync(string cipherId, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveFolderAsync(string? folderId, string name, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeleteFolderAsync(string folderId, CancellationToken ct = default) => Task.CompletedTask;
    public Task MoveCiphersAsync(IReadOnlyCollection<string> ids, string? folderId, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeleteCiphersAsync(IReadOnlyCollection<string> ids, bool permanent, CancellationToken ct = default) => Task.CompletedTask;
    public Task RestoreCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default) => Task.CompletedTask;
}

file sealed class NoopSyncService : ISyncService
{
    public int SyncCount { get; private set; }
    public Task<IReadOnlyList<Cipher>> SyncAsync(CancellationToken ct = default)
    {
        SyncCount++;
        return Task.FromResult<IReadOnlyList<Cipher>>(System.Array.Empty<Cipher>());
    }

    public Task<IReadOnlyList<Cipher>> SyncAsync(
        Core.Session.VaultSessionLease lease, CancellationToken ct = default) => SyncAsync(ct);
}
