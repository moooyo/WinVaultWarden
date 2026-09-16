using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Api;
using Api.Dtos;
using Core.Abstractions;
using Core.Enums;
using Core.Models;
using Core.Services;
using Crypto;
using Vault;

// Synthetic review data only. Never target a production service.
var verifyOnly = args.Contains("--verify-only", StringComparer.Ordinal);
var positionalArgs = args.Where(arg => arg != "--verify-only").ToArray();
var server = positionalArgs.ElementAtOrDefault(0) ?? "http://127.0.0.1:18083";
var secretDirectory = Path.GetFullPath(positionalArgs.ElementAtOrDefault(1) ?? ".secrets/ui-review");
if (new Uri(server).Host != "127.0.0.1")
    throw new InvalidOperationException("The review fixture must use the explicit loopback SSH tunnel.");
if (!verifyOnly) Directory.CreateDirectory(secretDirectory);
const string email = "review@winvaultwarden.example";
var password = LoadPassword("review");
var notices = new List<string>();
var ctx = new SeedContext(server);
await ctx.SignInAsync(email, password, "UI Review", allowRegistration: !verifyOnly);
Console.WriteLine("Authenticated synthetic review account.");

if (verifyOnly)
{
    await VerifyOnlyAsync();
    return;
}

var work = await Folder("Work");
var personal = await Folder("Personal");
var finance = await Folder("Finance");
await Folder("Empty folder");

Cipher Login(string name, string? folder, string username, string secret, string uri,
    bool favorite = false, string? totp = null) => new()
{
    Type = CipherType.Login, Name = name, FolderId = folder, Favorite = favorite,
    Login = new CipherLogin(username, secret, totp, [new CipherLoginUri(uri, null)]),
    Notes = "Synthetic UI review fixture. No real account or secret is represented."
};

await Add(Login("GitHub - engineering", work, "alex.review", "Correct#Review!9-Long-Sample", "https://github.com", true, "JBSWY3DPEHPK3PXP"));
await Add(Login("Google - personal", personal, "alex@example.com", "Synthetic!Mail#2026-Review", "https://accounts.google.com", true));
await Add(Login("Amazon - reused password", personal, "alex@example.com", "Summer2024", "https://amazon.com"));
await Add(Login("Reddit - reused password", personal, "alex-review", "Summer2024", "https://reddit.com"));
await Add(Login("Router - weak and insecure", personal, "admin", "admin", "http://192.0.2.1"));
await Add(Login("Legacy forum - archived", personal, "review.user", "Old!Sample#2026", "https://forum.example.com"));
await Add(Login("Expired trial - deleted", null, "alex@example.com", "Expired!Sample#2026", "https://trial.example.com"));
await Add(new Cipher
{
    Type = CipherType.Login, Name = "Contoso Cloud - detailed login", FolderId = work, Favorite = true, Reprompt = true,
    Login = new CipherLogin("alex.review@example.com", "Synthetic!Cloud#2026-Long", "JBSWY3DPEHPK3PXP",
        [new CipherLoginUri("https://cloud.example.com", 0), new CipherLoginUri("https://console.example.com", 1)])
        { PasswordRevisionDate = DateTimeOffset.UtcNow.AddDays(-12), AutofillOnPageLoad = false },
    Notes = "Review environment only.\nOwner: Platform team\nRecovery procedure: use the artificial recovery codes attached here.",
    Fields = [new CipherField("Tenant", "contoso-review", CipherFieldType.Text),
        new CipherField("Recovery code", "DEMO-0000-1111", CipherFieldType.Hidden),
        new CipherField("Approved", "true", CipherFieldType.Boolean)],
    PasswordHistory = [new PasswordHistoryEntry("Previous!Review#2025", DateTimeOffset.UtcNow.AddDays(-12))]
});
await Add(new Cipher { Type = CipherType.Card, Name = "Visa - review card", FolderId = finance, Favorite = true,
    Card = new CipherCard("ALEX REVIEW", "4111111111111111", "08", "2030", "123", "Visa"),
    Notes = "Public test card number. Never use for a real payment." });
