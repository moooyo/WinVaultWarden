using Api;
using Api.Dtos;
using System.Text.Json;
using Core.Enums;
using Core.Models;
using Core.Services;
using Core.Session;
using Crypto;
using Crypto.V2;
using Vault;
using Xunit;

namespace Vault.Tests;

public class VaultImportServiceTests
{
    private sealed class CapturingImportApi : IVaultWriteApiClient
    {
        public ImportRequest? Last { get; private set; }
        public int CommitCount { get; private set; }
        public int CallCount { get; private set; }
        public Action? AfterCommit { get; set; }

        public Task CreateCipherAsync(CipherRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateCipherAsync(string cipherId, CipherRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SoftDeleteCipherAsync(string cipherId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task HardDeleteCipherAsync(string cipherId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RestoreCipherAsync(string cipherId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task BulkSoftDeleteCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default) => throw new NotSupportedException();
        public Task BulkHardDeleteCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default) => throw new NotSupportedException();
        public Task BulkRestoreCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default) => throw new NotSupportedException();
        public Task BulkMoveCiphersAsync(IReadOnlyCollection<string> ids, string? folderId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CreateFolderAsync(FolderRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateFolderAsync(string folderId, FolderRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteFolderAsync(string folderId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task ImportCiphersAsync(ImportRequest request, CancellationToken ct = default)
        {
            CallCount++;
            ct.ThrowIfCancellationRequested();
            Last = request;
            CommitCount++;
            AfterCommit?.Invoke();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSync : ISyncService
    {
        public int Calls { get; private set; }
        public VaultSessionLease? LastLease { get; private set; }

        public Task<IReadOnlyList<Cipher>> SyncAsync(CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<Cipher>>(Array.Empty<Cipher>());
        }

        public Task<IReadOnlyList<Cipher>> SyncAsync(
            VaultSessionLease lease, CancellationToken ct = default)
        {
            LastLease = lease;
            return SyncAsync(ct);
        }
    }

    private sealed class ThrowingSync : ISyncService
    {
        public Task<IReadOnlyList<Cipher>> SyncAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("sync boom");

        public Task<IReadOnlyList<Cipher>> SyncAsync(
            Core.Session.VaultSessionLease lease, CancellationToken ct = default) => SyncAsync(ct);
    }

    private sealed class SwitchingProvider(
        VaultSession session,
        IVaultWriteApiClient api) : IVaultApiClientProvider
    {
        public TClient GetAnonymous<TClient>(ServerOrigin origin)
            where TClient : class, IAnonymousApiRole =>
            throw new NotSupportedException();

        public TClient GetAuthenticated<TClient>(VaultSessionLease lease)
            where TClient : class, IAuthenticatedApiRole
        {
            var next = session.BeginAccountSession(
                "account-b",
                "b@example.com",
                new ServerOrigin("https://b.example"));
            session.TryCommit(next, () => session.SetUnlockedKey(
                new SymmetricCryptoKey(
                    Enumerable.Range(64, 64).Select(i => (byte)i).ToArray())));
            return (TClient)(object)api;
        }
    }

    private sealed class RecordingProvider(
        IVaultWriteApiClient api) : IVaultApiClientProvider
    {
        public VaultSessionLease? LastLease { get; private set; }

        public TClient GetAnonymous<TClient>(ServerOrigin origin)
            where TClient : class, IAnonymousApiRole =>
            throw new NotSupportedException();

        public TClient GetAuthenticated<TClient>(VaultSessionLease lease)
            where TClient : class, IAuthenticatedApiRole
        {
            LastLease = lease;
            return (TClient)(object)api;
        }
    }

    [Fact]
    public async Task ImportAsync_EncryptsAndSendsImport_WithRelations_ThenResyncs()
    {
        var api = new CapturingImportApi();
        var sync = new FakeSync();
        var session = new VaultSession();
        session.BeginAccountSession("test-account", "test@test.com", new Core.Models.ServerOrigin("https://vault.example"));
        session.SetUnlockedKey(new SymmetricCryptoKey(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray()));
        var provider = new RecordingProvider(api);
        var svc = new VaultImportService(provider, new CipherEncryptor(new CryptoService()), sync, session);

        // 1 folder + 1 login in that folder
        var json = "{\"encrypted\":false,\"folders\":[{\"id\":\"f0\",\"name\":\"Work\"}]," +
                   "\"items\":[{\"type\":1,\"name\":\"A\",\"folderId\":\"f0\",\"login\":{\"username\":\"u\",\"password\":\"p\",\"uris\":[]}}]}";
        var n = await svc.ImportAsync(ImportFormat.Json, json, TestContext.Current.CancellationToken);

        Assert.Equal(1, n);
        Assert.Single(api.Last!.Ciphers);
        Assert.Single(api.Last.Folders);
        Assert.Equal(0, api.Last.FolderRelationships[0].Key);   // cipher0
        Assert.Equal(0, api.Last.FolderRelationships[0].Value); // folder0
        Assert.Null(api.Last.Ciphers[0].FolderId);
        // 名称必须真正加密成 EncString(AES-256-CBC-HMAC,"2." 前缀)而非明文——仅断言非空会让明文回归漏网(R22)。
        var encName = api.Last.Ciphers[0].Name;
        Assert.StartsWith("2.", encName);
        Assert.NotEqual("A", encName);
        Assert.Equal(1, sync.Calls);
        Assert.Equal("test-account", sync.LastLease!.AccountId);
        Assert.Same(provider.LastLease, sync.LastLease);
    }

    [Theory]
    [InlineData(CipherType.BankAccount)]
    [InlineData(CipherType.DriversLicense)]
    [InlineData(CipherType.Passport)]
    public async Task ImportAsync_UnsupportedTypeInMixedBatch_IsRejectedBeforeApiCall(
        CipherType type)
    {
        var fixture = NewImportFixture();
        var json =
            $"{{\"encrypted\":false,\"folders\":[{{\"id\":\"f0\",\"name\":\"Work\"}}]," +
            $"\"items\":[{{\"type\":1,\"name\":\"Login\",\"login\":{{\"uris\":[]}}}}," +
            $"{{\"type\":{(int)type},\"name\":\"Unsupported\"}}]}}";

        var exception = await Assert.ThrowsAsync<UnsupportedCipherTypeWriteException>(() =>
            fixture.Service.ImportAsync(
                ImportFormat.Json,
                json,
                TestContext.Current.CancellationToken));

        Assert.Equal(type, exception.CipherType);
        Assert.Null(fixture.Api.Last);
        Assert.Equal(0, fixture.Sync.Calls);
    }

    [Theory]
    [InlineData(CipherType.BankAccount, "bankAccount")]
    [InlineData(CipherType.DriversLicense, "driversLicense")]
    [InlineData(CipherType.Passport, "passport")]
    public async Task ImportAsync_NegotiatedNewCipherType_IsEncryptedAndImported(
        CipherType type,
        string childProperty)
    {
        var fixture = NewImportFixture(new VaultCapabilities(true, false, false));
        var json =
            $"{{\"encrypted\":false,\"folders\":[],\"items\":[" +
            $"{{\"type\":{(int)type},\"name\":\"Supported\",\"{childProperty}\":{{}}}}]}}";

        var count = await fixture.Service.ImportAsync(
            ImportFormat.Json,
            json,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, count);
        var request = Assert.Single(fixture.Api.Last!.Ciphers);
        Assert.Equal((int)type, request.Type);
        Assert.Equal(1, fixture.Sync.Calls);
    }

    [Theory]
    [InlineData("{\"type\":1,\"name\":\"Missing login\"}")]
    [InlineData("{\"type\":1,\"name\":\"Ambiguous\",\"login\":{\"uris\":[]},\"card\":{}}")]
    public async Task ImportAsync_InvalidTypedShape_IsRejectedBeforeApiCall(string itemJson)
    {
        var fixture = NewImportFixture();
        var json = $"{{\"encrypted\":false,\"folders\":[],\"items\":[{itemJson}]}}";

        await Assert.ThrowsAsync<InvalidCipherWriteShapeException>(() =>
            fixture.Service.ImportAsync(
                ImportFormat.Json,
                json,
                TestContext.Current.CancellationToken));

        Assert.Null(fixture.Api.Last);
        Assert.Equal(0, fixture.Sync.Calls);
    }

    [Fact]
    public async Task ImportAsync_WriteSucceedsButResyncFails_ThrowsVaultRefreshException()
    {
        // R22:写入已在服务端成功,但写后重同步失败 → 抛 VaultRefreshException(区分"写失败"与"写成功但刷新失败")。
        var api = new CapturingImportApi();
        var session = new VaultSession();
        session.BeginAccountSession("test-account", "test@test.com", new Core.Models.ServerOrigin("https://vault.example"));
        session.SetUnlockedKey(new SymmetricCryptoKey(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray()));
        var svc = new VaultImportService(FakeVaultApiClientProvider.ForAuthenticated<IVaultWriteApiClient>(api), new CipherEncryptor(new CryptoService()), new ThrowingSync(), session);

        var json = "{\"encrypted\":false,\"folders\":[],\"items\":[{\"type\":1,\"name\":\"A\",\"login\":{\"username\":\"u\",\"password\":\"p\",\"uris\":[]}}]}";
        await Assert.ThrowsAsync<VaultRefreshException>(() =>
            svc.ImportAsync(ImportFormat.Json, json, TestContext.Current.CancellationToken));

        Assert.NotNull(api.Last); // 写已成功(异常仅来自写后刷新)
    }

    private sealed record ImportFixture(
        VaultImportService Service,
        CapturingImportApi Api,
        FakeSync Sync,
        VaultSession Session);

    private static ImportFixture NewImportFixture(VaultCapabilities? capabilities = null)
    {
        var api = new CapturingImportApi();
        var sync = new FakeSync();
        var session = new VaultSession();
        var lease = session.BeginAccountSession(
            "account-a",
            "a@example.com",
            new Core.Models.ServerOrigin("https://a.example"));
        var key = new SymmetricCryptoKey(
            Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());
        session.TryCommit(lease, () =>
        {
            session.SetUnlockedKey(key);
            session.SetTokens("access-a", "refresh-a");
        });
        Assert.True(session.TrySetCapabilities(lease, capabilities ?? VaultCapabilities.None));
        var service = new VaultImportService(
            FakeVaultApiClientProvider.ForAuthenticated<IVaultWriteApiClient>(api),
            new CipherEncryptor(new CryptoService()),
            sync,
            session);
        return new ImportFixture(service, api, sync, session);
    }

    [Fact]
    public async Task ImportAsync_EncryptedTrue_MakesNoApiOrSyncCall()
    {
        var fixture = NewImportFixture();

        await Assert.ThrowsAsync<Porting.EncryptedBitwardenJsonNotSupportedException>(() =>
            fixture.Service.ImportAsync(
                ImportFormat.Json,
                "{\"encrypted\":true,\"folders\":[],\"items\":[]}",
                TestContext.Current.CancellationToken));

        Assert.Null(fixture.Api.Last);
        Assert.Equal(0, fixture.Sync.Calls);
    }

    [Fact]
    public async Task ImportAsync_Fido2Credentials_AreEncryptedIntoCipherRequest()
    {
        var fixture = NewImportFixture();
        const string json =
            """
            {
              "encrypted": false,
              "folders": [],
              "items": [{
                "type": 1,
                "name": "Example",
                "login": {
                  "username": "user@example.com",
                  "uris": [],
                  "fido2Credentials": [{
                    "credentialId": "credential-id",
                    "keyType": "public-key",
                    "keyAlgorithm": "ECDSA",
                    "keyCurve": "P-256",
                    "keyValue": "private-key-pkcs8",
                    "rpId": "example.com",
                    "counter": "42",
                    "discoverable": "true"
                  }]
                }
              }]
            }
            """;

        await fixture.Service.ImportAsync(
            ImportFormat.Json, json, TestContext.Current.CancellationToken);

        var request = Assert.Single(
            Assert.Single(fixture.Api.Last!.Ciphers)
                .Login!.Fido2Credentials!);
        Assert.StartsWith("2.", request.CredentialId);
        Assert.StartsWith("2.", request.KeyValue);
        Assert.StartsWith("2.", request.Counter);
        Assert.StartsWith("2.", request.Discoverable);
    }

    [Fact]
    public void Parse_MalformedJson_ReturnsError() =>
        Assert.NotNull(new VaultImportService(FakeVaultApiClientProvider.ForAuthenticated<IVaultWriteApiClient>(new CapturingImportApi()), new CipherEncryptor(new CryptoService()), new FakeSync(), new VaultSession())
            .Parse(ImportFormat.Json, "{bad").Error);

    [Theory]
    [InlineData("card")]
    [InlineData("identity")]
    [InlineData("sshkey")]
    [InlineData("bankaccount")]
    [InlineData("driverslicense")]
    [InlineData("passport")]
    [InlineData("future")]
    public void Parse_CsvTypeWithoutRepresentableShape_ReturnsActionableError(string typeWord)
    {
        var service = new VaultImportService(
            FakeVaultApiClientProvider.ForAuthenticated<IVaultWriteApiClient>(new CapturingImportApi()),
            new CipherEncryptor(new CryptoService()),
            new FakeSync(),
            new VaultSession());
        var csv =
            "folder,favorite,type,name,notes,fields,reprompt,login_uri,login_username,login_password,login_totp\n" +
            $",,{typeWord},Unsupported,,,,,,,";

        var preview = service.Parse(ImportFormat.Csv, csv);

        Assert.NotNull(preview.Error);
        Assert.Contains("JSON", preview.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportAsync_WhenLocked_Throws() =>
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new VaultImportService(FakeVaultApiClientProvider.ForAuthenticated<IVaultWriteApiClient>(new CapturingImportApi()), new CipherEncryptor(new CryptoService()), new FakeSync(), new VaultSession())
                .ImportAsync(ImportFormat.Json, "{\"items\":[]}", TestContext.Current.CancellationToken));

    [Fact]
    public async Task ImportAsync_AccountSwitchAfterEncryption_DoesNotUploadOrResync()
    {
        var session = new VaultSession();
        var lease = session.BeginAccountSession(
            "account-a",
            "a@example.com",
            new ServerOrigin("https://a.example"));
        session.TryCommit(lease, () => session.SetUnlockedKey(
            new SymmetricCryptoKey(
                Enumerable.Range(0, 64).Select(i => (byte)i).ToArray())));
        var api = new CapturingImportApi();
        var sync = new FakeSync();
        var service = new VaultImportService(
            new SwitchingProvider(session, api),
            new CipherEncryptor(new CryptoService()),
            sync,
            session);
        const string json =
            "{\"encrypted\":false,\"folders\":[],\"items\":[{\"type\":1,\"name\":\"A\",\"login\":{\"uris\":[]}}]}";

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.ImportAsync(
                ImportFormat.Json,
                json,
                TestContext.Current.CancellationToken));

        Assert.Null(api.Last);
        Assert.Equal(0, sync.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportAsync_ExportedIdsBecomeIndependentNewCiphers(bool v2)
    {
        var api = new CapturingImportApi();
        var sync = new FakeSync();
        var session = new VaultSession();
        session.BeginAccountSession("account", "me@example.com", ServerEndpoints.ForSelfHosted("https://vault.example"));
        if (v2)
            session.SetUnlockedKey(V2UserKey.Generate());
        else
            session.SetUnlockedKey(new SymmetricCryptoKey(new byte[64]));
        var service = new VaultImportService(
            new RecordingProvider(api), new CipherEncryptor(new CryptoService()), sync, session);
        const string json =
            """
            {"encrypted":false,"folders":[{"id":"original-folder","name":"Work"}],"items":[
              {"id":"original-item","type":1,"name":"Example","folderId":"original-folder",
               "login":{"username":"user","password":"secret","uris":[{"uri":"https://example.com"}]},
               "passwordHistory":[{"password":"previous","lastUsedDate":"2026-01-01T00:00:00Z"}],
               "fields":[{"name":"custom","value":"retained","type":0}]}
            ]}
            """;
        try
        {
            Assert.Null(service.Parse(ImportFormat.Json, json).Error);
            Assert.Equal(1, await service.ImportAsync(ImportFormat.Json, json, TestContext.Current.CancellationToken));
            var request = Assert.Single(api.Last!.Ciphers);
            Assert.NotNull(request.Key);
            Assert.StartsWith(v2 ? "7." : "2.", request.Key);
            Assert.Null(request.OrganizationId);
            Assert.Null(request.FolderId);
            Assert.Null(request.LastKnownRevisionDate);
            Assert.Equal(0, Assert.Single(api.Last.FolderRelationships).Key);
            Assert.Equal(0, Assert.Single(api.Last.FolderRelationships).Value);
            var requestElement = System.Text.Json.JsonSerializer.SerializeToElement(request, ApiJsonContext.Default.CipherRequest);
            var dto = requestElement.Deserialize(ApiJsonContext.Default.CipherDto)! with { Id = "new-server-id" };
            Assert.True(session.TryCopyUserKeyMaterial(session.CaptureLease(), out var copied));
            using (copied)
            {
                var decryptor = new VaultDecryptor(new CryptoService());
                var decrypted = copied!.Legacy is { } legacy
                    ? decryptor.DecryptCipher(dto, legacy)
                    : decryptor.DecryptCipher(dto, copied.V2!);
                Assert.Equal("Example", decrypted.Name);
                Assert.Equal("secret", decrypted.Login!.Password);
                Assert.Equal("previous", Assert.Single(decrypted.PasswordHistory).Password);
                Assert.Equal("retained", Assert.Single(decrypted.Fields).Value);
            }
            Assert.Equal(1, api.CommitCount);
            Assert.Equal(1, sync.Calls);
        }
        finally
        {
            session.Logout();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportAsync_ConfirmedWriteKeepsCommittedOutcomeWhenRefreshIsCancelled(bool lockSession)
    {
        var api = new CapturingImportApi();
        var session = new VaultSession();
        session.BeginAccountSession("account", "me@example.com", ServerEndpoints.ForSelfHosted("https://vault.example"));
        session.SetUnlockedKey(new SymmetricCryptoKey(new byte[64]));
        using var cancellation = new CancellationTokenSource();
        api.AfterCommit = () =>
        {
            cancellation.Cancel();
            if (lockSession)
                session.Lock();
        };
        var service = new VaultImportService(
            new RecordingProvider(api), new CipherEncryptor(new CryptoService()), new CancelledRefresh(), session);
        const string json = "{\"encrypted\":false,\"folders\":[],\"items\":[{\"type\":1,\"name\":\"Example\",\"login\":{}}]}";
        try
        {
            var exception = await Assert.ThrowsAsync<VaultRefreshException>(() =>
                service.ImportAsync(ImportFormat.Json, json, cancellation.Token));
            Assert.IsAssignableFrom<OperationCanceledException>(exception.InnerException);
            Assert.Equal(1, api.CommitCount);
        }
        finally
        {
            session.Logout();
        }
    }

    private sealed class CancelledRefresh : ISyncService
    {
        public Task<IReadOnlyList<Cipher>> SyncAsync(CancellationToken ct = default) =>
            Task.FromCanceled<IReadOnlyList<Cipher>>(ct);

        public Task<IReadOnlyList<Cipher>> SyncAsync(VaultSessionLease lease, CancellationToken ct = default) =>
            SyncAsync(ct);
    }

    [Theory]
    [MemberData(nameof(SupportedSourceFiles))]
    public async Task PrepareFileAsync_AllEightFormatsReachEncryptedImport(ImportFormat format, byte[] input)
    {
        foreach (var v2 in new[] { false, true })
        {
            var fixture = NewImportFixture();
            if (v2)
                fixture.Session.SetUnlockedKey(V2UserKey.Generate());
            try
            {
                var canonical = await fixture.Service.PrepareFileAsync(format, input, TestContext.Current.CancellationToken);
                var preview = fixture.Service.Parse(ImportFormat.Json, canonical);
                Assert.Null(preview.Error);
                Assert.Equal(1, preview.Ciphers);
                var restored = Assert.Single(Vault.Porting.BitwardenJsonCodec.Parse(canonical).Ciphers);
                Assert.Equal("secret", restored.Login!.Password);
                Assert.Equal(1, await fixture.Service.ImportAsync(ImportFormat.Json, canonical, TestContext.Current.CancellationToken));
                var request = Assert.Single(fixture.Api.Last!.Ciphers);
                Assert.StartsWith(v2 ? "7." : "2.", request.Key);
                Assert.Equal(1, fixture.Api.CommitCount);
            }
            finally
            {
                fixture.Session.Logout();
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportAsync_ExistingOwnedDestinationOverridesSourceFolderRelations(bool v2)
    {
        var fixture = NewImportFixture();
        if (v2)
            fixture.Session.SetUnlockedKey(V2UserKey.Generate());
        fixture.Session.SetSnapshot(new DecryptedVault(fixture.Session.Account,
            [new Folder { Id = "owned-destination", Name = "Destination" }], [], 0, 0));
        const string json = """
            {"encrypted":false,"folders":[{"id":"original","name":"Original"}],"items":[
              {"type":1,"name":"First","folderId":"original","login":{}},
              {"type":1,"name":"Second","login":{}}
            ]}
            """;
        var options = new VaultImportOptions("owned-destination");
        try
        {
            Assert.Equal("owned-destination", Assert.Single(fixture.Service.GetDestinationFolders()).Id);
            var preview = fixture.Service.Parse(ImportFormat.Json, json, options);
            Assert.Null(preview.Error);
            Assert.Equal(2, preview.Ciphers);
            Assert.Equal(0, preview.Folders);
            Assert.Equal(2, await fixture.Service.ImportAsync(ImportFormat.Json, json, options, TestContext.Current.CancellationToken));
            var destination = Assert.Single(fixture.Api.Last!.Folders);
            Assert.Equal("owned-destination", destination.Id);
            Assert.StartsWith(v2 ? "7." : "2.", destination.Name);
            Assert.Equal(new[] { 0, 1 }, fixture.Api.Last.FolderRelationships.Select(relation => relation.Key));
            Assert.All(fixture.Api.Last.FolderRelationships, relation => Assert.Equal(0, relation.Value));
        }
        finally
        {
            fixture.Session.Logout();
        }
    }

    [Fact]
    public async Task ImportAsync_DestinationMustStillBelongToCurrentUnlockedAccount()
    {
        var fixture = NewImportFixture();
        const string json = "{\"encrypted\":false,\"folders\":[],\"items\":[{\"type\":1,\"name\":\"A\",\"login\":{}}]}";
        try
        {
            Assert.NotNull(fixture.Service.Parse(ImportFormat.Json, json, new VaultImportOptions("foreign-folder")).Error);
            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.ImportAsync(
                ImportFormat.Json, json, new VaultImportOptions("foreign-folder"), TestContext.Current.CancellationToken));
            Assert.Null(fixture.Api.Last);
            fixture.Session.Lock();
            Assert.Empty(fixture.Service.GetDestinationFolders());
        }
        finally
        {
            fixture.Session.Logout();
        }
    }

    [Fact]
    public async Task PrepareFileAsync_RejectsBinaryAndUnicodeFailuresWithoutNetwork()
    {
        var fixture = NewImportFixture();
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.PrepareFileAsync(
                ImportFormat.OnePasswordArchive, "not-a-zip"u8.ToArray(), TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<System.Text.DecoderFallbackException>(() => fixture.Service.PrepareFileAsync(
                ImportFormat.ChromeCsv, new byte[] { 0xff }, TestContext.Current.CancellationToken));
            Assert.Null(fixture.Api.Last);
        }
        finally
        {
            fixture.Session.Logout();
        }
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("transport")]
    [InlineData("io")]
    [InlineData("timeout")]
    [InlineData("server-error")]
    [InlineData("partial-bad-request")]
    public async Task ImportAsync_DispatchedFailureReportsUncertainOutcomeWithoutRefresh(string failure)
    {
        var fixture = NewImportFixture();
        fixture.Api.AfterCommit = () => throw failure switch
        {
            "cancel" => new TaskCanceledException("The synthetic response was lost."),
            "transport" => new HttpRequestException("The synthetic connection was lost."),
            "io" => new IOException("The synthetic response stream ended."),
            "timeout" => new TimeoutException("The synthetic request timed out."),
            "server-error" => new VaultWriteException(System.Net.HttpStatusCode.InternalServerError, "Synthetic failure", null),
            _ => new VaultWriteException(System.Net.HttpStatusCode.BadRequest, "Synthetic error after one saved item", null),
        };
        const string json = "{\"encrypted\":false,\"folders\":[],\"items\":[{\"type\":1,\"name\":\"A\",\"login\":{}}]}";
        try
        {
            var exception = await Assert.ThrowsAsync<VaultImportOutcomeUncertainException>(() => fixture.Service.ImportAsync(
                ImportFormat.Json, json, TestContext.Current.CancellationToken));
            Assert.Equal(1, exception.AttemptedCipherCount);
            Assert.Equal(1, fixture.Api.CallCount);
            Assert.Equal(1, fixture.Api.CommitCount);
            Assert.Equal(0, fixture.Sync.Calls);
            Assert.NotNull(exception.InnerException);
        }
        finally
        {
            fixture.Session.Logout();
        }
    }

    [Fact]
    public async Task ImportAsync_PreCancelledOperationDoesNotDispatchOrReportUncertainOutcome()
    {
        var fixture = NewImportFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        const string json = "{\"encrypted\":false,\"folders\":[],\"items\":[{\"type\":1,\"name\":\"A\",\"login\":{}}]}";
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.ImportAsync(ImportFormat.Json, json, cancellation.Token));
            Assert.Equal(0, fixture.Api.CallCount);
            Assert.Equal(0, fixture.Api.CommitCount);
            Assert.Equal(0, fixture.Sync.Calls);
        }
        finally
        {
            fixture.Session.Logout();
        }
    }

    [Fact]
    public async Task ImportAsync_PreDispatchClientFailureRemainsARegularFailure()
    {
        var fixture = NewImportFixture();
        var failure = new HttpRequestException("Synthetic client preparation failed before dispatch.");
        var service = new VaultImportService(new FailingClientProvider(failure), new CipherEncryptor(new CryptoService()), fixture.Sync, fixture.Session);
        const string json = "{\"encrypted\":false,\"folders\":[],\"items\":[{\"type\":1,\"name\":\"A\",\"login\":{}}]}";
        try
        {
            var actual = await Assert.ThrowsAsync<HttpRequestException>(() => service.ImportAsync(
                ImportFormat.Json, json, TestContext.Current.CancellationToken));
            Assert.Same(failure, actual);
            Assert.Equal(0, fixture.Api.CallCount);
            Assert.Equal(0, fixture.Sync.Calls);
        }
        finally
        {
            fixture.Session.Logout();
        }
    }

    private sealed class FailingClientProvider(Exception failure) : IVaultApiClientProvider
    {
        public TClient GetAnonymous<TClient>(ServerOrigin origin) where TClient : class, IAnonymousApiRole => throw failure;
        public TClient GetAuthenticated<TClient>(VaultSessionLease lease) where TClient : class, IAuthenticatedApiRole => throw failure;
    }

    public static IEnumerable<object[]> SupportedSourceFiles()
    {
        var files = new Dictionary<ImportFormat, string>
        {
            [ImportFormat.Json] = "{\"encrypted\":false,\"folders\":[],\"items\":[{\"type\":1,\"name\":\"Example\",\"login\":{\"username\":\"user\",\"password\":\"secret\"}}]}",
            [ImportFormat.Csv] = "folder,favorite,type,name,notes,fields,reprompt,login_uri,login_username,login_password,login_totp\n" + string.Join(',', new[] { "Work", "0", "login", "Example", "Notes", "", "0", "https://example.test", "user", "secret", "" }),
            [ImportFormat.ChromeCsv] = "name,url,username,password,note\r\nExample,https://example.test,user,secret,Notes",
            [ImportFormat.EdgeCsv] = "name,url,username,password,note\r\nExample,https://example.test,user,secret,Notes",
            [ImportFormat.FirefoxCsv] = "url,username,password,httpRealm,formActionOrigin,guid,timeCreated,timeLastUsed,timePasswordChanged\r\nhttps://example.test,user,secret,,,synthetic-guid,1600000000000,1600000000000,1600000000000",
            [ImportFormat.LastPassCsv] = "url,username,password,extra,name,grouping,fav\r\nhttps://example.test,user,secret,Notes,Example,Work,0",
            [ImportFormat.KeePassXml] = "<KeePassFile><Meta/><Root><Group><Name>Database</Name><Entry><String><Key>Title</Key><Value>Example</Value></String><String><Key>UserName</Key><Value>user</Value></String><String><Key>Password</Key><Value>secret</Value></String></Entry></Group></Root></KeePassFile>",
        };
        foreach (var (format, text) in files)
            yield return [format, System.Text.Encoding.UTF8.GetBytes(text)];

        using var buffer = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(buffer, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var attributes = new StreamWriter(zip.CreateEntry("export.attributes").Open(), new System.Text.UTF8Encoding(false)))
                attributes.Write("{\"version\":3,\"description\":\"1Password Unencrypted Export\",\"createdAt\":1600000000}");
            using var data = new StreamWriter(zip.CreateEntry("export.data").Open(), new System.Text.UTF8Encoding(false));
            data.Write("""
                {"accounts":[{"attrs":{"uuid":"account","accountName":"Account"},"vaults":[
                  {"attrs":{"uuid":"vault","name":"Vault","type":"P"},"items":[
                    {"uuid":"item","favIndex":0,"createdAt":1600000000,"updatedAt":1600000010,"state":"active","categoryUuid":"001",
                     "overview":{"title":"Example","urls":[],"tags":[]},"details":{"notesPlain":"","sections":[],"passwordHistory":[],"loginFields":[
                       {"designation":"username","name":"username","fieldType":"T","value":"user"},
                       {"designation":"password","name":"password","fieldType":"P","value":"secret"}
                     ]}}
                  ]}
                ]}]}
                """);
        }
        yield return [ImportFormat.OnePasswordArchive, buffer.ToArray()];
    }
}
