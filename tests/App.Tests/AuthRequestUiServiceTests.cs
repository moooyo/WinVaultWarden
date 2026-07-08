using App.Services;
using Core.Models;
using Core.Services;
using Xunit;

namespace App.Tests;

public class AuthRequestUiServiceTests
{
    // Map 必须把 Core 的 FingerprintPhrase 透传到 UI 层 AuthRequestItem。
    [Fact]
    public async Task ListPending_MapsFingerprintPhraseThrough()
    {
        var stub = new StubAuthRequestService();
        var ui = new AuthRequestUiService(stub);

        var items = await ui.ListPendingAsync();

        var item = Assert.Single(items);
        Assert.Equal("alpha-bravo-charlie-delta-echo", item.FingerprintPhrase);
    }

    private sealed class StubAuthRequestService : IAuthRequestService
    {
        public Task<IReadOnlyList<PendingAuthRequest>> ListPendingAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PendingAuthRequest>>(new[]
            {
                new PendingAuthRequest("id-1", "Android", "1.2.3.4", "2025-01-01T10:00:00Z", "pk")
                {
                    FingerprintPhrase = "alpha-bravo-charlie-delta-echo",
                },
            });

        public Task ApproveAsync(string id, string publicKey, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DenyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
    }
}
