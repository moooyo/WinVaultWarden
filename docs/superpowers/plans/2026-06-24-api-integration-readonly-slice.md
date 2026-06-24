# API Integration Readonly Slice Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the read-only real Vaultwarden data path from login to sync, decrypt, session snapshot, and WinUI display models.

**Architecture:** Keep HTTP, cryptography, session, and decrypt orchestration WinUI-free so the main pipeline is testable with plain `dotnet test`. App remains a thin shell that binds existing ViewModels to real `IVaultUiService`, `IDeviceUiService`, and account settings services after the session is unlocked.

**Tech Stack:** .NET 10, Windows App SDK / WinUI 3 for App integration, `System.Text.Json`, `HttpClient`, DPAPI (`ProtectedData`), existing `CryptoService`, xUnit with fake `HttpMessageHandler`.

---

## File Structure

- Create `src/Vault/Vault.csproj`: WinUI-free orchestration library referencing Core, Crypto, and Api.
- Create `tests/Vault.Tests/Vault.Tests.csproj`: unit tests for Api request flow, decryptor, auth/session, refresh, projection fixtures.
- Modify `WinVaultWarden.slnx`: add Vault and Vault.Tests projects for x64/arm64.
- Modify `src/Core/Models/Cipher.cs`: expand into decrypted domain shape with type-specific child records.
- Modify `src/Core/Models/Folder.cs`, `src/Core/Models/User.cs`: add fields needed by decrypted session/profile display.
- Create `src/Core/Models/DeviceInfo.cs`, `src/Core/Models/AccountInfo.cs`.
- Create `src/Core/Session/VaultState.cs`, `src/Core/Session/IVaultSnapshot.cs`.
- Create `src/Core/Abstractions/ITokenStore.cs`, `src/Core/Models/PersistedSession.cs`.
- Keep `src/Core/Abstractions/IApiClient.cs` as the base-address abstraction for existing callers.
- Create `src/Api/IReadonlyApiClient.cs`: typed API methods returning Api DTOs. Vault may depend on this Api-level interface because `src/Vault` already references `src/Api`; Core must never reference Api DTOs.
- Modify `src/Core/Services/IAuthService.cs`, `ISyncService.cs`, `IVaultService.cs`: add read-only session-oriented contracts.
- Replace `src/Api/Dtos/*.cs` with complete read-only DTOs.
- Modify `src/Api/ApiClient.cs`: implement config/prelogin/token/refresh/sync/devices methods.
- Create `src/Api/AuthHeaderHandler.cs`: bearer/header injection and single refresh retry.
- Modify `src/Crypto/CryptoService.cs`: add `DecryptToString` and `DecryptItemKey` helpers.
- Create Vault files:
  - `src/Vault/VaultSession.cs`
  - `src/Vault/VaultDecryptor.cs`
  - `src/Vault/AuthService.cs`
  - `src/Vault/SyncService.cs`
  - `src/Vault/VaultBootstrapper.cs`
  - `src/Vault/VaultService.cs`
  - `src/Vault/MemoryTokenStore.cs` for tests
- Modify App files:
  - `src/App/App.csproj`: reference Vault and DPAPI package if required by target framework.
  - `src/App/Services/DpapiTokenStore.cs`
  - `src/App/Services/VaultUiService.cs`
  - `src/App/Services/DeviceUiService.cs`
  - `src/App/Services/AccountUiService.cs`
  - `src/App/Services/ServiceConfiguration.cs`
  - `src/App/ViewModels/LoginViewModel.cs`
  - `src/App/ViewModels/SettingsViewModel.cs`
  - `src/App/MainWindow.xaml.cs`
- Keep mock services in `src/App/Services/Mock*.cs` for tests/design-time, but remove them from production DI after real services are connected.
- Modify `tests/App.Tests/App.Tests.csproj`: link real App projection services that remain pure C#.

---

## Task 1: Core Domain, Session Contracts, and Test Project Scaffolding

**Files:**
- Modify: `src/Core/Models/Cipher.cs`
- Modify: `src/Core/Models/Folder.cs`
- Modify: `src/Core/Models/User.cs`
- Create: `src/Core/Models/DeviceInfo.cs`
- Create: `src/Core/Models/AccountInfo.cs`
- Create: `src/Core/Models/PersistedSession.cs`
- Create: `src/Core/Session/VaultState.cs`
- Create: `src/Core/Session/IVaultSnapshot.cs`
- Create: `src/Core/Abstractions/ITokenStore.cs`
- Modify: `src/Core/Services/IAuthService.cs`
- Modify: `src/Core/Services/ISyncService.cs`
- Modify: `src/Core/Services/IVaultService.cs`
- Create: `src/Vault/Vault.csproj`
- Create: `tests/Vault.Tests/Vault.Tests.csproj`
- Modify: `WinVaultWarden.slnx`

