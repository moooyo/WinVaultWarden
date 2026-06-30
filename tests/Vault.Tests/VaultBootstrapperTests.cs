using System.Net;
using System.Text;
using System.Text.Json;
using Api;
using Core.Enums;
using Crypto;
using Vault;
using Xunit;

namespace Vault.Tests;

public class VaultBootstrapperTests
{
    [Fact]
    public async Task BootstrapAsync_DecryptsSyncAndStoresDevices()
    {
        var crypto = new CryptoService();
        var userKey = new SymmetricCryptoKey(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, SyncJson(crypto, userKey)));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK,
            """{"data":[{"id":"d1","name":"Desktop","type":6,"identifier":"d1","creationDate":"2026-06-24T00:00:00Z","isTrusted":false,"object":"device"}],"object":"list","continuationToken":null}"""));
        var api = new ApiClient(new HttpClient(handler));
        api.SetBaseAddress("https://vault.example");
        var session = new VaultSession();
        session.SetUnlockedKey(userKey);
        var bootstrapper = new VaultBootstrapper(api, new VaultDecryptor(crypto), session);

        await bootstrapper.BootstrapAsync("https://vault.example", TestContext.Current.CancellationToken);

        Assert.Equal(Core.Session.VaultState.Unlocked, session.State);
        Assert.Equal("GitHub", Assert.Single(session.Ciphers).Name);
        Assert.Equal("Work", Assert.Single(session.Folders).Name);
        Assert.Equal("Desktop", Assert.Single(session.Devices).Name);
        Assert.Equal("me@example.com", session.Account.Email);
    }

    [Fact]
    public async Task BootstrapAsync_SetsEncryptedPrivateKey()
    {
        var crypto = new CryptoService();
        var userKey = new SymmetricCryptoKey(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());
        const string fakePrivateKey = "2.fakeEncryptedPrivateKey|iv|mac";
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, SyncJsonWithPrivateKey(crypto, userKey, fakePrivateKey)));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK,
            """{"data":[{"id":"d1","name":"Desktop","type":6,"identifier":"d1","creationDate":"2026-06-24T00:00:00Z","isTrusted":false,"object":"device"}],"object":"list","continuationToken":null}"""));
        var api = new ApiClient(new HttpClient(handler));
        api.SetBaseAddress("https://vault.example");
        var session = new VaultSession();
        session.SetUnlockedKey(userKey);
        var bootstrapper = new VaultBootstrapper(api, new VaultDecryptor(crypto), session);

        await bootstrapper.BootstrapAsync("https://vault.example", TestContext.Current.CancellationToken);

        Assert.Equal(fakePrivateKey, session.EncryptedPrivateKey);
    }

    private static string SyncJsonWithPrivateKey(CryptoService crypto, SymmetricCryptoKey key, string privateKey)
    {
        static string Enc(CryptoService crypto, SymmetricCryptoKey key, string value) =>
            crypto.Encrypt(Encoding.UTF8.GetBytes(value), key).ToString();

        return JsonSerializer.Serialize(new
        {
            @object = "sync",
            profile = new { id = "u1", email = "me@example.com", name = "Me", privateKey },
            folders = new[]
            {
                new { id = "f1", name = Enc(crypto, key, "Work"), revisionDate = "2026-06-24T00:00:00Z" },
            },
            ciphers = new[]
            {
                new
                {
                    id = "c1",
                    type = (int)CipherType.Login,
                    name = Enc(crypto, key, "GitHub"),
                    favorite = false,
                    reprompt = 0,
                    login = new { username = Enc(crypto, key, "octo") },
                },
            },
        });
    }

    private static string SyncJson(CryptoService crypto, SymmetricCryptoKey key)
    {
        static string Enc(CryptoService crypto, SymmetricCryptoKey key, string value) =>
            crypto.Encrypt(Encoding.UTF8.GetBytes(value), key).ToString();

        return JsonSerializer.Serialize(new
        {
            @object = "sync",
            profile = new { id = "u1", email = "me@example.com", name = "Me" },
            folders = new[]
            {
                new { id = "f1", name = Enc(crypto, key, "Work"), revisionDate = "2026-06-24T00:00:00Z" },
            },
            ciphers = new[]
            {
                new
                {
                    id = "c1",
                    type = (int)CipherType.Login,
                    name = Enc(crypto, key, "GitHub"),
                    favorite = false,
                    reprompt = 0,
                    login = new { username = Enc(crypto, key, "octo") },
                },
            },
        });
    }
}