await Add(new Cipher { Type = CipherType.Identity, Name = "Alex Review - identity", FolderId = personal,
    Identity = new CipherIdentity("Mx", "Alex", "Morgan", "Review", "alex.review", "Contoso Review",
        "000-00-0000", "TEST000000", "DEMO-000000", "alex@example.com", "+1 202 555 0100",
        "100 Example Avenue", "Suite 10", null, "Example City", "CA", "00000", "United States"),
    Notes = "Entirely fictional identity for layout review." });
await Add(new Cipher { Type = CipherType.SecureNote, Name = "Recovery plan - secure note", FolderId = personal,
    SecureNote = new CipherSecureNote(0), Favorite = true,
    Notes = "# Review recovery plan\n\n1. Contact the synthetic emergency contact.\n2. Inspect the attached review checklist.\n3. Confirm the vault state is shown correctly.\n\nNo operational credentials are included." });
await Add(new Cipher { Type = CipherType.SshKey, Name = "Review deployment - SSH key", FolderId = work,
    Ssh = new CipherSsh("-----BEGIN OPENSSH PRIVATE KEY-----\nUI-REVIEW-NONFUNCTIONAL-KEY\n-----END OPENSSH PRIVATE KEY-----",
        "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIReviewFixtureOnly review@example.com", "SHA256:ReviewFixtureNonfunctionalKey"),
    Notes = "Nonfunctional key-shaped text for UI review." });

var archived = ctx.Session.Ciphers.Single(c => c.Name == "Legacy forum - archived");
if (!archived.IsArchived) await ctx.Write.ArchiveCipherAsync(archived.Id);
var deleted = ctx.Session.Ciphers.Single(c => c.Name == "Expired trial - deleted");
if (!deleted.IsDeleted) await ctx.Write.DeleteCipherAsync(deleted.Id, false);

await Optional("Attachment", async () =>
{
    var cipher = ctx.Session.Ciphers.Single(c => c.Name == "Contoso Cloud - detailed login");
    if (cipher.Attachments.Count == 0)
    {
        var content = Encoding.UTF8.GetBytes("UI review attachment\nThis is synthetic content without real credentials.\n");
        await ctx.Attachments.UploadAsync(cipher.Id, new ReopenableFileSource("review-checklist.txt", content.Length,
            _ => ValueTask.FromResult<Stream>(new MemoryStream(content, writable: false))));
    }
});

await Optional("Sends", async () =>
{
    var existing = (await ctx.Sends.GetSendsAsync()).Select(s => s.Name).ToHashSet();
    if (!existing.Contains("Review handoff - text"))
        await ctx.SendWrite.SaveTextSendAsync(new SendDraftModel
        {
            Type = SendType.Text, Name = "Review handoff - text", TextContent = "Synthetic handoff note for UI review.",
            Notes = "Exercise the text Send details and editor.", DeletionDate = DateTimeOffset.UtcNow.AddDays(7),
            ExpirationDate = DateTimeOffset.UtcNow.AddDays(3), MaxAccessCount = 10, HideEmail = true
        }, ctx.Session.CaptureLease());
    if (!existing.Contains("Disabled review message"))
        await ctx.SendWrite.SaveTextSendAsync(new SendDraftModel
        {
            Type = SendType.Text, Name = "Disabled review message", TextContent = "Hidden synthetic message.", TextHidden = true,
            Disabled = true, DeletionDate = DateTimeOffset.UtcNow.AddDays(7), PasswordChange = new SendPasswordChange.Set("Synthetic-Send-Password")
        }, ctx.Session.CaptureLease());
    if (!existing.Contains("Review handoff - file"))
    {
        var bytes = Encoding.UTF8.GetBytes("Review handoff file. No real secrets.\n");
        await ctx.SendWrite.CreateFileSendAsync(new SendDraftModel
        {
            Type = SendType.File, Name = "Review handoff - file", FileName = "ui-review.txt", Notes = "File Send review fixture.",
            DeletionDate = DateTimeOffset.UtcNow.AddDays(7), MaxAccessCount = 5
        }, new SendFileSource("ui-review.txt", bytes.Length,
            _ => ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false))), ctx.Session.CaptureLease());
    }
});

