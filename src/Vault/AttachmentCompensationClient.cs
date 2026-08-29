using System.Net;
using Api;
using Core.Session;

namespace Vault;

public sealed class AttachmentCompensationClient : IAttachmentCompensationClient
{
    private readonly IApiClientFactory _factory;

    public AttachmentCompensationClient(IApiClientFactory factory) => _factory = factory;

    public async Task DeleteAsync(
        AccessTokenBinding binding,
        string cipherId,
        string attachmentId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentException.ThrowIfNullOrWhiteSpace(cipherId);
        ArgumentException.ThrowIfNullOrWhiteSpace(attachmentId);

        using var client = _factory.CreateAuthenticated(
            binding.Endpoints,
            () => binding,
            (_, _) => Task.FromResult(false));
        try
        {
            await client.DeleteAttachmentAsync(cipherId, attachmentId, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            // The metadata is already absent, which satisfies compensation.
        }
    }
}