- [ ] **Step 1: Add Vault and Vault.Tests project files**

Create `src/Vault/Vault.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Core\Core.csproj" />
    <ProjectReference Include="..\Crypto\Crypto.csproj" />
    <ProjectReference Include="..\Api\Api.csproj" />
  </ItemGroup>
</Project>
```

Create `tests/Vault.Tests/Vault.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.0.1" />
    <PackageReference Include="xunit.v3" Version="3.2.1" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Core\Core.csproj" />
    <ProjectReference Include="..\..\src\Crypto\Crypto.csproj" />
    <ProjectReference Include="..\..\src\Api\Api.csproj" />
    <ProjectReference Include="..\..\src\Vault\Vault.csproj" />
  </ItemGroup>
</Project>
```

Add both projects to `WinVaultWarden.slnx` with the same x64/arm64 platform entries as Core/Crypto/Api.

- [ ] **Step 2: Write failing domain compile tests**

Create `tests/Vault.Tests/CoreDomainTests.cs`:

```csharp
using Core.Enums;
using Core.Models;
using Core.Session;
using Xunit;

namespace Vault.Tests;

public class CoreDomainTests
{
    [Fact]
    public void Cipher_CanRepresentDecryptedLoginWithFieldsAndDeletionState()
    {
        var cipher = new Cipher
        {
            Id = "c1",
            Type = CipherType.Login,
            Name = "GitHub",
            Notes = "note",
            FolderId = "f1",
            Favorite = true,
            Reprompt = true,
            RevisionDate = DateTimeOffset.Parse("2026-06-24T00:00:00Z"),
            DeletedDate = DateTimeOffset.Parse("2026-06-25T00:00:00Z"),
            Login = new CipherLogin("octo", "secret", "totp", [new CipherLoginUri("https://github.com", null)]),
            Fields = [new CipherField("Recovery", "secret", CipherFieldType.Hidden)],
        };

        Assert.True(cipher.IsDeleted);
        Assert.Equal("octo", cipher.Login!.Username);
        Assert.Equal(CipherFieldType.Hidden, cipher.Fields[0].Type);
    }

    [Fact]
    public void Snapshot_ExposesReadOnlyVaultState()
    {
        IVaultSnapshot snapshot = new TestSnapshot();

        Assert.Equal(VaultState.Unlocked, snapshot.State);
        Assert.Empty(snapshot.Ciphers);
        Assert.Empty(snapshot.Folders);
        Assert.Empty(snapshot.Devices);
        Assert.Equal("me@example.com", snapshot.Account.Email);
    }

    private sealed class TestSnapshot : IVaultSnapshot
    {
        public VaultState State => VaultState.Unlocked;
        public IReadOnlyList<Cipher> Ciphers { get; } = [];
        public IReadOnlyList<Folder> Folders { get; } = [];
        public IReadOnlyList<DeviceInfo> Devices { get; } = [];
        public AccountInfo Account { get; } = new("me@example.com", "https://vault.example", "M", "PBKDF2 600000");
    }
}
```

Run:

```powershell
dotnet test tests/Vault.Tests/Vault.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~CoreDomainTests"
```

Expected: FAIL because the new domain/session types do not exist.

- [ ] **Step 3: Implement Core domain and session contracts**

Replace `src/Core/Models/Cipher.cs` with decrypted domain types:

```csharp
using Core.Enums;

namespace Core.Models;

public sealed class Cipher
{
    public string Id { get; init; } = string.Empty;
    public CipherType Type { get; init; }
    public string? OrganizationId { get; init; }
    public string? FolderId { get; init; }
    public bool Favorite { get; init; }
    public bool Reprompt { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Notes { get; init; }
    public DateTimeOffset CreationDate { get; init; }
    public DateTimeOffset RevisionDate { get; init; }
    public DateTimeOffset? DeletedDate { get; init; }
    public bool IsDeleted => DeletedDate is not null;
    public CipherLogin? Login { get; init; }
    public CipherCard? Card { get; init; }
    public CipherIdentity? Identity { get; init; }
    public CipherSecureNote? SecureNote { get; init; }
    public CipherSsh? Ssh { get; init; }
    public IReadOnlyList<CipherField> Fields { get; init; } = Array.Empty<CipherField>();
}

public sealed record CipherLogin(string? Username, string? Password, string? Totp, IReadOnlyList<CipherLoginUri> Uris);
public sealed record CipherLoginUri(string? Uri, int? Match);
public sealed record CipherCard(string? CardholderName, string? Number, string? ExpMonth, string? ExpYear, string? Code, string? Brand);
public sealed record CipherIdentity(
    string? Title,
    string? FirstName,
    string? MiddleName,
    string? LastName,
    string? Username,
    string? Company,
    string? Ssn,
    string? PassportNumber,
    string? LicenseNumber,
    string? Email,
    string? Phone,
    string? Address1,
    string? Address2,
    string? Address3,
    string? City,
    string? State,
    string? PostalCode,
    string? Country);
public sealed record CipherSecureNote(int Type);
public sealed record CipherSsh(string? PrivateKey, string? PublicKey, string? Fingerprint);
public sealed record CipherField(string Name, string? Value, CipherFieldType Type);
public enum CipherFieldType { Text = 0, Hidden = 1, Boolean = 2 }
```