await Optional("Organization and collection", async () =>
{
    if (!ctx.Session.Organizations.Any(o => o.Name == "Contoso Review"))
    {
        using var orgKey = new SymmetricCryptoKey(RandomNumberGenerator.GetBytes(64));
        var keyApi = ctx.Clients.GetAuthenticated<IEmergencyAccessApiClient>(ctx.Session.CaptureLease());
        var publicKey = await keyApi.GetPublicKeyAsync(ctx.Session.Account.Id);
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ctx.Session.AccessToken);
        var response = await http.PostAsJsonAsync(server + "/api/organizations", new
        {
            name = "Contoso Review", billingEmail = email, planType = 0,
            collectionName = ctx.Crypto.Encrypt(Encoding.UTF8.GetBytes("Engineering"), orgKey).ToString(),
            key = ctx.Crypto.EncryptRsa(orgKey.FullKey, Convert.FromBase64String(publicKey.PublicKey!)).ToString()
        });
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Organization creation returned {(int)response.StatusCode}.");
        await ctx.Sync.SyncAsync();
    }
    var org = ctx.Session.Organizations.Single(o => o.Name == "Contoso Review");
    var collection = ctx.Session.Collections.First(c => c.OrganizationId == org.Id);
    await Add(new Cipher { Type = CipherType.Login, Name = "Shared deployment console", OrganizationId = org.Id,
        CollectionIds = [collection.Id], Login = new CipherLogin("review-team", "Synthetic!Shared#2026", null,
            [new CipherLoginUri("https://deploy.example.com", null)]), Notes = "Shared synthetic organization item." });
});

await Optional("Emergency access", async () =>
{
    var contact = new SeedContext(server);
    const string contactEmail = "trusted-contact@winvaultwarden.example";
    await contact.SignInAsync(contactEmail, LoadPassword("trusted-contact"), "Jordan Review");
    var emergency = new EmergencyAccessService(ctx.Clients, ctx.Crypto, ctx.Decryptor, ctx.Session);
    var contacts = await emergency.GetTrustedAsync();
    if (!contacts.Any(c => c.Email == contactEmail))
        await emergency.InviteAsync(contactEmail, EmergencyAccessType.View, 3);
    contacts = await emergency.GetTrustedAsync();
    var accepted = contacts.FirstOrDefault(c => c.Email == contactEmail && c.Status == EmergencyAccessStatus.Accepted);
    if (accepted?.GranteeId is not null) await emergency.ConfirmAsync(accepted.Id, accepted.GranteeId);
    if (!contacts.Any(c => c.Email == "pending-contact@winvaultwarden.example"))
        await emergency.InviteAsync("pending-contact@winvaultwarden.example", EmergencyAccessType.Takeover, 7);

    var awaiting = new SeedContext(server);
    const string awaitingEmail = "accepted-contact@winvaultwarden.example";
    await awaiting.SignInAsync(awaitingEmail, LoadPassword("accepted-contact"), "Sam Review");
    if (!contacts.Any(c => c.Email == awaitingEmail))
        await emergency.InviteAsync(awaitingEmail, EmergencyAccessType.View, 7);

    if (!contact.Session.Ciphers.Any(c => c.Name == "Emergency recovery note"))
        await contact.Write.SaveCipherAsync(new Cipher { Type = CipherType.SecureNote, Name = "Emergency recovery note",
            Notes = "Synthetic emergency vault content for read-only recovery review.", SecureNote = new CipherSecureNote(0) });
    var contactEmergency = new EmergencyAccessService(contact.Clients, contact.Crypto, contact.Decryptor, contact.Session);
    if (!(await contactEmergency.GetTrustedAsync()).Any(c => c.Email == email))
        await contactEmergency.InviteAsync(email, EmergencyAccessType.View, 3);
    var incoming = (await contactEmergency.GetTrustedAsync()).Single(c => c.Email == email);
    if (incoming.Status == EmergencyAccessStatus.Accepted)
        await contactEmergency.ConfirmAsync(incoming.Id, ctx.Session.Account.Id);
    var granted = (await emergency.GetGrantedAsync()).Single(c => c.Id == incoming.Id);
    if (granted.Status == EmergencyAccessStatus.Confirmed) await emergency.InitiateAsync(granted.Id);
    granted = (await emergency.GetGrantedAsync()).Single(c => c.Id == incoming.Id);
    if (granted.Status == EmergencyAccessStatus.RecoveryInitiated) await contactEmergency.ApproveAsync(granted.Id);
    await emergency.ViewAsync(granted.Id, contactEmail);
});

