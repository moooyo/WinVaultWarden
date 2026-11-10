using System.Net;
using System.Text.Json;
using Api;
using Api.Dtos;
using Core.Models;
using Core.Services;
using Crypto;
using Vault;
using Xunit;

namespace Vault.Tests;

public partial class VaultWriteServiceTests
{
    [Fact]
    public async Task CreateFolderAndGetId_ReturnsServerIdAndRefreshesOriginalLease()
    {
        var (service, session) = NewUnlockedServiceWithSnapshot();
        var lease = session.CaptureLease();
        session.UpsertFolder(new Folder
        {
            Id = "existing-folder", Name = "Work", RevisionDate = DateTimeOffset.UtcNow,
        });
        _api.FolderCreateResponse = new FolderDto("server-created-folder", "2.encrypted", null);

        var id = await service.CreateFolderAndGetIdAsync("Work", TestContext.Current.CancellationToken);

        Assert.Equal("server-created-folder", id);
        Assert.Equal("folder-create-response", Assert.Single(_api.Calls));
        Assert.StartsWith("2.", _api.LastFolder!.Name);
        Assert.Equal(1, _sync.Calls);
        Assert.True(lease.SameSessionAs(_sync.LastLease));
    }

    [Fact]
    public async Task CreateFolderAndGetId_ResponseReadFailureMarksSnapshotStaleWithoutGuessingId()
    {
        var (service, session) = NewUnlockedServiceWithSnapshot();
        var failure = new FolderCreateSuccessResponseReadException(HttpStatusCode.OK, new JsonException());
        _api.FolderCreateFailure = failure;

        var thrown = await Assert.ThrowsAsync<FolderCreateSuccessResponseReadException>(() =>
            service.CreateFolderAndGetIdAsync("Work", TestContext.Current.CancellationToken));

        Assert.Same(failure, thrown);
        Assert.True(session.DataStale);
        Assert.Equal("folder-create-response", Assert.Single(_api.Calls));
        Assert.Equal(0, _sync.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task CreateFolderAndGetId_InvalidResponseIdDoesNotReturnANameBasedMatch(string? id)
    {
        var (service, session) = NewUnlockedServiceWithSnapshot();
        session.UpsertFolder(new Folder
        {
            Id = "existing-folder", Name = "Work", RevisionDate = DateTimeOffset.UtcNow,
        });
        _api.FolderCreateResponse = new FolderDto(id!, "2.encrypted", null);

        await Assert.ThrowsAsync<FolderCreateSuccessResponseReadException>(() =>
            service.CreateFolderAndGetIdAsync("Work", TestContext.Current.CancellationToken));

        Assert.True(session.DataStale);
        Assert.Equal("folder-create-response", Assert.Single(_api.Calls));
        Assert.Equal(0, _sync.Calls);
    }

    [Fact]
    public async Task CreateFolderAndGetId_RefreshFailureKeepsCommittedWriteSemantics()
    {
        var (service, session) = NewUnlockedServiceWithSnapshot();
        var failure = new HttpRequestException("Refresh unavailable.");
        _sync.Failure = failure;

        var thrown = await Assert.ThrowsAsync<VaultRefreshException>(() =>
            service.CreateFolderAndGetIdAsync("Work", TestContext.Current.CancellationToken));

        Assert.Same(failure, thrown.InnerException);
        Assert.True(session.DataStale);
        Assert.Equal("folder-create-response", Assert.Single(_api.Calls));
        Assert.Equal(1, _sync.Calls);
    }

    [Fact]
    public async Task CreateFolderAndGetId_CancelledBeforeDispatchDoesNotWriteOrRefresh()
    {
        var service = NewUnlockedService();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.CreateFolderAndGetIdAsync("Work", cancellation.Token));

        Assert.Empty(_api.Calls);
        Assert.Equal(0, _sync.Calls);
    }

    [Fact]
    public async Task CreateFolderAndGetId_WhenLockedDoesNotWriteOrRefresh()
    {
        var service = new VaultWriteService(
            FakeVaultApiClientProvider.ForAuthenticated<IVaultWriteApiClient>(_api),
            new CipherEncryptor(new CryptoService()), _sync, new VaultSession());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateFolderAndGetIdAsync("Work", TestContext.Current.CancellationToken));

        Assert.Empty(_api.Calls);
        Assert.Equal(0, _sync.Calls);
    }

    [Fact]
    public async Task CreateFolderAndGetId_AccountSwitchAfterCreateDoesNotReturnIdOrRefresh()
    {
        var (service, session) = NewUnlockedServiceWithSnapshot();
        _api.BeforeFolderCreateCompletion = () =>
        {
            session.BeginAccountSession("replacement", "new@example.com", new ServerOrigin("https://other.example"));
            session.SetUnlockedKey(new SymmetricCryptoKey(new byte[64]));
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.CreateFolderAndGetIdAsync("Work", TestContext.Current.CancellationToken));

        Assert.Equal("folder-create-response", Assert.Single(_api.Calls));
        Assert.Equal(0, _sync.Calls);
        Assert.False(session.DataStale);
    }

    [Fact]
    public async Task CreateFolderAndGetId_AccountSwitchDuringRefreshDoesNotReturnOldId()
    {
        var (service, session) = NewUnlockedServiceWithSnapshot();
        var lease = session.CaptureLease();
        _sync.BeforeCompletion = () =>
        {
            session.BeginAccountSession("replacement", "new@example.com", new ServerOrigin("https://other.example"));
            session.SetUnlockedKey(new SymmetricCryptoKey(new byte[64]));
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.CreateFolderAndGetIdAsync("Work", TestContext.Current.CancellationToken));

        Assert.Equal(1, _sync.Calls);
        Assert.True(lease.SameSessionAs(_sync.LastLease));
        Assert.False(session.DataStale);
    }
}