Modify `Folder.cs` to include decrypted name and revision:

```csharp
namespace Core.Models;

public sealed class Folder
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public DateTimeOffset RevisionDate { get; init; }
}
```

Create `DeviceInfo.cs`:

```csharp
namespace Core.Models;

public sealed record DeviceInfo(
    string Id,
    string? Name,
    int Type,
    string? Identifier,
    DateTimeOffset? CreationDate,
    bool IsTrusted);
```

Create `AccountInfo.cs`:

```csharp
namespace Core.Models;

public sealed record AccountInfo(string Email, string ServerUrl, string Initial, string KdfSummary)
{
    public static AccountInfo Empty { get; } = new(string.Empty, string.Empty, string.Empty, string.Empty);
}
```

Create `PersistedSession.cs`:

```csharp
using Core.Enums;

namespace Core.Models;

public sealed record PersistedSession(
    string ServerUrl,
    string Email,
    string DeviceIdentifier,
    string RefreshToken,
    string ProtectedUserKey,
    KdfType KdfType,
    int KdfIterations,
    int? KdfMemory,
    int? KdfParallelism);
```

Create `src/Core/Session/VaultState.cs`:

```csharp
namespace Core.Session;

public enum VaultState
{
    LoggedOut,
    Locked,
    Unlocking,
    Syncing,
    Unlocked,
    Error,
}
```

Create `src/Core/Session/IVaultSnapshot.cs`:

```csharp
using Core.Models;

namespace Core.Session;

public interface IVaultSnapshot
{
    VaultState State { get; }
    IReadOnlyList<Cipher> Ciphers { get; }
    IReadOnlyList<Folder> Folders { get; }
    IReadOnlyList<DeviceInfo> Devices { get; }
    AccountInfo Account { get; }
}
```

Create `ITokenStore.cs`:

```csharp
using Core.Models;

namespace Core.Abstractions;

public interface ITokenStore
{
    bool TryLoad(out PersistedSession session);
    void Save(PersistedSession session);
    void Clear();
}
```

Update service interfaces:

```csharp
namespace Core.Services;

public interface IAuthService
{
    Task<AuthResult> LoginAsync(string serverUrl, string email, string masterPassword, CancellationToken ct = default);
    Task<AuthResult> SubmitTwoFactorAsync(string code, CancellationToken ct = default);
    Task<AuthResult> UnlockAsync(string masterPassword, CancellationToken ct = default);
    Task LockAsync(CancellationToken ct = default);
    Task LogoutAsync(CancellationToken ct = default);
}

public abstract record AuthResult
{
    public sealed record Success : AuthResult;
    public sealed record TwoFactorRequired(IReadOnlyList<int> Providers) : AuthResult;
    public sealed record Failure(string Message) : AuthResult;
}
```

```csharp
using Core.Models;

namespace Core.Services;

public interface ISyncService
{
    Task<IReadOnlyList<Cipher>> SyncAsync(CancellationToken ct = default);
}
```

```csharp
using Core.Models;
using Core.Session;

namespace Core.Services;

public interface IVaultService
{
    IVaultSnapshot Snapshot { get; }
    IReadOnlyList<Cipher> GetCiphers();
    IReadOnlyList<Folder> GetFolders();
    IReadOnlyList<DeviceInfo> GetDevices();
}
```

- [ ] **Step 4: Verify and commit**

Run:

```powershell
dotnet test tests/Vault.Tests/Vault.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~CoreDomainTests"
dotnet test -p:Platform=x64
```

Expected: PASS. Commit:

```powershell
git add WinVaultWarden.slnx src/Core src/Vault tests/Vault.Tests
git commit -m "feat: add vault core session contracts"
```