await Optional("Additional devices", async () =>
{
    var master = ctx.Crypto.DeriveMasterKey(password, email, KdfType.Pbkdf2, 600_000, null, null);
    try
    {
        var hash = ctx.Crypto.ComputeMasterPasswordHash(master, password);
        var api = ctx.Clients.GetAnonymous<IIdentityApiClient>(ServerEndpoints.FromWebVault(server));
        foreach (var (id, name) in new[] {
            ("cd116b46-3ae0-40fa-bcf0-4bce54d82001", "Review laptop"),
            ("cd116b46-3ae0-40fa-bcf0-4bce54d82002", "Review workstation") })
        {
            var result = await api.ConnectTokenAsync(ConnectTokenRequest.Password(email, hash, id, name, null));
            if (result is not ConnectTokenResult.Success)
                throw new InvalidOperationException($"Device sign-in failed: {result.GetType().Name}.");
        }
        var requests = ctx.Clients.GetAuthenticated<IAuthRequestApiClient>(ctx.Session.CaptureLease());
        if ((await requests.GetPendingAsync()).Data is not { Count: > 0 })
        {
            using var rsa = RSA.Create(2048);
            await ctx.Clients.GetAnonymous<IPublicAuthRequestApiClient>(ServerEndpoints.FromWebVault(server)).CreateAsync(
                new AuthRequestRequest(Convert.ToHexString(RandomNumberGenerator.GetBytes(13))[..25],
                    "cd116b46-3ae0-40fa-bcf0-4bce54d82002", email,
                    Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()), 0));
        }
    }
    finally { CryptographicOperations.ZeroMemory(master); }
});

var ciphers = await ctx.Sync.SyncAsync();
var sends = await ctx.Sends.GetSendsAsync();
var finalEmergency = new EmergencyAccessService(ctx.Clients, ctx.Crypto, ctx.Decryptor, ctx.Session);
var trustedContacts = await finalEmergency.GetTrustedAsync();
var grantedAccess = await finalEmergency.GetGrantedAsync();
var devices = await ctx.Clients.GetAuthenticated<IReadonlyApiClient>(ctx.Session.CaptureLease()).GetDevicesAsync();
var summary = new
{
    generatedUtc = DateTimeOffset.UtcNow, server, email, backend = "Vaultwarden 1.37.2", ciphertextPasswordFile = Path.Combine(secretDirectory, "review.password.dpapi"),
    cipherCount = ciphers.Count, types = ciphers.GroupBy(c => c.Type).ToDictionary(g => g.Key.ToString(), g => g.Count()),
    folders = ctx.Session.Folders.Select(f => f.Name), favorites = ciphers.Count(c => c.Favorite),
    archived = ciphers.Count(c => c.IsArchived), deleted = ciphers.Count(c => c.IsDeleted),
    attachments = ciphers.Sum(c => c.Attachments.Count), sends = sends.Select(s => new { s.Name, type = s.Type.ToString(), s.Disabled }),
    organizations = ctx.Session.Organizations.Select(o => o.Name), collections = ctx.Session.Collections.Select(c => c.Name),
    trustedContacts = trustedContacts.Select(c => new { c.Email, status = c.Status.ToString() }),
    grantedAccess = grantedAccess.Select(c => new { c.Email, status = c.Status.ToString() }),
    deviceCount = devices.Data?.Length ?? 0,
    unsupported = new[] { "BankAccount, DriversLicense and Passport: Vaultwarden 1.37.2 accepts only cipher types 1 through 5.",
        "Trusted devices: Vaultwarden 1.37.2 always returns isTrusted=false." }, notices
};
Directory.CreateDirectory("artifacts/ui-review");
await File.WriteAllTextAsync("artifacts/ui-review/backend-fixture.json", JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Seed complete: {ciphers.Count} ciphers, {ctx.Session.Folders.Count} folders, {sends.Count} Sends, {ctx.Session.Organizations.Count} organizations.");
foreach (var notice in notices) Console.WriteLine(notice);

string LoadPassword(string identity)
{
    var path = Path.Combine(secretDirectory, identity + ".password.dpapi");
    byte[] bytes;
    if (File.Exists(path))
        bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
    else
    {
        if (verifyOnly)
            throw new InvalidOperationException("Verification requires an existing encrypted fixture credential.");
        bytes = Encoding.UTF8.GetBytes(Convert.ToBase64String(RandomNumberGenerator.GetBytes(30)) + "aA!1");
        File.WriteAllBytes(path, ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser));
    }
    try { return Encoding.UTF8.GetString(bytes); }
    finally { CryptographicOperations.ZeroMemory(bytes); }
}

