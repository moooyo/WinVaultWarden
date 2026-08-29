using Api;
using Core.Session;

namespace Vault;

/// <summary>
/// Creates a one-shot authenticated client from the metadata-create authorization receipt. Refresh
/// is disabled and the normal session provider is bypassed so compensation remains possible after
/// lock while the token stays constrained to its recorded origin.
/// </summary>
public sealed class SendCompensationClient : ISendCompensationClient
{
    private readonly IApiClientFactory _factory;

    public SendCompensationClient(IApiClientFactory factory) => _factory = factory;

    public async Task DeleteAsync(AccessTokenBinding binding, string sendId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentException.ThrowIfNullOrWhiteSpace(sendId);

        using var client = _factory.CreateAuthenticated(
            binding.Endpoints,
            () => binding,
            (_, _) => Task.FromResult(false));
        await client.DeleteSendForReplacementAsync(sendId, ct);
    }
}