---

## Task 2: Api DTOs, ApiClient, and AuthHeaderHandler

**Files:**
- Create: `src/Api/IReadonlyApiClient.cs`
- Modify: `src/Api/Dtos/PreloginDtos.cs`
- Modify: `src/Api/Dtos/TokenDtos.cs`
- Modify: `src/Api/Dtos/SyncDtos.cs`
- Create: `src/Api/Dtos/ConfigDtos.cs`
- Create: `src/Api/Dtos/DeviceDtos.cs`
- Modify: `src/Api/ApiClient.cs`
- Create: `src/Api/AuthHeaderHandler.cs`
- Create: `tests/Vault.Tests/FakeHttpMessageHandler.cs`
- Create: `tests/Vault.Tests/ApiClientTests.cs`

- [ ] **Step 1: Write failing API client tests**

Create `tests/Vault.Tests/FakeHttpMessageHandler.cs`:

```csharp
using System.Net;
using System.Text;

namespace Vault.Tests;

public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();
    public List<HttpRequestMessage> Requests { get; } = new();
    public List<string> Bodies { get; } = new();

    public void Enqueue(Func<HttpRequestMessage, HttpResponseMessage> response) => _responses.Enqueue(response);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
        if (_responses.Count == 0)
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };

        return _responses.Dequeue()(request);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };
}
```

Create `tests/Vault.Tests/ApiClientTests.cs`:

```csharp
using System.Net;
using Api;
using Api.Dtos;
using Core.Enums;
using Xunit;

namespace Vault.Tests;

public class ApiClientTests
{
    [Fact]
    public async Task Prelogin_PostsExpectedPathAndEmail()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(req => FakeHttpMessageHandler.Json(HttpStatusCode.OK,
            """{"kdf":0,"kdfIterations":600000,"kdfMemory":null,"kdfParallelism":null}"""));
        var client = new ApiClient(new HttpClient(handler));
        client.SetBaseAddress("https://vault.example/");

        var response = await client.PreloginAsync("me@example.com");

        Assert.Equal(KdfType.Pbkdf2, response.Kdf);
        Assert.Equal("/identity/accounts/prelogin", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("\"email\":\"me@example.com\"", handler.Bodies[0]);
    }

    [Fact]
    public async Task ConnectToken_PasswordGrant_UsesFormFields()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(req => FakeHttpMessageHandler.Json(HttpStatusCode.OK,
            """{"access_token":"a","refresh_token":"r","expires_in":3600,"token_type":"Bearer","scope":"api offline_access","Key":"2.X","PrivateKey":null,"Kdf":0,"KdfIterations":600000}"""));
        var client = new ApiClient(new HttpClient(handler));
        client.SetBaseAddress("https://vault.example");

        var result = await client.ConnectTokenAsync(ConnectTokenRequest.Password(
            "me@example.com",
            "hash",
            "device-id",
            "WinVaultWarden",
            null));

        var success = Assert.IsType<ConnectTokenResult.Success>(result);
        Assert.Equal("a", success.Token.AccessToken);
        Assert.Equal("/identity/connect/token", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("grant_type=password", handler.Bodies[0]);
        Assert.Contains("client_id=desktop", handler.Bodies[0]);
        Assert.Contains("device_type=6", handler.Bodies[0]);
        Assert.Contains("username=me%40example.com", handler.Bodies[0]);
    }

    [Fact]
    public async Task ConnectToken_TwoFactorError_ReturnsTwoFactorRequired()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(req => FakeHttpMessageHandler.Json(HttpStatusCode.BadRequest,
            """{"error":"invalid_grant","error_description":"Two factor required.","TwoFactorProviders":[0]}"""));
        var client = new ApiClient(new HttpClient(handler));
        client.SetBaseAddress("https://vault.example");

        var result = await client.ConnectTokenAsync(ConnectTokenRequest.Password("me@example.com", "hash", "d", "n", null));

        var twoFactor = Assert.IsType<ConnectTokenResult.TwoFactorRequired>(result);
        Assert.Equal([0], twoFactor.Providers);
    }

    [Fact]
    public async Task GetSync_UsesExcludeDomainsQuery()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(req => FakeHttpMessageHandler.Json(HttpStatusCode.OK,
            """{"object":"sync","profile":{"email":"me@example.com"},"folders":[],"ciphers":[]}"""));
        var client = new ApiClient(new HttpClient(handler));
        client.SetBaseAddress("https://vault.example");

        var sync = await client.GetSyncAsync();

        Assert.Equal("sync", sync.Object);
        Assert.Equal("/api/sync", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("excludeDomains=true", handler.Requests[0].RequestUri!.Query.TrimStart('?'));
    }
}
```