async Task<string> Folder(string name)
{
    if (!ctx.Session.Folders.Any(f => f.Name == name)) await ctx.Write.SaveFolderAsync(null, name);
    return ctx.Session.Folders.Single(f => f.Name == name).Id;
}

async Task Add(Cipher cipher)
{
    if (ctx.Session.Ciphers.Any(c => c.Name == cipher.Name)) return;
    await ctx.Write.SaveCipherAsync(cipher);
    Console.WriteLine($"Created fixture: {cipher.Name}");
}

async Task Optional(string feature, Func<Task> work)
{
    try { await work(); Console.WriteLine($"Prepared {feature}."); }
    catch (Exception ex) { notices.Add($"{feature}: {ex.GetType().Name}"); }
}

async Task VerifyOnlyAsync()
{
    // After sign-in, verification uses only existing read endpoints and never seeds data.
    var existingCiphers = await ctx.Sync.SyncAsync();
    var existingSends = await ctx.Sends.GetSendsAsync();
    var emergency = new EmergencyAccessService(ctx.Clients, ctx.Crypto, ctx.Decryptor, ctx.Session);
    var existingTrusted = await emergency.GetTrustedAsync();
    var existingGranted = await emergency.GetGrantedAsync();
    var existingDevices = await ctx.Clients.GetAuthenticated<IReadonlyApiClient>(ctx.Session.CaptureLease()).GetDevicesAsync();
    var contoso = existingCiphers.FirstOrDefault(c => c.Name == "Contoso Cloud - detailed login");
    var shared = existingCiphers.FirstOrDefault(c => c.Name == "Shared deployment console");
    var contosoSignals = new
    {
        found = contoso is not null,
        hasPassword = !string.IsNullOrEmpty(contoso?.Login?.Password),
        hasTotp = !string.IsNullOrEmpty(contoso?.Login?.Totp),
        canViewPassword = contoso?.CanViewPassword == true,
        reprompt = contoso?.Reprompt == true
    };
    var sharedSignals = new
    {
        found = shared is not null,
        hasUsername = !string.IsNullOrEmpty(shared?.Login?.Username),
        hasPassword = !string.IsNullOrEmpty(shared?.Login?.Password),
        hasUri = shared?.Login?.Uris.Any(uri => !string.IsNullOrWhiteSpace(uri.Uri)) == true
    };
    var report = new
    {
        generatedUtc = DateTimeOffset.UtcNow,
        mode = "verify-only",
        server,
        account = email,
        seedWritesPerformed = false,
        registrationFallbackEnabled = false,
        cipherCount = existingCiphers.Count,
        skippedCipherCount = ctx.Session.SkippedCipherCount,
        folderCount = ctx.Session.Folders.Count,
        sendCount = existingSends.Count,
        organizationCount = ctx.Session.Organizations.Count,
        collectionCount = ctx.Session.Collections.Count,
        attachmentCount = existingCiphers.Sum(cipher => cipher.Attachments.Count),
        trustedContactStates = existingTrusted.GroupBy(contact => contact.Status).ToDictionary(group => group.Key.ToString(), group => group.Count()),
        grantedAccessStates = existingGranted.GroupBy(access => access.Status).ToDictionary(group => group.Key.ToString(), group => group.Count()),
        deviceCount = existingDevices.Data?.Length ?? 0,
        pendingDeviceCount = existingDevices.Data?.Count(device => device.DevicePendingAuthRequest is not null) ?? 0,
        contosoLogin = contosoSignals,
        sharedLogin = sharedSignals
    };
    Directory.CreateDirectory("docs/ui-review");
    const string reportPath = "docs/ui-review/backend-verification.json";
    await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine(JsonSerializer.Serialize(new { contosoLogin = contosoSignals, sharedLogin = sharedSignals }));
    Console.WriteLine($"Read-only fixture verification saved to {reportPath}.");
}

sealed class SeedContext
{
    public readonly VaultSession Session = new();
    public readonly CryptoService Crypto = new();
    public readonly VaultApiClientProvider Clients;
    public readonly VaultDecryptor Decryptor;
    public readonly SyncService Sync;
    public readonly VaultWriteService Write;
    public readonly SendService Sends;
    public readonly SendWriteService SendWrite;
    public readonly AttachmentService Attachments;
    private readonly AuthService _auth;
    private readonly string _server;

    public SeedContext(string server)
    {
        _server = server;
        var store = new MemoryTokenStore();
        var factory = new ApiClientFactory();
        ITokenRefresher? refresher = null;
        Clients = new VaultApiClientProvider(factory, Session, (lease, ct) => refresher!.TryRefreshAsync(lease, ct));
        Decryptor = new VaultDecryptor(Crypto);
        var cache = new VaultCacheService(new NullCacheStore(), Decryptor, Session);
        Sync = new SyncService(Clients, Decryptor, Session, cache);
        var bootstrapper = new VaultBootstrapper(Sync, Clients, Session);
        refresher = new TokenRefreshService(Clients, Session, store);
        _auth = new AuthService(Clients, Crypto, Session, store, bootstrapper, cache, refresher);
        Write = new VaultWriteService(Clients, new CipherEncryptor(Crypto), Sync, Session,
            cipherCreateStore: new Vault.Tests.FakeCipherCreatePendingStore());
        var sendCrypto = new SendCryptoService(Crypto);
        var timeout = new TransferIdleTimeoutPolicy(TransferIdleTimeoutPolicy.DefaultIdleTimeout);
        Sends = new SendService(Clients, sendCrypto, Session);
        SendWrite = new SendWriteService(Clients, sendCrypto, Session, new SendCompensationClient(factory), timeout);
        Attachments = new AttachmentService(Clients, new AttachmentCryptoService(Crypto), Decryptor, Session, Sync,
            new AttachmentCompensationClient(factory), timeout);
    }

    public async Task SignInAsync(string email, string password, string name, bool allowRegistration = true)
    {
        var result = await _auth.LoginAsync(_server, email, password);
        if (result is AuthResult.Success) return;
        if (!allowRegistration)
            throw new InvalidOperationException($"Fixture sign-in failed: {result.GetType().Name}.");
        await new RegisterService(Crypto, Clients).RegisterAsync(_server, email, name, password, null);
        result = await _auth.LoginAsync(_server, email, password);
        if (result is not AuthResult.Success) throw new InvalidOperationException($"Fixture sign-in failed: {result.GetType().Name}.");
    }
}

sealed class NullCacheStore : ICacheStore
{
    public void SaveVaultCache(VaultCacheEntry entry) { }
    public bool TryLoadVaultCache(out VaultCacheEntry entry) { entry = null!; return false; }
    public void ClearVaultCache() { }
}