Run:

```powershell
dotnet test tests/Vault.Tests/Vault.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~ApiClientTests"
```

Expected: FAIL because `IReadonlyApiClient` and `ApiClient` methods do not exist.

- [ ] **Step 2: Implement DTOs and IReadonlyApiClient**

Create `src/Api/IReadonlyApiClient.cs`:

```csharp
using Api.Dtos;

namespace Api;

public interface IReadonlyApiClient
{
    void SetBaseAddress(string baseUrl);
    Task<ConfigResponse> GetConfigAsync(CancellationToken ct = default);
    Task<PreloginResponse> PreloginAsync(string email, CancellationToken ct = default);
    Task<ConnectTokenResult> ConnectTokenAsync(ConnectTokenRequest request, CancellationToken ct = default);
    Task<TokenResponse> RefreshTokenAsync(string refreshToken, CancellationToken ct = default);
    Task<SyncResponse> GetSyncAsync(CancellationToken ct = default);
    Task<ListResponse<DeviceDto>> GetDevicesAsync(CancellationToken ct = default);
}
```

Use DTOs with `[JsonPropertyName]`. Include these exact record names: `ConfigResponse`, `PreloginResponse`, `TokenResponse`, `ConnectTokenRequest`, `TwoFactorPayload`, `ConnectTokenResult`, `ConnectTokenErrorResponse`, `SyncResponse`, `ProfileDto`, `CipherDto`, `LoginDto`, `LoginUriDto`, `CardDto`, `IdentityDto`, `SecureNoteDto`, `SshKeyDto`, `FieldDto`, `FolderDto`, `DeviceDto`, and `ListResponse<T>`.

- [ ] **Step 3: Implement ApiClient**

`ApiClient` should:

- Use `JsonSerializerOptions(JsonSerializerDefaults.Web)`.
- POST prelogin JSON to `/identity/accounts/prelogin`.
- POST token requests as `FormUrlEncodedContent`.
- Parse success/2FA/error responses without throwing on 400.
- GET `/api/sync?excludeDomains=true`, `/api/devices`, and `/api/config`.
- Call `EnsureSuccessStatusCode` for read endpoints.

- [ ] **Step 4: Implement AuthHeaderHandler**

Create `AuthHeaderHandler` with constructor dependencies kept small:

```csharp
public sealed class AuthHeaderHandler : DelegatingHandler
{
    public const string ClientName = "desktop";
    public const string ClientVersion = "2026.6.0";
    public const string DeviceType = "6";
}
```

It must inject `Bitwarden-Client-Name`, `Bitwarden-Client-Version`, `Device-Type`, and a bearer token when a token provider returns one. It must retry one 401 after a refresh callback succeeds. Keep the token provider interfaces internal to Api or Core, but write tests in Task 5 when `VaultSession` exists.

- [ ] **Step 5: Verify and commit**

Run:

```powershell
dotnet test tests/Vault.Tests/Vault.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~ApiClientTests"
dotnet test -p:Platform=x64
```

Expected: PASS. Commit:

```powershell
git add src/Api tests/Vault.Tests
git commit -m "feat: implement readonly api client"
```

---

## Task 3: Crypto Read Helpers

**Files:**
- Modify: `src/Crypto/CryptoService.cs`
- Create: `tests/Crypto.Tests/DecryptReadHelpersTests.cs`

- [ ] **Step 1: Write failing helper tests**

Create `tests/Crypto.Tests/DecryptReadHelpersTests.cs`:

```csharp
using System.Text;
using Crypto;
using Xunit;

namespace Crypto.Tests;

public class DecryptReadHelpersTests
{
    [Fact]
    public void DecryptToString_ReturnsUtf8Plaintext()
    {
        var service = new CryptoService();
        var key = new SymmetricCryptoKey(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());
        var enc = service.Encrypt(Encoding.UTF8.GetBytes("hello"), key).ToString();

        var text = service.DecryptToString(enc, key);

        Assert.Equal("hello", text);
    }

    [Fact]
    public void DecryptItemKey_ReturnsSymmetricKeyFromEncryptedBytes()
    {
        var service = new CryptoService();
        var userKey = new SymmetricCryptoKey(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());
        var itemBytes = Enumerable.Range(64, 64).Select(i => (byte)i).ToArray();
        var encryptedItemKey = service.Encrypt(itemBytes, userKey).ToString();

        var itemKey = service.DecryptItemKey(encryptedItemKey, userKey);
        var enc = service.Encrypt(Encoding.UTF8.GetBytes("item"), itemKey).ToString();

        Assert.Equal("item", service.DecryptToString(enc, itemKey));
    }
}
```

Run:

```powershell
dotnet test tests/Crypto.Tests/Crypto.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~DecryptReadHelpersTests"
```

Expected: FAIL because helpers do not exist.

- [ ] **Step 2: Implement helpers**

Add to `CryptoService`:

```csharp
public string? DecryptToString(string? encStringText, SymmetricCryptoKey key)
{
    if (string.IsNullOrWhiteSpace(encStringText))
        return null;

    var bytes = Decrypt(EncString.Parse(encStringText), key);
    return Encoding.UTF8.GetString(bytes);
}

public SymmetricCryptoKey DecryptItemKey(string cipherKeyEnc, SymmetricCryptoKey userKey)
{
    var bytes = Decrypt(EncString.Parse(cipherKeyEnc), userKey);
    return new SymmetricCryptoKey(bytes);
}
```

- [ ] **Step 3: Verify and commit**

Run:

```powershell
dotnet test tests/Crypto.Tests/Crypto.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~DecryptReadHelpersTests"
dotnet test -p:Platform=x64
```

Expected: PASS. Commit:

```powershell
git add src/Crypto/CryptoService.cs tests/Crypto.Tests/DecryptReadHelpersTests.cs
git commit -m "feat: add vault decrypt read helpers"
```

---

## Task 4: VaultSession and VaultDecryptor

**Files:**
- Create: `src/Vault/VaultSession.cs`
- Create: `src/Vault/VaultDecryptor.cs`
- Create: `tests/Vault.Tests/VaultDecryptorTests.cs`

- [ ] **Step 1: Write encrypted fixture tests**

Create `tests/Vault.Tests/VaultDecryptorTests.cs`. Use `CryptoService.Encrypt` in the test to produce encrypted DTO strings instead of storing real secrets. Cover:

- Five cipher types decrypt into `Cipher.Login/Card/Identity/SecureNote/Ssh`.
- Folder names decrypt.
- `cipher.key` item-level key overrides user key.
- Hidden/boolean/text custom fields decrypt and keep type.
- Bad item is skipped while good item remains.

The test helper should build `SyncResponse` directly with DTO objects, not JSON, so failures point at decryptor behavior.

- [ ] **Step 2: Implement VaultSession**

`VaultSession` must:

- Implement `IVaultSnapshot`.
- Hold access token, refresh token, user key, account, devices, ciphers, folders in memory.
- Expose methods `SetTokens`, `SetUnlockedKey`, `SetSnapshot`, `SetDevices`, `SetAccount`, `Lock`, and `Clear`.
- Never log or persist plaintext.

- [ ] **Step 3: Implement VaultDecryptor**

`VaultDecryptor` constructor accepts `CryptoService`. Implement:

```csharp
public DecryptedVault Decrypt(SyncResponse sync, SymmetricCryptoKey userKey, string serverUrl)
```

`DecryptedVault` contains `AccountInfo Account`, `IReadOnlyList<Folder> Folders`, `IReadOnlyList<Cipher> Ciphers`, and `int SkippedCipherCount`.

Decrypt every EncString text via `DecryptToString`. For item-level keys, call `DecryptItemKey`. Catch exceptions per cipher and skip only that cipher.

- [ ] **Step 4: Verify and commit**

Run:

```powershell
dotnet test tests/Vault.Tests/Vault.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~VaultDecryptorTests"
dotnet test -p:Platform=x64
```

Expected: PASS. Commit:

```powershell
git add src/Vault tests/Vault.Tests/VaultDecryptorTests.cs
git commit -m "feat: decrypt synced vault snapshot"
```

---

## Task 5: AuthService, Refresh, SyncService, and Bootstrapper

**Files:**
- Create: `src/Vault/MemoryTokenStore.cs`
- Create: `src/Vault/AuthService.cs`
- Create: `src/Vault/SyncService.cs`
- Create: `src/Vault/VaultBootstrapper.cs`
- Create: `src/Vault/VaultService.cs`
- Create: `tests/Vault.Tests/AuthServiceTests.cs`
- Create: `tests/Vault.Tests/AuthHeaderHandlerTests.cs`
- Create: `tests/Vault.Tests/VaultBootstrapperTests.cs`

- [ ] **Step 1: Write failing auth flow tests**

`AuthServiceTests` must cover:

- PBKDF2 login success: prelogin -> token -> decrypt `Key` -> token store saved -> session unlocked.
- Argon2id prelogin returns `AuthResult.Failure("暂不支持 Argon2id 账户")`.
- Two factor required returns `AuthResult.TwoFactorRequired([0])`, then `SubmitTwoFactorAsync("123456")` succeeds and includes `two_factor_provider=0` and `two_factor_token=123456`.
- Unlock with persisted session and correct master password refreshes token and unlocks.
- Wrong unlock password returns failure and remains locked.

- [ ] **Step 2: Write failing handler/bootstrap tests**

`AuthHeaderHandlerTests` must cover:

- Request has `Bitwarden-Client-Name`, `Bitwarden-Client-Version`, `Device-Type`, and bearer token.
- 401 triggers one refresh and retries once.
- Refresh failure leaves session locked.

`VaultBootstrapperTests` must cover:

- `SyncService` calls `GetSyncAsync`, decrypts response, stores ciphers/folders/account.
- Devices are fetched and stored after sync.

- [ ] **Step 3: Implement MemoryTokenStore**

Use an in-memory `PersistedSession?` for tests and design-time.

- [ ] **Step 4: Implement AuthService**

`AuthService` dependencies:

- `IReadonlyApiClient`
- `CryptoService`
- `VaultSession`
- `ITokenStore`
- `VaultBootstrapper`

It must preserve pending 2FA context after a `TwoFactorRequired` result: server URL, email, master password hash, stretched key, device id, and master-derived material needed to decrypt user key after the second token response. Do not store the raw master password beyond the current method call; recompute or keep only master key bytes until 2FA completes, then clear pending context.

- [ ] **Step 5: Implement SyncService, Bootstrapper, VaultService**

`SyncService` calls `IReadonlyApiClient.GetSyncAsync` and `VaultDecryptor.Decrypt`.

`VaultBootstrapper` calls sync, devices, and session setters. It sets state `Syncing` before HTTP and `Unlocked` after success.

`VaultService` reads `VaultSession`.

- [ ] **Step 6: Verify and commit**

Run:

```powershell
dotnet test tests/Vault.Tests/Vault.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~AuthServiceTests|FullyQualifiedName~AuthHeaderHandlerTests|FullyQualifiedName~VaultBootstrapperTests"
dotnet test -p:Platform=x64
```

Expected: PASS. Commit:

```powershell
git add src/Vault tests/Vault.Tests
git commit -m "feat: add vault auth and sync session"
```

---

## Task 6: Real App Projection Services

**Files:**
- Create: `src/App/Services/VaultUiService.cs`
- Create: `src/App/Services/DeviceUiService.cs`
- Create: `src/App/Services/AccountUiService.cs`
- Modify: `tests/App.Tests/App.Tests.csproj`
- Create: `tests/App.Tests/RealVaultUiServiceTests.cs`
- Create: `tests/App.Tests/RealDeviceUiServiceTests.cs`

- [ ] **Step 1: Write failing projection tests**

Create tests that build a fake `IVaultSnapshot` with decrypted Core models and assert:

- `VaultUiService.GetItems()` creates all five `VaultItemKind` rows.
- `GetFilters()` counts all/type/favorite/trash/folder correctly.
- `GetDetail()` maps Login/Card/Identity/Note/Ssh fields, favorite, reprompt, custom field type/secret.
- `DeviceUiService.GetDevices()` marks current device by identifier and maps device name/type.
- `AccountUiService` exposes email/server/initial/KDF summary.

- [ ] **Step 2: Implement real projection services**

Use existing mock service output shapes as the contract. Keep these services pure C# so App.Tests can link them.

`VaultUiService` should not decrypt; it only maps already decrypted Core models.

- [ ] **Step 3: Verify and commit**

Run:

```powershell
dotnet test tests/App.Tests/App.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~RealVaultUiServiceTests|FullyQualifiedName~RealDeviceUiServiceTests"
dotnet test -p:Platform=x64
```

Expected: PASS. Commit:

```powershell
git add src/App/Services/VaultUiService.cs src/App/Services/DeviceUiService.cs src/App/Services/AccountUiService.cs tests/App.Tests
git commit -m "feat: map vault session to app ui models"
```

---

## Task 7: App DI, Login, Unlock, Lock, Logout, and Settings

**Files:**
- Modify: `src/App/App.csproj`
- Create: `src/App/Services/DpapiTokenStore.cs`
- Modify: `src/App/Services/ServiceConfiguration.cs`
- Modify: `src/App/ViewModels/LoginViewModel.cs`
- Modify: `src/App/ViewModels/SettingsViewModel.cs`
- Modify: `src/App/MainWindow.xaml.cs`
- Modify: `src/App/Views/LoginPage.xaml`
- Modify: `tests/App.Tests/App.Tests.csproj`
- Create: `tests/App.Tests/DpapiTokenStoreTests.cs`
- Create: `tests/App.Tests/LoginViewModelAuthTests.cs`
- Create: `tests/App.Tests/SettingsViewModelAccountTests.cs`

- [ ] **Step 1: Write failing App behavior tests**

Cover:

- `LoginViewModel.LoginCommand` calls `IAuthService.LoginAsync` and invokes a success callback on `AuthResult.Success`.
- `AuthResult.TwoFactorRequired` flips `IsTwoFactorStage` and submit calls `SubmitTwoFactorAsync`.
- Unlock stage hides server/email editing and calls `UnlockAsync`.
- Settings view model reads from `IAccountUiService`.
- `DpapiTokenStore` saves, loads, and clears a `PersistedSession` without exposing JSON plaintext in the file.

- [ ] **Step 2: Implement DpapiTokenStore**

Use `%LocalAppData%\WinVaultWarden\session.bin`. Serialize `PersistedSession` to UTF-8 JSON, protect with `ProtectedData.Protect(..., DataProtectionScope.CurrentUser)`, and unprotect on load. If unprotect or deserialize fails, return false.

- [ ] **Step 3: Rewire DI**

Register:

- `VaultSession` singleton
- `CryptoService`
- `ApiClient`
- `AuthHeaderHandler`
- `AuthService`, `SyncService`, `VaultBootstrapper`, `VaultDecryptor`, `VaultService`
- `ITokenStore` as `DpapiTokenStore`
- real `IVaultUiService`, `IDeviceUiService`, and `IAccountUiService`

Keep mock services available as concrete classes but no longer bind them to interfaces in production DI.

- [ ] **Step 4: Update LoginPage/LoginViewModel**

Add observable state: `IsTwoFactorStage`, `TwoFactorCode`, `IsUnlockStage`, `Status`, `CanEditServer`. Use existing MVVM Toolkit style; use partial properties where WinUI binds directly.

On success, call an injected navigation callback supplied by `LoginPage.xaml.cs` or `MainWindow`.

- [ ] **Step 5: Update MainWindow lock/logout**

On app startup:

- If `ITokenStore.TryLoad` succeeds, show unlock stage.
- Otherwise show login.

Lock calls `IAuthService.LockAsync` then shows unlock.

Logout calls `IAuthService.LogoutAsync` then shows login.

- [ ] **Step 6: Verify and commit**

Run:

```powershell
dotnet test tests/App.Tests/App.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~DpapiTokenStoreTests|FullyQualifiedName~LoginViewModelAuthTests|FullyQualifiedName~SettingsViewModelAccountTests"
dotnet build src/App/App.csproj -c Debug -p:Platform=x64
dotnet test -p:Platform=x64
```

Expected: PASS. Commit:

```powershell
git add src/App tests/App.Tests
git commit -m "feat: connect app to vault session services"
```

---

## Task 8: Final Regression, Scope Audit, and Push

**Files:**
- Modify only files required by failing tests or build errors.

- [ ] **Step 1: Run full tests**

Run:

```powershell
dotnet test -p:Platform=x64
```

Expected: PASS for App.Tests, Crypto.Tests, and Vault.Tests.

- [ ] **Step 2: Build WinUI App**

Run:

```powershell
dotnet build src/App/App.csproj -c Debug -p:Platform=x64
```

Expected: PASS. Existing `MVVMTK0045` warnings outside touched code can remain if not introduced by this plan.

- [ ] **Step 3: Scope and whitespace audit**

Run:

```powershell
git diff --check
git status --short --branch
git diff --stat origin/main..HEAD
```

Expected:

- `git diff --check` has no output.
- User untracked files, if still present, remain untracked and unstaged.
- Diff is limited to Core/Api/Crypto/Vault/App service/viewmodel integration and tests.

- [ ] **Step 4: Final code review**

Dispatch a code reviewer with range `origin/main..HEAD`. Required focus:

- No plaintext master password, master key, user key, or decrypted cipher data is persisted or logged.
- DTO field names match Vaultwarden contract.
- 401 retry happens once.
- Argon2id fails with a clear message.
- UI services read session snapshot and no longer hardcode mock data in production DI.

- [ ] **Step 5: Commit fixes if needed and push**

If Step 1-4 required fixes:

```powershell
git add src tests
git commit -m "fix: polish readonly vault integration"
```

Then push:

```powershell
git push origin main
```
